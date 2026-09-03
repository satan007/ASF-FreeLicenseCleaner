using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Composition;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
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
/// Anything omitted keeps its default from <see cref="CleanerOptions"/>.
///
/// Bot commands (Master access, same as native rmlicense):
///   flc status   - show current queue counters and effective settings
///   flc scan     - trigger an immediate full Steam scan
/// </summary>
[Export(typeof(IPlugin))]
[SuppressMessage("ReSharper", "MemberCanBeFileLocal")]
internal sealed class FreeLicenseCleanerPlugin : IASF, IBot, IBotModules, IBotCommand2 {
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
	}

	private static readonly ConcurrentDictionary<Bot, CleanerWorker> Workers = new();

	private static readonly string DataDirectory = Path.Combine(
		Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
		"data"
	);

	public string Name => nameof(FreeLicenseCleanerPlugin);

	public Version Version => typeof(FreeLicenseCleanerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo(
			$"{nameof(FreeLicenseCleanerPlugin)} loaded. Enable it per-bot with \"{ConfigKeys.Enabled}\": true in that bot's config."
		);

		return Task.CompletedTask;
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
		}

		// Stop a worker left over from a previous init (e.g. config reload)
		// before possibly starting a new one.
		if (Workers.TryRemove(bot, out CleanerWorker? previous)) {
			_ = previous.StopAsync();
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
			await worker.StopAsync().ConfigureAwait(false);
		}
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
			_ => "Usage: flc <status|scan>"
		};
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
}
