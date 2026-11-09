using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class MixedReceiptRevalidationTests
{
    private const string SqliteOperationKey = "xms-original-committed-operation";
    private const string SqliteFingerprint = "sha256:original-committed-input";
    private const string CompletedUtc = "2026-11-01T12:34:56.0000000+00:00";
    private const int OriginalRows = 3;

    public static IEnumerable<object[]> TargetFailures()
    {
        foreach (var kind in new[] { "xbrl-json", "xbrl-xml" })
        foreach (var entryPoint in new[] { "publish", "reconcile" })
        foreach (var failure in new[]
        {
            "missing-target", "target-is-directory", "unreadable-database", "replacement-database",
            "missing-receipt-table", "missing-receipt", "conflicting-intent", "rows-increased", "rows-decreased"
        })
            yield return new object[] { kind, entryPoint, failure };
    }

    public static IEnumerable<object[]> MalformedReceipts()
    {
        foreach (var kind in new[] { "xbrl-json", "xbrl-xml" })
        foreach (var failure in new[]
        {
            "missing-intent-column", "missing-rows-column", "missing-time-column",
            "null-intent", "blank-intent", "null-rows", "zero-rows", "negative-rows",
            "overflow-rows", "text-rows", "real-rows", "fractional-rows", "blob-rows",
            "null-time", "blank-time", "invalid-time", "integer-time", "blob-time"
        })
            yield return new object[] { kind, failure };
    }

    public static IEnumerable<object[]> InvalidCachedEvidence()
    {
        foreach (var kind in new[] { "xbrl-json", "xbrl-xml" })
        foreach (var entryPoint in new[] { "publish", "reconcile" })
        foreach (var failure in new[]
        {
            "null-key", "empty-key", "blank-key", "null-intent", "empty-intent", "blank-intent",
            "zero-rows", "negative-rows"
        })
            yield return new object[] { kind, entryPoint, failure };
    }

    public static IEnumerable<object[]> SqliteReceiptStateMatrix()
    {
        var priorStates = new (DestinationPublicationState State, string? FailureCode)[]
        {
            (DestinationPublicationState.Pending, null),
            (DestinationPublicationState.InProgress, null),
            (DestinationPublicationState.Rejected, null),
            (DestinationPublicationState.Unknown, DiagnosticCodes.PubPartialFailure),
            (DestinationPublicationState.Unknown, DiagnosticCodes.PubReceiptConflict),
            (DestinationPublicationState.Confirmed, null)
        };
        foreach (var kind in new[] { "xbrl-json", "xbrl-xml" })
        foreach (var (priorState, failureCode) in priorStates)
        foreach (var evidence in new[] { "matching", "missing", "unavailable", "conflicting" })
            yield return new object[] { kind, priorState, failureCode!, evidence };
    }

    [Theory]
    [MemberData(nameof(TargetFailures))]
    public async Task PreviouslyConfirmedTargetFailure_IsStickyAcrossRestartsAndRecoversReadOnly(
        string kind, string entryPoint, string failure)
    {
        using var fixture = await Fixture.CreateAsync(kind);
        await DamageTargetAsync(fixture, failure);
        var before = fixture.DestinationSnapshot();

        await AssertConflictAsync(fixture, entryPoint, OriginalRows);
        Assert.Equal(before, fixture.DestinationSnapshot());

        // Each call uses a new local store, as after a restart. A prior confirmation
        // must not become a never-committed retry just because the receipt vanished.
        await AssertConflictAsync(fixture, "publish", OriginalRows);
        await AssertConflictAsync(fixture, "reconcile", OriginalRows);
        await AssertConflictAsync(fixture, "publish", OriginalRows);
        Assert.Equal(before, fixture.DestinationSnapshot());

        fixture.RestoreOriginalTarget();
        await AssertRecoveredWithoutWritesAsync(fixture);
    }

    [Theory]
    [MemberData(nameof(MalformedReceipts))]
    public async Task MalformedDurableReceipt_CannotConfirmCachedSuccessOrChangeOriginalRows(
        string kind, string failure)
    {
        using var fixture = await Fixture.CreateAsync(kind);
        await WriteMalformedReceiptAsync(fixture.TargetPath, failure);
        var before = fixture.DestinationSnapshot();

        await AssertConflictAsync(fixture, "reconcile", OriginalRows);
        await AssertConflictAsync(fixture, "publish", OriginalRows);
        await AssertConflictAsync(fixture, "reconcile", OriginalRows);
        Assert.Equal(before, fixture.DestinationSnapshot());

        fixture.RestoreOriginalTarget();
        await AssertRecoveredWithoutWritesAsync(fixture);
    }

    [Theory]
    [MemberData(nameof(InvalidCachedEvidence))]
    public async Task IncompleteCachedConfirmation_RemainsConflictWithoutGuessingPreparedEvidence(
        string kind, string entryPoint, string failure)
    {
        using var fixture = await Fixture.CreateAsync(kind);
        var (column, value) = failure switch
        {
            "null-key" => ("sqlite_operation_key", (object?)null),
            "empty-key" => ("sqlite_operation_key", (object?)string.Empty),
            "blank-key" => ("sqlite_operation_key", (object?)" \t "),
            "null-intent" => ("sqlite_intent_fingerprint", (object?)null),
            "empty-intent" => ("sqlite_intent_fingerprint", (object?)string.Empty),
            "blank-intent" => ("sqlite_intent_fingerprint", (object?)" \t "),
            "zero-rows" => ("sqlite_rows_written", (object?)0),
            "negative-rows" => ("sqlite_rows_written", (object?)(-1)),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        await ExecuteAsync(fixture.StatePath,
            $"UPDATE mixed_publication_reconciliation SET {column}=$value WHERE operation_key=$key",
            ("$value", value), ("$key", fixture.Intent.OperationKey));
        var expectedRows = column == "sqlite_rows_written" ? (int?)value : OriginalRows;
        var before = fixture.DestinationSnapshot();

        await AssertConflictAsync(fixture, entryPoint, expectedRows);
        await AssertConflictAsync(fixture, "publish", expectedRows);
        await AssertConflictAsync(fixture, "reconcile", expectedRows);
        Assert.Equal(before, fixture.DestinationSnapshot());

        var persisted = fixture.OpenStore().Find(fixture.Intent.OperationKey)!;
        if (column == "sqlite_operation_key") Assert.Equal((string?)value, persisted.SqliteOperationKey);
        if (column == "sqlite_intent_fingerprint") Assert.Equal((string?)value, persisted.SqliteIntentFingerprint);
    }

    [Theory]
    [InlineData("xbrl-json", false)]
    [InlineData("xbrl-json", true)]
    [InlineData("xbrl-xml", false)]
    [InlineData("xbrl-xml", true)]
    public async Task LegacyConfirmationWithoutCachedCount_UsesOnlyValidDurableReceipt(
        string kind, bool receiptInitiallyMissing)
    {
        using var fixture = await Fixture.CreateAsync(kind);
        fixture.OpenStore().SetSqlite(fixture.Intent.OperationKey, DestinationPublicationState.Confirmed);
        if (receiptInitiallyMissing)
        {
            await ExecuteAsync(fixture.TargetPath, "DELETE FROM xms_idempotency_receipts");
            await AssertConflictAsync(fixture, "publish", expectedRows: null);
            await AssertConflictAsync(fixture, "reconcile", expectedRows: null);
            await AssertConflictAsync(fixture, "publish", expectedRows: null);
            fixture.RestoreOriginalTarget();
        }

        await AssertRecoveredWithoutWritesAsync(fixture);
    }

    [Theory]
    [InlineData("xbrl-json", "missing", DestinationPublicationState.Pending)]
    [InlineData("xbrl-json", "different", DestinationPublicationState.Pending)]
    [InlineData("xbrl-json", "missing", DestinationPublicationState.Rejected)]
    [InlineData("xbrl-json", "different", DestinationPublicationState.Confirmed)]
    [InlineData("xbrl-xml", "missing", DestinationPublicationState.Pending)]
    [InlineData("xbrl-xml", "different", DestinationPublicationState.Pending)]
    [InlineData("xbrl-xml", "missing", DestinationPublicationState.Rejected)]
    [InlineData("xbrl-xml", "different", DestinationPublicationState.Confirmed)]
    public async Task CachedSqliteConfirmation_IsCheckedBeforeEitherDestinationDelegate(
        string kind, string fileCondition, DestinationPublicationState fileState)
    {
        using var fixture = await Fixture.CreateAsync(kind);
        await ExecuteAsync(fixture.TargetPath, "DELETE FROM xms_idempotency_receipts");
        if (fileCondition == "missing") File.Delete(fixture.FilePath);
        else await File.WriteAllTextAsync(fixture.FilePath, "unrelated existing report; do not overwrite");
        fixture.OpenStore().SetFile(fixture.Intent.OperationKey, fileState);
        var before = fixture.DestinationSnapshot();
        var expectedFailureCode = fileState == DestinationPublicationState.Confirmed
            ? DiagnosticCodes.PubPartialFailure : DiagnosticCodes.PubReceiptConflict;

        await AssertConflictAsync(fixture, "publish", OriginalRows, expectedFailureCode: expectedFailureCode);
        await AssertConflictAsync(fixture, "reconcile", OriginalRows);
        await AssertConflictAsync(fixture, "publish", OriginalRows, expectedFailureCode: expectedFailureCode);
        Assert.Equal(before, fixture.DestinationSnapshot());
        if (fileState == DestinationPublicationState.Confirmed)
            Assert.Equal(DestinationPublicationState.Unknown,
                fixture.OpenStore().Find(fixture.Intent.OperationKey)!.FileState);
    }

    [Theory]
    [InlineData("xbrl-json")]
    [InlineData("xbrl-xml")]
    public async Task ValidCachedConfirmation_RechecksDurableReceiptAndIsIdempotentWithoutWrites(string kind)
    {
        using var fixture = await Fixture.CreateAsync(kind);
        await AssertRecoveredWithoutWritesAsync(fixture);
        await AssertRecoveredWithoutWritesAsync(fixture);

        var coordinator = fixture.OpenCoordinator();
        var attempts = new DelegateAttempts();
        var successful = await coordinator.PublishAsync(fixture.Intent,
            attempts.PublishFileAsync, attempts.PublishSqliteAsync);
        Assert.Null(successful.Failure);
        Assert.Equal(0, attempts.FileWrites);
        Assert.Equal(0, attempts.SqliteWrites);

        // This second change proves success was not cached in a coordinator instance
        // or trusted indefinitely after a successful read-only reconciliation.
        await ExecuteAsync(fixture.TargetPath,
            "UPDATE xms_idempotency_receipts SET written_rows=4 WHERE operation_key=$key",
            ("$key", SqliteOperationKey));
        await AssertConflictAsync(fixture, "publish", OriginalRows, coordinator);
        fixture.RestoreOriginalTarget();
        await AssertRecoveredWithoutWritesAsync(fixture);
    }

    [Theory]
    [InlineData("xbrl-json", DestinationPublicationState.Unknown)]
    [InlineData("xbrl-json", DestinationPublicationState.Rejected)]
    [InlineData("xbrl-xml", DestinationPublicationState.Unknown)]
    [InlineData("xbrl-xml", DestinationPublicationState.Rejected)]
    public async Task NeverConfirmedAbsentReceipt_RemainsRetryableAfterReadOnlyReconciliation(
        string kind, DestinationPublicationState priorState)
    {
        using var fixture = await Fixture.CreateAsync(kind, committed: false);
        fixture.OpenStore().SetSqlite(fixture.Intent.OperationKey, priorState,
            failureCode: priorState == DestinationPublicationState.Unknown
                ? DiagnosticCodes.PubPartialFailure : DiagnosticCodes.IoSchemaMismatch);
        var before = fixture.DestinationSnapshot();

        var absent = await fixture.OpenCoordinator().ReconcileAsync(fixture.Intent.OperationKey);

        Assert.NotNull(absent);
        Assert.Equal(DestinationPublicationState.Rejected, absent.SqliteState);
        Assert.Equal("PUB_NOT_COMMITTED", absent.SqliteFailureCode);
        Assert.Null(absent.SqliteRowsWritten);
        Assert.Equal(before, fixture.DestinationSnapshot());

        var fileWrites = 0;
        var sqliteWrites = 0;
        var retried = await fixture.OpenCoordinator().PublishAsync(fixture.Intent,
            _ =>
            {
                fileWrites++;
                throw new InvalidOperationException("The existing report must not be rewritten.");
            },
            async (prepared, cancellationToken) =>
            {
                sqliteWrites++;
                prepared(SqliteOperationKey, SqliteFingerprint);
                await InsertOriginalCommitAsync(fixture.TargetPath, cancellationToken);
                return new SqlitePublishReceipt(SqliteOperationKey, SqliteFingerprint, OriginalRows,
                    DateTimeOffset.Parse(CompletedUtc, CultureInfo.InvariantCulture));
            });

        Assert.Null(retried.Failure);
        Assert.Equal(0, fileWrites);
        Assert.Equal(1, sqliteWrites);
        AssertConfirmed(retried.Record, kind);
        await AssertRecoveredWithoutWritesAsync(fixture);
    }

    [Theory]
    [MemberData(nameof(SqliteReceiptStateMatrix))]
    public async Task ReconciliationUsesDurableReceiptEvidenceAcrossPriorSqliteStates(
        string kind, DestinationPublicationState priorState, string? priorFailureCode, string evidence)
    {
        using var fixture = await Fixture.CreateAsync(kind);
        var protectedReceipt = priorState == DestinationPublicationState.Confirmed
            || (priorState == DestinationPublicationState.Unknown
                && priorFailureCode == DiagnosticCodes.PubReceiptConflict);
        fixture.OpenStore().SetSqlite(fixture.Intent.OperationKey, priorState,
            rowsWritten: priorState == DestinationPublicationState.Confirmed
                || priorFailureCode == DiagnosticCodes.PubReceiptConflict ? OriginalRows : null,
            failureCode: priorFailureCode);

        switch (evidence)
        {
            case "matching":
                break;
            case "missing":
                await ExecuteAsync(fixture.TargetPath, "DELETE FROM xms_idempotency_receipts");
                break;
            case "unavailable":
                File.Delete(fixture.TargetPath);
                break;
            case "conflicting":
                await ExecuteAsync(fixture.TargetPath,
                    "UPDATE xms_idempotency_receipts SET intent_fingerprint='sha256:other-input' WHERE operation_key=$key",
                    ("$key", SqliteOperationKey));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        var reconciled = await fixture.OpenCoordinator().ReconcileAsync(fixture.Intent.OperationKey);

        Assert.NotNull(reconciled);
        Assert.Equal(DestinationPublicationState.Confirmed, reconciled.FileState);
        Assert.Equal(SqliteOperationKey, reconciled.SqliteOperationKey);
        Assert.Equal(SqliteFingerprint, reconciled.SqliteIntentFingerprint);
        if (evidence == "matching")
        {
            Assert.Equal(DestinationPublicationState.Confirmed, reconciled.SqliteState);
            Assert.Null(reconciled.SqliteFailureCode);
            Assert.Equal(OriginalRows, reconciled.SqliteRowsWritten);
        }
        else if (evidence == "missing")
        {
            Assert.Equal(protectedReceipt ? DestinationPublicationState.Unknown : DestinationPublicationState.Rejected,
                reconciled.SqliteState);
            Assert.Equal(protectedReceipt ? DiagnosticCodes.PubReceiptConflict : "PUB_NOT_COMMITTED",
                reconciled.SqliteFailureCode);
            Assert.Equal(protectedReceipt ? OriginalRows : null, reconciled.SqliteRowsWritten);
        }
        else if (evidence == "unavailable")
        {
            Assert.Equal(DestinationPublicationState.Unknown, reconciled.SqliteState);
            Assert.Equal(protectedReceipt ? DiagnosticCodes.PubReceiptConflict : DiagnosticCodes.PubPartialFailure,
                reconciled.SqliteFailureCode);
            Assert.Equal(protectedReceipt ? OriginalRows : null, reconciled.SqliteRowsWritten);
        }
        else
        {
            Assert.Equal(DestinationPublicationState.Unknown, reconciled.SqliteState);
            Assert.Equal(DiagnosticCodes.PubReceiptConflict, reconciled.SqliteFailureCode);
            Assert.Equal(protectedReceipt ? OriginalRows : null, reconciled.SqliteRowsWritten);
        }

        Assert.Equal(reconciled, fixture.OpenStore().Find(fixture.Intent.OperationKey));
    }

    [Theory]
    [InlineData("xbrl-json", "wrong-key")]
    [InlineData("xbrl-json", "wrong-intent")]
    [InlineData("xbrl-json", "zero-rows")]
    [InlineData("xbrl-json", "negative-rows")]
    [InlineData("xbrl-xml", "wrong-key")]
    [InlineData("xbrl-xml", "wrong-intent")]
    [InlineData("xbrl-xml", "zero-rows")]
    [InlineData("xbrl-xml", "negative-rows")]
    public async Task ContradictoryReturnedReceipt_PreservesPreparedIdentityAndBlocksAutomaticRetry(
        string kind, string failure)
    {
        using var fixture = await Fixture.CreateAsync(kind, committed: false);
        var before = fixture.DestinationSnapshot();
        var fileWrites = 0;
        var sqliteAttempts = 0;
        var attempted = await fixture.OpenCoordinator().PublishAsync(fixture.Intent,
            _ =>
            {
                fileWrites++;
                throw new InvalidOperationException("A confirmed report must not be rewritten.");
            },
            (prepared, _) =>
            {
                sqliteAttempts++;
                prepared(SqliteOperationKey, SqliteFingerprint);
                return Task.FromResult(new SqlitePublishReceipt(
                    failure == "wrong-key" ? "unexpected-operation" : SqliteOperationKey,
                    failure == "wrong-intent" ? "sha256:unexpected-input" : SqliteFingerprint,
                    failure == "zero-rows" ? 0 : failure == "negative-rows" ? -1 : OriginalRows,
                    DateTimeOffset.Parse(CompletedUtc, CultureInfo.InvariantCulture)));
            });

        Assert.Equal(0, fileWrites);
        Assert.Equal(1, sqliteAttempts);
        Assert.Equal(DiagnosticCodes.PubReceiptConflict, Assert.IsType<PublicationException>(attempted.Failure).Code);
        Assert.Equal(DestinationPublicationState.Unknown, attempted.Record.SqliteState);
        Assert.Equal(DiagnosticCodes.PubReceiptConflict, attempted.Record.SqliteFailureCode);
        Assert.Null(attempted.Record.SqliteRowsWritten);
        Assert.Equal(SqliteOperationKey, attempted.Record.SqliteOperationKey);
        Assert.Equal(SqliteFingerprint, attempted.Record.SqliteIntentFingerprint);
        Assert.Equal(attempted.Record, fixture.OpenStore().Find(fixture.Intent.OperationKey));

        await AssertConflictAsync(fixture, "publish", expectedRows: null);
        await AssertConflictAsync(fixture, "reconcile", expectedRows: null);
        await AssertConflictAsync(fixture, "publish", expectedRows: null);
        Assert.Equal(before, fixture.DestinationSnapshot());

        // Recovery needs actual durable evidence, not another write attempt.
        await InsertOriginalCommitAsync(fixture.TargetPath, CancellationToken.None);
        await AssertRecoveredWithoutWritesAsync(fixture);
    }

    private static async Task AssertConflictAsync(Fixture fixture, string entryPoint, int? expectedRows,
        MixedPublicationCoordinator? coordinator = null,
        string expectedFailureCode = DiagnosticCodes.PubReceiptConflict)
    {
        var before = fixture.DestinationSnapshot();
        MixedPublicationRecord? record;
        if (entryPoint == "reconcile")
        {
            record = await (coordinator ?? fixture.OpenCoordinator()).ReconcileAsync(fixture.Intent.OperationKey);
        }
        else
        {
            var attempts = new DelegateAttempts();
            var result = await (coordinator ?? fixture.OpenCoordinator()).PublishAsync(fixture.Intent,
                attempts.PublishFileAsync, attempts.PublishSqliteAsync);
            Assert.Equal(0, attempts.FileWrites);
            Assert.Equal(0, attempts.SqliteWrites);
            var failure = Assert.IsType<PublicationException>(result.Failure);
            Assert.Equal(expectedFailureCode, failure.Code);
            record = result.Record;
        }

        Assert.NotNull(record);
        Assert.Equal(DestinationPublicationState.Unknown, record.SqliteState);
        Assert.Equal(DiagnosticCodes.PubReceiptConflict, record.SqliteFailureCode);
        Assert.Equal(expectedRows, record.SqliteRowsWritten);
        Assert.Equal(fixture.Intent.FileKind, record.FileKind);
        var outcome = Assert.Single(record.ToManifestOutcomes(), item => item.Kind == "sqlite");
        Assert.Equal(DestinationPublicationState.Unknown, outcome.State);
        Assert.Equal(DiagnosticCodes.PubReceiptConflict, outcome.FailureCode);
        Assert.Equal(expectedRows, outcome.RowsWritten);
        Assert.Equal(record, fixture.OpenStore().Find(fixture.Intent.OperationKey));
        Assert.Equal(before, fixture.DestinationSnapshot());
    }

    private static async Task AssertRecoveredWithoutWritesAsync(Fixture fixture)
    {
        var before = fixture.DestinationSnapshot();
        var reconciled = await fixture.OpenCoordinator().ReconcileAsync(fixture.Intent.OperationKey);
        Assert.NotNull(reconciled);
        AssertConfirmed(reconciled, fixture.Intent.FileKind);

        var attempts = new DelegateAttempts();
        var result = await fixture.OpenCoordinator().PublishAsync(fixture.Intent,
            attempts.PublishFileAsync, attempts.PublishSqliteAsync);
        Assert.Null(result.Failure);
        Assert.Equal(0, attempts.FileWrites);
        Assert.Equal(0, attempts.SqliteWrites);
        AssertConfirmed(result.Record, fixture.Intent.FileKind);
        Assert.Equal(result.Record, fixture.OpenStore().Find(fixture.Intent.OperationKey));
        Assert.Equal(before, fixture.DestinationSnapshot());
        Assert.Equal(fixture.ReportBytes, await File.ReadAllBytesAsync(fixture.FilePath));

        // Inspect actual rows and receipt values independently of the production
        // receipt reader, so permissive parsing cannot also become the test oracle.
        await using var connection = OpenConnection(fixture.TargetPath, readOnly: true);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM facts";
        Assert.Equal(3L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM xms_idempotency_receipts";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT operation_key,intent_fingerprint,written_rows,completed_utc "
            + "FROM xms_idempotency_receipts";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(SqliteOperationKey, reader.GetString(0));
        Assert.Equal(SqliteFingerprint, reader.GetString(1));
        Assert.Equal(3L, reader.GetInt64(2));
        Assert.Equal(CompletedUtc, reader.GetString(3));
        Assert.False(await reader.ReadAsync());
    }

    private static void AssertConfirmed(MixedPublicationRecord record, string kind)
    {
        Assert.Equal(DestinationPublicationState.Confirmed, record.FileState);
        Assert.Equal(DestinationPublicationState.Confirmed, record.SqliteState);
        Assert.Null(record.SqliteFailureCode);
        Assert.Equal(OriginalRows, record.SqliteRowsWritten);
        Assert.Equal(SqliteOperationKey, record.SqliteOperationKey);
        Assert.Equal(SqliteFingerprint, record.SqliteIntentFingerprint);
        var fileOutcome = Assert.Single(record.ToManifestOutcomes(), item => item.Kind == kind);
        Assert.Equal(DestinationPublicationState.Confirmed, fileOutcome.State);
        Assert.Equal(record.FileDigest, fileOutcome.Digest);
    }

    private static async Task DamageTargetAsync(Fixture fixture, string failure)
    {
        switch (failure)
        {
            case "missing-target":
                File.Delete(fixture.TargetPath);
                break;
            case "target-is-directory":
                File.Delete(fixture.TargetPath);
                Directory.CreateDirectory(fixture.TargetPath);
                break;
            case "unreadable-database":
                await File.WriteAllTextAsync(fixture.TargetPath, "not a SQLite database; never replace these bytes");
                break;
            case "replacement-database":
                File.Delete(fixture.TargetPath);
                await CreateTargetAsync(fixture.TargetPath, committed: true);
                await ExecuteAsync(fixture.TargetPath,
                    "UPDATE xms_idempotency_receipts SET operation_key='another-operation'; "
                    + "UPDATE facts SET payload='replacement database data'");
                break;
            case "missing-receipt-table":
                await ExecuteAsync(fixture.TargetPath, "DROP TABLE xms_idempotency_receipts");
                break;
            case "missing-receipt":
                await ExecuteAsync(fixture.TargetPath, "DELETE FROM xms_idempotency_receipts");
                break;
            case "conflicting-intent":
                await ExecuteAsync(fixture.TargetPath,
                    "UPDATE xms_idempotency_receipts SET intent_fingerprint='sha256:other-input'");
                break;
            case "rows-increased":
                await ExecuteAsync(fixture.TargetPath, "UPDATE xms_idempotency_receipts SET written_rows=4");
                break;
            case "rows-decreased":
                await ExecuteAsync(fixture.TargetPath, "UPDATE xms_idempotency_receipts SET written_rows=2");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    private static async Task WriteMalformedReceiptAsync(string targetPath, string failure)
    {
        // No declared affinities here: values that SQLite stores as TEXT/REAL/BLOB
        // must not be silently coerced into a valid receipt by GetInt32/GetString.
        await ExecuteAsync(targetPath, """
            DROP TABLE xms_idempotency_receipts;
            CREATE TABLE xms_idempotency_receipts(
                operation_key PRIMARY KEY, intent_fingerprint, written_rows, completed_utc);
            INSERT INTO xms_idempotency_receipts VALUES($key,$intent,3,$completed);
            """, ("$key", SqliteOperationKey), ("$intent", SqliteFingerprint), ("$completed", CompletedUtc));
        var sql = failure switch
        {
            "missing-intent-column" => "ALTER TABLE xms_idempotency_receipts DROP COLUMN intent_fingerprint",
            "missing-rows-column" => "ALTER TABLE xms_idempotency_receipts DROP COLUMN written_rows",
            "missing-time-column" => "ALTER TABLE xms_idempotency_receipts DROP COLUMN completed_utc",
            "null-intent" => "UPDATE xms_idempotency_receipts SET intent_fingerprint=NULL",
            "blank-intent" => "UPDATE xms_idempotency_receipts SET intent_fingerprint='   '",
            "null-rows" => "UPDATE xms_idempotency_receipts SET written_rows=NULL",
            "zero-rows" => "UPDATE xms_idempotency_receipts SET written_rows=0",
            "negative-rows" => "UPDATE xms_idempotency_receipts SET written_rows=-1",
            "overflow-rows" => "UPDATE xms_idempotency_receipts SET written_rows=2147483648",
            "text-rows" => "UPDATE xms_idempotency_receipts SET written_rows='3'",
            "real-rows" => "UPDATE xms_idempotency_receipts SET written_rows=3.0",
            "fractional-rows" => "UPDATE xms_idempotency_receipts SET written_rows=3.5",
            "blob-rows" => "UPDATE xms_idempotency_receipts SET written_rows=X'33'",
            "null-time" => "UPDATE xms_idempotency_receipts SET completed_utc=NULL",
            "blank-time" => "UPDATE xms_idempotency_receipts SET completed_utc='   '",
            "invalid-time" => "UPDATE xms_idempotency_receipts SET completed_utc='not-a-timestamp'",
            "integer-time" => "UPDATE xms_idempotency_receipts SET completed_utc=20261101",
            "blob-time" => "UPDATE xms_idempotency_receipts SET completed_utc=CAST(completed_utc AS BLOB)",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        await ExecuteAsync(targetPath, sql);
    }

    private static async Task CreateTargetAsync(string path, bool committed)
    {
        await ExecuteAsync(path, """
            CREATE TABLE facts(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE xms_idempotency_receipts(
                operation_key TEXT PRIMARY KEY NOT NULL,
                intent_fingerprint TEXT NOT NULL,
                written_rows INTEGER NOT NULL,
                completed_utc TEXT NOT NULL);
            """);
        if (committed) await InsertOriginalCommitAsync(path, CancellationToken.None);
    }

    private static async Task InsertOriginalCommitAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(path);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO facts(id,payload) VALUES(1,'first fact'),(2,'second fact'),(3,'third fact');
            INSERT INTO xms_idempotency_receipts(operation_key,intent_fingerprint,written_rows,completed_utc)
            VALUES($key,$intent,3,$completed);
            """;
        command.Parameters.AddWithValue("$key", SqliteOperationKey);
        command.Parameters.AddWithValue("$intent", SqliteFingerprint);
        command.Parameters.AddWithValue("$completed", CompletedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = OpenConnection(path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection OpenConnection(string path, bool readOnly = false)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

    private sealed class DelegateAttempts
    {
        public int FileWrites { get; private set; }
        public int SqliteWrites { get; private set; }

        public Task<PublicationReceipt> PublishFileAsync(CancellationToken cancellationToken)
        {
            FileWrites++;
            throw new InvalidOperationException("Receipt revalidation must never invoke the file publisher.");
        }

        public Task<SqlitePublishReceipt> PublishSqliteAsync(
            Action<string, string> prepared, CancellationToken cancellationToken)
        {
            SqliteWrites++;
            throw new InvalidOperationException("Receipt revalidation must never invoke the SQLite publisher.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "xms-receipt-revalidation-" + Guid.NewGuid().ToString("N"));
        private readonly string _destinations;
        private byte[] _originalTargetBytes = Array.Empty<byte>();

        private Fixture(string kind)
        {
            _destinations = Path.Combine(_root, "destinations");
            Directory.CreateDirectory(_destinations);
            StatePath = Path.Combine(_root, "local-state.db");
            FilePath = Path.Combine(_destinations, kind == "xbrl-json" ? "report.json" : "report.xml");
            TargetPath = Path.Combine(_destinations, "target.db");
            ReportBytes = Encoding.UTF8.GetBytes(kind == "xbrl-json"
                ? "{\"facts\":{\"f1\":{\"value\":\"3\"}}}"
                : "<?xml version=\"1.0\"?><xbrl><fact>3</fact></xbrl>");
            var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(ReportBytes)).ToLowerInvariant();
            Intent = new MixedPublicationIntent("mix-" + Guid.NewGuid().ToString("N"),
                "sha256:mixed-original-intent", FilePath, digest, ReportBytes.LongLength, TargetPath, "facts", kind);
        }

        public string StatePath { get; }
        public string FilePath { get; }
        public string TargetPath { get; }
        public byte[] ReportBytes { get; }
        public MixedPublicationIntent Intent { get; }

        public static async Task<Fixture> CreateAsync(string kind, bool committed = true)
        {
            var fixture = new Fixture(kind);
            try
            {
                await File.WriteAllBytesAsync(fixture.FilePath, fixture.ReportBytes);
                await CreateTargetAsync(fixture.TargetPath, committed);
                fixture._originalTargetBytes = await File.ReadAllBytesAsync(fixture.TargetPath);
                var store = fixture.OpenStore();
                store.GetOrCreate(fixture.Intent);
                store.SetFile(fixture.Intent.OperationKey, DestinationPublicationState.Confirmed);
                store.SetSqlitePrepared(fixture.Intent.OperationKey, SqliteOperationKey, SqliteFingerprint);
                if (committed)
                    store.SetSqlite(fixture.Intent.OperationKey, DestinationPublicationState.Confirmed, OriginalRows);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public MixedPublicationStore OpenStore() => new(new LocalApplicationDatabase(StatePath));
        public MixedPublicationCoordinator OpenCoordinator() => new(OpenStore());

        public string[] DestinationSnapshot()
            => Directory.EnumerateDirectories(_destinations, "*", SearchOption.AllDirectories)
                .Select(path => "directory:" + Path.GetRelativePath(_destinations, path))
                .Concat(Directory.EnumerateFiles(_destinations, "*", SearchOption.AllDirectories)
                    .Select(path => "file:" + Path.GetRelativePath(_destinations, path) + ":"
                        + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
                .Order(StringComparer.Ordinal).ToArray();

        public void RestoreOriginalTarget()
        {
            if (Directory.Exists(TargetPath)) Directory.Delete(TargetPath);
            File.WriteAllBytes(TargetPath, _originalTargetBytes);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
