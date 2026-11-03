using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;

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
}
