using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Application;

/// <summary>Raised when a destination is already locked by another writer (OPS02).</summary>
public sealed class DestinationLockedException : Exception
{
    public string Code{ get; } = DiagnosticCodes.PubDestinationLocked;
    public string DestinationPath { get; }

    public DestinationLockedException(string destinationPath, string message)
        : base(message)
        => DestinationPath = destinationPath;
}

/// <summary>
/// Per-path advisory lock with a revision token (OPS02). The lock is a sidecar
/// <c>&lt;destination&gt;.xbsl-lock</c> file opened with FileShare.None: a second
/// in-process thread or a second OS process both fail to share it, so concurrent
/// writers serialize instead of interleaving. The token binds the acquisition to the
/// destination's digest at that moment, letting publication detect a destination that
/// changed underneath a lock holder.
/// </summary>
public sealed class DestinationLock : IDisposable
{
    private static readonly HashSet<string> HeldThisProcess = new(StringComparer.Ordinal);
    private static readonly object HeldGate = new();

    private FileStream? _handle;

    public string DestinationPath { get; }
    public string LockFilePath { get; }
    /// <summary>Unique acquisition token, echoed into the publication receipt.</summary>
    public string Token { get; }
    /// <summary>Digest of the pre-existing destination (null when none existed).</summary>
    public string? DestinationDigestAtAcquire { get; }

    private DestinationLock(FileStream handle, string destinationPath, string lockFilePath,
        string token, string? digestAtAcquire)
    {
        _handle = handle;
        DestinationPath = destinationPath;
        LockFilePath = lockFilePath;
        Token = token;
        DestinationDigestAtAcquire = digestAtAcquire;
    }

    public bool IsHeld => _handle is not null;

    /// <summary>
    /// Try to acquire without blocking. Returns null when the destination is already
    /// locked (by this process or another).
    /// </summary>
    public static DestinationLock? TryAcquire(string destinationPath)
    {
        var full = Path.GetFullPath(destinationPath);
        lock (HeldGate)
        {
            if (!HeldThisProcess.Add(full)) return null; // same process, second holder
            FileStream? handle = null;
            try
            {
                var lockPath = full + ".xbsl-lock";
                try
                {
                    handle = new FileStream(lockPath, FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None, 1,
                        FileOptions.DeleteOnClose);
                }
                catch (IOException)
                {
                    lock (HeldGate) HeldThisProcess.Remove(full); // another process holds it
                    return null;
                }

                var digest = File.Exists(full) ? DigestFile(full) : null;
                return new DestinationLock(handle, full, lockPath,
                    "rev-" + Guid.NewGuid().ToString("N"), digest);
            }
            catch
            {
                // Digesting can fail after the sidecar was opened. Release the OS
                // handle before removing the process marker, not just the marker.
                try { handle?.Dispose(); }
                finally { HeldThisProcess.Remove(full); }
                throw;
            }
        }
    }

    /// <summary>Acquire or throw <see cref="DestinationLockedException"/>.</summary>
    public static DestinationLock Acquire(string destinationPath)
        => TryAcquire(destinationPath)
           ?? throw new DestinationLockedException(destinationPath,
               $"Destination '{destinationPath}' is already locked by another writer.");

    /// <summary>Acquire with bounded retry; serializes concurrent publishers.</summary>
    public static async Task<DestinationLock> AcquireAsync(
        string destinationPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Monotonic deadline: Environment.TickCount64 is immune to wall-clock adjustments
        // (NTP slew, DST, manual set) that could otherwise make a DateTime.UtcNow deadline
        // overshoot or abandon early. ConfigureAwait(false) keeps the returned task safe for
        // the synchronous PublicationService.Publish caller, which blocks on its completion.
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = TryAcquire(destinationPath);
            if (acquired is not null) return acquired;
            if (Environment.TickCount64 >= deadline)
                throw new DestinationLockedException(destinationPath,
                    $"Destination '{destinationPath}' remained locked past the acquisition deadline.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_handle is null) return;
        _handle.Dispose();
        _handle = null;
        lock (HeldGate) HeldThisProcess.Remove(DestinationPath);
    }

    public static string DigestFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return "sha256:" + Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static string DigestBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// SQLite-backed per-run journal (OPS02). A supplied <c>rootOverride</c> selects the
/// legacy JSON journal for compatibility and isolated tests. Every publication phase is
/// recorded before it happens; a host restart lost mid-replace leaves an unfinalized
/// entry that <see cref="RecoverUnfinalized"/> adjudicates before retry.
/// </summary>
public sealed class RunJournal
{
    private readonly string? _legacyRoot;
    private readonly LocalApplicationDatabase? _database;
    public string Root => _database?.Path ?? _legacyRoot!;

    public RunJournal(string? rootOverride = null, string? databasePath = null)
    {
        if (rootOverride is not null)
        {
            _legacyRoot = rootOverride;
            return;
        }
        _database = new LocalApplicationDatabase(databasePath);
        if (databasePath is null) ImportLegacyJournal();
    }

    private string EntryPath(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.IndexOfAny(['/', '\\']) >= 0
            || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A legacy journal run ID must be a single filename component.", nameof(runId));
        return Path.Combine(_legacyRoot!, runId + ".json");
    }

    public JournalEntry? Load(string runId) => Load(runId, out _);

    private JournalEntry? Load(string runId, out string? persistedUpdatedUtc)
    {
        persistedUpdatedUtc = null;
        if (_database is null)
            return File.Exists(EntryPath(runId)) ? JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(EntryPath(runId))) : null;
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,destination_path,state,staged_digest,final_digest,lock_token,updated_utc,validated_digest FROM run_journal WHERE run_id=$id";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        persistedUpdatedUtc = reader.GetString(6);
        return ReadEntry(reader);
    }

    public void Save(JournalEntry entry)
    {
        if (_database is null) Directory.CreateDirectory(_legacyRoot!);
        // Save never acquires a destination lock. Publishers release this short-lived
        // entry guard before waiting for that lock, so recovery's destination -> entry
        // ordering cannot deadlock with a publisher's pre-lock journal writes.
        using var entryLock = DestinationLock.AcquireAsync(WriteGuardPath(entry.RunId),
            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        SaveCore(entry);
    }

    private void SaveCore(JournalEntry entry)
    {
        if (_database is not null)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO run_journal(run_id,destination_path,state,staged_digest,final_digest,lock_token,updated_utc,validated_digest)
                VALUES($id,$destination,$state,$staged,$final,$lock,$updated,$validated)
                ON CONFLICT(run_id) DO UPDATE SET destination_path=excluded.destination_path,state=excluded.state,
                    staged_digest=excluded.staged_digest,final_digest=excluded.final_digest,
                    lock_token=excluded.lock_token,updated_utc=excluded.updated_utc,
                    validated_digest=excluded.validated_digest;
                """;
            Bind(command, entry);
            command.ExecuteNonQuery();
            SaveReceipt(connection, transaction, entry);
            transaction.Commit();
            return;
        }
        Directory.CreateDirectory(_legacyRoot!);
        // Write-then-rename keeps the journal itself crash-safe.
        var tmp = EntryPath(entry.RunId) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entry,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, EntryPath(entry.RunId), overwrite: true);
    }

    private static void SaveReceipt(SqliteConnection connection, SqliteTransaction transaction, JournalEntry entry)
    {
        using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        if (entry.State == JournalState.Finalized && entry.FinalDigest is not null)
        {
            receipt.CommandText = "INSERT INTO publication_receipts(run_id,final_digest,completed_utc,validated_digest) VALUES($id,$digest,$updated,$validated) ON CONFLICT(run_id) DO UPDATE SET final_digest=excluded.final_digest,completed_utc=excluded.completed_utc,validated_digest=excluded.validated_digest";
            receipt.Parameters.AddWithValue("$id", entry.RunId);
            receipt.Parameters.AddWithValue("$digest", entry.FinalDigest);
            receipt.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O"));
            receipt.Parameters.AddWithValue("$validated", (object?)entry.ValidatedDigest ?? DBNull.Value);
        }
        else
        {
            receipt.CommandText = "DELETE FROM publication_receipts WHERE run_id=$id";
            receipt.Parameters.AddWithValue("$id", entry.RunId);
        }
        receipt.ExecuteNonQuery();
    }

    private string WriteGuardPath(string runId)
    {
        // Persisted IDs are data, never paths. The sidecar is beside the store and
        // uses only a hash; Windows JSON filename aliases must share a guard too.
        if (_database is null) _ = EntryPath(runId); // Validate before touching a sidecar.
        var identity = _database is null && OperatingSystem.IsWindows() ? runId.ToUpperInvariant() : runId;
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return _database is null
            ? Path.Combine(Path.GetFullPath(_legacyRoot!), ".xbsl-journal-write-" + idHash)
            : _database.Path + ".xbsl-journal-write-" + idHash;
    }

    public IReadOnlyList<JournalEntry> AllEntries()
    {
        if (_database is not null)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_id,destination_path,state,staged_digest,final_digest,lock_token,updated_utc,validated_digest FROM run_journal ORDER BY run_id";
            using var reader = command.ExecuteReader();
            var entries = new List<JournalEntry>();
            while (reader.Read()) entries.Add(ReadEntry(reader));
            return entries;
        }
        if (!Directory.Exists(_legacyRoot)) return Array.Empty<JournalEntry>();
        return Directory.EnumerateFiles(_legacyRoot!, "*.json")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(p)))
            .OfType<JournalEntry>()
            .ToList();
    }

    private void ImportLegacyJournal()
    {
        using (var connection = _database!.OpenConnection())
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT value FROM app_settings WHERE key='legacy_json_journal_imported'";
            if (check.ExecuteScalar() is string) return;
        }
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xbrlmappingstudio", "journal");
        if (!Directory.Exists(legacy)) return;
        var complete = true;
        foreach (var file in Directory.EnumerateFiles(legacy, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(file));
                if (entry is not null && Load(entry.RunId) is null) Save(entry);
            }
            catch (Exception ex) when (ex is IOException or JsonException or SqliteException or DestinationLockedException) { complete = false; }
        }
        if (complete)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO app_settings(key,value,updated_utc) VALUES('legacy_json_journal_imported','1',$time) ON CONFLICT(key) DO NOTHING";
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static JournalEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), (JournalState)reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture))
        { ValidatedDigest = reader.IsDBNull(7) ? null : reader.GetString(7) };

    private static void Bind(SqliteCommand command, JournalEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.RunId);
        command.Parameters.AddWithValue("$destination", entry.DestinationPath);
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$staged", (object?)entry.StagedDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$final", (object?)entry.FinalDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$lock", (object?)entry.LockToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$validated", (object?)entry.ValidatedDigest ?? DBNull.Value);
    }

    /// <summary>
    /// Adjudicate incomplete publications under a nonblocking destination lock. Only
    /// durable locked-validation or replacement evidence can confirm landed bytes;
    /// recovery never authorizes or retries publication. Uncertain entries are left
    /// untouched and do not prevent independent entries from being checked.
    /// </summary>
    public IReadOnlyList<RecoveryAction> RecoverUnfinalized()
        => RecoverUnfinalized(beforeAcquireForTest: null);

    // Runs after the initial read and before acquisition so tests can deterministically
    // exercise a publisher changing the row while recovery's snapshot is stale.
    internal IReadOnlyList<RecoveryAction> RecoverUnfinalized(Action<JournalEntry>? beforeAcquireForTest,
        Action<JournalEntry>? beforeFinalizeForTest = null, Action<JournalEntry>? afterReceiptForTest = null)
    {
        var actions = new List<RecoveryAction>();
        foreach (var runId in RecoveryRunIds())
        {
            try
            {
                var snapshot = Load(runId);
                if (snapshot is null || snapshot.RunId != runId)
                {
                    actions.Add(Retry(runId, "Journal entry is missing or its identity is inconsistent."));
                    continue;
                }
                if (snapshot.State is JournalState.Finalized or JournalState.Aborted) continue;
                if (string.IsNullOrWhiteSpace(snapshot.DestinationPath))
                {
                    actions.Add(Retry(runId, "Journal entry has no destination."));
                    continue;
                }

                beforeAcquireForTest?.Invoke(snapshot);
                using var destinationLock = DestinationLock.TryAcquire(snapshot.DestinationPath);
                if (destinationLock is null)
                {
                    actions.Add(Retry(runId, "Destination is busy; publication was not confirmed."));
                    continue;
                }

                // The snapshot predates acquisition. A publisher may have finalized,
                // aborted, removed, or repointed this run before we obtained its lock.
                var entry = Load(runId, out var persistedUpdatedUtc);
                if (entry is null || entry.RunId != runId)
                {
                    actions.Add(Retry(runId, "Journal entry changed or disappeared before recovery acquired the lock."));
                    continue;
                }
                if (entry.State is JournalState.Finalized or JournalState.Aborted) continue;
                if (string.IsNullOrWhiteSpace(entry.DestinationPath)
                    || !string.Equals(Path.GetFullPath(entry.DestinationPath), destinationLock.DestinationPath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    actions.Add(Retry(runId, "Journal destination changed before recovery acquired the lock."));
                    continue;
                }

                if (!HasPublicationEvidence(entry))
                {
                    actions.Add(Retry(runId, "No consistent locked-validation or replacement evidence was recorded."));
                    continue;
                }
                if (!File.Exists(entry.DestinationPath))
                {
                    actions.Add(Retry(runId, "Destination missing; publication was not confirmed."));
                    continue;
                }
                var finalDigest = DestinationLock.DigestFile(entry.DestinationPath);
                if (!StringComparer.Ordinal.Equals(finalDigest, entry.StagedDigest))
                {
                    actions.Add(Retry(runId, "Destination digest does not match the staged payload."));
                    continue;
                }

                // Retain the historical token: the recovery lock is synchronization,
                // not evidence that the original publication was authorized. Persist
                // the durable receipt before removing the prior accepted copy.
                beforeFinalizeForTest?.Invoke(entry);
                if (!TryFinalizeRecovery(entry, persistedUpdatedUtc, finalDigest, afterReceiptForTest))
                {
                    actions.Add(Retry(runId, "Journal entry is busy or changed after recovery checked it."));
                    continue;
                }
                actions.Add(new RecoveryAction(runId, RecoveryDecision.Finalized,
                    "Destination matches recorded publication evidence; entry finalized."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or SqliteException or JsonException or ArgumentException or FormatException
                or InvalidOperationException or InvalidCastException or OverflowException
                or NotSupportedException or System.Security.SecurityException)
            {
                actions.Add(Retry(runId, "Publication could not be safely confirmed: " + ex.Message));
            }
        }
        return actions;
    }

    private bool TryFinalizeRecovery(JournalEntry expected, string? persistedUpdatedUtc, string finalDigest,
        Action<JournalEntry>? afterReceiptForTest)
    {
        // The destination lock does not protect pre-lock Save calls for the same run.
        // Serialize the conditional receipt write AND cleanup against those calls.
        // Recovery never waits for an entry writer; independent entries can progress.
        using var entryLock = DestinationLock.TryAcquire(WriteGuardPath(expected.RunId));
        if (entryLock is null) return false;
        var finalized = expected with { State = JournalState.Finalized, FinalDigest = finalDigest,
            UpdatedUtc = DateTimeOffset.UtcNow };
        if (_database is null)
        {
            var current = Load(expected.RunId);
            if (current is null || current != expected || !current.UpdatedUtc.EqualsExact(expected.UpdatedUtc)) return false;
            SaveCore(finalized);
        }
        else
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE run_journal SET state=$newState,final_digest=$newFinal,updated_utc=$newUpdated
                WHERE run_id=$id AND destination_path=$destination AND state=$state
                    AND staged_digest IS $staged AND final_digest IS $final AND lock_token IS $lock
                    AND updated_utc=$updated AND validated_digest IS $validated;
                """;
            Bind(command, expected);
            // Compare the actual stored timestamp spelling, including legacy formats,
            // rather than reformatting the DateTimeOffset read from the database.
            command.Parameters["$updated"].Value = persistedUpdatedUtc!;
            command.Parameters.AddWithValue("$newState", (int)JournalState.Finalized);
            command.Parameters.AddWithValue("$newFinal", finalDigest);
            command.Parameters.AddWithValue("$newUpdated", finalized.UpdatedUtc.ToString("O"));
            if (command.ExecuteNonQuery() != 1) return false;
            SaveReceipt(connection, transaction, finalized);
            transaction.Commit();
        }
        afterReceiptForTest?.Invoke(finalized);
        DeletePriorPublicationBackup(expected);
        return true;
    }

    // Enumerate identities rather than deserializing the whole batch: a malformed or
    // unreadable row must not hide healthy, independent publications later in the scan.
    private IReadOnlyList<string> RecoveryRunIds()
    {
        if (_database is null)
            return !Directory.Exists(_legacyRoot) ? Array.Empty<string>()
                : Directory.EnumerateFiles(_legacyRoot!, "*.json")
                    .Select(path => Path.GetFileNameWithoutExtension(path))
                    .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM run_journal ORDER BY run_id";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static bool HasPublicationEvidence(JournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.StagedDigest)
            || (entry.FinalDigest is not null && entry.FinalDigest != entry.StagedDigest)) return false;
        return entry.State switch
        {
            JournalState.Validated => !string.IsNullOrWhiteSpace(entry.LockToken)
                && entry.ValidatedDigest == entry.StagedDigest,
            // Legacy replaced rows predate ValidatedDigest, but a present digest
            // must agree. Replacement evidence remains backward compatible.
            JournalState.Replaced => entry.ValidatedDigest is null || entry.ValidatedDigest == entry.StagedDigest,
            _ => false
        };
    }

    private static RecoveryAction Retry(string runId, string detail)
        => new(runId, RecoveryDecision.RetryNeeded, detail);

    private static void DeletePriorPublicationBackup(JournalEntry entry)
    {
        // A persisted ID is data, not a path. Never let separators, traversal, ADS,
        // or other platform-specific filename syntax redirect backup cleanup.
        if (string.IsNullOrWhiteSpace(entry.RunId)
            || !entry.RunId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return;
        var backupPath = entry.DestinationPath + $".xbsl-prev-{entry.RunId}";
        try { File.Delete(backupPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (System.Security.SecurityException) { }
    }
}

// Numeric values 0-3 are persisted by existing local databases and must remain stable.
public enum JournalState
{
    Staged = 0,
    Verified = 1,
    Replaced = 2,
    Finalized = 3,
    Aborted = 4,
    Validated = 5
}
public enum RecoveryDecision { Finalized, RetryNeeded }

public sealed record JournalEntry(
    string RunId,
    string DestinationPath,
    JournalState State,
    string? StagedDigest,
    string? FinalDigest,
    string? LockToken,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>Digest of the exact staged artifact accepted by the profile worker.</summary>
    public string? ValidatedDigest { get; init; }
}

public sealed record RecoveryAction(string RunId, RecoveryDecision Decision, string Detail);
