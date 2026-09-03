using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace FreeLicenseCleaner;

/// <summary>
/// One background loop per bot. Ports the standalone Python script's
/// architecture (full Steam scan -> persisted queue -> one throttled RL
/// request at a time) to run natively inside the ASF process, using
/// Bot.Actions.RemoveLicensePackage() and Bot.ArchiWebHandler directly
/// instead of going through ASF's own IPC/console text output.
/// </summary>
internal sealed partial class CleanerWorker {
	// Same defaults as the Python script (SUCCESS/RATE_LIMIT/ERROR/FULL_SCAN_INTERVAL).
	private const int MaxAttempts = 5;
	private const int SuccessDelaySeconds = 30;
	private const int RateLimitDelaySeconds = 660;
	private const int ErrorDelaySeconds = 30;
	private const int IdleDelaySeconds = 300;
	private const int FullScanIntervalMinutes = 1440;
	private const int StorePageDelayMilliseconds = 500;
	private const int MaxStorePages = 1000;

	private static readonly Uri LicensesUri = new("https://store.steampowered.com/account/licenses/");

	private static string PluginVersion => typeof(CleanerWorker).Assembly.GetName().Version?.ToString() ?? "0";

	private readonly Bot _bot;
	private readonly bool _dryRun;
	private readonly StateStore _state;
	private readonly CancellationTokenSource _cts = new();

	private Task? _loopTask;

	internal CleanerWorker(Bot bot, bool dryRun, string dataDirectory) {
		_bot = bot;
		_dryRun = dryRun;

		Directory.CreateDirectory(dataDirectory);

		_state = new StateStore(Path.Combine(dataDirectory, $"{bot.BotName}.json"));
	}

	internal void Start() => _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));

	internal async Task StopAsync() {
		_cts.Cancel();

		if (_loopTask == null) {
			return;
		}

		try {
			await _loopTask.ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// Expected on shutdown.
		}
	}

	internal string GetStatusText() {
		IReadOnlyDictionary<LicenseStatus, int> stats = _state.Statistics();

		int Count(LicenseStatus status) => stats.TryGetValue(status, out int value) ? value : 0;

		return $"FreeLicenseCleaner | DryRun={_dryRun} | "
			+ $"pending={Count(LicenseStatus.Pending)} | "
			+ $"processed={Count(LicenseStatus.Processed)} | "
			+ $"duplicate={Count(LicenseStatus.Duplicate)} | "
			+ $"invalid_state={Count(LicenseStatus.InvalidState)} | "
			+ $"failed_max_attempts={Count(LicenseStatus.FailedMaxAttempts)}";
	}

	internal async Task<string> TriggerFullScanAsync() {
		try {
			int added = await PerformFullScanAsync("manual trigger").ConfigureAwait(false);

			return $"Full scan complete, {added} new license(s) added. {GetStatusText()}";
		} catch (Exception e) {
			_bot.ArchiLogger.LogGenericException(e);

			return $"Full scan failed: {e.Message}";
		}
	}

	private async Task RunLoopAsync(CancellationToken cancellationToken) {
		_bot.ArchiLogger.LogGenericInfo($"Free License Cleaner loop started (DryRun={_dryRun}).");

		while (!cancellationToken.IsCancellationRequested) {
			TimeSpan delay;

			try {
				if (!_bot.IsConnectedAndLoggedOn) {
					delay = TimeSpan.FromSeconds(ErrorDelaySeconds);
				} else {
					(bool scanRequired, string reason) = _state.FullScanRequired(PluginVersion, FullScanIntervalMinutes);

					if (scanRequired) {
						await PerformFullScanAsync(reason).ConfigureAwait(false);
					}

					delay = await ProcessOnePendingAsync().ConfigureAwait(false);
				}
			} catch (OperationCanceledException) {
				break;
			} catch (Exception e) {
				_bot.ArchiLogger.LogGenericException(e);

				delay = TimeSpan.FromSeconds(ErrorDelaySeconds);
			}

			try {
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}
		}

		_bot.ArchiLogger.LogGenericInfo("Free License Cleaner loop stopped.");
	}

	private async Task<TimeSpan> ProcessOnePendingAsync() {
		(uint SubID, LicenseRecord Record)? next = _state.GetNextPending();

		if (next == null) {
			return TimeSpan.FromSeconds(IdleDelaySeconds);
		}

		(uint subID, LicenseRecord record) = next.Value;

		// Safety net: a SubID that keeps returning a status we don't
		// recognise as terminal would otherwise stay 'pending' forever and
		// block every other SubID queued behind it (this is exactly what
		// happened with InvalidState in the original script before it was
		// made terminal below).
		if (record.Attempts >= MaxAttempts) {
			_state.MarkAttempt(subID, LicenseStatus.FailedMaxAttempts, "max attempts exceeded", true);

			_bot.ArchiLogger.LogGenericWarning(
				$"SubID {subID} exceeded {MaxAttempts} attempts without a terminal result. Marked as failed and will not be retried."
			);

			return TimeSpan.FromSeconds(SuccessDelaySeconds);
		}

		if (_dryRun) {
			_bot.ArchiLogger.LogGenericInfo($"[DryRun] Would remove sub/{subID} ({record.Name}).");

			return TimeSpan.FromSeconds(SuccessDelaySeconds);
		}

		_bot.ArchiLogger.LogGenericInfo($"Removing sub/{subID} ({record.Name})...");

		EResult result;

		try {
			result = await _bot.Actions.RemoveLicensePackage(subID).ConfigureAwait(false);
		} catch (Exception e) {
			_bot.ArchiLogger.LogGenericException(e);
			_state.MarkAttempt(subID, LicenseStatus.Pending, e.Message, false);

			return TimeSpan.FromSeconds(ErrorDelaySeconds);
		}

		_bot.ArchiLogger.LogGenericInfo($"SubID {subID} -> {result}");

		switch (result) {
			case EResult.OK:
				_state.MarkAttempt(subID, LicenseStatus.Processed, result.ToString(), true);

				return TimeSpan.FromSeconds(SuccessDelaySeconds);

			case EResult.DuplicateRequest:
				_state.MarkAttempt(subID, LicenseStatus.Duplicate, result.ToString(), true);

				return TimeSpan.FromSeconds(SuccessDelaySeconds);

			case EResult.InvalidState:
				// Terminal: this is what previously caused the standalone
				// script to loop forever on the same SubID (e.g. Eldevin,
				// sub/43301) - Steam consistently refuses to remove it, so
				// retrying is pointless.
				_state.MarkAttempt(subID, LicenseStatus.InvalidState, result.ToString(), true);

				return TimeSpan.FromSeconds(SuccessDelaySeconds);

			case EResult.RateLimitExceeded:
				_state.MarkAttempt(subID, LicenseStatus.Pending, result.ToString(), false);
				_bot.ArchiLogger.LogGenericWarning($"RateLimitExceeded for SubID {subID}.");

				return TimeSpan.FromSeconds(RateLimitDelaySeconds);

			default:
				_state.MarkAttempt(subID, LicenseStatus.Pending, result.ToString(), false);

				_bot.ArchiLogger.LogGenericWarning(
					$"Unrecognised RemoveLicense result for SubID {subID}: {result} (attempt {record.Attempts + 1}/{MaxAttempts})"
				);

				return TimeSpan.FromSeconds(ErrorDelaySeconds);
		}
	}

	private async Task<int> PerformFullScanAsync(string reason) {
		_bot.ArchiLogger.LogGenericInfo($"Full Steam license scan starting. Reason: {reason}");

		List<FreeLicenseCandidate> licenses = new();
		HashSet<uint> seen = new();

		Uri? nextUri = LicensesUri;
		int page = 0;

		while ((nextUri != null) && (page < MaxStorePages)) {
			page++;

			using HtmlDocumentResponse? response = await _bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(nextUri).ConfigureAwait(false);

			IDocument? document = response?.Content;

			if (document == null) {
				throw new InvalidOperationException($"Steam did not return a valid licenses page (page {page}).");
			}

			foreach (IElement element in document.QuerySelectorAll("[onclick]")) {
				string? onclick = element.GetAttribute("onclick");

				if (string.IsNullOrEmpty(onclick)) {
					continue;
				}

				Match match = RemoveFreeLicenseRegex().Match(onclick);

				if (!match.Success) {
					continue;
				}

				uint subID = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

				if (!seen.Add(subID)) {
					continue;
				}

				licenses.Add(new FreeLicenseCandidate(subID, DecodeLicenseName(match.Groups[2].Value)));
			}

			IElement? nextLink = document.QuerySelector("a.license_paginator_next");
			string? href = nextLink?.GetAttribute("href");

			nextUri = string.IsNullOrEmpty(href) ? null : new Uri(LicensesUri, href);

			if ((page == 1) || (page % 10 == 0)) {
				_bot.ArchiLogger.LogGenericInfo($"Steam licenses page {page} | free candidates so far: {licenses.Count}");
			}

			if (nextUri != null) {
				await Task.Delay(StorePageDelayMilliseconds).ConfigureAwait(false);
			}
		}

		int newCount = _state.AddMany(licenses);

		_state.MarkFullScan(PluginVersion);

		_bot.ArchiLogger.LogGenericInfo(
			$"Full scan complete. Pages: {page} | Free candidates: {licenses.Count} | New: {newCount} | {GetStatusText()}"
		);

		return newCount;
	}

	private static string DecodeLicenseName(string base64) {
		try {
			int padding = base64.Length % 4;

			if (padding > 0) {
				base64 += new string('=', 4 - padding);
			}

			byte[] bytes = Convert.FromBase64String(base64);
			string result = Encoding.UTF8.GetString(bytes).Trim();

			return result.Length > 0 ? result : "Unknown";
		} catch (FormatException) {
			return "Unknown";
		}
	}

	// Matches the same onclick="RemoveFreeLicense(43301, 'RWxkZXZpbg==')" pattern
	// the original Python script parsed with REMOVE_FREE_LICENSE_RE.
	[GeneratedRegex("""RemoveFreeLicense\s*\(\s*(\d+)\s*,\s*['"]([^'"]*)['"]\s*\)""", RegexOptions.IgnoreCase)]
	private static partial Regex RemoveFreeLicenseRegex();
}
