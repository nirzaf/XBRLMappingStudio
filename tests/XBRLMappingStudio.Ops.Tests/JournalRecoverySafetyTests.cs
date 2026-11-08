using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

/// <summary>Recovery adjudicates synthetic landed bytes; it never retries a publication.</summary>
public sealed class JournalRecoverySafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreLockOrUnvalidatedSameBytesCannotCreateReceipt(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var entries = new[]
        {
            workspace.Entry("staged", JournalState.Staged),
            workspace.Entry("verified", JournalState.Verified),
            workspace.Entry("no-token", JournalState.Validated) with { LockToken = null },
            workspace.Entry("empty-token", JournalState.Validated) with { LockToken = "" },
            workspace.Entry("blank-token", JournalState.Validated) with { LockToken = " \t" }
        };
        foreach (var entry in entries) workspace.SaveWithBackup(entry);

        var actions = workspace.Journal.RecoverUnfinalized();

        Assert.Equal(entries.Length, actions.Count);
        Assert.All(actions, action => Assert.Equal(RecoveryDecision.RetryNeeded, action.Decision));
        foreach (var entry in entries) workspace.AssertUnchanged(entry);
    }

    [Theory]
    [InlineData(false, JournalState.Staged)]
    [InlineData(false, JournalState.Verified)]
    [InlineData(true, JournalState.Staged)]
    [InlineData(true, JournalState.Verified)]
    public void RecoveryAbortsAndRemovesOnlyDigestMatchingPreLockStage(bool sqlite, JournalState state)
    {
        using var workspace = new Workspace(sqlite);
        var entry = workspace.Entry("orphan-stage-" + state, state) with { LockToken = null };
        workspace.SaveWithBackup(entry);
        var stagingPath = entry.DestinationPath + ".xbsl-staging-" + entry.RunId;
        File.WriteAllText(stagingPath, Workspace.LandedBytes);

        var action = Assert.Single(workspace.Journal.RecoverUnfinalized());

        Assert.Equal(RecoveryDecision.Aborted, action.Decision);
        Assert.Equal(JournalState.Aborted, workspace.Journal.Load(entry.RunId)!.State);
        Assert.False(File.Exists(stagingPath));
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(entry.DestinationPath));
        workspace.AssertReceipt(entry.RunId, null, null);
        Assert.Empty(workspace.Journal.RecoverUnfinalized());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryPreservesMismatchedPreLockStage(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var entry = workspace.Entry("mismatched-stage") with { State = JournalState.Staged, LockToken = null };
        workspace.SaveWithBackup(entry);
        var stagingPath = entry.DestinationPath + ".xbsl-staging-" + entry.RunId;
        File.WriteAllText(stagingPath, "not the journaled payload");

        var action = Assert.Single(workspace.Journal.RecoverUnfinalized());

        Assert.Equal(RecoveryDecision.RetryNeeded, action.Decision);
        Assert.Equal(entry, workspace.Journal.Load(entry.RunId));
        Assert.True(File.Exists(stagingPath));
        workspace.AssertReceipt(entry.RunId, null, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrContradictoryEvidenceRemainsUnconfirmed(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var entries = new[]
        {
            workspace.Entry("no-validation") with { ValidatedDigest = null },
            workspace.Entry("wrong-validation") with { ValidatedDigest = "sha256:other" },
            workspace.Entry("empty-validation") with { ValidatedDigest = "" },
            workspace.Entry("no-staged") with { StagedDigest = null },
            workspace.Entry("empty-staged") with { StagedDigest = "", ValidatedDigest = "" },
            workspace.Entry("contradictory-final") with { FinalDigest = "sha256:other" },
            workspace.Entry("replaced-contradiction", JournalState.Replaced) with { ValidatedDigest = "sha256:other" },
            workspace.Entry("replaced-empty", JournalState.Replaced) with { ValidatedDigest = "" },
            workspace.Entry("unknown-state", (JournalState)99)
        };
        foreach (var entry in entries) workspace.SaveWithBackup(entry);

        var actions = workspace.Journal.RecoverUnfinalized();

        Assert.Equal(entries.Length, actions.Count);
        Assert.All(actions, action => Assert.Equal(RecoveryDecision.RetryNeeded, action.Decision));
        foreach (var entry in entries) workspace.AssertUnchanged(entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedValidationFinalizesWithHistoricalTokenAndIsIdempotent(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var entry = workspace.Entry("locked-validation");
        using (var originalLock = DestinationLock.Acquire(entry.DestinationPath))
        {
            entry = entry with { LockToken = originalLock.Token };
            workspace.SaveWithBackup(entry);
        }

        var action = Assert.Single(workspace.Journal.RecoverUnfinalized());

        Assert.Equal(RecoveryDecision.Finalized, action.Decision);
        var finalized = workspace.Journal.Load(entry.RunId)!;
        Assert.Equal(JournalState.Finalized, finalized.State);
        Assert.Equal(entry.StagedDigest, finalized.FinalDigest);
        Assert.Equal(entry.ValidatedDigest, finalized.ValidatedDigest);
        Assert.Equal(entry.LockToken, finalized.LockToken);
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(entry.DestinationPath));
        Assert.False(File.Exists(workspace.Backup(entry)));
        workspace.AssertReceipt(entry.RunId, entry.StagedDigest, entry.ValidatedDigest);
        Assert.Empty(workspace.Journal.RecoverUnfinalized());
        Assert.Equal(finalized, workspace.Journal.Load(entry.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyReplacedWithoutValidationDigestRemainsRecoverable(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var entry = workspace.Entry("legacy-replaced", JournalState.Replaced)
            with { ValidatedDigest = null, LockToken = null };
        workspace.SaveWithBackup(entry);

        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);

        var finalized = workspace.Journal.Load(entry.RunId)!;
        Assert.Null(finalized.ValidatedDigest);
        Assert.Null(finalized.LockToken);
        Assert.Equal(entry.StagedDigest, finalized.FinalDigest);
        workspace.AssertReceipt(entry.RunId, entry.StagedDigest, null);
        Assert.False(File.Exists(workspace.Backup(entry)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAndDivergentDestinationsPreserveBackupAndIndependentProgress(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var missing = workspace.Entry("a-missing");
        var divergent = workspace.Entry("b-divergent");
        var healthy = workspace.Entry("z-healthy");
        foreach (var entry in new[] { missing, divergent, healthy }) workspace.SaveWithBackup(entry);
        File.Delete(missing.DestinationPath);
        File.WriteAllText(divergent.DestinationPath, "unrelated-newer-destination");

        var actions = workspace.Journal.RecoverUnfinalized();

        AssertDecision(actions, missing, RecoveryDecision.RetryNeeded);
        AssertDecision(actions, divergent, RecoveryDecision.RetryNeeded);
        AssertDecision(actions, healthy, RecoveryDecision.Finalized);
        workspace.AssertUnchanged(missing, destinationBytes: null);
        workspace.AssertUnchanged(divergent, "unrelated-newer-destination");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActivePublisherLockIsNonblockingAndIndependentDestinationStillFinalizes(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var busy = workspace.Entry("a-busy");
        var healthy = workspace.Entry("z-healthy");
        workspace.SaveWithBackup(busy);
        workspace.SaveWithBackup(healthy);
        using var activePublisher = DestinationLock.Acquire(busy.DestinationPath);

        var actions = workspace.Journal.RecoverUnfinalized();

        AssertDecision(actions, busy, RecoveryDecision.RetryNeeded);
        AssertDecision(actions, healthy, RecoveryDecision.Finalized);
        Assert.True(activePublisher.IsHeld);
        workspace.AssertUnchanged(busy);
        activePublisher.Dispose();
        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableDestinationDoesNotLeakLockHandleOrStopIndependentRecovery(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var unreadable = workspace.Entry("a-unreadable");
        var healthy = workspace.Entry("z-healthy");
        workspace.SaveWithBackup(unreadable);
        workspace.SaveWithBackup(healthy);
        using (var exclusiveDestination = new FileStream(unreadable.DestinationPath,
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // File.Exists succeeds, but hashing fails after the sidecar is opened.
            Assert.Throws<IOException>(() => DestinationLock.TryAcquire(unreadable.DestinationPath));
            AssertSidecarCanBeOpenedExclusively(unreadable.DestinationPath);

            var actions = workspace.Journal.RecoverUnfinalized();

            AssertDecision(actions, unreadable, RecoveryDecision.RetryNeeded);
            AssertDecision(actions, healthy, RecoveryDecision.Finalized);
            Assert.Equal(unreadable, workspace.Journal.Load(unreadable.RunId));
            Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(unreadable)));
            workspace.AssertReceipt(unreadable.RunId, null, null);
            AssertSidecarCanBeOpenedExclusively(unreadable.DestinationPath);
        }
        using var reacquired = DestinationLock.Acquire(unreadable.DestinationPath);
        Assert.True(reacquired.IsHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptJournalRowDoesNotAbortIndependentEntries(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var corrupt = workspace.Entry("a-corrupt");
        var healthy = workspace.Entry("z-healthy");
        workspace.SaveWithBackup(corrupt);
        workspace.SaveWithBackup(healthy);
        if (sqlite)
            workspace.Execute("UPDATE run_journal SET updated_utc='not-a-date' WHERE run_id='a-corrupt'");
        else
            File.WriteAllText(workspace.JsonPath(corrupt.RunId), "{incomplete-json");

        var actions = workspace.Journal.RecoverUnfinalized();

        AssertDecision(actions, corrupt, RecoveryDecision.RetryNeeded);
        AssertDecision(actions, healthy, RecoveryDecision.Finalized);
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(corrupt.DestinationPath));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(corrupt)));
        workspace.AssertReceipt(corrupt.RunId, null, null);
        if (!sqlite) Assert.Equal("{incomplete-json", File.ReadAllText(workspace.JsonPath(corrupt.RunId)));
    }

    [Fact]
    public void UnreadableLegacyJournalFileDoesNotHideIndependentRows()
    {
        using var workspace = new Workspace(sqlite: false);
        var unreadable = workspace.Entry("a-unreadable-row");
        var healthy = workspace.Entry("z-healthy");
        workspace.SaveWithBackup(unreadable);
        workspace.SaveWithBackup(healthy);
        using (var exclusiveRow = new FileStream(workspace.JsonPath(unreadable.RunId),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var actions = workspace.Journal.RecoverUnfinalized();

            AssertDecision(actions, unreadable, RecoveryDecision.RetryNeeded);
            AssertDecision(actions, healthy, RecoveryDecision.Finalized);
        }
        workspace.AssertUnchanged(unreadable);
    }

    [Fact]
    public void RetryDecisionDoesNotRemoveOrChangeAnExistingSqliteReceipt()
    {
        using var workspace = new Workspace(sqlite: true);
        var entry = workspace.Entry("uncertain-receipt") with { LockToken = null };
        workspace.SaveWithBackup(entry);
        workspace.Execute("""
            INSERT INTO publication_receipts(run_id,final_digest,completed_utc,validated_digest)
            VALUES('uncertain-receipt','existing-receipt-digest','2026-01-01T00:00:00Z','existing-validation');
            """);

        Assert.Equal(RecoveryDecision.RetryNeeded, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);

        Assert.Equal(entry, workspace.Journal.Load(entry.RunId));
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(entry.DestinationPath));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(entry)));
        workspace.AssertReceipt(entry.RunId, "existing-receipt-digest", "existing-validation");
    }

    [Theory]
    [InlineData(false, JournalState.Finalized)]
    [InlineData(true, JournalState.Finalized)]
    [InlineData(false, JournalState.Aborted)]
    [InlineData(true, JournalState.Aborted)]
    public void RowIsReloadedAfterAcquiringLockAndNewerTerminalStateIsUntouched(bool sqlite, JournalState terminalState)
    {
        using var workspace = new Workspace(sqlite);
        var snapshot = workspace.Entry("stale-terminal");
        workspace.SaveWithBackup(snapshot);
        var newer = snapshot with { State = terminalState, LockToken = "newer-historical-token",
            FinalDigest = terminalState == JournalState.Finalized ? snapshot.StagedDigest : null };
        var seamCalls = 0;

        var actions = workspace.Journal.RecoverUnfinalized(entry =>
        {
            Assert.Equal(snapshot, entry);
            using var publisherLock = DestinationLock.Acquire(entry.DestinationPath);
            workspace.Journal.Save(newer);
            seamCalls++;
        });

        Assert.Equal(1, seamCalls);
        Assert.Empty(actions);
        Assert.Equal(newer, workspace.Journal.Load(snapshot.RunId));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(snapshot)));
        workspace.AssertReceipt(newer.RunId, newer.FinalDigest,
            terminalState == JournalState.Finalized ? newer.ValidatedDigest : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadedDifferentDestinationIsNotFinalizedUnderOldDestinationLock(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var snapshot = workspace.Entry("stale-destination");
        workspace.SaveWithBackup(snapshot);
        var otherDestination = workspace.PathFor("other.json");
        File.WriteAllText(otherDestination, Workspace.LandedBytes);
        var newer = snapshot with { DestinationPath = otherDestination, LockToken = "newer-token" };
        File.WriteAllText(workspace.Backup(newer), Workspace.PriorBytes);

        var action = Assert.Single(workspace.Journal.RecoverUnfinalized(_ => workspace.Journal.Save(newer)));

        Assert.Equal(RecoveryDecision.RetryNeeded, action.Decision);
        workspace.AssertUnchanged(newer);
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(snapshot.DestinationPath));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(snapshot)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadedValidationEvidenceRatherThanSnapshotControlsDecision(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var snapshot = workspace.Entry("stale-evidence");
        workspace.SaveWithBackup(snapshot);
        var newer = snapshot with { LockToken = null };

        var action = Assert.Single(workspace.Journal.RecoverUnfinalized(_ => workspace.Journal.Save(newer)));

        Assert.Equal(RecoveryDecision.RetryNeeded, action.Decision);
        workspace.AssertUnchanged(newer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisappearingRowIsNotResurrected(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var snapshot = workspace.Entry("disappearing");
        workspace.SaveWithBackup(snapshot);

        var action = Assert.Single(workspace.Journal.RecoverUnfinalized(_ =>
        {
            if (sqlite) workspace.Execute("DELETE FROM run_journal WHERE run_id='disappearing'");
            else File.Delete(workspace.JsonPath(snapshot.RunId));
        }));

        Assert.Equal(RecoveryDecision.RetryNeeded, action.Decision);
        Assert.Null(workspace.Journal.Load(snapshot.RunId));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(snapshot)));
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(snapshot.DestinationPath));
        workspace.AssertReceipt(snapshot.RunId, null, null);
    }

    public static IEnumerable<object[]> ConcurrentRowChanges()
    {
        foreach (var sqlite in new[] { false, true })
        foreach (var change in new[] { "staged", "verified", "pre-lock-validation", "aborted", "finalized",
            "destination", "staged-digest", "final-digest", "validated-digest", "lock-token", "updated", "offset", "missing" })
            yield return new object[] { sqlite, change };
    }

    [Theory]
    [MemberData(nameof(ConcurrentRowChanges))]
    public void ChangesAfterLockedReloadCannotBeOverwrittenOrCleanedUp(bool sqlite, string change)
    {
        using var workspace = new Workspace(sqlite);
        var expected = workspace.Entry("a-after-reload");
        var healthy = workspace.Entry("z-independent");
        workspace.SaveWithBackup(expected);
        workspace.SaveWithBackup(healthy);
        var writer = workspace.Reopen();
        var otherDestination = workspace.PathFor("repointed.json");
        File.WriteAllText(otherDestination, Workspace.LandedBytes);
        var newer = change switch
        {
            "staged" => expected with { State = JournalState.Staged, LockToken = null, ValidatedDigest = null },
            "verified" => expected with { State = JournalState.Verified, LockToken = null },
            "pre-lock-validation" => expected with { LockToken = null },
            "aborted" => expected with { State = JournalState.Aborted },
            "finalized" => expected with { State = JournalState.Finalized, FinalDigest = expected.StagedDigest },
            "destination" => expected with { DestinationPath = otherDestination },
            "staged-digest" => expected with { StagedDigest = "sha256:new-stage" },
            "final-digest" => expected with { FinalDigest = "sha256:new-final" },
            "validated-digest" => expected with { ValidatedDigest = "sha256:new-validation" },
            "lock-token" => expected with { LockToken = "newer-acquisition" },
            "updated" => expected with { UpdatedUtc = expected.UpdatedUtc.AddTicks(1) },
            "offset" => expected with { UpdatedUtc = expected.UpdatedUtc.ToOffset(TimeSpan.FromHours(1)) },
            _ => expected
        };
        if (change == "destination") File.WriteAllText(workspace.Backup(newer), Workspace.PriorBytes);
        var seamCalls = 0;

        var actions = workspace.Journal.RecoverUnfinalized(beforeAcquireForTest: null,
            beforeFinalizeForTest: loaded =>
            {
                if (loaded.RunId != expected.RunId) return;
                Assert.Equal(expected, loaded);
                // The actual destination lock is already held, but a legitimate
                // pre-lock publisher can still save its same-run journal state.
                Assert.Null(DestinationLock.TryAcquire(expected.DestinationPath));
                if (change == "missing")
                {
                    if (sqlite) workspace.Execute("DELETE FROM run_journal WHERE run_id='a-after-reload'");
                    else File.Delete(workspace.JsonPath(expected.RunId));
                }
                else writer.Save(newer);
                seamCalls++;
            });

        Assert.Equal(1, seamCalls);
        AssertDecision(actions, expected, RecoveryDecision.RetryNeeded);
        AssertDecision(actions, healthy, RecoveryDecision.Finalized);
        var actual = workspace.Journal.Load(expected.RunId);
        if (change == "missing") Assert.Null(actual);
        else
        {
            Assert.Equal(newer, actual);
            Assert.True(newer.UpdatedUtc.EqualsExact(actual!.UpdatedUtc));
        }
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(expected.DestinationPath));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(expected)));
        if (change == "destination")
        {
            Assert.Equal(Workspace.LandedBytes, File.ReadAllText(newer.DestinationPath));
            Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(newer)));
        }
        workspace.AssertReceipt(expected.RunId, change == "finalized" ? newer.FinalDigest : null,
            change == "finalized" ? newer.ValidatedDigest : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveJournalWriterMakesRecoveryRetryWithoutWaitingOrBlockingOtherRuns(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var busy = workspace.Entry("a-busy-journal");
        var healthy = workspace.Entry("z-independent");
        workspace.SaveWithBackup(busy);
        workspace.SaveWithBackup(healthy);
        using var writerGuard = DestinationLock.Acquire(workspace.WriteGuardPath(busy.RunId));

        var actions = workspace.Journal.RecoverUnfinalized();

        AssertDecision(actions, busy, RecoveryDecision.RetryNeeded);
        AssertDecision(actions, healthy, RecoveryDecision.Finalized);
        workspace.AssertUnchanged(busy);
        writerGuard.Dispose();
        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicSaveUsesSharedGuardAcrossJournalInstances(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var original = workspace.Entry("serialized-save");
        workspace.SaveWithBackup(original);
        var newer = original with { State = JournalState.Staged, LockToken = null };
        var writer = workspace.Reopen();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending;
        using (var guard = DestinationLock.Acquire(workspace.WriteGuardPath(original.RunId)))
        {
            pending = Task.Run(() =>
            {
                attempted.SetResult();
                writer.Save(newer);
            });
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(100)));
            Assert.Equal(original, workspace.Journal.Load(original.RunId));
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(newer, workspace.Journal.Load(original.RunId));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(original)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptIsDurableBeforeCleanupAndBothLocksRemainHeld(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var original = workspace.Entry("receipt-before-cleanup");
        workspace.SaveWithBackup(original);
        var seamCalls = 0;

        var actions = workspace.Journal.RecoverUnfinalized(beforeAcquireForTest: null,
            afterReceiptForTest: finalized =>
            {
                Assert.Equal(JournalState.Finalized, finalized.State);
                Assert.Equal(finalized, workspace.Reopen().Load(original.RunId));
                workspace.AssertReceipt(original.RunId, original.StagedDigest, original.ValidatedDigest);
                Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(original)));
                Assert.Null(DestinationLock.TryAcquire(original.DestinationPath));
                Assert.Null(DestinationLock.TryAcquire(workspace.WriteGuardPath(original.RunId)));
                seamCalls++;
            });

        Assert.Equal(1, seamCalls);
        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(actions).Decision);
        Assert.False(File.Exists(workspace.Backup(original)));
        // A later legitimate Save is allowed, but cannot race the old cleanup.
        var later = original with { State = JournalState.Staged, LockToken = null };
        workspace.Reopen().Save(later);
        File.WriteAllText(workspace.Backup(later), "later-run-backup");
        Assert.Equal(RecoveryDecision.RetryNeeded, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);
        Assert.Equal(later, workspace.Journal.Load(original.RunId));
        Assert.Equal("later-run-backup", File.ReadAllText(workspace.Backup(later)));
    }

    [Fact]
    public void SqliteConditionalFinalizationSupportsLegacyTimestampSpelling()
    {
        using var workspace = new Workspace(sqlite: true);
        var entry = workspace.Entry("legacy-time", JournalState.Replaced) with { ValidatedDigest = null };
        workspace.SaveWithBackup(entry);
        workspace.Execute("UPDATE run_journal SET updated_utc='2026-01-01T00:00:00Z' WHERE run_id='legacy-time'");

        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);

        workspace.AssertReceipt(entry.RunId, entry.StagedDigest, null);
        Assert.False(File.Exists(workspace.Backup(entry)));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("/absolute")]
    public void LegacyJournalRejectsPathLikeIdsBeforeWritingGuardOrEntry(string runId)
    {
        using var workspace = new Workspace(sqlite: false);
        var entry = workspace.Entry("safe") with { RunId = runId };

        Assert.Throws<ArgumentException>(() => workspace.Journal.Save(entry));
        Assert.Throws<ArgumentException>(() => workspace.Journal.Load(runId));

        Assert.Empty(Directory.EnumerateFiles(workspace.Journal.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReceiptSaveFailureRetainsBackupAndDoesNotStopOtherEntries(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var failing = workspace.Entry("a-save-failure");
        var healthy = workspace.Entry("z-healthy");
        workspace.SaveWithBackup(failing);
        workspace.SaveWithBackup(healthy);
        if (sqlite)
            workspace.Execute("""
                CREATE TRIGGER fail_recovery_receipt BEFORE INSERT ON publication_receipts
                WHEN NEW.run_id='a-save-failure'
                BEGIN SELECT RAISE(ABORT, 'synthetic receipt failure'); END;
                """);
        else
            Directory.CreateDirectory(workspace.JsonPath(failing.RunId) + ".tmp");

        var actions = workspace.Journal.RecoverUnfinalized();

        AssertDecision(actions, failing, RecoveryDecision.RetryNeeded);
        AssertDecision(actions, healthy, RecoveryDecision.Finalized);
        workspace.AssertUnchanged(failing);
        // Removing the synthetic fault permits adjudication, never re-publication.
        if (sqlite) workspace.Execute("DROP TRIGGER fail_recovery_receipt");
        else Directory.Delete(workspace.JsonPath(failing.RunId) + ".tmp");
        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);
        Assert.False(File.Exists(workspace.Backup(failing)));
        workspace.AssertReceipt(failing.RunId, failing.StagedDigest, failing.ValidatedDigest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyTerminalEntriesAndTheirBackupFilesRemainUntouched(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var finalized = workspace.Entry("finalized", JournalState.Finalized);
        finalized = finalized with { FinalDigest = finalized.StagedDigest };
        var aborted = workspace.Entry("aborted", JournalState.Aborted);
        workspace.SaveWithBackup(finalized);
        workspace.SaveWithBackup(aborted);

        Assert.Empty(workspace.Journal.RecoverUnfinalized());
        Assert.Empty(workspace.Journal.RecoverUnfinalized());

        Assert.Equal(finalized, workspace.Journal.Load(finalized.RunId));
        Assert.Equal(aborted, workspace.Journal.Load(aborted.RunId));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(finalized)));
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(aborted)));
        workspace.AssertReceipt(finalized.RunId, finalized.FinalDigest, finalized.ValidatedDigest);
        workspace.AssertReceipt(aborted.RunId, null, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntrustedCleanupSuffixIsNotUsedAsAPath(bool sqlite)
    {
        using var workspace = new Workspace(sqlite);
        var entry = workspace.Entry("unsafe suffix");
        workspace.SaveWithBackup(entry);

        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);

        // Conservative cleanup leaves nonportable or unexpected ID spellings alone.
        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(entry)));
        workspace.AssertReceipt(entry.RunId, entry.StagedDigest, entry.ValidatedDigest);
    }

    [Fact]
    public void SqliteTraversalRunIdCannotDeleteUnrelatedFileDuringCleanup()
    {
        using var workspace = new Workspace(sqlite: true);
        var entry = workspace.Entry("safe-file") with { RunId = "/../victim" };
        Directory.CreateDirectory(entry.DestinationPath + ".xbsl-prev-");
        var victim = workspace.PathFor("victim");
        File.WriteAllText(victim, "unrelated-file");
        workspace.Journal.Save(entry);

        Assert.Equal(RecoveryDecision.Finalized, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);

        Assert.Equal("unrelated-file", File.ReadAllText(victim));
        workspace.AssertReceipt(entry.RunId, entry.StagedDigest, entry.ValidatedDigest);
    }

    [Fact]
    public void LegacyRowIdentityCannotRedirectRecoveryToAnotherJournalFile()
    {
        using var workspace = new Workspace(sqlite: false);
        var entry = workspace.Entry("safe-file");
        workspace.SaveWithBackup(entry);
        File.WriteAllText(workspace.JsonPath(entry.RunId), System.Text.Json.JsonSerializer.Serialize(
            entry with { RunId = "../outside" }));

        Assert.Equal(RecoveryDecision.RetryNeeded, Assert.Single(workspace.Journal.RecoverUnfinalized()).Decision);

        Assert.Equal(Workspace.PriorBytes, File.ReadAllText(workspace.Backup(entry)));
        Assert.Equal(Workspace.LandedBytes, File.ReadAllText(entry.DestinationPath));
    }

    private static void AssertDecision(IReadOnlyList<RecoveryAction> actions, JournalEntry entry, RecoveryDecision decision)
        => Assert.Equal(decision, Assert.Single(actions, action => action.RunId == entry.RunId).Decision);

    private static void AssertSidecarCanBeOpenedExclusively(string destination)
    {
        using var probe = new FileStream(destination + ".xbsl-lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    private sealed class Workspace : IDisposable
    {
        internal const string LandedBytes = "synthetic-landed-report";
        internal const string PriorBytes = "synthetic-prior-accepted-report";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-recovery-" + Guid.NewGuid().ToString("N"));
        private readonly bool _sqlite;
        internal RunJournal Journal { get; }

        internal Workspace(bool sqlite)
        {
            _sqlite = sqlite;
            Directory.CreateDirectory(_root);
            Journal = sqlite ? new RunJournal(databasePath: PathFor("state.db"))
                : new RunJournal(PathFor("journal"));
        }

        internal string PathFor(string name) => Path.Combine(_root, name);
        internal string JsonPath(string runId) => Path.Combine(Journal.Root, runId + ".json");
        internal string Backup(JournalEntry entry) => entry.DestinationPath + ".xbsl-prev-" + entry.RunId;

        internal RunJournal Reopen() => _sqlite ? new RunJournal(databasePath: Journal.Root) : new RunJournal(Journal.Root);

        internal string WriteGuardPath(string runId)
        {
            var identity = !_sqlite && OperatingSystem.IsWindows() ? runId.ToUpperInvariant() : runId;
            var idHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return _sqlite ? Journal.Root + ".xbsl-journal-write-" + idHash
                : Path.Combine(Journal.Root, ".xbsl-journal-write-" + idHash);
        }

        internal JournalEntry Entry(string runId, JournalState state = JournalState.Validated)
        {
            var destination = PathFor(runId + ".json");
            File.WriteAllText(destination, LandedBytes);
            var digest = DestinationLock.DigestBytes(Encoding.UTF8.GetBytes(LandedBytes));
            return new JournalEntry(runId, destination, state, digest, null, "historical-lock-token", DateTimeOffset.UtcNow)
                { ValidatedDigest = digest };
        }

        internal void SaveWithBackup(JournalEntry entry)
        {
            Journal.Save(entry);
            File.WriteAllText(Backup(entry), PriorBytes);
        }

        internal void AssertUnchanged(JournalEntry entry, string? destinationBytes = LandedBytes)
        {
            Assert.Equal(entry, Journal.Load(entry.RunId));
            Assert.Equal(PriorBytes, File.ReadAllText(Backup(entry)));
            if (destinationBytes is null) Assert.False(File.Exists(entry.DestinationPath));
            else Assert.Equal(destinationBytes, File.ReadAllText(entry.DestinationPath));
            AssertReceipt(entry.RunId, null, null);
        }

        internal void Execute(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Journal.Root, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal void AssertReceipt(string runId, string? finalDigest, string? validatedDigest)
        {
            if (!_sqlite) return; // Legacy journal's finalized row is the receipt.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Journal.Root, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT final_digest, validated_digest FROM publication_receipts WHERE run_id=$id";
            command.Parameters.AddWithValue("$id", runId);
            using var reader = command.ExecuteReader();
            Assert.Equal(finalDigest is not null, reader.Read());
            if (finalDigest is null) return;
            Assert.Equal(finalDigest, reader.GetString(0));
            Assert.Equal(validatedDigest, reader.IsDBNull(1) ? null : reader.GetString(1));
            Assert.False(reader.Read());
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
