using System.Text.Json;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Application;

/// <summary>Immutable proof of one completed publication (OPS02).</summary>
public sealed record PublicationReceipt(
    string RunId,
    string DestinationPath,
    string StagedDigest,
    string FinalDigest,
    string LockToken,
    DateTimeOffset CompletedUtc);

/// <summary>
/// Four-phase destination publication: Stage → Verify → Replace → Receipt (OPS02).
/// The payload is written to a private staging sidecar, re-opened and digest-checked
/// before it may touch the destination, swapped atomically via <see cref="File.Replace"/>
/// under a <see cref="DestinationLock"/>, and only then finalized in the journal.
/// A failure injected at any phase leaves the previously accepted destination intact;
/// a crash between phases is adjudicated by <see cref="RunJournal.RecoverUnfinalized"/>.
/// </summary>
public sealed class PublicationService
{
    private readonly RunJournal _journal;

    public PublicationService(RunJournal journal) => _journal = journal;

    /// <summary>
    /// Publish <paramref name="payload"/> to <paramref name="destinationPath"/>.
    /// <paramref name="beforeReplaceFault"/> exists for fault-injection tests that must
    /// prove the prior accepted output survives a mid-replace failure; production callers
    /// pass null.
    /// </summary>
    public PublicationReceipt Publish(
        string destinationPath,
        byte[] payload,
        string? runId = null,
        Action<string>? beforeReplaceFault = null,
        TimeSpan? lockTimeout = null)
    {
        runId ??= "run-" + Guid.NewGuid().ToString("N");
        var full = Path.GetFullPath(destinationPath);
        var stagedPath = full + $".xbsl-staging-{runId}";

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // ---- Stage: private sidecar write, never the destination itself.
        string stagedDigest;
        try
        {
            File.WriteAllBytes(stagedPath, payload);
            stagedDigest = DestinationLock.DigestFile(stagedPath);
        }
        catch (IOException ex)
        {
            throw PublicationFailed(DiagnosticCodes.PubStagingFailed,
                $"Staging write for run '{runId}' failed: {ex.Message}", runId, full);
        }
        _journal.Save(new JournalEntry(runId, full, JournalState.Staged,
            stagedDigest, null, null, DateTimeOffset.UtcNow));

        // ---- Verify: re-open the staged bytes and confirm the digest matches the intent.
        var expectedDigest = DestinationLock.DigestBytes(payload);
        if (stagedDigest != expectedDigest)
        {
            TryDelete(stagedPath);
            _journal.Save(LoadAndAdvance(runId, full, JournalState.Staged, null, stagedDigest));
            throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                $"Staged artifact digest {stagedDigest} does not match the payload digest {expectedDigest}.",
                runId, full);
        }
        _journal.Save(new JournalEntry(runId, full, JournalState.Verified,
            stagedDigest, null, null, DateTimeOffset.UtcNow));

        // ---- Replace: acquire the destination lock (waited, so concurrent publishers
        // serialize), then swap atomically.
        using var lk = DestinationLock.AcquireAsync(
            full, lockTimeout ?? TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        _journal.Save(new JournalEntry(runId, full, JournalState.Verified,
            stagedDigest, null, lk.Token, DateTimeOffset.UtcNow));

        var backupPath = full + $".xbsl-prev-{runId}";
        try
        {
            beforeReplaceFault?.Invoke(stagedPath); // test seam: simulate host failure here
            if (File.Exists(full))
                File.Replace(stagedPath, full, backupPath);
            else
                File.Move(stagedPath, full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Prior destination untouched by the failed swap; staging is cleaned up.
            TryDelete(stagedPath);
            TryDelete(backupPath);
            throw PublicationFailed(DiagnosticCodes.PubPartialFailure,
                $"Atomic replace failed for run '{runId}'; prior destination preserved. {ex.Message}",
                runId, full);
        }
        _journal.Save(new JournalEntry(runId, full, JournalState.Replaced,
            stagedDigest, null, lk.Token, DateTimeOffset.UtcNow));

        // ---- Verify-after-replace + Receipt: final bytes must equal the staged ones.
        var finalDigest = DestinationLock.DigestFile(full);
        if (finalDigest != stagedDigest)
        {
            throw PublicationFailed(DiagnosticCodes.PubDigestMismatch,
                $"Post-replace digest {finalDigest} diverges from staged {stagedDigest}.",
                runId, full);
        }
        TryDelete(backupPath); // prior accepted copy no longer needed
        var receipt = new PublicationReceipt(runId, full, stagedDigest, finalDigest,
            lk.Token, DateTimeOffset.UtcNow);
        _journal.Save(new JournalEntry(runId, full, JournalState.Finalized,
            stagedDigest, finalDigest, lk.Token, DateTimeOffset.UtcNow));
        return receipt;
    }

    private JournalEntry LoadAndAdvance(string runId, string full, JournalState state,
        string? finalDigest, string? stagedDigest)
        => new(runId, full, state, stagedDigest, finalDigest, null, DateTimeOffset.UtcNow);

    private static PublicationException PublicationFailed(string code, string message,
        string runId, string destination)
        => new(code, message, runId, destination);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
    }
}

/// <summary>Publication failure carrying the stable diagnostic code for the surface layer.</summary>
public sealed class PublicationException : Exception
{
    public string Code { get; }
    public string RunId { get; }
    public string DestinationPath { get; }

    public PublicationException(string code, string message, string runId, string destinationPath)
        : base(message)
    {
        Code = code;
        RunId = runId;
        DestinationPath = destinationPath;
    }

    public Diagnostic ToDiagnostic() => new()
    {
        Code = Code,
        Subsystem = DiagnosticSubsystem.Publication,
        Severity = DiagnosticSeverity.Error,
        Stage = DiagnosticStage.Publication,
        IsBlocking = true,
        Message = Message,
        OperationId = RunId,
    };
}
