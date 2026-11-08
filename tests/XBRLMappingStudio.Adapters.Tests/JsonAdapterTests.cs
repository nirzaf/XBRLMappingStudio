using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>IO05 — JSON object/array shapes and the guide §4 budgets: duplicate keys,
/// oversized depth and out-of-budget numbers.</summary>
public sealed class JsonAdapterTests
{
    [Fact]
    public async Task ArrayOfObjects_ProducesOneRecordPerElement()
    {
        using var ws = new TempWorkspace();
        ws.Write("a.json", "[{\"id\":\"1\",\"amt\":10},{\"id\":\"2\",\"amt\":20}]");
        var conn = new Adapters.SourceConnection("j", "json", ws.PathFor("a.json"));
        var ad = new JsonAdapter();

        var schema = await ad.InspectSchemaAsync(conn, default);
        var rows = new List<DataRecord>();
        await foreach (var r in ad.ReadFullAsync(conn, schema, default)) rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"].AsString());
        Assert.Equal(10m, rows[0]["amt"].AsDecimal());
        Assert.Equal(20m, rows[1]["amt"].AsDecimal());
    }

    [Fact]
    public async Task SingleObject_ProducesOneRecord_FlatteningNestedPaths()
    {
        using var ws = new TempWorkspace();
        ws.Write("o.json", "{\"name\":\"Acme\",\"meta\":{\"region\":\"QA\",\"year\":2025}}");
        var conn = new Adapters.SourceConnection("j", "json", ws.PathFor("o.json"));
        var ad = new JsonAdapter();
        var schema = await ad.InspectSchemaAsync(conn, default);
        var rows = new List<DataRecord>();
        await foreach (var r in ad.ReadFullAsync(conn, schema, default)) rows.Add(r);

        Assert.Single(rows);
        Assert.Equal("Acme", rows[0]["name"].AsString());
        Assert.Equal("QA", rows[0]["meta.region"].AsString());
        Assert.Equal(2025m, rows[0]["meta.year"].AsDecimal());
    }

    [Fact]
    public async Task DuplicateKey_Rejected()
    {
        using var ws = new TempWorkspace();
        ws.Write("d.json", "{\"a\":1,\"a\":2}");
        var conn = new Adapters.SourceConnection("j", "json", ws.PathFor("d.json"));
        var ad = new JsonAdapter();
        var ex = await Assert.ThrowsAsync<Adapters.AdapterException>(() => ad.InspectSchemaAsync(conn, default));
        Assert.Equal(DiagnosticCodes.IoDuplicateKey, ex.Code);
    }

    [Fact]
    public async Task OversizedDepth_Rejected()
    {
        using var ws = new TempWorkspace();
        ws.Write("deep.json", "{\"a\":{\"b\":{\"c\":1}}}");
        var conn = new Adapters.SourceConnection("j", "json", ws.PathFor("deep.json"));
        var ad = new JsonAdapter(new JsonAdapter.Options(MaxDepth: 2));
        var ex = await Assert.ThrowsAsync<Adapters.AdapterException>(() => ad.InspectSchemaAsync(conn, default));
        Assert.Equal(DiagnosticCodes.IoDepthExceeded, ex.Code);
    }

    [Fact]
    public async Task OutOfBudgetNumber_Rejected()
    {
        using var ws = new TempWorkspace();
        ws.Write("big.json", "{\"a\":123456789012345678901234567890123}");
        var conn = new Adapters.SourceConnection("j", "json", ws.PathFor("big.json"));
        var ad = new JsonAdapter();
        var ex = await Assert.ThrowsAsync<Adapters.AdapterException>(() => ad.InspectSchemaAsync(conn, default));
        Assert.Equal(DiagnosticCodes.IoNumberOutOfBudget, ex.Code);
    }

    public static TheoryData<string, int> InexactNumbers => new()
    {
        { "1e-29", 28 },
        { "-1E-29", 28 },
        { "5e-29", 28 },
        { "9e-29", 28 },
        { "0.00000000000000000000000000001", 28 },
        { "1.00000000000000000000000000001", 29 },
        { "7.9228162514264337593543950336", 29 },
        { "8.1234567890123456789012345678", 29 },
        { "79228162514264337593543950336", 29 },
        { "-79228162514264337593543950336", 29 },
        { "1e29", 28 },
        { "1e999999999999999999999999999999999999", 28 },
        { "1e-999999999999999999999999999999999999", 28 },
        { "1.2300", 3 },
        { "1000e-3", 3 },
        { "79228162514264337593543950335", 28 }
    };

    [Theory]
    [MemberData(nameof(InexactNumbers))]
    public async Task InexactOrOverBudgetNumber_IsRejectedByEveryRead(string lexical, int digitBudget)
    {
        using var ws = new TempWorkspace();
        ws.Write("number.json", "{\"amount\":" + lexical + "}");
        var connection = new SourceConnection("json-source", "json", ws.PathFor("number.json"));
        var adapter = new JsonAdapter(new JsonAdapter.Options(MaxNumberDigits: digitBudget));
        await AssertEveryReadFails(adapter, connection, DiagnosticCodes.IoNumberOutOfBudget);
    }

    public static TheoryData<string, decimal, int> ExactNumbers => new()
    {
        { "1e-28", 0.0000000000000000000000000001m, 28 },
        { "-1E-28", -0.0000000000000000000000000001m, 28 },
        { "1.2300E+2", 123m, 28 },
        { "10e-29", 0.0000000000000000000000000001m, 28 },
        { "1000e-31", 0.0000000000000000000000000001m, 28 },
        { "0.0001000e4", 1m, 28 },
        { "1e28", 10000000000000000000000000000m, 28 },
        { "7.9228162514264337593543950335", 7.9228162514264337593543950335m, 29 },
        { "79228162514264337593543950335", decimal.MaxValue, 29 },
        { "-79228162514264337593543950335", decimal.MinValue, 29 },
        { "79228162514264337593543950335.0", decimal.MaxValue, 30 },
        { "0.00000000000000000000000000010", 0.0000000000000000000000000001m, 28 },
        { "-0.00000E-999999999999999999999999999999999999", 0m, 28 },
        { "0e999999999999999999999999999999999999", 0m, 28 },
        { "1e+" + new string('0', 300) + "1", 10m, 28 },
        { "0." + new string('0', 300) + "1e301", 1m, 28 },
        { "1" + new string('0', 300) + "e-300", 1m, 301 }
    };

    [Theory]
    [MemberData(nameof(ExactNumbers))]
    public async Task ExactNumber_PreservesLexicalValueAndSourceOnSampleAndFullReads(
        string lexical, decimal expected, int digitBudget)
    {
        using var ws = new TempWorkspace();
        ws.Write("number.json", "[{\"nested\":{\"amount\":" + lexical + "}}]");
        var connection = new SourceConnection("json-source", "json", ws.PathFor("number.json"));
        var adapter = new JsonAdapter(new JsonAdapter.Options(MaxNumberDigits: digitBudget));
        var schema = await adapter.InspectSchemaAsync(connection, default);
        Assert.Equal("nested.amount", Assert.Single(schema.Columns).Name);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        var full = new List<DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(connection, schema, default)) full.Add(row);
        foreach (var row in new[] { Assert.Single(sample.Rows), Assert.Single(full) })
        {
            Assert.Equal("json-source#r1", row.RowId);
            var value = row["nested.amount"];
            Assert.Equal(PortShape.Decimal, value.Shape);
            Assert.Equal(expected, value.AsDecimal());
            Assert.Equal(lexical, value.LexicalSource);
            var source = new SourceReference("json-source", "json#r1:nested.amount");
            Assert.Equal(source, value.Source);
            Assert.NotNull(row.CellSources);
            Assert.Equal(source, row.CellSources["nested.amount"]);
        }
    }

    [Theory]
    [InlineData("{\"a.b\":1,\"a\":{\"b\":2}}")]
    [InlineData("{\"a\":{\"b\":2},\"a.b\":1}")]
    [InlineData("{\"a.b\":null,\"a\":{\"b\":2}}")]
    [InlineData("{\"a\":{\"b\":null},\"a.b\":1}")]
    [InlineData("{\"a.b\":{},\"a\":{\"b\":[]}}")]
    [InlineData("{\"\":{\"a\":1},\"a\":2}")]
    [InlineData("{\"a\":2,\"\":{\"a\":1}}")]
    [InlineData("{\"a.\":null,\"a\":{\"\":2}}")]
    [InlineData("{\"a\":{\"\":2},\"a.\":null}")]
    [InlineData("{\"..\":1,\".\":{\"\":2}}")]
    [InlineData("{\".\":{\"\":2},\"..\":1}")]
    [InlineData("{\"a.b\":1,\"a\\u002eb\":2}")]
    public async Task AmbiguousFlattenedFields_AreRejectedByEveryRead(string json)
    {
        using var ws = new TempWorkspace();
        ws.Write("collision.json", json);
        var connection = new SourceConnection("json-source", "json", ws.PathFor("collision.json"));
        await AssertEveryReadFails(new JsonAdapter(), connection, DiagnosticCodes.IoDuplicateKey);
    }

    [Fact]
    public async Task UnambiguousSpecialKeys_KeepNamesAndSourceLocators()
    {
        using var ws = new TempWorkspace();
        ws.Write("keys.json", "[{\"\":1,\"a.b\":2,\"a\":{\"c\":3},\"/tilde~\":null,\"values\":[1.20e+1]}]");
        var connection = new SourceConnection("json-source", "json", ws.PathFor("keys.json"));
        var adapter = new JsonAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        var row = Assert.Single(sample.Rows);
        Assert.Equal(new[] { "", "a.b", "a.c", "/tilde~", "values" }, schema.Columns.Select(c => c.Name));
        Assert.NotNull(row.CellSources);
        foreach (var name in row.Fields.Keys)
            Assert.Equal(new SourceReference("json-source", $"json#r1:{name}"), row.CellSources[name]);
        Assert.Equal(1m, row[""].AsDecimal());
        Assert.Equal(2m, row["a.b"].AsDecimal());
        Assert.Equal(3m, row["a.c"].AsDecimal());
        Assert.True(row["/tilde~"].IsNil);
        Assert.Equal("[1.20e+1]", row["values"].AsString());
    }

    [Fact]
    public async Task ExactNumber_RetainsExistingDecimalScale()
    {
        using var ws = new TempWorkspace();
        ws.Write("scale.json", "{\"amount\":1.200}");
        var connection = new SourceConnection("json-source", "json", ws.PathFor("scale.json"));
        var adapter = new JsonAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        Assert.Equal("1.200", Assert.Single(sample.Rows)["amount"].AsString());
    }

    private static async Task AssertEveryReadFails(JsonAdapter adapter, SourceConnection connection, string code)
    {
        var schema = TabularSchema.FromSource(connection.SourceId, Array.Empty<ColumnSchema>());
        var inspect = await Assert.ThrowsAsync<AdapterException>(() => adapter.InspectSchemaAsync(connection, default));
        Assert.Equal(code, inspect.Code);
        var sample = await Assert.ThrowsAsync<AdapterException>(() => adapter.ReadSampleAsync(connection, schema, 1, default));
        Assert.Equal(code, sample.Code);
        var full = await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(connection, schema, default)) { }
        });
        Assert.Equal(code, full.Code);
    }
}
