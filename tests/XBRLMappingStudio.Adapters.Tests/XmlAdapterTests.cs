using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Tests;

/// <summary>IO06 — XML source: DTD/entity-expansion prohibited, and the same local name
/// in two namespaces stays distinct.</summary>
public sealed class XmlAdapterTests
{
    [Fact]
    public async Task SameLocalName_TwoNamespaces_RemainDistinct()
    {
        using var ws = new TempWorkspace();
        ws.Write("ns.xml",
            "<root>" +
            "<item xmlns:a=\"urn:a\" xmlns:b=\"urn:b\"><a:val>1</a:val><b:val>2</b:val></item>" +
            "<item xmlns:a=\"urn:a\" xmlns:b=\"urn:b\"><a:val>3</a:val><b:val>4</b:val></item>" +
            "</root>");
        var conn = new Adapters.SourceConnection("x", "xml", ws.PathFor("ns.xml"));
        var ad = new XmlAdapter();

        var schema = await ad.InspectSchemaAsync(conn, default);
        var rows = new List<DataRecord>();
        await foreach (var r in ad.ReadFullAsync(conn, schema, default)) rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Contains("[urn:a]val", rows[0].Fields.Keys);
        Assert.Contains("[urn:b]val", rows[0].Fields.Keys);
        Assert.Equal(1m, rows[0]["[urn:a]val"].AsDecimal());
        Assert.Equal(2m, rows[0]["[urn:b]val"].AsDecimal());
        Assert.Equal(4m, rows[1]["[urn:b]val"].AsDecimal());
    }

    [Fact]
    public async Task DoctypeEntityExpansion_IsBlocked()
    {
        using var ws = new TempWorkspace();
        ws.Write("ent.xml",
            "<?xml version=\"1.0\"?><!DOCTYPE foo [ <!ENTITY x \"boom\"> ]>" +
            "<root><r>&x;</r></root>");
        var conn = new Adapters.SourceConnection("x", "xml", ws.PathFor("ent.xml"));
        var ad = new XmlAdapter();
        var ex = await Assert.ThrowsAsync<Adapters.AdapterException>(() => ad.InspectSchemaAsync(conn, default));
        Assert.Equal(DiagnosticCodes.IoUnsafeXmlConstruct, ex.Code);
    }

    [Fact]
    public async Task ExternalEntity_Reference_IsBlocked()
    {
        using var ws = new TempWorkspace();
        ws.Write("ext.xml",
            "<?xml version=\"1.0\"?><!DOCTYPE root [ <!ENTITY % p SYSTEM \"file:///etc/passwd\"> %p; ]>" +
            "<root><r>1</r></root>");
        var conn = new Adapters.SourceConnection("x", "xml", ws.PathFor("ext.xml"));
        var ad = new XmlAdapter();
        await Assert.ThrowsAsync<Adapters.AdapterException>(() => ad.InspectSchemaAsync(conn, default));
    }

    [Theory]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("-0.00000000000000000000000000001")]
    [InlineData("0.00000000000000000000000000005")]
    [InlineData("0.00000000000000000000000000009")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("7.9228162514264337593543950336")]
    [InlineData("8.1234567890123456789012345678")]
    [InlineData("79228162514264337593543950335.1")]
    public async Task InferredNumberThatWouldRound_IsRejectedByEveryRead(string lexical)
    {
        using var ws = new TempWorkspace();
        ws.Write("number.xml", "<root><amount>" + lexical + "</amount></root>");
        var connection = new SourceConnection("xml-source", "xml", ws.PathFor("number.xml"));
        await AssertEveryReadFails(new XmlAdapter(), connection, DiagnosticCodes.IoNumberOutOfBudget);
    }

    public static TheoryData<string, decimal> ExactNumbers => new()
    {
        { "0.0000000000000000000000000001", 0.0000000000000000000000000001m },
        { "-0.0000000000000000000000000001", -0.0000000000000000000000000001m },
        { "+001.2300", 1.23m },
        { ".5", 0.5m },
        { "1.", 1m },
        { "7.9228162514264337593543950335", 7.9228162514264337593543950335m },
        { "79228162514264337593543950335", decimal.MaxValue },
        { "-79228162514264337593543950335", decimal.MinValue },
        { "79228162514264337593543950335.00000", decimal.MaxValue },
        { "0.00000000000000000000000000010", 0.0000000000000000000000000001m },
        { "-0." + new string('0', 300), 0m },
        { "000001.2300" + new string('0', 300), 1.23m }
    };

    [Theory]
    [MemberData(nameof(ExactNumbers))]
    public async Task ExactNumber_PreservesLexicalValueAndSourceOnSampleAndFullReads(string lexical, decimal expected)
    {
        using var ws = new TempWorkspace();
        ws.Write("number.xml", "<root><nested><amount>" + lexical + "</amount></nested></root>");
        var connection = new SourceConnection("xml-source", "xml", ws.PathFor("number.xml"));
        var adapter = new XmlAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        Assert.Equal("nested.amount", Assert.Single(schema.Columns).Name);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        var full = new List<DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(connection, schema, default)) full.Add(row);
        foreach (var row in new[] { Assert.Single(sample.Rows), Assert.Single(full) })
        {
            Assert.Equal("xml-source#r1", row.RowId);
            var value = row["nested.amount"];
            Assert.Equal(PortShape.Decimal, value.Shape);
            Assert.Equal(expected, value.AsDecimal());
            Assert.Equal(lexical, value.LexicalSource);
            var source = new SourceReference("xml-source", "xml#r1:nested.amount");
            Assert.Equal(source, value.Source);
            Assert.NotNull(row.CellSources);
            Assert.Equal(source, row.CellSources["nested.amount"]);
        }
    }

    [Theory]
    [InlineData("1e-29")]
    [InlineData(" 1.23 ")]
    [InlineData(" 0.00000000000000000000000000001 ")]
    [InlineData("1,000.00")]
    [InlineData("123-")]
    [InlineData("(123)")]
    [InlineData("NaN")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("-79228162514264337593543950336")]
    [InlineData("99999999999999999999999999999999999999999999999")]
    public async Task NonInferredNumber_RetainsExistingTextClassification(string lexical)
    {
        using var ws = new TempWorkspace();
        ws.Write("text.xml", "<root><value>" + lexical + "</value></root>");
        var connection = new SourceConnection("xml-source", "xml", ws.PathFor("text.xml"));
        var adapter = new XmlAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var sample = await adapter.ReadSampleAsync(connection, schema, 1, default);
        var value = Assert.Single(sample.Rows)["value"];
        Assert.Equal(PortShape.String, value.Shape);
        Assert.Equal(lexical.Trim(), value.AsString());
        Assert.Null(value.LexicalSource);
    }

    [Theory]
    [InlineData("<root amount='1'><amount>2</amount></root>")]
    [InlineData("<root amount='1'><amount xsi:nil='true'/></root>")]
    [InlineData("<root a.b='1'><a><b>2</b></a></root>")]
    [InlineData("<root><a.b>1</a.b><a><b>2</b></a></root>")]
    [InlineData("<root><a><b>2</b></a><a.b>1</a.b></root>")]
    [InlineData("<root><a.b xsi:nil='true'/><a><b>2</b></a></root>")]
    [InlineData("<root><a><b>2</b></a><a.b xsi:nil='1'/></root>")]
    [InlineData("<root><a.b>1</a.b><a b='2'/></root>")]
    [InlineData("<root><a b='2'/><a.b>1</a.b></root>")]
    [InlineData("<root _text='1'>2</root>")]
    [InlineData("<root _text='1'/>")]
    [InlineData("<root xmlns:a='urn:a' a:value='1'><a:value>2</a:value></root>")]
    public async Task AmbiguousFlattenedFields_AreRejectedByEveryRead(string xml)
    {
        using var ws = new TempWorkspace();
        xml = xml.Replace("<root", "<root xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'", StringComparison.Ordinal);
        ws.Write("collision.xml", xml);
        var connection = new SourceConnection("xml-source", "xml", ws.PathFor("collision.xml"));
        await AssertEveryReadFails(new XmlAdapter(), connection, DiagnosticCodes.IoDuplicateKey);
    }

    [Fact]
    public async Task UnambiguousNamesAndRepeatedRows_KeepNamesAndSourceLocators()
    {
        using var ws = new TempWorkspace();
        ws.Write("rows.xml", "<root xmlns:a='urn:a' xmlns:b='urn:b' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'>" +
            "<row id='01'><a:value>1</a:value><b:value>2</b:value><dotted.name>3</dotted.name><nil xsi:nil='true'/><blank/></row>" +
            "<row id='02'><a:value>4</a:value><b:value>5</b:value><dotted.name>6</dotted.name><nil xsi:nil='1'/><blank/></row></root>");
        var connection = new SourceConnection("xml-source", "xml", ws.PathFor("rows.xml"));
        var adapter = new XmlAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, default);
        var sample = await adapter.ReadSampleAsync(connection, schema, 2, default);
        Assert.Equal(new[] { "id", "[urn:a]value", "[urn:b]value", "dotted.name", "nil", "blank" }, schema.Columns.Select(c => c.Name));
        Assert.Equal(2, sample.Rows.Count);
        for (int index = 0; index < sample.Rows.Count; index++)
        {
            var row = sample.Rows[index];
            Assert.Equal($"xml-source#r{index + 1}", row.RowId);
            Assert.NotNull(row.CellSources);
            foreach (var name in row.Fields.Keys)
                Assert.Equal(new SourceReference("xml-source", $"xml#r{index + 1}:{name}"), row.CellSources[name]);
            Assert.True(row["nil"].IsNil);
            Assert.True(row["blank"].IsMissing);
            Assert.Equal(index == 0 ? 3m : 6m, row["dotted.name"].AsDecimal());
        }
    }

    private static async Task AssertEveryReadFails(XmlAdapter adapter, SourceConnection connection, string code)
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
