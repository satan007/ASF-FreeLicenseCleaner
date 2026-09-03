namespace FreeLicenseCleaner;

/// <summary>
/// Per-bot tunables, all overridable from that bot's config (see
/// FreeLicenseCleanerPlugin.ConfigKeys). Defaults match the original
/// standalone Python script's constants.
/// </summary>
internal sealed class CleanerOptions {
	public bool DryRun { get; set; } = true;

	/// <summary>Retries allowed for a SubID before it's permanently marked as failed.</summary>
	public int MaxAttempts { get; set; } = 5;

	/// <summary>Pause after a successful/duplicate/invalid-state RL result.</summary>
	public int SuccessDelaySeconds { get; set; } = 30;

	/// <summary>Pause after Steam returns RateLimitExceeded.</summary>
	public int RateLimitDelaySeconds { get; set; } = 660;

	/// <summary>Pause after an IPC/network error or an unrecognised RL result.</summary>
	public int ErrorDelaySeconds { get; set; } = 30;

	/// <summary>Pause when the pending queue is empty and no scan is due.</summary>
	public int IdleDelaySeconds { get; set; } = 300;

	/// <summary>How often to re-scan the whole Steam licenses page from scratch.</summary>
	public int FullScanIntervalMinutes { get; set; } = 1440;

	/// <summary>Pause between successive Steam licenses page fetches during a full scan.</summary>
	public int StorePageDelayMilliseconds { get; set; } = 500;

	/// <summary>Hard cap on pages walked during a single full scan (safety net against an infinite paginator).</summary>
	public int MaxStorePages { get; set; } = 1000;
}
