using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>
/// IO05 generic JSON writing and IO06 generic XML writing. Each fixture verifies
/// the round-trip through the source adapter, and the staging failure seam proves
/// a prior accepted output survives a mid-write fault (guide §4 "Reopen the
/// result with an independent fixture assertion").
/// </summary>
public sealed class JsonAndXmlOutputAdapterTests : IAsyncLifetime
{
    private string _dir = "";

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xbrl-io05-io06-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private string TempPath(string name) => Path.Combine(_dir, name);

    private static DataRecord Record(params (string Key, DataValue Value)[] cells)
    {
        var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
        var srcs = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
        foreach (var (k, v) in cells)
        {
            fields[k] = v;
            srcs[k] = new SourceReference("src", k);
        }
        return new DataRecord { RowId = "r1", Fields = fields, CellSources = srcs };
    }

    // ---------- IO05 JSON writer ----------

    [Fact]
    public async Task JsonWriter_RoundTrips_NestedArrays_EmptyArray_Null_Missing_AndDistinct()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """[{"a":{"b":1},"list":[1,2,3],"strings":["a\"b","c\\d"],"empty":[],"nothing":null,"s":"hi"},{"other":true}]""");
        var source = TempPath("input.json");
        var dest = TempPath("output.json");
        await File.WriteAllBytesAsync(source, bytes);

        var reader = new JsonAdapter();
        var conn = new SourceConnection("src", "json", source);
        var schema = await reader.InspectSchemaAsync(conn, default);
        var records = new List<DataRecord>();
        await foreach (var r in reader.ReadFullAsync(conn, schema, default)) records.Add(r);
        Assert.Equal(2, records.Count);

        var writer = new JsonOutputAdapter();
        var payload = writer.Write(records);
        var outConn = new SourceConnection("dst", "json", dest);
        var staged = await writer.StageArtifactAsync(outConn, payload, ".json", default);
        var identity = await writer.PublishAsync(staged, outConn, default);
        Assert.Equal("json", identity.Kind);

        // Independent reader reopens the artifact: same two rows, same nested paths.
        var reparsed = new List<DataRecord>();
        var outReader = new JsonAdapter();
        var outSchema = await outReader.InspectSchemaAsync(outConn, default);
        await foreach (var r in outReader.ReadFullAsync(outConn, outSchema, default)) reparsed.Add(r);
        Assert.Equal(2, reparsed.Count);
        Assert.Equal("1", reparsed[0].Fields["a.b"].Raw?.ToString());
        Assert.Equal("hi", reparsed[0].Fields["s"].Raw?.ToString());
        Assert.Contains("[1, 2, 3]", reparsed[0].Fields["list"].Raw as string);
        Assert.Contains("a\\\"b", reparsed[0].Fields["strings"].Raw as string);
        Assert.Contains("[]", reparsed[0].Fields["empty"].Raw as string);
        // Explicit null and boolean survive as distinct leaves.
        Assert.True(reparsed[0].Fields["nothing"].IsNil);
        Assert.Equal(true, reparsed[1].Fields["other"].Raw);

        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(dest));
        Assert.Equal(JsonValueKind.Null, json.RootElement[0].GetProperty("nothing").ValueKind);
        Assert.False(json.RootElement[0].TryGetProperty("absent", out _));
    }

    [Fact]
    public async Task JsonWriter_PriorOutputSurvivesMidWriteFault()
    {
        var dest = TempPath("prior.json");
        await File.WriteAllTextAsync(dest, "[{\"sentinel\":\"old\"}]");

        var writer = new JsonOutputAdapter { ReconcileFailProbe = _ => true };
        var records = new[] { Record(("fresh", DataValue.StringValue("new", new SourceReference("s", "c")))) };
        var payload = writer.Write(records);
        var conn = new SourceConnection("dst", "json", dest);
        var staged = await writer.StageArtifactAsync(conn, payload, ".json", default);

        await Assert.ThrowsAnyAsync<IOException>(() => writer.PublishAsync(staged, conn, default));
        // Destination untouched.
        Assert.Equal("[{\"sentinel\":\"old\"}]", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task JsonWriter_DeterministicOrderingAcrossRuns()
    {
        // Two writes of the same record set must yield identical bytes (digest stability).
        var records = new[]
        {
            Record(
                ("a", DataValue.StringValue("1", new SourceReference("s", "a"))),
                ("b", DataValue.StringValue("2", new SourceReference("s", "b"))),
                ("c", DataValue.StringValue("3", new SourceReference("s", "c")))),
        };
        var writer = new JsonOutputAdapter();
        var first = writer.Write(records);
        var second = writer.Write(records);
        Assert.Equal(RunManifest.ComputeSha256(first), RunManifest.ComputeSha256(second));
        // Empty array shape is preserved.
        var empty = writer.Write(Array.Empty<DataRecord>());
        using var doc = JsonDocument.Parse(empty);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public void JsonWriter_OmitsMissing_AndKeepsLiteralJsonTextAsString()
    {
        var writer = new JsonOutputAdapter();
        var record = Record(
            ("missing", DataValue.Missing()),
            ("nil", DataValue.ExplicitNil(PortShape.String)),
            ("literal", DataValue.StringValue("[]")),
            ("integer", DataValue.IntegerValue(7)));
        using var doc = JsonDocument.Parse(writer.Write(new[] { record }));
        Assert.False(doc.RootElement[0].TryGetProperty("missing", out _));
        Assert.Equal(JsonValueKind.Null, doc.RootElement[0].GetProperty("nil").ValueKind);
        Assert.Equal(JsonValueKind.String, doc.RootElement[0].GetProperty("literal").ValueKind);
        Assert.Equal("[]", doc.RootElement[0].GetProperty("literal").GetString());
        Assert.Equal(JsonValueKind.Number, doc.RootElement[0].GetProperty("integer").ValueKind);
        Assert.Equal(7, doc.RootElement[0].GetProperty("integer").GetInt64());
    }

    [Fact]
    public void JsonWriter_SingleObjectModeRefusesMultipleRecordsAndConflictingPaths()
    {
        var single = new JsonOutputAdapter { EmitArray = false };
        Assert.Throws<AdapterException>(() => single.Write(new[]
        {
            Record(("a", DataValue.StringValue("1"))),
            Record(("a", DataValue.StringValue("2")))
        }));

        var conflict = Record(
            ("a", DataValue.StringValue("scalar")),
            ("a.b", DataValue.StringValue("nested")));
        Assert.Throws<AdapterException>(() => new JsonOutputAdapter().Write(new[] { conflict }));
    }

    // ---------- IO06 XML writer ----------

    [Fact]
    public async Task XmlWriter_DistinctNamespacesForSameLocalName_AndProhibitsDtd()
    {
        var record = Record(
            ("{urn:a}Amount", DataValue.StringValue("1.00", new SourceReference("s", "a"))),
            ("{urn:b}Amount", DataValue.StringValue("2.00", new SourceReference("s", "b"))),
            ("Plain", DataValue.StringValue("x", new SourceReference("s", "p"))));
        var writer = new XmlOutputAdapter { RootNamespace = "urn:root" };
        var bytes = writer.Write(new[] { record });
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("urn:a", text);
        Assert.Contains("urn:b", text);
        // Independent reader confirms both Amount elements are preserved distinctly.
        var parsed = System.Xml.Linq.XDocument.Parse(text);
        var amounts = parsed.Descendants().Where(e => e.Name.LocalName == "Amount").ToList();
        Assert.Equal(2, amounts.Count);
        Assert.Equal("urn:a", amounts[0].Name.NamespaceName);
        Assert.Equal("urn:b", amounts[1].Name.NamespaceName);

        var dest = TempPath("output.xml");
        var conn = new SourceConnection("dst", "xml", dest);
        var staged = await writer.StageArtifactAsync(conn, bytes, ".xml", default);
        var identity = await writer.PublishAsync(staged, conn, default);
        Assert.Equal("xml", identity.Kind);
    }

    [Fact]
    public void XmlWriter_MixedContentIsRefusedNotSilentlyFlattened()
    {
        var record = Record(
            ("Note", DataValue.StringValue("<b>bold</b>", new SourceReference("s", "n"))));
        var ex = Assert.Throws<AdapterException>(() => new XmlOutputAdapter().Write(new[] { record }));
        Assert.Equal(DiagnosticCodes.IoUnsafeXmlConstruct, ex.Code);
    }

    [Fact]
    public async Task XmlWriter_PriorOutputSurvivesMidWriteFault()
    {
        var dest = TempPath("prior.xml");
        await File.WriteAllTextAsync(dest, "<rows><row><sentinel>old</sentinel></row></rows>");
        var writer = new XmlOutputAdapter { ReconcileFailProbe = _ => true };
        var record = Record(("k", DataValue.StringValue("v", new SourceReference("s", "k"))));
        var bytes = writer.Write(new[] { record });
        var conn = new SourceConnection("dst", "xml", dest);
        var staged = await writer.StageArtifactAsync(conn, bytes, ".xml", default);

        await Assert.ThrowsAnyAsync<IOException>(() => writer.PublishAsync(staged, conn, default));
        Assert.Contains("sentinel", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task XmlWriter_OmitsMissingAndPreservesNil()
    {
        var dest = TempPath("nil.xml");
        var record = Record(
            ("Absent", DataValue.Missing()),
            ("Nil", DataValue.ExplicitNil(PortShape.String)));
        var writer = new XmlOutputAdapter();
        var bytes = writer.Write(new[] { record });
        var text = Encoding.UTF8.GetString(bytes);
        using var parsed = System.Xml.Linq.XDocument.Parse(text);
        var row = Assert.Single(parsed.Root!.Elements());
        Assert.Null(row.Element("Absent"));
        var nil = row.Element("Nil");
        Assert.NotNull(nil);
        Assert.Equal("true", (string?)nil!.Attribute(System.Xml.Linq.XName.Get(
            "nil", "http://www.w3.org/2001/XMLSchema-instance")));

        var conn = new SourceConnection("dst", "xml", dest);
        var staged = await writer.StageArtifactAsync(conn, bytes, ".xml", default);
        await writer.PublishAsync(staged, conn, default);
        var reader = new XmlAdapter();
        var schema = await reader.InspectSchemaAsync(conn, default);
        var roundTrip = new List<DataRecord>();
        await foreach (var r in reader.ReadFullAsync(conn, schema, default)) roundTrip.Add(r);
        Assert.True(Assert.Single(roundTrip).Fields["Nil"].IsNil);
    }
}
