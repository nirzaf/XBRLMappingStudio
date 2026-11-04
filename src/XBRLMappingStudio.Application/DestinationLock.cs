using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Persistent per-run journal at <c>~/.xbrlmappingstudio/journal/&lt;runId&gt;.json</c>
/// (OPS02). Every publication phase is recorded before it happens; a host restart lost
/// mid-replace leaves an unfinalized entry that <see cref="RecoverUnfinalized"/> can
/// adjudicate against the on-disk destination digest before any retry is attempted.
/// </summary>
public sealed class RunJournal
{
    public string Root { get; }

    public RunJournal(string? rootOverride = null)
        => Root = rootOverride
           ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".xbrlmappingstudio", "journal");

    private string EntryPath(string runId) => Path.Combine(Root, runId + ".json");

    public JournalEntry? Load(string runId)
        => File.Exists(EntryPath(runId))
            ? JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(EntryPath(runId)))
            : null;

    public void Save(JournalEntry entry)
    {
        Directory.CreateDirectory(Root);
        // Write-then-rename keeps the journal itself crash-safe.
        var tmp = EntryPath(entry.RunId) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entry,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, EntryPath(entry.RunId), overwrite: true);
    }

    public IReadOnlyList<JournalEntry> AllEntries()
    {
        if (!Directory.Exists(Root)) return Array.Empty<JournalEntry>();
        return Directory.EnumerateFiles(Root, "*.json")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(p)))
            .OfType<JournalEntry>()
            .ToList();
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
