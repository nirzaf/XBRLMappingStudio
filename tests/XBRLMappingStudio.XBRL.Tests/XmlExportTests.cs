using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR05 — xBRL 2.1 XML instance output. Verifies the document carries the standard
/// <c>xbrli:xbrl</c>/<c>xbrli:context</c>/<c>xbrli:unit</c> roots, reopens into a
/// semantically equivalent fact multiset, and that constructs the model cannot represent
/// block export with a stable <see cref="DiagnosticCodes.XbrUnsupportedConstruct"/> code
/// rather than being silently dropped.
/// </summary>
public sealed class XmlExportTests
{
    private static FilingProfile Profile => SyntheticProfile.Build();

    private static XbrlFact Revenue() => new()
    {
        ConceptName = SyntheticProfile.Qualified("Revenue"),
        EntityScheme = "http://xbrl.org/entity/",
        EntityIdentifier = "ABC-Corp",
        Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
        Unit = new XbrlUnit("QAR"),
        Value = 1_250_000m,
        Decimals = -3
    };

    [Fact]
    public void Serialize_ProducesXbrl21Roots()
    {
        var xml = XmlSerializer.Serialize(Profile, new[] { Revenue() });
        Assert.Contains("<xbrli:xbrl", xml);
        Assert.Contains("<xbrli:context", xml);
        Assert.Contains("<xbrli:unit", xml);
        Assert.Contains("http://www.xbrl.org/2003/instance", xml);
        Assert.Contains(SyntheticProfile.Namespace, xml);
    }

    [Fact]
    public void Serialize_SharedContextsAreDeduplicated()
    {
        // Two facts identical except value share one context and one unit.
        var f1 = Revenue();
        var f2 = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Expenses"),
            EntityScheme = f1.EntityScheme,
            EntityIdentifier = f1.EntityIdentifier,
            Period = f1.Period,
            Unit = f1.Unit,
            Value = 40m,
            Decimals = 0
        };
        var xml = XmlSerializer.Serialize(Profile, new[] { f1, f2 });
        int contextCount = CountOccurrences(xml, "<xbrli:context ");
        int unitCount = CountOccurrences(xml, "<xbrli:unit ");
        Assert.Equal(1, contextCount);
        Assert.Equal(1, unitCount);
    }

    [Fact]
    public void Reopen_IsSemanticallyEquivalentToJson()
    {
        var facts = new[] { Revenue() };
        var viaXml = XmlSerializer.ReadFacts(XmlSerializer.Serialize(Profile, facts));
        var viaJson = OimSerializer.ReadFacts(OimSerializer.Serialize(Profile, facts));
        Assert.Equal(
            viaXml.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal),
            viaJson.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void BothUnitForms_BlockExport_WithUnsupportedConstruct()
    {
        var bad = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            CompoundUnit = new CompoundUnit(new[] { "USD" }, new[] { "shares" }),
            Value = 1m,
            Decimals = 0
        };
        var ex = Assert.Throws<XbrlExportException>(() => XmlSerializer.Serialize(Profile, new[] { bad }));
        Assert.Equal(DiagnosticCodes.XbrUnsupportedConstruct, ex.Code);
    }

    [Fact]
    public void UnqualifiedConcept_BlocksExport_AsTupleLikeConstruct()
    {
        var bad = new XbrlFact
        {
            ConceptName = "Revenue", // no namespace prefix
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 1m,
            Decimals = 0
        };
        var ex = Assert.Throws<XbrlExportException>(() => XmlSerializer.Serialize(Profile, new[] { bad }));
        Assert.Equal(DiagnosticCodes.XbrUnsupportedConstruct, ex.Code);
    }

    [Fact]
    public void ReadFacts_RejectsDtd_Prohibited()
    {
        // A DOCTYPE entity-expansion attempt must be refused by the DTD-prohibited reader.
        const string doc = @"<?xml version=""1.0""?>
<!DOCTYPE xbrl [ <!ENTITY boom ""AAAA""> ]>
<xbrli:xbrl xmlns:xbrli=""http://www.xbrl.org/2003/instance""/>";
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(doc));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
