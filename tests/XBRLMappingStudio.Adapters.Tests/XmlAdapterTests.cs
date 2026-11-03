using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;

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
}
