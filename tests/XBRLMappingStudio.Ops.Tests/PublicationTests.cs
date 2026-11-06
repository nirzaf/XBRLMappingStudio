using System.Text;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Ops.Tests;

/// <summary>
/// OPS02 — destination locking, private staging, atomic replace, receipts and
/// crash-recovery adjudication through the run journal.
/// </summary>
public class PublicationTests : IDisposable
{
    private readonly string _dir;
    private readonly RunJournal _journal;

    public PublicationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ops-pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _journal = new RunJournal(Path.Combine(_dir, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Dest(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Publish_HappyPath_WritesDestinationAndFinalizedReceipt()
    {
        var svc = new PublicationService(_journal);
        var payload = Encoding.UTF8.GetBytes("xbrl-json-v1");

        var receipt = svc.Publish(Dest("out.json"), payload, runId: "run-happy");

        Assert.Equal(payload, File.ReadAllBytes(Dest("out.json")));
        Assert.Equal(receipt.StagedDigest, receipt.FinalDigest);
        var entry = _journal.Load("run-happy");
        Assert.Equal(JournalState.Finalized, entry!.State);
        Assert.Equal(receipt.LockToken, entry.LockToken);
        // Staging sidecar cleaned up.
        Assert.Empty(Directory.GetFiles(_dir, "*.xbsl-staging-*"));
    }

    [Fact]
    public void MixedPublication_RefusesDifferentExistingFile_AndConfirmsIdenticalRetryWithoutReplacement()
    {
        var svc = new PublicationService(_journal);
        var destination = Dest("mixed-no-overwrite.json");
        var acceptedBytes = Encoding.UTF8.GetBytes("previously-accepted");
        var intendedBytes = Encoding.UTF8.GetBytes("new-intent");
        File.WriteAllBytes(destination, acceptedBytes);

        var conflict = Assert.Throws<PublicationException>(() => svc.Publish(
            destination, intendedBytes, runId: "mixed-conflict", refuseDifferentExisting: true));

        Assert.Equal(DiagnosticCodes.PubReceiptConflict, conflict.Code);
        Assert.Equal(acceptedBytes, File.ReadAllBytes(destination));

        var sameDigest = DestinationLock.DigestBytes(acceptedBytes);
        var receipt = svc.Publish(destination, acceptedBytes, runId: "mixed-same-content",
            refuseDifferentExisting: true);

        Assert.Equal(sameDigest, receipt.FinalDigest);
        Assert.Equal(acceptedBytes, File.ReadAllBytes(destination));
        Assert.Equal(JournalState.Aborted, _journal.Load("mixed-conflict")!.State);
        Assert.Equal(JournalState.Finalized, _journal.Load("mixed-same-content")!.State);
    }

    [Fact]
    public void Publish_ValidatesExactStagedBytes_AndTiesReceiptToValidatedDigest()
    {
        var svc = new PublicationService(_journal);
        var dest = Dest("validated.json");
        var payload = Encoding.UTF8.GetBytes("validated-xbrl-json");
        string? observedStagePath = null;

        var receipt = svc.Publish(dest, payload, runId: "run-validated",
            validateStagedArtifactAsync: (stage, _) =>
            {
                observedStagePath = stage;
                Assert.Equal(payload, File.ReadAllBytes(stage));
                return Task.FromResult(DestinationLock.DigestFile(stage));
            });

        var digest = DestinationLock.DigestBytes(payload);
        Assert.NotNull(observedStagePath);
        Assert.Equal(digest, receipt.ValidatedDigest);
        Assert.Equal(digest, receipt.StagedDigest);
        Assert.Equal(digest, receipt.FinalDigest);
        var journalEntry = _journal.Load("run-validated")!;
        Assert.Equal(JournalState.Finalized, journalEntry.State);
        Assert.Equal(digest, journalEntry.ValidatedDigest);
        Assert.Empty(Directory.GetFiles(_dir, "*.xbsl-staging-*"));
    }

    [Fact]
    public void Publish_ValidationFailureAbortsAndPreservesPriorOutput()
    {
        var svc = new PublicationService(_journal);
        var dest = Dest("validation-failure.json");
        var prior = Encoding.UTF8.GetBytes("accepted-before-validation");
        File.WriteAllBytes(dest, prior);

        var ex = Assert.Throws<PublicationException>(() => svc.Publish(dest,
            Encoding.UTF8.GetBytes("rejected-report"), runId: "run-validation-failure",
            validateStagedArtifactAsync: (_, _) => throw new PublicationException(
                DiagnosticCodes.XbrValidationFailed, "Profile rejected staged report.",
                "run-validation-failure", dest)));

        Assert.Equal(DiagnosticCodes.XbrValidationFailed, ex.Code);
        Assert.Equal(prior, File.ReadAllBytes(dest));
        Assert.Equal(JournalState.Aborted, _journal.Load("run-validation-failure")!.State);
        Assert.Empty(_journal.RecoverUnfinalized());
        Assert.Empty(Directory.GetFiles(_dir, "*.xbsl-staging-*"));
    }

    [Fact]
    public void Publish_PostValidationTamperingAbortsBeforeReplace()
    {
        var svc = new PublicationService(_journal);
        var dest = Dest("tampered-stage.json");
        var prior = Encoding.UTF8.GetBytes("accepted-before-tamper");
        var payload = Encoding.UTF8.GetBytes("validated-report");
        File.WriteAllBytes(dest, prior);

        var ex = Assert.Throws<PublicationException>(() => svc.Publish(dest, payload,
            runId: "run-post-validation-tamper",
            beforeReplaceFault: stage => File.WriteAllText(stage, "changed-after-validation"),
            validateStagedArtifactAsync: (stage, _) =>
                Task.FromResult(DestinationLock.DigestFile(stage))));

        Assert.Equal(DiagnosticCodes.PubDigestMismatch, ex.Code);
        Assert.Equal(prior, File.ReadAllBytes(dest));
        Assert.Equal(JournalState.Aborted, _journal.Load("run-post-validation-tamper")!.State);
        Assert.Empty(_journal.RecoverUnfinalized());
        Assert.Empty(Directory.GetFiles(_dir, "*.xbsl-staging-*"));
    }

    [Fact]
    public void ConcurrentWriters_Serialize_AndEveryReceiptMatchesSomeFinalState()
    {
        var svc = new PublicationService(_journal);
        var dest = Dest("contended.json");
        var receipts = new PublicationReceipt[4];

        Parallel.For(0, 4, i =>
        {
            var bytes = Encoding.UTF8.GetBytes($"payload-{i}");
            receipts[i] = svc.Publish(dest, bytes, runId: $"run-c{i}",
                lockTimeout: TimeSpan.FromSeconds(30));
        });

        var finalBytes = File.ReadAllBytes(dest);
        var finalDigest = DestinationLock.DigestBytes(finalBytes);
        // Every publication claims success; the destination is exactly one complete
        // payload (no interleave), and its digest matches that winner's receipt.
        Assert.Contains(receipts, r => r.FinalDigest == finalDigest);
        Assert.StartsWith("payload-", Encoding.UTF8.GetString(finalBytes));
        for (int i = 0; i < 4; i++)
            Assert.Equal(JournalState.Finalized, _journal.Load($"run-c{i}")!.State);
    }

    [Fact]
    public void FailureMidReplace_PreservesPriorAcceptedOutput()
    {
        var svc = new PublicationService(_journal);
        var dest = Dest("survives.json");
        var v1 = Encoding.UTF8.GetBytes("accepted-v1");
        svc.Publish(dest, v1, runId: "run-v1");

        var ex = Assert.Throws<PublicationException>(() => svc.Publish(
            dest, Encoding.UTF8.GetBytes("poison-v2"), runId: "run-v2",
            beforeReplaceFault: _ => throw new IOException("simulated host failure")));

        Assert.Equal(DiagnosticCodes.PubPartialFailure, ex.Code);
        Assert.Equal(v1, File.ReadAllBytes(dest)); // prior output byte-intact
        Assert.Empty(Directory.GetFiles(_dir, "*.xbsl-staging-*"));
    }

    [Fact]
    public void LockIsPerPath_HeldDestinationRejectsSecondAcquire()
    {
        var dest = Dest("locked.json");
        File.WriteAllText(dest, "x");
        using var first = DestinationLock.Acquire(dest);
        Assert.Null(DestinationLock.TryAcquire(dest)); // same-process second holder refused

        var other = Dest("other.json");
        using var second = DestinationLock.Acquire(other); // different path unaffected
        Assert.NotNull(second);

        first.Dispose();
        Assert.NotNull(DestinationLock.TryAcquire(dest)); // released
    }

    [Fact]
    public async Task AcquireAsync_TimesOut_WhenHeldElsewhere()
    {
        var dest = Dest("deadline.json");
        File.WriteAllText(dest, "x");
        using var holder = DestinationLock.Acquire(dest);

        await Assert.ThrowsAsync<DestinationLockedException>(async () =>
            await DestinationLock.AcquireAsync(dest, TimeSpan.FromMilliseconds(80)));
    }

    [Fact]
    public void Recovery_FinalizesCrashedButLandedPublication()
    {
        var dest = Dest("recovery.json");
        var payload = Encoding.UTF8.GetBytes("landed");
        var digest = DestinationLock.DigestBytes(payload);
        File.WriteAllText(dest, "landed");

        // Simulate a crash after the replace but before the Finalized journal write.
        _journal.Save(new JournalEntry("run-crash", Path.GetFullPath(dest),
            JournalState.Replaced, digest, null, "rev-x", DateTimeOffset.UtcNow));

        var actions = _journal.RecoverUnfinalized();

        Assert.Single(actions);
        Assert.Equal(RecoveryDecision.Finalized, actions[0].Decision);
        Assert.Equal(JournalState.Finalized, _journal.Load("run-crash")!.State);
    }

    [Fact]
    public void Recovery_FlagsRetry_WhenDestinationDiverged()
    {
        var dest = Dest("diverged.json");
        File.WriteAllText(dest, "partial-corruption");
        _journal.Save(new JournalEntry("run-diverge", Path.GetFullPath(dest),
            JournalState.Staged, DestinationLock.DigestBytes(Encoding.UTF8.GetBytes("intent")),
            null, null, DateTimeOffset.UtcNow));

        var actions = _journal.RecoverUnfinalized();

        Assert.Single(actions);
        Assert.Equal(RecoveryDecision.RetryNeeded, actions[0].Decision);
    }

    [Fact]
    public void Recovery_DoesNotFinalizeUnvalidatedPayloadEvenWhenPriorOutputMatches()
    {
        var dest = Dest("same-content-unvalidated.json");
        var payload = Encoding.UTF8.GetBytes("same-content");
        var digest = DestinationLock.DigestBytes(payload);
        File.WriteAllBytes(dest, payload);
        _journal.Save(new JournalEntry("run-unvalidated", Path.GetFullPath(dest),
            JournalState.Verified, digest, null, null, DateTimeOffset.UtcNow));

        var action = Assert.Single(_journal.RecoverUnfinalized());

        Assert.Equal(RecoveryDecision.RetryNeeded, action.Decision);
        Assert.Equal(JournalState.Verified, _journal.Load("run-unvalidated")!.State);
    }

    [Fact]
    public void Journal_DefaultsToVersionedLocalApplicationDatabase()
    {
        var journal = new RunJournal();
        Assert.Contains("XBRLMappingStudio", journal.Root.Replace('\\', '/'));
        Assert.EndsWith("state.db", journal.Root.Replace('\\', '/'));
    }
}
