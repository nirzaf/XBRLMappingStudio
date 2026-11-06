using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class MixedPublicationCoordinatorTests
{
    [Fact]
    public async Task FileConfirmedSqliteRejected_RetryKeepsFileAndUsesSameSqliteIntent()
    {
        using var workspace = new TemporaryDirectory();
        var statePath = Path.Combine(workspace.Path, "state.db");
        var database = new LocalApplicationDatabase(statePath);
        var filePath = Path.Combine(workspace.Path, "report.json");
        var bytes = Encoding.UTF8.GetBytes("accepted report bytes");
        var intent = Intent(filePath, bytes, Path.Combine(workspace.Path, "destination.db"));
        var coordinator = new MixedPublicationCoordinator(new MixedPublicationStore(database));
        var fileWrites = 0;
        var sqliteAttempts = 0;
        var operationKey = "xms-stable-operation";
        var databaseIntent = "sha256:stable-effective-intent";

        var first = await coordinator.PublishAsync(intent,
            token =>
            {
                fileWrites++;
                File.WriteAllBytes(filePath, bytes);
                return Task.FromResult(new PublicationReceipt("run-1", filePath,
                    intent.FileDigest, intent.FileDigest, "lock-1", DateTimeOffset.UtcNow));
            },
            (prepared, token) =>
            {
                sqliteAttempts++;
                prepared(operationKey, databaseIntent);
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    "Injected target schema refusal.", DiagnosticStage.Publication);
            });

        Assert.Equal(DestinationPublicationState.Confirmed, first.Record.FileState);
        Assert.Equal(DestinationPublicationState.Rejected, first.Record.SqliteState);
        Assert.Contains(first.Record.ToManifestOutcomes(), outcome =>
            outcome.Kind == "xbrl-json" && outcome.State == DestinationPublicationState.Confirmed
            && outcome.Digest == intent.FileDigest);
        Assert.Contains(first.Record.ToManifestOutcomes(), outcome =>
            outcome.Kind == "sqlite" && outcome.State == DestinationPublicationState.Rejected);

        var retry = await coordinator.PublishAsync(intent,
            token => throw new InvalidOperationException("A confirmed file must not be rewritten on retry."),
            (prepared, token) =>
            {
                sqliteAttempts++;
                prepared(operationKey, databaseIntent);
                return Task.FromResult(new XBRLMappingStudio.Adapters.Sqlite.SqlitePublishReceipt(
                    operationKey, databaseIntent, 2, DateTimeOffset.UtcNow));
            });

        Assert.Null(retry.Failure);
        Assert.Equal(1, fileWrites);
        Assert.Equal(2, sqliteAttempts);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.FileState);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.SqliteState);
        Assert.Equal(operationKey, retry.Record.SqliteOperationKey);
        Assert.Equal(databaseIntent, retry.Record.SqliteIntentFingerprint);
        Assert.Equal(2, retry.Record.SqliteRowsWritten);

        var reopened = new MixedPublicationStore(new LocalApplicationDatabase(statePath)).Find(intent.OperationKey);
        Assert.Equal(retry.Record, reopened);
        Assert.Equal("ok", database.IntegrityCheck());
    }

    [Fact]
    public async Task ExplicitReconciliation_ConfirmsDigestAndCommittedSqliteReceipt_AndRejectsMissingReceipt()
    {
        using var workspace = new TemporaryDirectory();
        var localDatabase = new LocalApplicationDatabase(Path.Combine(workspace.Path, "local.db"));
        var targetPath = Path.Combine(workspace.Path, "target.db");
        await CreateReceiptDatabaseAsync(targetPath, "xms-committed", "sha256:committed-intent", 4);

        var filePath = Path.Combine(workspace.Path, "committed.json");
        var bytes = Encoding.UTF8.GetBytes("committed file bytes");
        File.WriteAllBytes(filePath, bytes);
        var firstIntent = Intent(filePath, bytes, targetPath, "mixed-committed");
        var store = new MixedPublicationStore(localDatabase);
        _ = store.GetOrCreate(firstIntent);
        _ = store.SetFile(firstIntent.OperationKey, DestinationPublicationState.InProgress);
        _ = store.SetSqlitePrepared(firstIntent.OperationKey, "xms-committed", "sha256:committed-intent");
        _ = store.SetSqlite(firstIntent.OperationKey, DestinationPublicationState.Unknown,
            failureCode: DiagnosticCodes.PubPartialFailure);

        var coordinator = new MixedPublicationCoordinator(store);
        var reconciled = await coordinator.ReconcileAsync(firstIntent.OperationKey);

        Assert.NotNull(reconciled);
        Assert.Equal(DestinationPublicationState.Confirmed, reconciled!.FileState);
        Assert.Equal(DestinationPublicationState.Confirmed, reconciled.SqliteState);
        Assert.Equal(4, reconciled.SqliteRowsWritten);

        var absentIntent = Intent(Path.Combine(workspace.Path, "absent.json"), bytes, targetPath, "mixed-absent");
        _ = store.GetOrCreate(absentIntent);
        _ = store.SetSqlitePrepared(absentIntent.OperationKey, "xms-not-committed", "sha256:missing-intent");
        _ = store.SetSqlite(absentIntent.OperationKey, DestinationPublicationState.InProgress);

        var absent = await coordinator.ReconcileAsync(absentIntent.OperationKey);

        Assert.NotNull(absent);
        Assert.Equal(DestinationPublicationState.Rejected, absent!.SqliteState);
        Assert.Equal("PUB_NOT_COMMITTED", absent.SqliteFailureCode);
        Assert.Equal(DestinationPublicationState.Pending, absent.FileState);

        var missingTargetIntent = Intent(Path.Combine(workspace.Path, "unresolved.json"), bytes,
            Path.Combine(workspace.Path, "missing-target.db"), "mixed-target-unavailable");
        _ = store.GetOrCreate(missingTargetIntent);
        _ = store.SetSqlitePrepared(missingTargetIntent.OperationKey, "xms-target-unavailable", "sha256:unavailable-intent");
        _ = store.SetSqlite(missingTargetIntent.OperationKey, DestinationPublicationState.InProgress);

        var unavailable = await coordinator.ReconcileAsync(missingTargetIntent.OperationKey);

        Assert.NotNull(unavailable);
        Assert.Equal(DestinationPublicationState.Unknown, unavailable!.SqliteState);
        Assert.Equal(DiagnosticCodes.PubPartialFailure, unavailable.SqliteFailureCode);
    }

    private static MixedPublicationIntent Intent(string filePath, byte[] bytes, string sqlitePath, string? operationKey = null)
    {
        var digest = DestinationLock.DigestBytes(bytes);
        return new MixedPublicationIntent(operationKey ?? "mix-" + Guid.NewGuid().ToString("N"),
            "sha256:" + Guid.NewGuid().ToString("N"), filePath, digest, bytes.Length, sqlitePath, "facts");
    }

    private static async Task CreateReceiptDatabaseAsync(string path, string operationKey, string intent, int rows)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE xms_idempotency_receipts(
                operation_key TEXT PRIMARY KEY NOT NULL,
                intent_fingerprint TEXT NOT NULL,
                written_rows INTEGER NOT NULL,
                completed_utc TEXT NOT NULL);
            INSERT INTO xms_idempotency_receipts(operation_key,intent_fingerprint,written_rows,completed_utc)
            VALUES($key,$intent,$rows,$completed);
            """;
        command.Parameters.AddWithValue("$key", operationKey);
        command.Parameters.AddWithValue("$intent", intent);
        command.Parameters.AddWithValue("$rows", rows);
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-mixed-pub-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
