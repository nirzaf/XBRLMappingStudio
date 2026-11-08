using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.PublicationCrashHarness;

public static class CrashPublicationContract
{
    public static byte[] ReportBytes => Encoding.UTF8.GetBytes("accepted mixed-publication report");

    public static byte[] AtomicReportBytes => Encoding.UTF8.GetBytes("accepted atomic-publication report");

    public static byte[] JournalReportBytes(string format)
    {
        if (format == "json")
            return Encoding.UTF8.GetBytes("{\"documentInfo\":{\"documentType\":\"https://xbrl.org/2021/xbrl-json\"},\"facts\":{}}");
        if (format == "xml")
            return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><xbrli:xbrl xmlns:xbrli=\"http://www.xbrl.org/2003/instance\"/>");

        var record = new DataRecord
        {
            RowId = "crash-fixture-row-1",
            Fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
            {
                ["entity"] = DataValue.StringValue("Entity-00123-04"),
                ["label"] = DataValue.StringValue("staged, escaped\nrecord"),
                ["amount"] = DataValue.DecimalValue(12.5000m, lexical: "12.5000")
            }
        };
        var records = new[] { record };
        var headers = new[] { "entity", "label", "amount" };
        return format switch
        {
            "csv-v1" or "csv-v2" => new CsvAdapter(new CsvAdapter.Options(EmitBom: false)).WriteRecords(headers, records),
            "xlsx-v1" or "xlsx-v2" => new ExcelOutputAdapter().WriteWorkbook(headers,
                new[] { new object?[] { "Entity-00123-04", "staged, escaped\nrecord", 12.5000m } }),
            "json-record-v1" => new JsonOutputAdapter().Write(records),
            "xml-record-v1" => new XmlOutputAdapter().Write(records),
            _ => throw new ArgumentException("Unsupported journal crash fixture format.", nameof(format))
        };
    }

    public static string OperationKey(string stage) => "mixed-crash-" + stage;

    public static string AtomicRunId(string stage) => "atomic-mixed-" + stage;

    public static string SqliteOperationKey(string stage) => "sqlite-crash-" + stage;

    public static string SqliteIntentFingerprint(string stage) => "sha256:sqlite-intent-" + stage;
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 6 && StringComparer.Ordinal.Equals(args[0], "snapshot-capture"))
            return await PauseSnapshotCaptureAsync(args);

        if (args.Length == 5 && args[0].StartsWith("journal-", StringComparison.Ordinal))
            return CrashAtJournalBoundary(args);

        if (args.Length != 4)
            return 2;

        if (StringComparer.Ordinal.Equals(args[0], "mixed-atomic-after-move"))
            return await CrashMixedAfterAtomicMoveAsync(args);

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

    private static async Task<int> CrashMixedAfterAtomicMoveAsync(string[] args)
    {
        var stage = args[0];
        var statePath = Path.GetFullPath(args[1]);
        var filePath = Path.GetFullPath(args[2]);
        var sqlitePath = Path.GetFullPath(args[3]);
        var bytes = CrashPublicationContract.AtomicReportBytes;
        var digest = DestinationLock.DigestBytes(bytes);
        var intent = new MixedPublicationIntent(
            CrashPublicationContract.OperationKey(stage), "sha256:mixed-intent-" + stage,
            filePath, digest, bytes.LongLength, sqlitePath, "facts");
        var coordinator = new MixedPublicationCoordinator(
            new MixedPublicationStore(new LocalApplicationDatabase(statePath)));
        var service = new PublicationService(new RunJournal(databasePath: statePath));

        await coordinator.PublishAsync(intent,
            token =>
            {
                return Task.FromResult(service.Publish(filePath, bytes,
                    CrashPublicationContract.AtomicRunId(stage),
                    validateStagedArtifactAsync: (stagedPath, _) =>
                        Task.FromResult(DestinationLock.DigestFile(stagedPath)),
                    cancellationToken: token,
                    refuseDifferentExisting: true,
                    afterAtomicReplaceForTest: _ => KillCurrentProcess()));
            },
            async (prepared, token) =>
            {
                var operationKey = CrashPublicationContract.SqliteOperationKey(stage);
                var fingerprint = CrashPublicationContract.SqliteIntentFingerprint(stage);
                prepared(operationKey, fingerprint);
                return await CommitSqliteOnceAsync(sqlitePath, operationKey, fingerprint, token);
            });
        return 0;
    }

    private static async Task<int> PauseSnapshotCaptureAsync(string[] args)
    {
        var boundary = args[3];
        if (!SnapshotCaptureCrashContract.IsSupportedBoundary(boundary)) return 2;

        var source = SnapshotCaptureCrashContract.CreateSource(args[1]);
        var destination = Path.GetFullPath(args[2]);
        var readyFile = Path.GetFullPath(args[4]);
        var releaseFile = Path.GetFullPath(args[5]);
        string? stagingDirectory = null;
        var paused = false;
        void PauseAtBoundary(string observedBoundary, string currentDirectory)
        {
            if (observedBoundary == SnapshotCaptureCrashContract.StagingCreated)
                stagingDirectory = currentDirectory;
            if (paused || !StringComparer.Ordinal.Equals(observedBoundary, boundary)) return;
            paused = true;

            // A same-directory rename makes readiness visible only as a complete
            // document. The parent, never this child, owns abrupt termination.
            var readyTemporary = readyFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(
                    new SnapshotCaptureCrashContract.ReadySignal(observedBoundary, currentDirectory),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                using (var stream = new FileStream(readyTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(readyTemporary, readyFile);
            }
            finally
            {
                if (File.Exists(readyTemporary)) File.Delete(readyTemporary);
            }

            var wait = Stopwatch.StartNew();
            while (!File.Exists(releaseFile))
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(SnapshotCaptureCrashContract.ReleaseTimeoutSeconds))
                    throw new TimeoutException($"Snapshot capture was not released at '{observedBoundary}'.");
                Thread.Sleep(20);
            }
        }

        // Progress<T> may dispatch asynchronously. This inline implementation stops
        // the existing copy loop with its source/target transactions still open.
        IProgress<long>? copiedRows = boundary == SnapshotCaptureCrashContract.MidCopy
            ? new InlineProgress(rows =>
            {
                if (rows == 1)
                    PauseAtBoundary(SnapshotCaptureCrashContract.MidCopy,
                        stagingDirectory ?? throw new InvalidOperationException("Snapshot staging was not observed."));
            })
            : null;
        await SqliteSourceSnapshotStore.CaptureAsync(source, destination, CancellationToken.None,
            copiedRows, PauseAtBoundary);
        if (!paused) throw new InvalidOperationException($"Snapshot capture did not reach '{boundary}'.");
        return 0;
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
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

    private static int CrashAtJournalBoundary(string[] args)
    {
        var stage = args[0];
        if (stage is not ("journal-mid-write" or "journal-after-stage" or "journal-wait-lock" or "journal-before-replace" or "journal-after-replace"))
            return 2;
        var service = new PublicationService(new RunJournal(databasePath: Path.GetFullPath(args[1])));
        service.Publish(Path.GetFullPath(args[2]), CrashPublicationContract.JournalReportBytes(args[4]), args[3],
            beforeReplaceFault: stage == "journal-before-replace" ? _ => KillCurrentProcess() : null,
            lockTimeout: TimeSpan.FromMinutes(2),
            validateStagedArtifactAsync: (path, _) => Task.FromResult(DestinationLock.DigestFile(path)),
            afterAtomicReplaceForTest: stage == "journal-after-replace" ? _ => KillCurrentProcess() : null,
            afterStagedJournalForTest: stage == "journal-after-stage" ? _ => KillCurrentProcess() : null,
            afterPartialStageWriteForTest: stage == "journal-mid-write" ? KillCurrentProcess : null);
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
