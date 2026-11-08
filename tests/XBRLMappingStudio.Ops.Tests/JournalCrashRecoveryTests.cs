using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.PublicationCrashHarness;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class JournalCrashRecoveryTests
{
    public static IEnumerable<object[]> CrashCases()
    {
        foreach (var format in new[] { "json", "xml" })
        foreach (var stage in new[] { "journal-after-stage", "journal-wait-lock", "journal-before-replace", "journal-after-replace" })
        foreach (var prior in new[] { "absent", "different", "identical" })
            yield return new object[] { format, stage, prior };
    }

    [Theory]
    [InlineData("csv-v1")]
    [InlineData("csv-v2")]
    [InlineData("xlsx-v1")]
    [InlineData("xlsx-v2")]
    [InlineData("json-record-v1")]
    [InlineData("xml-record-v1")]
    public async Task AbruptTerminationDuringRecordAdapterStageWrite_AbortsPartialArtifact(string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-record-stage-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? child = null;
        try
        {
            var state = Path.Combine(root, "state.db");
            var destination = Path.Combine(root, "report.out");
            var runId = "record-stage-" + Guid.NewGuid().ToString("N");
            var prior = Encoding.UTF8.GetBytes("previous accepted bytes");
            var payload = CrashPublicationContract.JournalReportBytes(format);
            await File.WriteAllBytesAsync(destination, prior);
            var journal = new RunJournal(databasePath: state);
            child = StartHarness("journal-mid-write", state, destination, runId, format);
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, child.ExitCode);
            _ = await stdout;
            _ = await stderr;

            var stagedPath = destination + ".xbsl-staging-" + runId;
            var beforeRecovery = Assert.IsType<JournalEntry>(journal.Load(runId));
            Assert.Equal(JournalState.Writing, beforeRecovery.State);
            Assert.Null(beforeRecovery.StagedDigest);
            Assert.Null(beforeRecovery.LockToken);
            Assert.True(File.Exists(stagedPath));
            Assert.Equal(payload.Length / 2, new FileInfo(stagedPath).Length);
            Assert.Equal(prior, await File.ReadAllBytesAsync(destination));

            var action = Assert.Single(new RunJournal(databasePath: state).RecoverUnfinalized());

            Assert.Equal(RecoveryDecision.Aborted, action.Decision);
            Assert.Equal(JournalState.Aborted, journal.Load(runId)!.State);
            Assert.False(File.Exists(stagedPath));
            Assert.Equal(prior, await File.ReadAllBytesAsync(destination));
            Assert.Equal(0L, ReceiptCount(state, runId));
            Assert.Empty(journal.RecoverUnfinalized());
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                child.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(CrashCases))]
    public async Task AbruptTermination_RecoversOnlyLockedValidatedBytes(string format, string stage, string prior)
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-journal-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? child = null;
        DestinationLock? held = null;
        try
        {
            var state = Path.Combine(root, "state.db");
            var destination = Path.Combine(root, "report." + format);
            var runId = "run-" + Guid.NewGuid().ToString("N");
            var intended = CrashPublicationContract.JournalReportBytes(format);
            var original = prior == "identical" ? intended : Encoding.UTF8.GetBytes("previously accepted bytes");
            if (prior != "absent") await File.WriteAllBytesAsync(destination, original);
            var journal = new RunJournal(databasePath: state);
            if (stage == "journal-wait-lock") held = DestinationLock.Acquire(destination);
            child = StartHarness(stage, state, destination, runId, format);
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            if (stage == "journal-wait-lock")
            {
                var deadline = Stopwatch.StartNew();
                while (journal.Load(runId) is not { State: JournalState.Validated, LockToken: null })
                {
                    Assert.False(child.HasExited, "Crash harness exited before the pre-lock journal boundary.");
                    Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "Crash harness did not reach its pre-lock journal boundary.");
                    await Task.Delay(20);
                }
                // Kill an actual waiting publisher, not an injected exception. Its
                // validated bytes may already equal an older accepted destination.
                child.Kill(entireProcessTree: true);
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotEqual(0, child.ExitCode);
            _ = await stdout;
            _ = await stderr;
            held?.Dispose();
            held = null;

            var before = Assert.IsType<JournalEntry>(journal.Load(runId));
            Assert.Equal(stage == "journal-after-stage" ? JournalState.Staged : JournalState.Validated, before.State);
            if (stage == "journal-after-stage")
            {
                Assert.Equal(DestinationLock.DigestBytes(intended), before.StagedDigest);
                Assert.Null(before.ValidatedDigest);
                Assert.Null(before.LockToken);
            }
            else
            {
                Assert.Equal(DestinationLock.DigestBytes(intended), before.ValidatedDigest);
                if (stage == "journal-wait-lock") Assert.Null(before.LockToken);
                else Assert.False(string.IsNullOrWhiteSpace(before.LockToken));
            }
            var staging = destination + ".xbsl-staging-" + runId;
            var backup = destination + ".xbsl-prev-" + runId;
            Assert.Equal(stage != "journal-after-replace", File.Exists(staging));
            Assert.Equal(stage == "journal-after-replace" && prior != "absent", File.Exists(backup));
            if (File.Exists(backup)) Assert.Equal(original, await File.ReadAllBytesAsync(backup));

            // Fresh objects simulate a new application's recovery after process loss.
            var reopened = new RunJournal(databasePath: state);
            var action = Assert.Single(reopened.RecoverUnfinalized());
            var confirmed = stage == "journal-after-replace"
                || stage == "journal-before-replace" && prior == "identical";
            var safelyAborted = stage == "journal-after-stage";
            Assert.Equal(confirmed ? RecoveryDecision.Finalized
                : safelyAborted ? RecoveryDecision.Aborted : RecoveryDecision.RetryNeeded, action.Decision);
            Assert.Equal(runId, action.RunId);
            var after = Assert.IsType<JournalEntry>(reopened.Load(runId));
            Assert.Equal(before.LockToken, after.LockToken);
            Assert.Equal(before.ValidatedDigest, after.ValidatedDigest);
            if (confirmed)
            {
                Assert.Equal(JournalState.Finalized, after.State);
                Assert.Equal(before.StagedDigest, after.FinalDigest);
                Assert.Equal(intended, await File.ReadAllBytesAsync(destination));
                if (format == "json")
                {
                    using var parsed = JsonDocument.Parse(await File.ReadAllBytesAsync(destination));
                    Assert.Equal(JsonValueKind.Object, parsed.RootElement.GetProperty("facts").ValueKind);
                }
                else Assert.Equal("xbrl", XDocument.Load(destination).Root!.Name.LocalName);
                Assert.False(File.Exists(backup));
                Assert.Equal(1L, ReceiptCount(state, runId));
                Assert.Empty(reopened.RecoverUnfinalized());
                Assert.Equal(1L, ReceiptCount(state, runId));
            }
            else
            {
                Assert.Equal(safelyAborted ? JournalState.Aborted : before.State, after.State);
                if (!safelyAborted) Assert.Equal(before, after);
                Assert.Equal(prior != "absent", File.Exists(destination));
                if (File.Exists(destination)) Assert.Equal(original, await File.ReadAllBytesAsync(destination));
                Assert.Equal(0L, ReceiptCount(state, runId));
                if (safelyAborted) Assert.Empty(reopened.RecoverUnfinalized());
                else Assert.Equal(RecoveryDecision.RetryNeeded, Assert.Single(reopened.RecoverUnfinalized()).Decision);
            }
            // Only a digest-matching stage that never reached destination locking is cleaned.
            Assert.Equal(stage is not ("journal-after-replace" or "journal-after-stage"), File.Exists(staging));
        }
        finally
        {
            held?.Dispose();
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                child.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReceiptPersistenceFailure_PreservesPriorBackupUntilRecoveryCanConfirm()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-journal-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state.db");
            var destination = Path.Combine(root, "report.json");
            const string runId = "receipt-save-failed";
            var original = Encoding.UTF8.GetBytes("accepted prior version");
            var intended = CrashPublicationContract.JournalReportBytes("json");
            File.WriteAllBytes(destination, original);
            var journal = new RunJournal(databasePath: state);
            using (var db = new LocalApplicationDatabase(state).OpenConnection())
            using (var trigger = db.CreateCommand())
            {
                trigger.CommandText = "CREATE TRIGGER reject_final_receipt BEFORE INSERT ON publication_receipts BEGIN SELECT RAISE(ABORT, 'simulated receipt storage failure'); END";
                trigger.ExecuteNonQuery();
            }

            Assert.Throws<SqliteException>(() => new PublicationService(journal).Publish(destination, intended, runId,
                validateStagedArtifactAsync: (path, _) => Task.FromResult(DestinationLock.DigestFile(path))));

            Assert.Equal(intended, File.ReadAllBytes(destination));
            var backup = destination + ".xbsl-prev-" + runId;
            Assert.Equal(original, File.ReadAllBytes(backup));
            Assert.Equal(JournalState.Replaced, journal.Load(runId)!.State);
            Assert.Equal(0L, ReceiptCount(state, runId));
            using (var db = new LocalApplicationDatabase(state).OpenConnection())
            using (var drop = db.CreateCommand())
            {
                drop.CommandText = "DROP TRIGGER reject_final_receipt";
                drop.ExecuteNonQuery();
            }

            var action = Assert.Single(new RunJournal(databasePath: state).RecoverUnfinalized());
            Assert.Equal(RecoveryDecision.Finalized, action.Decision);
            Assert.Equal(intended, File.ReadAllBytes(destination));
            Assert.False(File.Exists(backup));
            Assert.Equal(1L, ReceiptCount(state, runId));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static long ReceiptCount(string state, string runId)
    {
        using var db = new LocalApplicationDatabase(state).OpenConnection();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT count(*) FROM publication_receipts WHERE run_id=$run";
        query.Parameters.AddWithValue("$run", runId);
        return (long)query.ExecuteScalar()!;
    }

    private static Process StartHarness(string stage, string state, string destination, string runId, string format)
    {
        var testAssembly = typeof(JournalCrashRecoveryTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(testAssembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
            typeof(CrashPublicationContract).Assembly.Location, stage, state, destination, runId, format
        }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start publication recovery harness.");
    }
}
