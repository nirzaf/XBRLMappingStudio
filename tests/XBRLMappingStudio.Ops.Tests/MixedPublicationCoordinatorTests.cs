using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.PublicationCrashHarness;

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

    [Fact]
    public async Task RestartWithUnconfirmedFileWrite_RemainsUnknownAndBlocksBothRetryLegs()
    {
        using var workspace = new TemporaryDirectory();
        var statePath = Path.Combine(workspace.Path, "state.db");
        var filePath = Path.Combine(workspace.Path, "report.json");
        var targetPath = Path.Combine(workspace.Path, "target.db");
        var bytes = Encoding.UTF8.GetBytes("intended report bytes");
        var intent = Intent(filePath, bytes, targetPath, "mixed-interrupted-before-file-confirmation");

        var originalStore = new MixedPublicationStore(new LocalApplicationDatabase(statePath));
        _ = originalStore.GetOrCreate(intent);
        _ = originalStore.SetFile(intent.OperationKey, DestinationPublicationState.InProgress);

        // Reopen the local database and coordinator as a fresh process would after a crash.
        var restartedStore = new MixedPublicationStore(new LocalApplicationDatabase(statePath));
        var restartedCoordinator = new MixedPublicationCoordinator(restartedStore);
        var recovered = await restartedCoordinator.ReconcileAsync(intent.OperationKey);

        Assert.NotNull(recovered);
        Assert.Equal(DestinationPublicationState.Unknown, recovered!.FileState);
        Assert.Equal(DiagnosticCodes.PubPartialFailure, recovered.FileFailureCode);
        Assert.Equal(DestinationPublicationState.Pending, recovered.SqliteState);
        Assert.False(File.Exists(filePath));

        var fileAttempts = 0;
        var sqliteAttempts = 0;
        var retry = await restartedCoordinator.PublishAsync(intent,
            _ =>
            {
                fileAttempts++;
                return Task.FromResult(new PublicationReceipt("run", filePath,
                    intent.FileDigest, intent.FileDigest, "lock", DateTimeOffset.UtcNow));
            },
            (_, _) =>
            {
                sqliteAttempts++;
                return Task.FromResult(new XBRLMappingStudio.Adapters.Sqlite.SqlitePublishReceipt(
                    "db-key", "sha256:db-intent", 1, DateTimeOffset.UtcNow));
            });

        Assert.NotNull(retry.Failure);
        Assert.Equal(DiagnosticCodes.PubPartialFailure, ((PublicationException)retry.Failure!).Code);
        Assert.Equal(0, fileAttempts);
        Assert.Equal(0, sqliteAttempts);
    }

    [Theory]
    [InlineData("file-in-progress")]
    [InlineData("file-landed")]
    [InlineData("sqlite-in-progress")]
    [InlineData("sqlite-prepared")]
    [InlineData("sqlite-committed")]
    public async Task AbruptProcessTermination_ReconcilesDurableMixedPublicationState(string stage)
    {
        using var workspace = new TemporaryDirectory();
        var statePath = Path.Combine(workspace.Path, "local-state.db");
        var targetPath = Path.Combine(workspace.Path, "destination.db");
        var filePath = Path.Combine(workspace.Path, "report.json");
        var priorBytes = Encoding.UTF8.GetBytes("prior accepted report");
        var reportBytes = CrashPublicationContract.ReportBytes;
        await File.WriteAllBytesAsync(filePath, priorBytes);
        await CreateCrashTargetAsync(targetPath);

        var exitCode = await StartCrashHarnessAsync(stage, statePath, filePath, targetPath);

        Assert.NotEqual(0, exitCode);
        var coordinator = new MixedPublicationCoordinator(
            new MixedPublicationStore(new LocalApplicationDatabase(statePath)));
        var recovered = await coordinator.ReconcileAsync(CrashPublicationContract.OperationKey(stage));

        Assert.NotNull(recovered);
        if (stage == "file-in-progress")
        {
            Assert.Equal(DestinationPublicationState.Unknown, recovered!.FileState);
            Assert.Equal(DestinationPublicationState.Pending, recovered.SqliteState);
            Assert.Equal(priorBytes, await File.ReadAllBytesAsync(filePath));

            var fileAttempts = 0;
            var sqliteAttempts = 0;
            var blocked = await coordinator.PublishAsync(IntentForCrash(stage, filePath, reportBytes, targetPath),
                _ =>
                {
                    fileAttempts++;
                    throw new InvalidOperationException("Unknown file outcomes must block publication retry.");
                },
                (_, _) =>
                {
                    sqliteAttempts++;
                    throw new InvalidOperationException("A blocked file outcome must prevent the SQLite leg.");
                });

            Assert.IsType<PublicationException>(blocked.Failure);
            Assert.Equal(0, fileAttempts);
            Assert.Equal(0, sqliteAttempts);
            return;
        }

        Assert.Equal(DestinationPublicationState.Confirmed, recovered!.FileState);
        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(filePath));
        var expectedSqliteState = stage == "sqlite-committed"
            ? DestinationPublicationState.Confirmed
            : stage is "sqlite-in-progress" or "sqlite-prepared"
                ? DestinationPublicationState.Rejected
                : DestinationPublicationState.Pending;
        Assert.Equal(expectedSqliteState, recovered.SqliteState);
        Assert.Equal(stage == "sqlite-committed" ? 1 : 0, await CountRowsAsync(targetPath, "facts"));

        var filePublishAttempts = 0;
        var sqlitePublishAttempts = 0;
        var retry = await coordinator.PublishAsync(IntentForCrash(stage, filePath, reportBytes, targetPath),
            _ =>
            {
                filePublishAttempts++;
                throw new InvalidOperationException("A digest-confirmed file must not be rewritten.");
            },
            async (prepared, token) =>
            {
                sqlitePublishAttempts++;
                var operationKey = CrashPublicationContract.SqliteOperationKey(stage);
                var dbIntent = CrashPublicationContract.SqliteIntentFingerprint(stage);
                prepared(operationKey, dbIntent);
                return await CommitCrashReceiptOnceAsync(targetPath, operationKey, dbIntent, token);
            });

        Assert.Null(retry.Failure);
        Assert.Equal(0, filePublishAttempts);
        Assert.Equal(stage == "sqlite-committed" ? 0 : 1, sqlitePublishAttempts);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.FileState);
        Assert.Equal(DestinationPublicationState.Confirmed, retry.Record.SqliteState);
        Assert.Equal(1, await CountRowsAsync(targetPath, "facts"));
        Assert.Equal(1, await CountRowsAsync(targetPath, "xms_idempotency_receipts"));
        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task AbruptProcessTerminationAfterAtomicReplace_ReconcilesAndRemovesPriorFileBackup()
    {
        using var workspace = new TemporaryDirectory();
        var statePath = Path.Combine(workspace.Path, "local-state.db");
        var filePath = Path.Combine(workspace.Path, "report.json");
        var runId = "atomic-crash-" + Guid.NewGuid().ToString("N");
        var priorBytes = Encoding.UTF8.GetBytes("prior accepted atomic report");
        var reportBytes = CrashPublicationContract.AtomicReportBytes;
        var backupPath = filePath + $".xbsl-prev-{runId}";
        var stagingPath = filePath + $".xbsl-staging-{runId}";
        await File.WriteAllBytesAsync(filePath, priorBytes);

        var exitCode = await StartCrashHarnessAsync("atomic-after-replace", statePath, filePath, runId);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(filePath));
        Assert.True(File.Exists(backupPath));
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(backupPath));

        var recovery = Assert.Single(new RunJournal(databasePath: statePath).RecoverUnfinalized());
        Assert.Equal(runId, recovery.RunId);
        Assert.Equal(RecoveryDecision.Finalized, recovery.Decision);
        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(filePath));
        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(stagingPath));
        Assert.Empty(new RunJournal(databasePath: statePath).RecoverUnfinalized());
    }

    private static MixedPublicationIntent IntentForCrash(
        string stage, string filePath, byte[] reportBytes, string targetPath)
        => new(CrashPublicationContract.OperationKey(stage), "sha256:mixed-intent-" + stage,
            Path.GetFullPath(filePath), DestinationLock.DigestBytes(reportBytes), reportBytes.LongLength,
            Path.GetFullPath(targetPath), "facts");

    private static async Task<int> StartCrashHarnessAsync(
        string stage, string statePath, string filePath, string targetPath)
    {
        var testAssembly = typeof(MixedPublicationCoordinatorTests).Assembly.Location;
        var runtimeConfig = Path.ChangeExtension(testAssembly, ".runtimeconfig.json");
        var dependencyFile = Path.ChangeExtension(testAssembly, ".deps.json");
        var harnessAssembly = typeof(CrashPublicationContract).Assembly.Location;
        Assert.True(File.Exists(runtimeConfig), $"Test runtime config was not found at {runtimeConfig}.");
        Assert.True(File.Exists(dependencyFile), $"Test dependency file was not found at {dependencyFile}.");
        Assert.True(File.Exists(harnessAssembly), $"Crash harness assembly was not found at {harnessAssembly}.");

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(testAssembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "exec", "--runtimeconfig", runtimeConfig, "--depsfile", dependencyFile,
            harnessAssembly, stage, statePath, filePath, targetPath
        })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the mixed-publication crash harness.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        _ = await stdout;
        _ = await stderr;
        return process.ExitCode;
    }

    private static async Task CreateCrashTargetAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE facts(payload TEXT NOT NULL);
            CREATE TABLE xms_idempotency_receipts(
                operation_key TEXT PRIMARY KEY NOT NULL,
                intent_fingerprint TEXT NOT NULL,
                written_rows INTEGER NOT NULL,
                completed_utc TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqlitePublishReceipt> CommitCrashReceiptOnceAsync(
        string targetPath, string operationKey, string intentFingerprint, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={targetPath};Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT intent_fingerprint,written_rows,completed_utc "
                + "FROM xms_idempotency_receipts WHERE operation_key=$key";
            existing.Parameters.AddWithValue("$key", operationKey);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                Assert.Equal(intentFingerprint, reader.GetString(0));
                var prior = new SqlitePublishReceipt(operationKey, intentFingerprint,
                    reader.GetInt32(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return prior;
            }
        }

        await using (var fact = connection.CreateCommand())
        {
            fact.Transaction = transaction;
            fact.CommandText = "INSERT INTO facts(payload) VALUES('one committed fact batch')";
            await fact.ExecuteNonQueryAsync(cancellationToken);
        }

        var completed = DateTimeOffset.UtcNow;
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT INTO xms_idempotency_receipts "
                + "(operation_key,intent_fingerprint,written_rows,completed_utc) "
                + "VALUES($key,$intent,1,$completed)";
            receipt.Parameters.AddWithValue("$key", operationKey);
            receipt.Parameters.AddWithValue("$intent", intentFingerprint);
            receipt.Parameters.AddWithValue("$completed", completed.ToString("O"));
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SqlitePublishReceipt(operationKey, intentFingerprint, 1, completed);
    }

    private static async Task<int> CountRowsAsync(string path, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
