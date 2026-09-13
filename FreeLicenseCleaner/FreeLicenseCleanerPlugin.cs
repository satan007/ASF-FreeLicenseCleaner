using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Composition;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;

namespace FreeLicenseCleaner;

/// <summary>
/// Entry point. Per bot, this plugin is a native replacement for the
/// standalone Python script + ASFEnhance's COOKIES/RL commands: it uses
/// Bot.ArchiWebHandler (already authenticated) to scan the Steam licenses
/// page, and Bot.Actions.RemoveLicensePackage() to remove them, one at a
/// time on a timer, exactly like the original script did over IPC.
///
/// Every knob (see <see cref="ConfigKeys"/>) is opt-in via that bot's own
/// config file, e.g.:
///   "FreeLicenseCleanerEnabled": true
///   "FreeLicenseCleanerDryRun": true   (defaults to true - flip to false
///                                       only after checking the logs)
///   "FreeLicenseCleanerMaxAttempts": 5
///   "FreeLicenseCleanerSuccessDelaySeconds": 30
///   "FreeLicenseCleanerRateLimitDelaySeconds": 660
///   "FreeLicenseCleanerErrorDelaySeconds": 30
///   "FreeLicenseCleanerIdleDelaySeconds": 300
///   "FreeLicenseCleanerFullScanIntervalMinutes": 1440
///   "FreeLicenseCleanerStorePageDelayMilliseconds": 500
///   "FreeLicenseCleanerMaxStorePages": 1000
///   "FreeLicenseCleanerExcludeSubIds": [12345, 67890]
///   "FreeLicenseCleanerMinPlaytimeToExcludeMinutes": 0   (0 = off; 1 = protect anything ever launched)
/// Anything omitted keeps its default from <see cref="CleanerOptions"/>.
///
/// Bot commands (Master access, same as native rmlicense):
///   flc status       - show current queue counters and effective settings
///   flc scan         - trigger an immediate full Steam scan
///   flc list         - show actual SubIDs/names per bucket (pending/invalid/excluded/failed)
///   flc retry subid  - move one SubID back to pending regardless of its current status
///
/// Implements IGitHubPluginUpdates so ASF checks this plugin for updates
/// the same way it checks itself (on ASF's usual update schedule, plus
/// the "update"/"updateplugins" command) - the release.yml workflow
/// always attaches exactly one FreeLicenseCleaner.zip asset, which
/// matches ASF's default asset-selection fallback, so no custom
/// GetTargetReleaseAsset() override is needed.
/// </summary>
[Export(typeof(IPlugin))]
[SuppressMessage("ReSharper", "MemberCanBeFileLocal")]
internal sealed class FreeLicenseCleanerPlugin : IASF, IBot, IBotModules, IBotCommand2, IGitHubPluginUpdates {
	private static class ConfigKeys {
		public const string Enabled = "FreeLicenseCleanerEnabled";
		public const string DryRun = "FreeLicenseCleanerDryRun";
		public const string MaxAttempts = "FreeLicenseCleanerMaxAttempts";
		public const string SuccessDelaySeconds = "FreeLicenseCleanerSuccessDelaySeconds";
		public const string RateLimitDelaySeconds = "FreeLicenseCleanerRateLimitDelaySeconds";
		public const string ErrorDelaySeconds = "FreeLicenseCleanerErrorDelaySeconds";
		public const string IdleDelaySeconds = "FreeLicenseCleanerIdleDelaySeconds";
		public const string FullScanIntervalMinutes = "FreeLicenseCleanerFullScanIntervalMinutes";
		public const string StorePageDelayMilliseconds = "FreeLicenseCleanerStorePageDelayMilliseconds";
		public const string MaxStorePages = "FreeLicenseCleanerMaxStorePages";
		public const string ExcludeSubIds = "FreeLicenseCleanerExcludeSubIds";
		public const string MinPlaytimeToExcludeMinutes = "FreeLicenseCleanerMinPlaytimeToExcludeMinutes";
	}

	private static readonly ConcurrentDictionary<Bot, CleanerWorker> Workers = new();

	/// <summary>
	/// Deliberately NOT inside this plugin's own folder
	/// (plugins/FreeLicenseCleaner/) - ASF's plugin auto-update applies a
	/// new release by moving the ENTIRE current contents of that folder
	/// into a backup directory and then dropping the freshly extracted
	/// release files in its place (see ArchiSteamFarm.Core.Utilities.
	/// UpdateFromArchive/MoveAllUpdateFiles). Only a handful of
	/// specifically-named subdirectories are exempted from that move
	/// (SharedInfo.ConfigDirectory/ArchivalLogsDirectory/DebugDirectory/
	/// PluginsDirectory/UpdateDirectoryNew/UpdateDirectoryOld) - a
	/// same-named "data" subfolder is NOT one of them, so it used to get
	/// swept into the backup directory on every update and never restored
	/// (the release zip ships no "data" folder), silently discarding all
	/// accumulated history. Storing state under ASF's own top-level
	/// SharedInfo.ConfigDirectory ("config/") instead sidesteps that
	/// mechanism entirely: that folder belongs to ASF itself, is never
	/// touched by a plugin's own update, and survives ASF's own
	/// self-updates too. Directory.GetCurrentDirectory() is used rather
	/// than AppContext.BaseDirectory/Assembly.Location because ASF sets
	/// the process's current directory to its own home directory
	/// (Program.cs, Directory.SetCurrentDirectory(SharedInfo.HomeDirectory))
	/// during startup, before any plugin loads - unlike AppContext.
	/// BaseDirectory, that also resolves correctly for a single-file
	/// publish of ASF.
	/// </summary>
	private static readonly string DataDirectory = Path.Combine(
		Directory.GetCurrentDirectory(),
		SharedInfo.ConfigDirectory,
		SharedInfo.PluginsDirectory,
		nameof(FreeLicenseCleanerPlugin)
	);

	private static bool _unsafeInstallWarned;

	public string Name => nameof(FreeLicenseCleanerPlugin);

	public Version Version => typeof(FreeLicenseCleanerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// IGitHubPluginUpdates: ASF compares this against Directory.Build.props's
	// <Version> and offers/performs an update from this repo's GitHub
	// Releases, using the same default asset-matching IGitHubPluginUpdates
	// already provides.
	public string RepositoryName => "satan007/ASF-FreeLicenseCleaner";

	/// <summary>
	/// ASF applies a plugin update by treating the directory this plugin's
	/// OWN assembly lives in as belonging entirely to this plugin (see the
	/// comment on <see cref="DataDirectory"/>). If that directory is the
	/// SHARED "plugins" folder itself - which happens if this plugin was
	/// ever unzipped directly into it instead of its own subfolder - an
	/// update would sweep every OTHER installed plugin's files into a
	/// backup directory too, as collateral damage, without restoring them
	/// (observed in practice: other plugins ended up in plugins/_old/,
	/// their own plugins/&lt;Name&gt;/ left empty). Refusing to update in
	/// that state is far cheaper than risking it again.
	/// </summary>
	public bool CanUpdate {
		get {
			if (!IsInstalledDirectlyInSharedPluginsFolder()) {
				return true;
			}

			WarnAboutUnsafeInstallLocationOnce();

			return false;
		}
	}

	public Task OnLoaded() {
		if (IsInstalledDirectlyInSharedPluginsFolder()) {
			WarnAboutUnsafeInstallLocationOnce();
		}

		MigrateLegacyDataDirectory();

		ASF.ArchiLogger.LogGenericInfo(
			$"{nameof(FreeLicenseCleanerPlugin)} loaded. Enable it per-bot with \"{ConfigKeys.Enabled}\": true in that bot's config."
		);

		return Task.CompletedTask;
	}

	private static bool IsInstalledDirectlyInSharedPluginsFolder() {
		string? assemblyDirectory = Path.GetDirectoryName(typeof(FreeLicenseCleanerPlugin).Assembly.Location);
		string? directoryName = string.IsNullOrEmpty(assemblyDirectory) ? null : Path.GetFileName(assemblyDirectory);

		return string.Equals(directoryName, SharedInfo.PluginsDirectory, StringComparison.Ordinal);
	}

	private static void WarnAboutUnsafeInstallLocationOnce() {
		if (_unsafeInstallWarned) {
			return;
		}

		_unsafeInstallWarned = true;

		ASF.ArchiLogger.LogGenericError(
			$"{nameof(FreeLicenseCleanerPlugin)} is installed directly in the shared \"{SharedInfo.PluginsDirectory}\" folder, not in its own subfolder. " +
			$"ASF's plugin auto-update applies an update by treating the ENTIRE folder this plugin's assembly lives in as belonging to this plugin - " +
			$"in this state that is the whole shared plugins folder, so an update would move every OTHER installed plugin's files out of the way too. " +
			$"Auto-update for this plugin is disabled until this is fixed: move its files into their own subfolder " +
			$"(e.g. \"{SharedInfo.PluginsDirectory}/FreeLicenseCleaner/\") and restart ASF."
		);
	}

	/// <summary>
	/// One-time upgrade path from pre-0.0.9 versions, which stored state
	/// under a "data" subfolder inside this plugin's OWN folder - see the
	/// comment on <see cref="DataDirectory"/> for why that was unsafe.
	/// Checks two candidate locations, both relative to this assembly's
	/// own folder:
	///   1. "data" directly - covers a manual copy/build upgrade, where
	///      nothing ever moved the old folder around.
	///   2. "_old/data" - covers the realistic case, an ASF-driven plugin
	///      auto-update: by the time THIS (new) code is running, ASF's own
	///      update machinery has already moved the pre-update contents of
	///      the plugin folder (old "data" included) into "_old" and
	///      extracted the new release over what's left - see the comment
	///      on <see cref="DataDirectory"/>.
	/// Best-effort only: "_old" itself is purged by ASF on the NEXT update
	/// cycle, so this only helps up to and including the update that
	/// first installs this fix - not a version bump beyond that.
	/// </summary>
	private static void MigrateLegacyDataDirectory() {
		if (Directory.Exists(DataDirectory)) {
			// Either already migrated, or a fresh install with nothing to migrate.
			return;
		}

		string? pluginDirectory = Path.GetDirectoryName(typeof(FreeLicenseCleanerPlugin).Assembly.Location);

		if (string.IsNullOrEmpty(pluginDirectory)) {
			return;
		}

		string[] legacyCandidates = [
			Path.Combine(pluginDirectory, "data"),
			Path.Combine(pluginDirectory, "_old", "data")
		];

		try {
			int migrated = 0;

			foreach (string legacyDataDirectory in legacyCandidates) {
				if (!Directory.Exists(legacyDataDirectory)) {
					continue;
				}

				foreach (string file in Directory.EnumerateFiles(legacyDataDirectory, "*.json")) {
					string destination = Path.Combine(DataDirectory, Path.GetFileName(file));

					if (!File.Exists(destination)) {
						Directory.CreateDirectory(DataDirectory);
						File.Copy(file, destination);

						migrated++;
					}
				}
			}

			if (migrated > 0) {
				ASF.ArchiLogger.LogGenericInfo($"{nameof(FreeLicenseCleanerPlugin)} migrated {migrated} saved state file(s) from the old, unsafe in-plugin-folder location to {DataDirectory}.");
			}
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);
		}
	}

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) => Task.CompletedTask;

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		bool enabled = false;
		CleanerOptions options = new();

		if (additionalConfigProperties != null) {
			ApplyBool(additionalConfigProperties, ConfigKeys.Enabled, value => enabled = value);
			ApplyBool(additionalConfigProperties, ConfigKeys.DryRun, value => options.DryRun = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.MaxAttempts, bot, value => options.MaxAttempts = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.SuccessDelaySeconds, bot, value => options.SuccessDelaySeconds = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.RateLimitDelaySeconds, bot, value => options.RateLimitDelaySeconds = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.ErrorDelaySeconds, bot, value => options.ErrorDelaySeconds = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.IdleDelaySeconds, bot, value => options.IdleDelaySeconds = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.FullScanIntervalMinutes, bot, value => options.FullScanIntervalMinutes = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.StorePageDelayMilliseconds, bot, value => options.StorePageDelayMilliseconds = value);
			ApplyInt(additionalConfigProperties, ConfigKeys.MaxStorePages, bot, value => options.MaxStorePages = value);
			ApplyUintSet(additionalConfigProperties, ConfigKeys.ExcludeSubIds, bot, value => options.ExcludeSubIds = value);
			ApplyNonNegativeInt(additionalConfigProperties, ConfigKeys.MinPlaytimeToExcludeMinutes, bot, value => options.MinPlaytimeToExcludeMinutes = value);
		}

		// Stop a worker left over from a previous init (e.g. config reload)
		// before possibly starting a new one.
		if (Workers.TryRemove(bot, out CleanerWorker? previous)) {
			_ = StopAndDisposeAsync(previous);
		}

		if (!enabled) {
			return Task.CompletedTask;
		}

		CleanerWorker worker = new(bot, options, DataDirectory);

		if (Workers.TryAdd(bot, worker)) {
			worker.Start();

			bot.ArchiLogger.LogGenericInfo($"Free License Cleaner started for this bot (DryRun={options.DryRun}).");
		}

		return Task.CompletedTask;
	}

	public async Task OnBotDestroy(Bot bot) {
		if (Workers.TryRemove(bot, out CleanerWorker? worker)) {
			await StopAndDisposeAsync(worker).ConfigureAwait(false);
		}
	}

	private static async Task StopAndDisposeAsync(CleanerWorker worker) {
		await worker.StopAsync().ConfigureAwait(false);

		worker.Dispose();
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		if ((args.Length == 0) || !args[0].Equals("FLC", StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		// Same access level ASF itself requires for the native rmlicense command.
		if (access < EAccess.Master) {
			return null;
		}

		if (!Workers.TryGetValue(bot, out CleanerWorker? worker)) {
			return $"Free License Cleaner is not enabled for {bot.BotName}. Add \"{ConfigKeys.Enabled}\": true to its config.";
		}

		string action = args.Length > 1 ? args[1].ToUpperInvariant() : "STATUS";

		return action switch {
			"STATUS" => worker.GetStatusText(),
			"SCAN" => await worker.TriggerFullScanAsync().ConfigureAwait(false),
			"LIST" => worker.GetListText(),
			"RETRY" => HandleRetry(worker, args),
			_ => "Usage: flc <status|scan|list|retry <subid>>"
		};
	}

	private static string HandleRetry(CleanerWorker worker, string[] args) {
		if ((args.Length < 3) || !uint.TryParse(args[2], out uint subID)) {
			return "Usage: flc retry <subid>";
		}

		return worker.RetrySubId(subID);
	}

	private static void ApplyBool(IReadOnlyDictionary<string, JsonElement> properties, string key, Action<bool> setter) {
		if (properties.TryGetValue(key, out JsonElement element) && (element.ValueKind is JsonValueKind.True or JsonValueKind.False)) {
			setter(element.GetBoolean());
		}
	}

	private static void ApplyInt(IReadOnlyDictionary<string, JsonElement> properties, string key, Bot bot, Action<int> setter) {
		if (!properties.TryGetValue(key, out JsonElement element)) {
			return;
		}

		if ((element.ValueKind != JsonValueKind.Number) || !element.TryGetInt32(out int value) || (value <= 0)) {
			bot.ArchiLogger.LogGenericWarning($"Ignoring invalid value for \"{key}\" - expected a positive integer, keeping the default.");

			return;
		}

		setter(value);
	}

	/// <summary>Like <see cref="ApplyInt"/>, but allows 0 (used for knobs where 0 means "feature off").</summary>
	private static void ApplyNonNegativeInt(IReadOnlyDictionary<string, JsonElement> properties, string key, Bot bot, Action<int> setter) {
		if (!properties.TryGetValue(key, out JsonElement element)) {
			return;
		}

		if ((element.ValueKind != JsonValueKind.Number) || !element.TryGetInt32(out int value) || (value < 0)) {
			bot.ArchiLogger.LogGenericWarning($"Ignoring invalid value for \"{key}\" - expected a non-negative integer, keeping the default.");

			return;
		}

		setter(value);
	}

	private static void ApplyUintSet(IReadOnlyDictionary<string, JsonElement> properties, string key, Bot bot, Action<HashSet<uint>> setter) {
		if (!properties.TryGetValue(key, out JsonElement element)) {
			return;
		}

		if (element.ValueKind != JsonValueKind.Array) {
			bot.ArchiLogger.LogGenericWarning($"Ignoring invalid value for \"{key}\" - expected an array of numbers, keeping the default.");

			return;
		}

		HashSet<uint> values = new();

		foreach (JsonElement item in element.EnumerateArray()) {
			if ((item.ValueKind != JsonValueKind.Number) || !item.TryGetUInt32(out uint value)) {
				bot.ArchiLogger.LogGenericWarning($"Ignoring invalid entry in \"{key}\" - expected a non-negative integer.");

				continue;
			}

			values.Add(value);
		}

		setter(values);
	}
}
