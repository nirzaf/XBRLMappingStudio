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
            try
            {
                var lockPath = full + ".xbsl-lock";
                FileStream handle;
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
                lock (HeldGate) HeldThisProcess.Remove(full);
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

    private string EntryPath(string runId) => Path.Combine(_legacyRoot!, runId + ".json");

    public JournalEntry? Load(string runId)
    {
        if (_database is null)
            return File.Exists(EntryPath(runId)) ? JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(EntryPath(runId))) : null;
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,destination_path,state,staged_digest,final_digest,lock_token,updated_utc FROM run_journal WHERE run_id=$id";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public void Save(JournalEntry entry)
    {
        if (_database is not null)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO run_journal(run_id,destination_path,state,staged_digest,final_digest,lock_token,updated_utc)
                VALUES($id,$destination,$state,$staged,$final,$lock,$updated)
                ON CONFLICT(run_id) DO UPDATE SET destination_path=excluded.destination_path,state=excluded.state,
                    staged_digest=excluded.staged_digest,final_digest=excluded.final_digest,
                    lock_token=excluded.lock_token,updated_utc=excluded.updated_utc;
                """;
            Bind(command, entry);
            command.ExecuteNonQuery();
            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            if (entry.State == JournalState.Finalized && entry.FinalDigest is not null)
            {
                receipt.CommandText = "INSERT INTO publication_receipts(run_id,final_digest,completed_utc) VALUES($id,$digest,$updated) ON CONFLICT(run_id) DO UPDATE SET final_digest=excluded.final_digest,completed_utc=excluded.completed_utc";
                receipt.Parameters.AddWithValue("$id", entry.RunId);
                receipt.Parameters.AddWithValue("$digest", entry.FinalDigest);
                receipt.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O"));
                receipt.ExecuteNonQuery();
            }
            else
            {
                receipt.CommandText = "DELETE FROM publication_receipts WHERE run_id=$id";
                receipt.Parameters.AddWithValue("$id", entry.RunId);
                receipt.ExecuteNonQuery();
            }
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

    public IReadOnlyList<JournalEntry> AllEntries()
    {
        if (_database is not null)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_id,destination_path,state,staged_digest,final_digest,lock_token,updated_utc FROM run_journal ORDER BY run_id";
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
            catch (Exception ex) when (ex is IOException or JsonException or SqliteException) { complete = false; }
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
        reader.IsDBNull(5) ? null : reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture));

    private static void Bind(SqliteCommand command, JournalEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.RunId);
        command.Parameters.AddWithValue("$destination", entry.DestinationPath);
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$staged", (object?)entry.StagedDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$final", (object?)entry.FinalDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("$lock", (object?)entry.LockToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O"));
    }

    /// <summary>
    /// Scan for entries that never reached <see cref="JournalState.Finalized"/> and
    /// verify each against the destination digest. An entry whose destination already
    /// matches the staged digest is finalized post-hoc (the replace actually landed);
    /// anything else is reported for retry, never blindly re-run.
    /// </summary>
    public IReadOnlyList<RecoveryAction> RecoverUnfinalized()
    {
        var actions = new List<RecoveryAction>();
        foreach (var entry in AllEntries())
        {
            if (entry.State == JournalState.Finalized) continue;
            var exists = File.Exists(entry.DestinationPath);
            if (exists && entry.StagedDigest is not null
                && DestinationLock.DigestFile(entry.DestinationPath) == entry.StagedDigest)
            {
                Save(entry with { State = JournalState.Finalized, FinalDigest = entry.StagedDigest });
                actions.Add(new RecoveryAction(entry.RunId, RecoveryDecision.Finalized,
                    "Destination matches staged digest; entry finalized."));
            }
            else
            {
                actions.Add(new RecoveryAction(entry.RunId, RecoveryDecision.RetryNeeded,
                    exists
                        ? "Destination digest does not match the staged payload."
                        : "Destination missing; publication never completed."));
            }
        }
        return actions;
    }
}

public enum JournalState { Staged, Verified, Replaced, Finalized }
public enum RecoveryDecision { Finalized, RetryNeeded }

public sealed record JournalEntry(
    string RunId,
    string DestinationPath,
    JournalState State,
    string? StagedDigest,
    string? FinalDigest,
    string? LockToken,
    DateTimeOffset UpdatedUtc);

public sealed record RecoveryAction(string RunId, RecoveryDecision Decision, string Detail);
