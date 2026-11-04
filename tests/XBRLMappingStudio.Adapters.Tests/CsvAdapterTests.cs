using System.Text;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>IO04 — CSV read/write: quoted newline, embedded quote, empty trailing field,
/// BOM policy and the formula-injection guard.</summary>
public sealed class CsvAdapterTests
{
    [Fact]
    public async Task RoundTrips_QuotedNewline_EmbeddedQuote_EmptyTrailingField()
    {
        using var ws = new TempWorkspace();
        var dest = new Adapters.SourceConnection("out", "csv", ws.PathFor("data.csv"));
        var writer = new CsvAdapter(new CsvAdapter.Options(HasHeader: true, EmitBom: false, AllowFormulas: true));

        var headers = new[] { "Name", "Note", "Empty" };
        var records = new IEnumerable<string?>[]
        {
            new string?[] { "a\"b", "line1\nline2", "" },
            new string?[] { "plain", "x", "" },
        };

        var payload = Encoding.UTF8.GetBytes(writer.Format(headers, records));
        var staged = await writer.StageArtifactAsync(dest, payload, ".csv", default);
        await writer.PublishAsync(staged, dest, default);

        var reader = new CsvAdapter(new CsvAdapter.Options(HasHeader: true));
        var conn = new Adapters.SourceConnection("in", "csv", ws.PathFor("data.csv"));
        var schema = await reader.InspectSchemaAsync(conn, default);
        Assert.Equal(new[] { "Name", "Note", "Empty" }, schema.Columns.Select(c => c.Name));

        var rows = new List<DataRecord>();
        await foreach (var r in reader.ReadFullAsync(conn, schema, default)) rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal("a\"b", rows[0]["Name"].AsString());
        Assert.Equal("line1\nline2", rows[0]["Note"].AsString());
        Assert.True(rows[0]["Empty"].IsMissing);   // empty trailing field is missing, not blank
        Assert.Equal("plain", rows[1]["Name"].AsString());
    }

    [Fact]
    public void Write_Guard_PrefixesFormula_WhenFormulasDisallowed()
    {
        var guard = new CsvAdapter(new CsvAdapter.Options(AllowFormulas: false));
        var text = guard.Format(new[] { "v" }, new IEnumerable<string?>[] { new string?[] { "=SUM(1)" } });
        Assert.Contains("'=SUM(1)", text);

        var loose = new CsvAdapter(new CsvAdapter.Options(AllowFormulas: true));
        var allowed = loose.Format(new[] { "v" }, new IEnumerable<string?>[] { new string?[] { "=SUM(1)" } });
        Assert.DoesNotContain("'=SUM(1)", allowed);
        Assert.Contains("=SUM(1)", allowed);
    }

    [Fact]
    public async Task WriteRecords_PreservesNumericLexemesAndExistingOutputOnReconcileFailure()
    {
        using var ws = new TempWorkspace();
        var path = ws.PathFor("records.csv");
        await File.WriteAllTextAsync(path, "accepted prior output");
        var before = await File.ReadAllBytesAsync(path);
        var destination = new Adapters.SourceConnection("out", "csv", path);
        var writer = new CsvAdapter(new CsvAdapter.Options(EmitBom: false, AllowFormulas: false));
        var records = new[]
        {
            new DataRecord
            {
                RowId = "row-1",
                Fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
                {
                    ["value"] = DataValue.DecimalValue(-12.50m),
                    ["account"] = DataValue.StringValue("00123"),
                    ["note"] = DataValue.StringValue("=SUM(1,2)")
                }
            }
        };

        var payload = writer.WriteRecords(new[] { "value", "account", "note" }, records);
        var text = Encoding.UTF8.GetString(payload);
        Assert.Contains("-12.50", text);
        Assert.Contains("00123", text);
        Assert.Contains("'=SUM(1,2)", text);
        Assert.Contains("\"'=SUM(1,2)\"", text);

        var staged = await writer.StageArtifactAsync(destination, payload, ".csv", default);
        writer.ReconcileFailProbe = _ => true;
        await Assert.ThrowsAsync<IOException>(() => writer.PublishAsync(staged, destination, default));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Write_EmitsBom_WhenPolicyIsBom()
    {
        using var ws = new TempWorkspace();
        var dest = new Adapters.SourceConnection("o", "csv", ws.PathFor("bom.csv"));
        var a = new CsvAdapter(new CsvAdapter.Options(HasHeader: true, EmitBom: true));
        var staged = await a.StageArtifactAsync(dest, Encoding.UTF8.GetBytes("x,y\n1,2"), ".csv", default);
        await a.PublishAsync(staged, dest, default);

        var bytes = File.ReadAllBytes(ws.PathFor("bom.csv"));
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public async Task Write_OmitsBom_WhenPolicyIsNoBom()
    {
        using var ws = new TempWorkspace();
        var dest = new Adapters.SourceConnection("o", "csv", ws.PathFor("nobom.csv"));
        var a = new CsvAdapter(new CsvAdapter.Options(HasHeader: true, EmitBom: false));
        var staged = await a.StageArtifactAsync(dest, Encoding.UTF8.GetBytes("x,y\n1,2"), ".csv", default);
        await a.PublishAsync(staged, dest, default);

        var bytes = File.ReadAllBytes(ws.PathFor("nobom.csv"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }
}
