using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class SqliteSourceAdapterTests
{
    [Fact]
    public async Task ReadsRowsWithSchemaAndCellLineageUsingReadOnlyConnection()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("source.db");
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (account TEXT NOT NULL, amount TEXT, active INTEGER); INSERT INTO ledger VALUES ('0007', '123.4500', 1), ('0008', NULL, 0);";
            await command.ExecuteNonQueryAsync();
        }

        var connection = new SourceConnection("ledger-source", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "ledger", ["decimalColumns"] = "amount" });
        var adapter = new SqliteSourceAdapter();
        Assert.Equal(new[] { "ledger" }, await adapter.DiscoverTablesAsync(connection, default));
        var schema = await adapter.InspectSchemaAsync(connection, default);
        Assert.Equal(new[] { "account", "amount", "active" }, schema.Columns.Select(c => c.Name));
        Assert.Equal(PortShape.Decimal, schema.Columns[1].InferredShape);

        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        Assert.Single(sample.Rows);
        Assert.True(sample.IsTruncated);
        Assert.Equal("0007", sample.Rows[0]["account"].AsString());
        Assert.Equal(123.45m, sample.Rows[0]["amount"].AsDecimal());
        Assert.Contains("ledger.amount", sample.Rows[0].CellSources!["amount"].Locator);

        var rows = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(connection, schema, default)) rows.Add(row);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[1]["amount"].IsNil);
        Assert.False(adapter.Capabilities.AllowsWriteTarget);
    }

    [Fact]
    public async Task RejectsFloatingPointFinancialValuesAndTableSqlInjection()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("source.db");
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE ledger (amount REAL); INSERT INTO ledger VALUES (1.25);";
            await command.ExecuteNonQueryAsync();
        }

        var adapter = new SqliteSourceAdapter();
        var connection = new SourceConnection("sqlite", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "ledger" });
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var error = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, default)) { }
        });
        Assert.Equal(DiagnosticCodes.IoNumberOutOfBudget, error.Code);

        var injected = connection with
        {
            Configuration = new Dictionary<string, string> { ["table"] = "ledger; DROP TABLE ledger" }
        };
        var missing = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(injected, default));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, missing.Code);
    }

    [Fact]
    public async Task TargetUpsertsTransactionallyAndUsesIntentBoundReceipt()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("target.db");
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT PRIMARY KEY, amount TEXT);";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        var publisher = new SqliteTargetPublisher();
        var first = await publisher.PublishAsync(destination,
            new[] { Record("007", 123.4500m) }, new[] { "account" }, "op-1", "sha256:intent-a");
        var replay = await publisher.PublishAsync(destination,
            new[] { Record("007", 123.4500m) }, new[] { "account" }, "op-1", "sha256:intent-a");
        Assert.Equal(first, replay);

        foreach (var intent in new[] { "sha256:intent-a", "sha256:intent-b" })
        {
            var conflict = await Assert.ThrowsAsync<AdapterException>(() => publisher.PublishAsync(destination,
                new[] { Record("007", 999m) }, new[] { "account" }, "op-1", intent));
            Assert.Equal(DiagnosticCodes.PubReceiptConflict, conflict.Code);
        }

        await using var read = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await read.OpenAsync();
        await using var query = read.CreateCommand();
        query.CommandText = "SELECT amount FROM report WHERE account=$account";
        query.Parameters.AddWithValue("$account", "007");
        Assert.Equal("123.4500", query.ExecuteScalar());
    }

    [Fact]
    public async Task FailedBatchRollsBackRowsAndReceipt()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("rollback.db");
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT PRIMARY KEY, amount TEXT); CREATE TRIGGER reject_bad BEFORE INSERT ON report WHEN NEW.account='bad' BEGIN SELECT RAISE(ABORT,'rejected'); END;";
            await command.ExecuteNonQueryAsync();
        }
        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTargetPublisher().PublishAsync(destination,
            new[] { Record("good", 1m), Record("bad", 2m) }, new[] { "account" }, "op-rollback", "sha256:intent"));

        await using var read = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await read.OpenAsync();
        await using var check = read.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM report";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='xms_idempotency_receipts'";
        Assert.Equal(0L, check.ExecuteScalar());
    }

    [Fact]
    public async Task TargetRejectsBusinessKeyWithoutUniqueConstraintBeforeWriting()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("non-unique.db");
        await using (var db = new SqliteConnection($"Data Source={path}"))
        {
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE report (account TEXT, amount TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        var destination = new SourceConnection("target", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "report" });
        var error = await Assert.ThrowsAsync<AdapterException>(() => new SqliteTargetPublisher().PublishAsync(destination,
            new[] { Record("007", 1m) }, new[] { "account" }, "op-key-check", "sha256:intent"));
        Assert.Equal(DiagnosticCodes.IoSchemaMismatch, error.Code);
    }

    private static XBRLMappingStudio.Engine.DataRecord Record(string account, decimal amount)
        => new()
        {
            RowId = account,
            Fields = new Dictionary<string, DataValue>
            {
                ["account"] = DataValue.StringValue(account),
                ["amount"] = DataValue.DecimalValue(amount)
            }
        };
}
