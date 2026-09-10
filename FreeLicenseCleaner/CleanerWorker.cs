using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;
using SteamKit2.Internal;

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

	// Deliberately NOT typeof(CleanerWorker).Assembly.GetName().Version:
	// that changes on every release (the plugin now has real auto-update,
	// so it bumps often), and a mismatch forces a full ~500+ page Steam
	// rescan - so tying it to the assembly version meant "updated the
	// plugin" silently became "rescan everything again", which looked
	// like prior progress had been wiped (it hadn't - AddMany() never
	// resets an existing record's status, only refreshes its name).
	// Bump this constant by hand only when PerformFullScanAsync's actual
	// scanning/parsing logic changes and old cached results might need a
	// fresh look (e.g. the href-vs-onclick fix would have warranted it).
	private const string ScanLogicVersion = "2";

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
			+ $"invalid_param={Count(LicenseStatus.InvalidParam)} | "
			+ $"excluded={Count(LicenseStatus.Excluded)} | "
			+ $"failed_max_attempts={Count(LicenseStatus.FailedMaxAttempts)} | "
			+ $"MaxAttempts={_options.MaxAttempts} | "
			+ $"SuccessDelay={_options.SuccessDelaySeconds}s | "
			+ $"RateLimitDelay={_options.RateLimitDelaySeconds}s | "
			+ $"ErrorDelay={_options.ErrorDelaySeconds}s | "
			+ $"IdleDelay={_options.IdleDelaySeconds}s | "
			+ $"FullScanInterval={_options.FullScanIntervalMinutes}min";
	}

	/// <summary>"flc list" - shows actual SubIDs/names, not just counts, for every non-pending bucket plus a capped preview of pending.</summary>
	internal string GetListText() {
		const int pendingPreview = 15;
		const int terminalPreview = 20;

		StringBuilder sb = new();

		void AppendSection(string label, LicenseStatus status, int limit, bool includeReason = false) {
			List<(uint SubID, LicenseRecord Record)> items = _state.GetByStatus(status, limit + 1);

			if (items.Count == 0) {
				return;
			}

			bool truncated = items.Count > limit;

			if (truncated) {
				items.RemoveAt(items.Count - 1);
			}

			sb.Append(label).Append(" (").Append(items.Count);

			if (truncated) {
				sb.Append('+');
			}

			sb.Append("): ");

			sb.AppendJoin(
				", ",
				items.Select(item => includeReason
					? $"{item.SubID} ({item.Record.Name}) [{item.Record.LastResult}]"
					: $"{item.SubID} ({item.Record.Name})")
			);

			sb.Append('\n');
		}

		AppendSection("Pending", LicenseStatus.Pending, pendingPreview);
		AppendSection("Invalid state", LicenseStatus.InvalidState, terminalPreview);
		AppendSection("Invalid param", LicenseStatus.InvalidParam, terminalPreview);
		AppendSection("Excluded", LicenseStatus.Excluded, terminalPreview, true);
		AppendSection("Failed (max attempts)", LicenseStatus.FailedMaxAttempts, terminalPreview);

		return sb.Length > 0 ? sb.ToString().TrimEnd('\n') : "Nothing tracked yet - run \"flc scan\" first.";
	}

	/// <summary>"flc retry &lt;subid&gt;" - manually re-queues a SubID regardless of its current status.</summary>
	internal string RetrySubId(uint subID) {
		return _state.ResetToPending(subID)
			? $"SubID {subID} reset to pending and will be retried."
			: $"SubID {subID} is not tracked - run \"flc scan\" first, or check the ID.";
	}

	internal async Task<string> TriggerFullScanAsync() {
		try {
			int added = await PerformFullScanAsync("manual trigger").ConfigureAwait(false);

			await RefreshPlaytimeProtectionAsync().ConfigureAwait(false);

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
					(bool scanRequired, string reason) = _state.FullScanRequired(ScanLogicVersion, _options.FullScanIntervalMinutes);

					if (scanRequired) {
						await PerformFullScanAsync(reason).ConfigureAwait(false);
						await RefreshPlaytimeProtectionAsync().ConfigureAwait(false);
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

		// Checked ahead of everything else, including attempts already
		// spent: an operator-excluded SubID is never sent, no matter how
		// it got into the queue (fresh scan, or already pending from
		// before the exclude list existed).
		if (_options.ExcludeSubIds.Contains(subID)) {
			_state.MarkAttempt(subID, LicenseStatus.Excluded, "excluded by configuration", true);

			_bot.ArchiLogger.LogGenericInfo($"SubID {subID} ({record.Name}) is excluded by configuration and will not be removed.");

			return TimeSpan.FromSeconds(_options.SuccessDelaySeconds);
		}

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

			case EResult.InvalidParam:
				// Terminal, same reasoning as InvalidState: observed in
				// production to repeat indefinitely for specific SubIDs
				// (e.g. sub/105231, sub/105234) rather than being a
				// transient failure, so retrying wastes MaxAttempts
				// cycles - each with a possible RateLimitExceeded wait in
				// between - for no benefit.
				_state.MarkAttempt(subID, LicenseStatus.InvalidParam, result.ToString(), true);

				return TimeSpan.FromSeconds(_options.SuccessDelaySeconds);

			case EResult.RateLimitExceeded:
				_state.MarkAttempt(subID, LicenseStatus.Pending, result.ToString(), false);
				_bot.ArchiLogger.LogGenericWarning($"RateLimitExceeded for SubID {subID}.");

				return TimeSpan.FromSeconds(_options.RateLimitDelaySeconds);

			default:
				// record is the same LicenseRecord instance stored in
				// _state.Licenses (StateStore never clones it), so
				// MarkAttempt() above has already incremented
				// record.Attempts in place by the time we log it here -
				// using "+ 1" on top of that double-counts the attempt.
				_state.MarkAttempt(subID, LicenseStatus.Pending, result.ToString(), false);

				_bot.ArchiLogger.LogGenericWarning(
					$"Unrecognised RemoveLicense result for SubID {subID}: {result} (attempt {record.Attempts}/{_options.MaxAttempts})"
				);

				return TimeSpan.FromSeconds(_options.ErrorDelaySeconds);
		}
	}

	/// <summary>
	/// Protects packages with real playtime from removal. Runs once per
	/// full scan (not per removal attempt, to avoid an extra Steam
	/// round-trip on every single item): fetches the bot's owned-games
	/// playtime by appID, resolves which appIDs each pending package
	/// contains via PICS, and permanently marks any pending package whose
	/// playtime meets <see cref="CleanerOptions.MinPlaytimeToExcludeMinutes"/>
	/// as Excluded. A package Steam hasn't handed ASF an access token for
	/// yet (unusual, but possible right after a license first appears)
	/// simply isn't checked this cycle - it stays pending and gets
	/// re-evaluated on the next scan.
	/// </summary>
	private async Task RefreshPlaytimeProtectionAsync() {
		if (_options.MinPlaytimeToExcludeMinutes <= 0) {
			return;
		}

		List<(uint SubID, LicenseRecord Record)> pending = _state.GetByStatus(LicenseStatus.Pending);

		if (pending.Count == 0) {
			return;
		}

		Dictionary<uint, int>? playtimeByAppId = await GetOwnedPlaytimeByAppIdAsync().ConfigureAwait(false);

		if (playtimeByAppId == null) {
			_bot.ArchiLogger.LogGenericWarning("Could not fetch owned games' playtime - playtime protection skipped for this cycle.");

			return;
		}

		Dictionary<uint, HashSet<uint>>? appIdsByPackage = await GetAppIdsByPackagesAsync(pending.Select(item => item.SubID).ToHashSet()).ConfigureAwait(false);

		if (appIdsByPackage == null) {
			_bot.ArchiLogger.LogGenericWarning("Could not resolve package contents - playtime protection skipped for this cycle.");

			return;
		}

		int protectedCount = 0;

		foreach ((uint subID, LicenseRecord record) in pending) {
			if (!appIdsByPackage.TryGetValue(subID, out HashSet<uint>? appIDs) || (appIDs.Count == 0)) {
				continue;
			}

			int maxPlaytime = 0;

			foreach (uint appID in appIDs) {
				if (playtimeByAppId.TryGetValue(appID, out int playtime) && (playtime > maxPlaytime)) {
					maxPlaytime = playtime;
				}
			}

			if (maxPlaytime < _options.MinPlaytimeToExcludeMinutes) {
				continue;
			}

			_state.MarkAttempt(
				subID,
				LicenseStatus.Excluded,
				$"played {maxPlaytime} min >= threshold {_options.MinPlaytimeToExcludeMinutes} min",
				true
			);

			_bot.ArchiLogger.LogGenericInfo($"SubID {subID} ({record.Name}) has {maxPlaytime} min playtime and will not be removed.");

			protectedCount++;
		}

		if (protectedCount > 0) {
			_bot.ArchiLogger.LogGenericInfo($"Playtime protection excluded {protectedCount} package(s) with real playtime from removal.");
		}
	}

	/// <summary>Owned appID -> playtime_forever (minutes), straight from Steam's Player_GetOwnedGames unified message (ASF's own GetOwnedGames() wrapper discards playtime, so we call the service ourselves).</summary>
	private async Task<Dictionary<uint, int>?> GetOwnedPlaytimeByAppIdAsync() {
		SteamUnifiedMessages? unifiedMessages = _bot.GetHandler<SteamUnifiedMessages>();

		if (unifiedMessages == null) {
			return null;
		}

		Player playerService = unifiedMessages.CreateService<Player>();

		CPlayer_GetOwnedGames_Request request = new() {
			steamid = _bot.SteamID,
			include_appinfo = false,
			include_free_sub = true,
			include_played_free_games = true,
			skip_unvetted_apps = false
		};

		SteamUnifiedMessages.ServiceMethodResponse<CPlayer_GetOwnedGames_Response> response;

		try {
			response = await playerService.GetOwnedGames(request).ToLongRunningTask().ConfigureAwait(false);
		} catch (Exception e) {
			_bot.ArchiLogger.LogGenericWarningException(e);

			return null;
		}

		if (response.Result != EResult.OK) {
			return null;
		}

		Dictionary<uint, int> result = new();

		foreach (CPlayer_GetOwnedGames_Response.Game game in response.Body.games) {
			result[(uint) game.appid] = game.playtime_forever;
		}

		return result;
	}

	/// <summary>
	/// Package -> app IDs it contains, via PICS - the same mechanism ASF
	/// itself uses internally (Bot.GetPackagesData()). Only queries
	/// packages for which ASF has already resolved an access token,
	/// mirroring ASF's own behaviour of skipping tokenless packages
	/// rather than guessing.
	/// </summary>
	private async Task<Dictionary<uint, HashSet<uint>>?> GetAppIdsByPackagesAsync(IReadOnlyCollection<uint> packageIDs) {
		if (ASF.GlobalDatabase == null) {
			return null;
		}

		HashSet<SteamApps.PICSRequest> packageRequests = new();

		foreach (uint packageID in packageIDs) {
			if (ASF.GlobalDatabase.PackageAccessTokensReadOnly.TryGetValue(packageID, out ulong token)) {
				packageRequests.Add(new SteamApps.PICSRequest(packageID, token));
			}
		}

		if (packageRequests.Count == 0) {
			return new Dictionary<uint, HashSet<uint>>();
		}

		AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet? resultSet;

		try {
			resultSet = await _bot.SteamApps.PICSGetProductInfo([], packageRequests).ToLongRunningTask().ConfigureAwait(false);
		} catch (Exception e) {
			_bot.ArchiLogger.LogGenericWarningException(e);

			return null;
		}

		if (resultSet?.Results == null) {
			return null;
		}

		Dictionary<uint, HashSet<uint>> result = new();

		foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo in resultSet.Results.SelectMany(static productInfoResult => productInfoResult.Packages).Where(static pair => pair.Key != 0).Select(static pair => pair.Value)) {
			if (productInfo.KeyValues == KeyValue.Invalid) {
				continue;
			}

			KeyValue appIDsKv = productInfo.KeyValues["appids"];

			if (appIDsKv == KeyValue.Invalid) {
				continue;
			}

			HashSet<uint> appIDs = new();

			foreach (string? appIDText in appIDsKv.Children.Select(static app => app.Value)) {
				if (uint.TryParse(appIDText, out uint appID) && (appID != 0)) {
					appIDs.Add(appID);
				}
			}

			result[productInfo.ID] = appIDs;
		}

		return result;
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

			// Steam puts the call in href="javascript:RemoveFreeLicense(...)"
			// on <a class="free_license_remove_link">, not in an onclick
			// attribute - querying for [onclick] elements (an earlier
			// version of this code) never matched anything. Scanning the
			// serialized markup instead, exactly like the original
			// script's regex-over-raw-HTML approach, is attribute-agnostic
			// and doesn't depend on guessing Steam's exact markup.
			using (StringWriter writer = new()) {
				document.ToHtml(writer, HtmlMarkupFormatter.Instance);

				string html = writer.ToString();

				foreach (Match match in RemoveFreeLicenseRegex().Matches(html)) {
					uint subID = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

					if (!seen.Add(subID)) {
						continue;
					}

					licenses.Add(new FreeLicenseCandidate(subID, DecodeLicenseName(match.Groups[2].Value)));
				}
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

		_state.MarkFullScan(ScanLogicVersion);

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
