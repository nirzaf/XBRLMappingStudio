using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR06 — a report is a first-class source: <see cref="XbrlReportAdapter"/> yields one
/// record per fact and preserves repeated facts, several units, and multiple languages as
/// distinct rows. Both xBRL-JSON and xBRL-XML inputs are supported.
/// </summary>
public sealed class ReportImportTests : IDisposable
{
    private static FilingProfile Profile => SyntheticProfile.Build();
    private readonly string _dir;

    public ReportImportTests()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-xbrl-tests", Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static IReadOnlyList<XbrlFact> Report() => new[]
    {
        Revenue("QAR", 1_000_000m, lang: "en"),
        Revenue("USD", 300_000m, lang: "fr"),   // two units
        Revenue("QAR", 1_000_000m, lang: "ar"), // multiple languages
        Revenue("QAR", 250_000m, lang: "en")    // repeated concept, different period
    };

    private static XbrlFact Revenue(string unit, decimal value, string lang) => new()
    {
        ConceptName = SyntheticProfile.Qualified("Revenue"),
        EntityScheme = "http://xbrl.org/entity/",
        EntityIdentifier = "ABC-Corp",
        Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
        Unit = new XbrlUnit(unit),
        Value = value,
        Decimals = -3,
        Language = lang
    };

    private string Write(string name, string content)
    {
        var p = System.IO.Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static async Task<List<DataRecord>> Drain(XbrlReportAdapter adapter, SourceConnection conn)
    {
        var schema = await adapter.InspectSchemaAsync(conn, CancellationToken.None);
        var rows = new List<DataRecord>();
        await foreach (var r in adapter.ReadFullAsync(conn, schema, CancellationToken.None))
            rows.Add(r);
        return rows;
    }

    [Fact]
    public async Task JsonReport_YieldsOneRecordPerFact()
    {
        var path = Write("report.json", OimSerializer.Serialize(Profile, Report()));
        var adapter = new XbrlReportAdapter();
        var conn = new SourceConnection("src", adapter.Kind, path);
        var rows = await Drain(adapter, conn);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(SyntheticProfile.Qualified("Revenue"), r.Fields["concept"].AsString()));

        // Two distinct units preserved across records.
        Assert.Contains(rows, r => r.Fields["unit"].AsString() == "QAR");
        Assert.Contains(rows, r => r.Fields["unit"].AsString() == "USD");

        // Three distinct languages preserved.
        var langs = rows.Select(r => r.Fields["lang"].AsString()).Distinct().ToList();
        Assert.Contains("en", langs);
        Assert.Contains("fr", langs);
        Assert.Contains("ar", langs);
    }

    [Fact]
    public async Task XmlReport_YieldsSameRecordsAsJson()
    {
        var facts = Report();
        var jsonPath = Write("report.json", OimSerializer.Serialize(Profile, facts));
        var xmlPath = Write("report.xml", XmlSerializer.Serialize(Profile, facts));

        var adapter = new XbrlReportAdapter();
        var jsonRows = await Drain(adapter, new SourceConnection("j", adapter.Kind, jsonPath));
        var xmlRows = await Drain(adapter, new SourceConnection("x", adapter.Kind, xmlPath));

        // Same multiset of facts regardless of encoding (compare on the value+unit+lang projection).
        static string[] Proj(IEnumerable<DataRecord> rs) => rs
            .Select(r => $"{r.Fields["unit"].AsString()}|{r.Fields["value"].AsDecimal()}|{r.Fields["lang"].AsString()}")
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(Proj(jsonRows), Proj(xmlRows));
    }

    [Fact]
    public async Task RepeatedFact_PreservedAsDistinctRows()
    {
        // Two identical facts (same aspects) remain two records — report import is lossless.
        var facts = new[] { Revenue("QAR", 1_000_000m, "en"), Revenue("QAR", 1_000_000m, "en") };
        var path = Write("dup.json", OimSerializer.Serialize(Profile, facts));
        var adapter = new XbrlReportAdapter();
        var rows = await Drain(adapter, new SourceConnection("src", adapter.Kind, path));
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].Fields["value"].AsDecimal(), rows[1].Fields["value"].AsDecimal());
    }

    [Fact]
    public async Task CorruptReport_ThrowsAdapterException()
    {
        var path = Write("bad.json", "{ this is not xbrl }");
        var adapter = new XbrlReportAdapter();
        var conn = new SourceConnection("src", adapter.Kind, path);
        var schema = await adapter.InspectSchemaAsync(conn, CancellationToken.None);
        await Assert.ThrowsAsync<AdapterException>(async () =>
        {
            await foreach (var _ in adapter.ReadFullAsync(conn, schema, CancellationToken.None)) { }
        });
    }
}
