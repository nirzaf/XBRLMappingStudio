using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Diagnostics;
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
}
