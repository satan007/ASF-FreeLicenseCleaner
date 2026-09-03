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
///
/// All timing/retry knobs live in <see cref="CleanerOptions"/>, which the
/// plugin builds from that bot's own config - see
/// FreeLicenseCleanerPlugin.ConfigKeys.
/// </summary>
internal sealed partial class CleanerWorker : IDisposable {
	private static readonly Uri LicensesUri = new("https://store.steampowered.com/account/licenses/");

	private static string PluginVersion => typeof(CleanerWorker).Assembly.GetName().Version?.ToString() ?? "0";

	private readonly Bot _bot;
	private readonly CleanerOptions _options;
	private readonly StateStore _state;
	private readonly CancellationTokenSource _cts = new();

	private Task? _loopTask;

	internal CleanerWorker(Bot bot, CleanerOptions options, string dataDirectory) {
		_bot = bot;
		_options = options;

		Directory.CreateDirectory(dataDirectory);

		_state = new StateStore(Path.Combine(dataDirectory, $"{bot.BotName}.json"));
	}

	internal void Start() => _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));

	internal async Task StopAsync() {
		await _cts.CancelAsync().ConfigureAwait(false);

		if (_loopTask == null) {
			return;
		}

		try {
			await _loopTask.ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// Expected on shutdown.
		}
	}

	public void Dispose() => _cts.Dispose();

	internal string GetStatusText() {
		IReadOnlyDictionary<LicenseStatus, int> stats = _state.Statistics();

		int Count(LicenseStatus status) => stats.TryGetValue(status, out int value) ? value : 0;

		return $"FreeLicenseCleaner | DryRun={_options.DryRun} | "
			+ $"pending={Count(LicenseStatus.Pending)} | "
			+ $"processed={Count(LicenseStatus.Processed)} | "
			+ $"duplicate={Count(LicenseStatus.Duplicate)} | "
			+ $"invalid_state={Count(LicenseStatus.InvalidState)} | "
			+ $"failed_max_attempts={Count(LicenseStatus.FailedMaxAttempts)} | "
			+ $"MaxAttempts={_options.MaxAttempts} | "
			+ $"SuccessDelay={_options.SuccessDelaySeconds}s | "
			+ $"RateLimitDelay={_options.RateLimitDelaySeconds}s | "
			+ $"ErrorDelay={_options.ErrorDelaySeconds}s | "
			+ $"IdleDelay={_options.IdleDelaySeconds}s | "
			+ $"FullScanInterval={_options.FullScanIntervalMinutes}min";
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
		_bot.ArchiLogger.LogGenericInfo($"Free License Cleaner loop started (DryRun={_options.DryRun}).");

		while (!cancellationToken.IsCancellationRequested) {
			TimeSpan delay;

			try {
				if (!_bot.IsConnectedAndLoggedOn) {
					delay = TimeSpan.FromSeconds(_options.ErrorDelaySeconds);
				} else {
					(bool scanRequired, string reason) = _state.FullScanRequired(PluginVersion, _options.FullScanIntervalMinutes);

					if (scanRequired) {
						await PerformFullScanAsync(reason).ConfigureAwait(false);
					}

					delay = await ProcessOnePendingAsync().ConfigureAwait(false);
				}
			} catch (OperationCanceledException) {
				break;
			} catch (Exception e) {
				_bot.ArchiLogger.LogGenericException(e);

				delay = TimeSpan.FromSeconds(_options.ErrorDelaySeconds);
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
			return TimeSpan.FromSeconds(_options.IdleDelaySeconds);
		}

		(uint subID, LicenseRecord record) = next.Value;

		// Safety net: a SubID that keeps returning a status we don't
		// recognise as terminal would otherwise stay 'pending' forever and
		// block every other SubID queued behind it (this is exactly what
		// happened with InvalidState in the original script before it was
		// made terminal below).
		if (record.Attempts >= _options.MaxAttempts) {
			_state.MarkAttempt(subID, LicenseStatus.FailedMaxAttempts, "max attempts exceeded", true);

			_bot.ArchiLogger.LogGenericWarning(
				$"SubID {subID} exceeded {_options.MaxAttempts} attempts without a terminal result. Marked as failed and will not be retried."
			);

			return TimeSpan.FromSeconds(_options.SuccessDelaySeconds);
		}

		if (_options.DryRun) {
			_bot.ArchiLogger.LogGenericInfo($"[DryRun] Would remove sub/{subID} ({record.Name}).");

			return TimeSpan.FromSeconds(_options.SuccessDelaySeconds);
		}

		_bot.ArchiLogger.LogGenericInfo($"Removing sub/{subID} ({record.Name})...");

		EResult result;

		try {
			result = await _bot.Actions.RemoveLicensePackage(subID).ConfigureAwait(false);
		} catch (Exception e) {
			_bot.ArchiLogger.LogGenericException(e);
			_state.MarkAttempt(subID, LicenseStatus.Pending, e.Message, false);

			return TimeSpan.FromSeconds(_options.ErrorDelaySeconds);
		}

		_bot.ArchiLogger.LogGenericInfo($"SubID {subID} -> {result}");

		switch (result) {
			case EResult.OK:
				_state.MarkAttempt(subID, LicenseStatus.Processed, result.ToString(), true);

				return TimeSpan.FromSeconds(_options.SuccessDelaySeconds);

			case EResult.DuplicateRequest:
				_state.MarkAttempt(subID, LicenseStatus.Duplicate, result.ToString(), true);

				return TimeSpan.FromSeconds(_options.SuccessDelaySeconds);

			case EResult.InvalidState:
				// Terminal: this is what previously caused the standalone
				// script to loop forever on the same SubID (e.g. Eldevin,
				// sub/43301) - Steam consistently refuses to remove it, so
				// retrying is pointless.
				_state.MarkAttempt(subID, LicenseStatus.InvalidState, result.ToString(), true);

				return TimeSpan.FromSeconds(_options.SuccessDelaySeconds);

			case EResult.RateLimitExceeded:
				_state.MarkAttempt(subID, LicenseStatus.Pending, result.ToString(), false);
				_bot.ArchiLogger.LogGenericWarning($"RateLimitExceeded for SubID {subID}.");

				return TimeSpan.FromSeconds(_options.RateLimitDelaySeconds);

			default:
				_state.MarkAttempt(subID, LicenseStatus.Pending, result.ToString(), false);

				_bot.ArchiLogger.LogGenericWarning(
					$"Unrecognised RemoveLicense result for SubID {subID}: {result} (attempt {record.Attempts + 1}/{_options.MaxAttempts})"
				);

				return TimeSpan.FromSeconds(_options.ErrorDelaySeconds);
		}
	}

	private async Task<int> PerformFullScanAsync(string reason) {
		_bot.ArchiLogger.LogGenericInfo($"Full Steam license scan starting. Reason: {reason}");

		List<FreeLicenseCandidate> licenses = new();
		HashSet<uint> seen = new();

		Uri? nextUri = LicensesUri;
		int page = 0;

		while ((nextUri != null) && (page < _options.MaxStorePages)) {
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
				await Task.Delay(_options.StorePageDelayMilliseconds).ConfigureAwait(false);
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
