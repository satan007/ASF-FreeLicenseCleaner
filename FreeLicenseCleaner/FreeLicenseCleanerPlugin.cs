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
/// Opt-in per bot via that bot's own config file:
///   "FreeLicenseCleanerEnabled": true
///   "FreeLicenseCleanerDryRun": true   (defaults to true - flip to false
///                                       only after checking the logs)
///
/// Bot commands (Master access, same as native rmlicense):
///   flc status   - show current queue counters
///   flc scan     - trigger an immediate full Steam scan
/// </summary>
[Export(typeof(IPlugin))]
[SuppressMessage("ReSharper", "MemberCanBeFileLocal")]
internal sealed class FreeLicenseCleanerPlugin : IASF, IBot, IBotModules, IBotCommand2 {
	private const string ConfigEnabledKey = "FreeLicenseCleanerEnabled";
	private const string ConfigDryRunKey = "FreeLicenseCleanerDryRun";

	private static readonly ConcurrentDictionary<Bot, CleanerWorker> Workers = new();

	private static readonly string DataDirectory = Path.Combine(
		Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
		"data"
	);

	public string Name => nameof(FreeLicenseCleanerPlugin);

	public Version Version => typeof(FreeLicenseCleanerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo(
			$"{nameof(FreeLicenseCleanerPlugin)} loaded. Enable it per-bot with \"{ConfigEnabledKey}\": true in that bot's config."
		);

		return Task.CompletedTask;
	}

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) => Task.CompletedTask;

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		bool enabled = false;
		bool dryRun = true;

		if (additionalConfigProperties != null) {
			foreach ((string key, JsonElement value) in additionalConfigProperties) {
				if ((value.ValueKind != JsonValueKind.True) && (value.ValueKind != JsonValueKind.False)) {
					continue;
				}

				switch (key) {
					case ConfigEnabledKey:
						enabled = value.GetBoolean();

						break;
					case ConfigDryRunKey:
						dryRun = value.GetBoolean();

						break;
				}
			}
		}

		// Stop a worker left over from a previous init (e.g. config reload)
		// before possibly starting a new one.
		if (Workers.TryRemove(bot, out CleanerWorker? previous)) {
			_ = previous.StopAsync();
		}

		if (!enabled) {
			return Task.CompletedTask;
		}

		CleanerWorker worker = new(bot, dryRun, DataDirectory);

		if (Workers.TryAdd(bot, worker)) {
			worker.Start();

			bot.ArchiLogger.LogGenericInfo($"Free License Cleaner started for this bot (DryRun={dryRun}).");
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
			return $"Free License Cleaner is not enabled for {bot.BotName}. Add \"{ConfigEnabledKey}\": true to its config.";
		}

		string action = args.Length > 1 ? args[1].ToUpperInvariant() : "STATUS";

		return action switch {
			"STATUS" => worker.GetStatusText(),
			"SCAN" => await worker.TriggerFullScanAsync().ConfigureAwait(false),
			_ => "Usage: flc <status|scan>"
		};
	}
}
