using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.PublicationCrashHarness;

public static class CrashPublicationContract
{
    public static byte[] ReportBytes => Encoding.UTF8.GetBytes("accepted mixed-publication report");

    public static byte[] AtomicReportBytes => Encoding.UTF8.GetBytes("accepted atomic-publication report");

    public static string OperationKey(string stage) => "mixed-crash-" + stage;

    public static string SqliteOperationKey(string stage) => "sqlite-crash-" + stage;

    public static string SqliteIntentFingerprint(string stage) => "sha256:sqlite-intent-" + stage;
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
            return 2;

        if (StringComparer.Ordinal.Equals(args[0], "atomic-after-replace"))
            return CrashAfterAtomicReplace(args);

        var stage = args[0];
        var statePath = Path.GetFullPath(args[1]);
        var filePath = Path.GetFullPath(args[2]);
        var sqlitePath = Path.GetFullPath(args[3]);
        var bytes = CrashPublicationContract.ReportBytes;
        var digest = DestinationLock.DigestBytes(bytes);
        var intent = new MixedPublicationIntent(
            CrashPublicationContract.OperationKey(stage), "sha256:mixed-intent-" + stage,
            filePath, digest, bytes.LongLength, sqlitePath, "facts");
        var coordinator = new MixedPublicationCoordinator(
            new MixedPublicationStore(new LocalApplicationDatabase(statePath)));

        await coordinator.PublishAsync(intent,
            async token =>
            {
                if (stage == "file-in-progress") KillCurrentProcess();

                await File.WriteAllBytesAsync(filePath, bytes, token);
                if (stage == "file-landed") KillCurrentProcess();

                return new PublicationReceipt("crash-fixture", filePath, digest, digest,
                    "crash-fixture-lock", DateTimeOffset.UtcNow);
            },
            async (prepared, token) =>
            {
                if (stage == "sqlite-in-progress") KillCurrentProcess();

                var operationKey = CrashPublicationContract.SqliteOperationKey(stage);
                var dbIntent = CrashPublicationContract.SqliteIntentFingerprint(stage);
                prepared(operationKey, dbIntent);
                if (stage == "sqlite-prepared") KillCurrentProcess();

                var receipt = await CommitSqliteOnceAsync(sqlitePath, operationKey, dbIntent, token);
                if (stage == "sqlite-committed") KillCurrentProcess();
                return receipt;
            });

        return 0;
    }

    private static int CrashAfterAtomicReplace(string[] args)
    {
        var statePath = Path.GetFullPath(args[1]);
        var filePath = Path.GetFullPath(args[2]);
        var runId = args[3];
        var service = new PublicationService(new RunJournal(databasePath: statePath));
        service.Publish(filePath, CrashPublicationContract.AtomicReportBytes, runId,
            validateStagedArtifactAsync: (stagedPath, _) =>
                Task.FromResult(DestinationLock.DigestFile(stagedPath)),
            afterAtomicReplaceForTest: _ => KillCurrentProcess());
        return 0;
    }

    private static async Task<SqlitePublishReceipt> CommitSqliteOnceAsync(
        string sqlitePath,
        string operationKey,
        string intentFingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
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
                if (!StringComparer.Ordinal.Equals(reader.GetString(0), intentFingerprint))
                    throw new InvalidDataException("The existing SQLite receipt is bound to another intent.");
                var prior = new SqlitePublishReceipt(operationKey, intentFingerprint,
                    reader.GetInt32(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return prior;
            }
        }

        await using (var insertFact = connection.CreateCommand())
        {
            insertFact.Transaction = transaction;
            insertFact.CommandText = "INSERT INTO facts(payload) VALUES($payload)";
            insertFact.Parameters.AddWithValue("$payload", "one committed fact batch");
            await insertFact.ExecuteNonQueryAsync(cancellationToken);
        }

        var completedUtc = DateTimeOffset.UtcNow;
        await using (var insertReceipt = connection.CreateCommand())
        {
            insertReceipt.Transaction = transaction;
            insertReceipt.CommandText = "INSERT INTO xms_idempotency_receipts "
                + "(operation_key,intent_fingerprint,written_rows,completed_utc) "
                + "VALUES($key,$intent,1,$completed)";
            insertReceipt.Parameters.AddWithValue("$key", operationKey);
            insertReceipt.Parameters.AddWithValue("$intent", intentFingerprint);
            insertReceipt.Parameters.AddWithValue("$completed", completedUtc.ToString("O", CultureInfo.InvariantCulture));
            await insertReceipt.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SqlitePublishReceipt(operationKey, intentFingerprint, 1, completedUtc);
    }

    private static void KillCurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("The process termination request returned unexpectedly.");
    }
}
