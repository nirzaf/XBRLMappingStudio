using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.CLI;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class FilePublicationRecoveryCommandTests
{
    [Fact]
    public void LockedValidatedBytes_FinalizeReceiptAndReturnSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-cli-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "state.db");
            var destination = Path.Combine(root, "report.json");
            var payload = Encoding.UTF8.GetBytes("synthetic validated report");
            File.WriteAllBytes(destination, payload);
            var runId = "run-" + Guid.NewGuid().ToString("N");
            var digest = DestinationLock.DigestBytes(payload);
            var journal = new RunJournal(databasePath: statePath);
            journal.Save(new JournalEntry(runId, destination, JournalState.Validated,
                digest, null, "original-publication-lock", DateTimeOffset.UtcNow)
            { ValidatedDigest = digest });
            using var output = new StringWriter();

            var exitCode = FilePublicationRecoveryCommand.Execute(journal, output);

            Assert.Equal(ExitCodes.Success, exitCode);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(1, json.RootElement.GetProperty("checkedCount").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("finalizedCount").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("retryNeededCount").GetInt32());
            var action = Assert.Single(json.RootElement.GetProperty("actions").EnumerateArray());
            Assert.Equal(runId, action.GetProperty("runId").GetString());
            Assert.Equal("Finalized", action.GetProperty("decision").GetString());
            Assert.Equal(JournalState.Finalized, journal.Load(runId)!.State);
            Assert.Equal(digest, journal.Load(runId)!.FinalDigest);
            Assert.Equal(1L, ReceiptCount(statePath, runId));
            Assert.Equal(payload, File.ReadAllBytes(destination));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void MatchingBytesWithoutLockedValidation_ReturnRetryNeededAndDoNotCreateReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-cli-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "state.db");
            var destination = Path.Combine(root, "report.xml");
            var payload = Encoding.UTF8.GetBytes("synthetic report bytes");
            File.WriteAllBytes(destination, payload);
            var runId = "run-" + Guid.NewGuid().ToString("N");
            var digest = DestinationLock.DigestBytes(payload);
            var journal = new RunJournal(databasePath: statePath);
            journal.Save(new JournalEntry(runId, destination, JournalState.Validated,
                digest, null, null, DateTimeOffset.UtcNow)
            { ValidatedDigest = digest });
            using var output = new StringWriter();

            var exitCode = FilePublicationRecoveryCommand.Execute(journal, output);

            Assert.Equal(ExitCodes.OperationalFailure, exitCode);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(0, json.RootElement.GetProperty("finalizedCount").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("retryNeededCount").GetInt32());
            Assert.Equal(JournalState.Validated, journal.Load(runId)!.State);
            Assert.Null(journal.Load(runId)!.FinalDigest);
            Assert.Equal(0L, ReceiptCount(statePath, runId));
            Assert.Equal(payload, File.ReadAllBytes(destination));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void CancelledCommand_DoesNotFinalizeUnprocessedEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "xms-cli-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var statePath = Path.Combine(root, "state.db");
            var destination = Path.Combine(root, "report.json");
            var payload = Encoding.UTF8.GetBytes("synthetic validated report");
            File.WriteAllBytes(destination, payload);
            var runId = "run-" + Guid.NewGuid().ToString("N");
            var digest = DestinationLock.DigestBytes(payload);
            var journal = new RunJournal(databasePath: statePath);
            journal.Save(new JournalEntry(runId, destination, JournalState.Validated,
                digest, null, "original-publication-lock", DateTimeOffset.UtcNow)
            { ValidatedDigest = digest });
            using var output = new StringWriter();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                FilePublicationRecoveryCommand.Execute(journal, output, cancellation.Token));

            Assert.Empty(output.ToString());
            Assert.Equal(JournalState.Validated, journal.Load(runId)!.State);
            Assert.Equal(0L, ReceiptCount(statePath, runId));
            Assert.Equal(payload, File.ReadAllBytes(destination));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static long ReceiptCount(string databasePath, string runId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM publication_receipts WHERE run_id=$runId";
        command.Parameters.AddWithValue("$runId", runId);
        return (long)command.ExecuteScalar()!;
    }
}
