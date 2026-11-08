using System.Globalization;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class SqliteReceiptReconciliationTests
{
    private const string OperationKey = "op-1";
    private const string Intent = "sha256:intent-a";
    private const string Completed = "2026-11-08T14:15:16.1234567+00:00";
    private const string ReceiptSchema = "CREATE TABLE xms_idempotency_receipts(operation_key TEXT PRIMARY KEY NOT NULL,intent_fingerprint TEXT NOT NULL,written_rows INTEGER NOT NULL,completed_utc TEXT NOT NULL);";

    [Fact]
    public async Task ReconcilesPublishedReceiptWithoutChangingTarget()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("published.db");
        await ExecuteAsync(path, "CREATE TABLE report(account TEXT PRIMARY KEY,amount TEXT);");
        var destination = Destination(path);
        var published = await new SqliteTargetPublisher().PublishAsync(destination,
            new[] { new DataRecord
            {
                RowId = "row-1",
                Fields = new Dictionary<string, DataValue>
                {
                    ["account"] = DataValue.StringValue("account-1"),
                    ["amount"] = DataValue.StringValue("10.00")
                }
            } }, new[] { "account" }, OperationKey, Intent);

        var result = await ReconcileWithoutChangesAsync(path, OperationKey, published.IntentFingerprint);

        Assert.Equal(SqliteReceiptReconciliation.Found, result.State);
        Assert.Equal(published, result.Receipt);
    }

    [Theory]
    [InlineData("2026-11-08T14:15:16.1234567+00:00", 1)]
    [InlineData("2026-11-08 14:15:16+00:00", 2147483647)]
    [InlineData("11/08/2026 14:15:16 +00:00", 2)]
    public async Task AcceptsValidAndLegacyInvariantTimestamps(string completed, int rows)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("legacy.db");
        await ExecuteAsync(path, ReceiptSchema + $"INSERT INTO xms_idempotency_receipts VALUES('{OperationKey}','{Intent}',{rows},'{completed}');");
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = await ReconcileWithoutChangesAsync(path);

            Assert.Equal(SqliteReceiptReconciliation.Found, result.State);
            Assert.Equal(new SqlitePublishReceipt(OperationKey, Intent, rows,
                DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture)), result.Receipt);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Theory]
    [InlineData("BLOB", "1")]
    [InlineData("NUMERIC", "'1'")]
    [InlineData("INTEGER", "'1.0'")]
    public async Task AcceptsGenuineIntegerStorageRegardlessOfDeclaredAffinity(string affinity, string value)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("integer-storage.db");
        await ExecuteAsync(path,
            $"CREATE TABLE xms_idempotency_receipts(operation_key TEXT,intent_fingerprint TEXT,written_rows {affinity},completed_utc TEXT);"
            + $"INSERT INTO xms_idempotency_receipts VALUES('{OperationKey}','{Intent}',{value},'{Completed}');");
        Assert.Equal("integer", await ScalarAsync(path, "SELECT typeof(written_rows) FROM xms_idempotency_receipts"));

        var result = await ReconcileWithoutChangesAsync(path);

        Assert.Equal(SqliteReceiptReconciliation.Found, result.State);
        Assert.Equal(1, Assert.IsType<SqlitePublishReceipt>(result.Receipt).WrittenRows);
    }

    [Theory]
    [InlineData("INTEGER", "0", "integer")]
    [InlineData("INTEGER", "-1", "integer")]
    [InlineData("INTEGER", "2147483648", "integer")]
    [InlineData("INTEGER", "4294967297", "integer")]
    [InlineData("INTEGER", "9223372036854775807", "integer")]
    [InlineData("INTEGER", "-9223372036854775808", "integer")]
    [InlineData("INTEGER", "1.5", "real")]
    [InlineData("INTEGER", "'not-a-number'", "text")]
    [InlineData("INTEGER", "X'31'", "blob")]
    [InlineData("INTEGER", "NULL", "null")]
    [InlineData("TEXT", "1", "text")]
    [InlineData("TEXT", "'1.5'", "text")]
    [InlineData("REAL", "1", "real")]
    [InlineData("BLOB", "'1'", "text")]
    [InlineData("BLOB", "1.0", "real")]
    [InlineData("BLOB", "X'01000000'", "blob")]
    [InlineData("NUMERIC", "'9223372036854775808'", "real")]
    public async Task RejectsMalformedRowCountsWithoutCoercion(string affinity, string value, string storageClass)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("rows.db");
        await ExecuteAsync(path,
            $"CREATE TABLE xms_idempotency_receipts(operation_key TEXT,intent_fingerprint TEXT,written_rows {affinity},completed_utc TEXT);"
            + $"INSERT INTO xms_idempotency_receipts VALUES('{OperationKey}','{Intent}',{value},'{Completed}');");
        Assert.Equal(storageClass, await ScalarAsync(path, "SELECT typeof(written_rows) FROM xms_idempotency_receipts"));

        var result = await ReconcileWithoutChangesAsync(path);

        Assert.Equal(SqliteReceiptReconciliation.IntentConflict, result.State);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData("TEXT", "''", "text")]
    [InlineData("TEXT", "'   '", "text")]
    [InlineData("TEXT", "'not-a-date'", "text")]
    [InlineData("TEXT", "'2026-02-30T00:00:00Z'", "text")]
    [InlineData("TEXT", "'99999-11-08T00:00:00Z'", "text")]
    [InlineData("TEXT", "NULL", "null")]
    [InlineData("TEXT", "CAST('2026-11-08T14:15:16Z' AS BLOB)", "blob")]
    [InlineData("BLOB", "20261108", "integer")]
    [InlineData("BLOB", "20261108.0", "real")]
    public async Task RejectsMalformedCompletionTimesWithoutConversionExceptions(string affinity, string value, string storageClass)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("time.db");
        await ExecuteAsync(path,
            $"CREATE TABLE xms_idempotency_receipts(operation_key TEXT,intent_fingerprint TEXT,written_rows INTEGER,completed_utc {affinity});"
            + $"INSERT INTO xms_idempotency_receipts VALUES('{OperationKey}','{Intent}',1,{value});");
        Assert.Equal(storageClass, await ScalarAsync(path, "SELECT typeof(completed_utc) FROM xms_idempotency_receipts"));

        var result = await ReconcileWithoutChangesAsync(path);

        Assert.Equal(SqliteReceiptReconciliation.IntentConflict, result.State);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("''")]
    [InlineData("'   '")]
    [InlineData("'sha256:intent-b'")]
    [InlineData("'SHA256:INTENT-A'")]
    [InlineData("'sha256:intent-a '")]
    [InlineData("42")]
    [InlineData("42.5")]
    [InlineData("CAST('sha256:intent-a' AS BLOB)")]
    public async Task RejectsInvalidOrNonExactFingerprint(string value)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("intent.db");
        await ExecuteAsync(path,
            "CREATE TABLE xms_idempotency_receipts(operation_key TEXT,intent_fingerprint BLOB,written_rows INTEGER,completed_utc TEXT);"
            + $"INSERT INTO xms_idempotency_receipts VALUES('{OperationKey}',{value},1,'{Completed}');");

        var result = await ReconcileWithoutChangesAsync(path);

        Assert.Equal(SqliteReceiptReconciliation.IntentConflict, result.State);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData("TEXT COLLATE NOCASE", "'OP-1'", "op-1")]
    [InlineData("TEXT COLLATE RTRIM", "'op-1 '", "op-1")]
    [InlineData("INTEGER", "123", "123")]
    [InlineData("REAL", "123.5", "123.5")]
    public async Task RejectsCoercedOrNonExactOperationIdentity(string definition, string value, string requestedKey)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("key.db");
        await ExecuteAsync(path,
            $"CREATE TABLE xms_idempotency_receipts(operation_key {definition},intent_fingerprint TEXT,written_rows INTEGER,completed_utc TEXT);"
            + $"INSERT INTO xms_idempotency_receipts VALUES({value},'{Intent}',1,'{Completed}');");

        var result = await ReconcileWithoutChangesAsync(path, requestedKey);

        Assert.Equal(SqliteReceiptReconciliation.IntentConflict, result.State);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData(null, "sha256:intent-a")]
    [InlineData("", "sha256:intent-a")]
    [InlineData("   ", "sha256:intent-a")]
    [InlineData("op-1", null)]
    [InlineData("op-1", "")]
    [InlineData("op-1", "   ")]
    public async Task RejectsBlankRequestedIdentities(string? key, string? intent)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("blank-identities.db");
        await ExecuteAsync(path, ReceiptSchema);

        var result = await ReconcileWithoutChangesAsync(path, key!, intent!);

        Assert.Equal(SqliteReceiptReconciliation.IntentConflict, result.State);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData("sha256:intent-a")]
    [InlineData("sha256:conflicting-intent")]
    public async Task RejectsDuplicateReceiptsEvenWhenTheFirstRowIsValid(string duplicateIntent)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("duplicates.db");
        await ExecuteAsync(path,
            "CREATE TABLE xms_idempotency_receipts(operation_key TEXT,intent_fingerprint TEXT,written_rows INTEGER,completed_utc TEXT);"
            + $"INSERT INTO xms_idempotency_receipts VALUES('{OperationKey}','{Intent}',1,'{Completed}'),('{OperationKey}','{duplicateIntent}',1,'{Completed}');");

        var result = await ReconcileWithoutChangesAsync(path);

        Assert.Equal(SqliteReceiptReconciliation.IntentConflict, result.State);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData("operation_key TEXT,intent_fingerprint TEXT,written_rows INTEGER")]
    [InlineData("operation_key TEXT,intent_fingerprint TEXT,completed_utc TEXT")]
    [InlineData("operation_key TEXT,written_rows INTEGER,completed_utc TEXT")]
    [InlineData("intent_fingerprint TEXT,written_rows INTEGER,completed_utc TEXT")]
    public async Task RejectsReceiptTablesMissingRequiredColumns(string columns)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("missing-columns.db");
        await ExecuteAsync(path, $"CREATE TABLE xms_idempotency_receipts({columns});");

        var result = await ReconcileWithoutChangesAsync(path);

        Assert.Equal(SqliteReceiptReconciliation.IntentConflict, result.State);
        Assert.Null(result.Receipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReceiptTableOrRowIsNotFoundWithoutCreatingAnything(bool hasReceiptTable)
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("absent-receipt.db");
        await ExecuteAsync(path, "CREATE TABLE report(account TEXT PRIMARY KEY,amount TEXT);"
            + (hasReceiptTable ? ReceiptSchema
                + $"INSERT INTO xms_idempotency_receipts VALUES('different-operation','{Intent}',1,'{Completed}');" : ""));

        var result = await ReconcileWithoutChangesAsync(path);

        Assert.Equal(SqliteReceiptReconciliation.NotFound, result.State);
        Assert.Null(result.Receipt);
    }

    [Fact]
    public async Task MissingTargetIsUnavailableAndIsNeverCreated()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("missing.db");

        var result = await new SqliteTargetPublisher().ReconcileReceiptAsync(Destination(path), OperationKey, Intent);

        Assert.Equal(SqliteReceiptReconciliation.TargetUnavailable, result.State);
        Assert.Null(result.Receipt);
        Assert.Empty(Directory.EnumerateFileSystemEntries(ws.Directory));
    }

    [Fact]
    public async Task CancellationIsNotConvertedToReceiptConflict()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("cancelled.db");
        await ExecuteAsync(path, ReceiptSchema);
        var before = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqliteTargetPublisher().ReconcileReceiptAsync(Destination(path), OperationKey, Intent, cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DatabaseReadFailureRemainsAvailableForCallerClassification()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("damaged.db");
        await File.WriteAllTextAsync(path, "This file is not a SQLite database.");
        var before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<SqliteException>(() =>
            new SqliteTargetPublisher().ReconcileReceiptAsync(Destination(path), OperationKey, Intent));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    private static SourceConnection Destination(string path) => new("target", "sqlite", path,
        new Dictionary<string, string> { ["table"] = "report" });

    private static async Task<(SqliteReceiptReconciliation State, SqlitePublishReceipt? Receipt)> ReconcileWithoutChangesAsync(
        string path, string key = OperationKey, string intent = Intent)
    {
        var before = await File.ReadAllBytesAsync(path);
        var files = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Order(StringComparer.Ordinal).ToArray();

        var result = await new SqliteTargetPublisher().ReconcileReceiptAsync(Destination(path), key, intent);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(files, Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!).Order(StringComparer.Ordinal).ToArray());
        return result;
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
