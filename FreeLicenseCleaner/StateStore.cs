using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FreeLicenseCleaner;

/// <summary>
/// Persistent per-bot state, backed by a single JSON file instead of
/// SQLite - this is a plugin loaded into ASF's own process, so we avoid
/// pulling in a native SQLite dependency for a dataset that is at most a
/// few tens of thousands of small records.
///
/// Every mutation is followed by an atomic save (write to .tmp, then
/// replace), mirroring the "commit after every write" behaviour of the
/// original script's SQLite connection.
/// </summary>
internal sealed class StateStore {
	private static readonly JsonSerializerOptions SerializerOptions = new() {
		WriteIndented = true
	};

	private readonly string _filePath;
	private readonly object _lock = new();
	private readonly CleanerState _state;

	internal StateStore(string filePath) {
		_filePath = filePath;
		_state = Load(filePath);
	}

	private static CleanerState Load(string filePath) {
		if (!File.Exists(filePath)) {
			return new CleanerState();
		}

		try {
			string json = File.ReadAllText(filePath);

			return JsonSerializer.Deserialize<CleanerState>(json) ?? new CleanerState();
		} catch (Exception) {
			// Corrupted/unreadable state file - start fresh rather than
			// crash the bot that loaded us.
			return new CleanerState();
		}
	}

	private void SaveUnlocked() {
		string json = JsonSerializer.Serialize(_state, SerializerOptions);

		string? directory = Path.GetDirectoryName(_filePath);

		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		string tempPath = _filePath + ".tmp";

		File.WriteAllText(tempPath, json);
		File.Move(tempPath, _filePath, true);
	}

	internal (bool Required, string Reason) FullScanRequired(string scanLogicVersion, int intervalMinutes) {
		lock (_lock) {
			if (_state.ScanLogicVersion != scanLogicVersion) {
				string reason = _state.ScanLogicVersion == null
					? "no previous scan"
					: $"scan logic changed {_state.ScanLogicVersion} -> {scanLogicVersion}";

				return (true, reason);
			}

			if (_state.LastFullScanUtc == null) {
				return (true, "no previous scan timestamp");
			}

			TimeSpan age = DateTime.UtcNow - _state.LastFullScanUtc.Value;

			return age >= TimeSpan.FromMinutes(intervalMinutes) ? (true, "scan interval expired") : (false, "scan still fresh");
		}
	}

	internal void MarkFullScan(string scanLogicVersion) {
		lock (_lock) {
			_state.ScanLogicVersion = scanLogicVersion;
			_state.LastFullScanUtc = DateTime.UtcNow;

			SaveUnlocked();
		}
	}

	/// <summary>Inserts new SubIDs as 'pending', refreshes the name of ones we already know.</summary>
	internal int AddMany(IReadOnlyCollection<FreeLicenseCandidate> licenses) {
		lock (_lock) {
			int newCount = 0;

			foreach (FreeLicenseCandidate license in licenses) {
				if (_state.Licenses.TryGetValue(license.SubID, out LicenseRecord? existing)) {
					existing.Name = license.Name;

					continue;
				}

				_state.Licenses[license.SubID] = new LicenseRecord {
					Name = license.Name,
					FirstSeenUtc = DateTime.UtcNow
				};

				newCount++;
			}

			SaveUnlocked();

			return newCount;
		}
	}

	/// <summary>Smallest 'pending' SubID.</summary>
	internal (uint SubID, LicenseRecord Record)? GetNextPending() {
		lock (_lock) {
			KeyValuePair<uint, LicenseRecord> pending = _state.Licenses
				.Where(pair => pair.Value.Status == LicenseStatus.Pending)
				.OrderBy(pair => pair.Key)
				.FirstOrDefault();

			return pending.Value == null ? null : (pending.Key, pending.Value);
		}
	}

	/// <summary>
	/// Records the outcome of one RL attempt. When <paramref name="terminal"/>
	/// is false the SubID stays 'pending' so it will be retried later
	/// (bounded by CleanerWorker.MaxAttempts).
	/// </summary>
	internal void MarkAttempt(uint subID, LicenseStatus status, string? resultText, bool terminal) {
		lock (_lock) {
			if (!_state.Licenses.TryGetValue(subID, out LicenseRecord? record)) {
				return;
			}

			record.Attempts++;
			record.LastAttemptUtc = DateTime.UtcNow;
			record.LastResult = resultText;
			record.Status = terminal ? status : LicenseStatus.Pending;

			SaveUnlocked();
		}
	}

	internal IReadOnlyDictionary<LicenseStatus, int> Statistics() {
		lock (_lock) {
			return _state.Licenses.Values
				.GroupBy(record => record.Status)
				.ToDictionary(group => group.Key, group => group.Count());
		}
	}

	/// <summary>SubIDs with the given status, smallest first, for "flc list".</summary>
	internal List<(uint SubID, LicenseRecord Record)> GetByStatus(LicenseStatus status, int? limit = null) {
		lock (_lock) {
			IEnumerable<KeyValuePair<uint, LicenseRecord>> query = _state.Licenses
				.Where(pair => pair.Value.Status == status)
				.OrderBy(pair => pair.Key);

			if (limit.HasValue) {
				query = query.Take(limit.Value);
			}

			return query.Select(pair => (pair.Key, pair.Value)).ToList();
		}
	}

	/// <summary>
	/// Moves a tracked SubID back to 'pending' and clears its attempt
	/// count, regardless of its current status - for "flc retry".
	/// </summary>
	internal bool ResetToPending(uint subID) {
		lock (_lock) {
			if (!_state.Licenses.TryGetValue(subID, out LicenseRecord? record)) {
				return false;
			}

			record.Status = LicenseStatus.Pending;
			record.Attempts = 0;

			SaveUnlocked();

			return true;
		}
	}
}
