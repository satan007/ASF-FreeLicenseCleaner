using System;
using System.Collections.Generic;

namespace FreeLicenseCleaner;

/// <summary>
/// Terminal/non-terminal state of a single SubID, mirrors the original
/// standalone script's SQLite "status" column.
/// </summary>
internal enum LicenseStatus {
	Pending,
	Processed,
	Duplicate,
	InvalidState,
	InvalidParam,
	Excluded,
	FailedMaxAttempts
}

/// <summary>One tracked SubID.</summary>
internal sealed class LicenseRecord {
	public string Name { get; set; } = "";
	public LicenseStatus Status { get; set; } = LicenseStatus.Pending;
	public int Attempts { get; set; }
	public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
	public DateTime? LastAttemptUtc { get; set; }
	public string? LastResult { get; set; }
}

/// <summary>Whole persisted state for one bot, serialized as-is to JSON.</summary>
internal sealed class CleanerState {
	public string? PluginVersion { get; set; }
	public DateTime? LastFullScanUtc { get; set; }
	public Dictionary<uint, LicenseRecord> Licenses { get; set; } = new();
}

/// <summary>One free license candidate found while scanning the Steam licenses page.</summary>
internal readonly record struct FreeLicenseCandidate(uint SubID, string Name);
