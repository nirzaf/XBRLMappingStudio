using XBRLMappingStudio.Diagnostics;
using System.Xml.Linq;

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
    private static FilingProfile Profile => TestFilingProfiles.WithNumericCompoundUnitConcept();

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
        Assert.Contains("encoding=\"utf-8\"", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<xbrli:xbrl", xml);
        Assert.Contains("<xbrli:context", xml);
        Assert.Contains("<xbrli:unit", xml);
        Assert.Contains("http://www.xbrl.org/2003/instance", xml);
        Assert.Contains(SyntheticProfile.Namespace, xml);
    }

    [Fact]
    public void WriteLargeReport_StopsAtConfiguredByteBudget()
    {
        using var destination = new MemoryStream();

        var error = Assert.Throws<InvalidDataException>(() => XmlSerializer.WriteLargeReport(
            Profile, new[] { Revenue() }, destination, maxOutputBytes: 16, maxNamespaces: 64));

        Assert.Contains("xBRL-XML report exceeded", error.Message, StringComparison.Ordinal);
        Assert.InRange(destination.Length, 0, 16);
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
        var xml = XmlSerializer.Serialize(Profile, facts);
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        var xmlEndDate = XDocument.Parse(xml).Root!
            .Element(xbrli + "context")!
            .Element(xbrli + "period")!
            .Element(xbrli + "endDate")!
            .Value;
        Assert.Equal("2025-12-31", xmlEndDate);

        var viaXml = XmlSerializer.ReadFacts(xml);
        var viaJson = OimSerializer.ReadFacts(OimSerializer.Serialize(Profile, facts));
        Assert.Equal(
            viaXml.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal),
            viaJson.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new DateOnly(2026, 1, 1), Assert.Single(viaXml).Period.End);
    }

    [Fact]
    public void Reader_RejectsEndDateThatCannotBecomeExclusiveBoundary()
    {
        var xml = XmlSerializer.Serialize(Profile, new[] { Revenue() })
            .Replace("2025-12-31</xbrli:endDate>", "9999-12-31</xbrli:endDate>", StringComparison.Ordinal);

        var error = Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(xml));

        Assert.Equal(DiagnosticCodes.XbrUnsupportedConstruct, error.Code);
    }

    [Fact]
    public void ProfileAwareReader_NormalizesXmlQNameAliasesByNamespaceUri()
    {
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 1_250_000m,
            Decimals = -3,
            Dimensions = new[]
            {
                new Dimension(SyntheticProfile.Qualified("Region"), SyntheticProfile.Qualified("EMEA"))
            }
        };
        var xml = XmlSerializer.Serialize(Profile, new[] { fact })
            .Replace("xmlns:synthetic=", "xmlns:taxonomy=", StringComparison.Ordinal)
            .Replace("<synthetic:Revenue", "<taxonomy:Revenue", StringComparison.Ordinal)
            .Replace("</synthetic:Revenue", "</taxonomy:Revenue", StringComparison.Ordinal)
            .Replace("dimension=\"synthetic:Region\"", "dimension=\"taxonomy:Region\"", StringComparison.Ordinal)
            .Replace(">synthetic:EMEA<", ">taxonomy:EMEA<", StringComparison.Ordinal)
            .Replace("xmlns:iso4217=", "xmlns:currency=", StringComparison.Ordinal)
            .Replace("iso4217:", "currency:", StringComparison.Ordinal);

        var reopened = Assert.Single(XmlSerializer.ReadFacts(xml, Profile));

        Assert.Equal(fact.SemanticKey(), reopened.SemanticKey());
        Assert.Equal(SyntheticProfile.Qualified("Revenue"), reopened.ConceptName);
        Assert.Equal(SyntheticProfile.Qualified("Region"), Assert.Single(reopened.Dimensions).Name);
        Assert.Equal(SyntheticProfile.Qualified("EMEA"), Assert.Single(reopened.Dimensions).MemberName);
        Assert.Equal("QAR", reopened.Unit?.Measure);

        var unknownNamespace = xml.Replace(SyntheticProfile.Namespace, "urn:unapproved:taxonomy",
            StringComparison.Ordinal);
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(unknownNamespace, Profile));
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

    [Fact]
    public void ReadFacts_RejectsMissingContextsNestedFractionsAndUnmodeledSegments()
    {
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        var valid = XmlSerializer.Serialize(Profile, new[] { Revenue() });

        var missingContext = XDocument.Parse(valid);
        missingContext.Root!.Elements(XNamespace.Get(SyntheticProfile.Namespace) + "Revenue").Single()
            .Attribute("contextRef")!.Remove();
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(missingContext.ToString()));

        var unknownContext = XDocument.Parse(valid);
        unknownContext.Root!.Elements(XNamespace.Get(SyntheticProfile.Namespace) + "Revenue").Single()
            .SetAttributeValue("contextRef", "not-present");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(unknownContext.ToString()));

        var fraction = XDocument.Parse(valid);
        var fractionFact = fraction.Root!.Elements(XNamespace.Get(SyntheticProfile.Namespace) + "Revenue").Single();
        fractionFact.RemoveNodes();
        fractionFact.Add(new XElement(xbrli + "numerator", "12"), new XElement(xbrli + "denominator", "3"));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(fraction.ToString()));

        var malformedNumeric = XDocument.Parse(valid);
        malformedNumeric.Root!.Elements(XNamespace.Get(SyntheticProfile.Namespace) + "Revenue").Single().Value = "12x";
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(malformedNumeric.ToString()));

        var dimensionalFact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ACME",
            Period = Revenue().Period,
            Unit = new XbrlUnit("QAR"),
            Value = 1m,
            Decimals = 0,
            Dimensions = new[] { new Dimension("synthetic:Region", "synthetic:EMEA") }
        };
        var unmodeledSegment = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { dimensionalFact }));
        var segment = unmodeledSegment.Root!.Element(xbrli + "context")!
            .Element(xbrli + "entity")!.Element(xbrli + "segment")!;
        segment.Add(new XElement(XNamespace.Get("urn:unsupported:segment") + "Unknown", "extra"));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(unmodeledSegment.ToString()));

        var segmentText = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { dimensionalFact }));
        segmentText.Root!.Element(xbrli + "context")!.Element(xbrli + "entity")!
            .Element(xbrli + "segment")!.Add("unmodeled segment text");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(segmentText.ToString()));

        var nestedMember = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { dimensionalFact }));
        nestedMember.Root!.Element(xbrli + "context")!.Element(xbrli + "entity")!
            .Element(xbrli + "segment")!.Elements().Single()
            .Add(new XElement(XNamespace.Get("urn:unsupported:member") + "Child", "nested"));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(nestedMember.ToString()));

        var scenario = XDocument.Parse(valid);
        scenario.Root!.Element(xbrli + "context")!.Add(new XElement(xbrli + "scenario"));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(scenario.ToString()));
    }

    [Fact]
    public void ReadFacts_RejectsUnknownAndExtraUnitChildren()
    {
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        XNamespace synthetic = SyntheticProfile.Namespace;
        var valid = XmlSerializer.Serialize(Profile, new[] { Revenue() });

        var unknownUnit = XDocument.Parse(valid);
        unknownUnit.Root!.Elements(synthetic + "Revenue").Single().SetAttributeValue("unitRef", "missing");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(unknownUnit.ToString()));

        var productUnit = XDocument.Parse(valid);
        productUnit.Root!.Element(xbrli + "unit")!.Add(new XElement(xbrli + "measure", "iso4217:USD"));
        Assert.Equal("QAR*USD", Assert.Single(XmlSerializer.ReadFacts(productUnit.ToString()))
            .CompoundUnit?.CanonicalKey);

        var unexpectedUnitChild = XDocument.Parse(valid);
        unexpectedUnitChild.Root!.Element(xbrli + "unit")!.Add(new XElement(xbrli + "divide"));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(unexpectedUnitChild.ToString()));

        var compound = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ACME",
            Period = Revenue().Period,
            CompoundUnit = new CompoundUnit(new[] { "USD" }, new[] { "shares" }),
            Value = 1m,
            Decimals = 0
        };
        var extraDivideChild = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { compound }));
        extraDivideChild.Root!.Element(xbrli + "unit")!.Element(xbrli + "divide")!
            .Add(new XElement(xbrli + "measure", "iso4217:QAR"));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(extraDivideChild.ToString()));
    }

    [Fact]
    public void ReadFacts_RejectsUnmodeledContextEntityPeriodAndUnitAttributes()
    {
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        var valid = XmlSerializer.Serialize(Profile, new[] { Revenue() });

        var contextAttribute = XDocument.Parse(valid);
        contextAttribute.Root!.Element(xbrli + "context")!.SetAttributeValue("unexpected", "x");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(contextAttribute.ToString()));

        var entityAttribute = XDocument.Parse(valid);
        entityAttribute.Root!.Element(xbrli + "context")!.Element(xbrli + "entity")!
            .SetAttributeValue("unexpected", "x");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(entityAttribute.ToString()));

        var periodAttribute = XDocument.Parse(valid);
        periodAttribute.Root!.Element(xbrli + "context")!.Element(xbrli + "period")!
            .SetAttributeValue("unexpected", "x");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(periodAttribute.ToString()));

        var unitAttribute = XDocument.Parse(valid);
        unitAttribute.Root!.Element(xbrli + "unit")!.SetAttributeValue("unexpected", "x");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(unitAttribute.ToString()));

        var periodText = XDocument.Parse(valid);
        periodText.Root!.Element(xbrli + "context")!.Element(xbrli + "period")!.Add("unsupported");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(periodText.ToString()));

        var unsupportedPrecision = XDocument.Parse(valid);
        unsupportedPrecision.Root!.Elements(XNamespace.Get(SyntheticProfile.Namespace) + "Revenue").Single()
            .SetAttributeValue("precision", "2");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(unsupportedPrecision.ToString()));
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void ReadFacts_UsesSupportedDecimalsRangeBoundaries(int decimals)
    {
        XNamespace synthetic = SyntheticProfile.Namespace;
        var document = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { Revenue() }));
        document.Root!.Element(synthetic + "Revenue")!.SetAttributeValue("decimals", decimals);

        var reopened = Assert.Single(XmlSerializer.ReadFacts(document.ToString()));

        Assert.Equal(decimals, reopened.Decimals);
    }

    [Theory]
    [InlineData("-31")]
    [InlineData("31")]
    [InlineData("")]
    [InlineData("not-an-integer")]
    [InlineData("2147483648")]
    public void ReadFacts_RejectsInvalidOrUnsupportedDecimals(string decimals)
    {
        XNamespace synthetic = SyntheticProfile.Namespace;
        var document = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { Revenue() }));
        document.Root!.Element(synthetic + "Revenue")!.SetAttributeValue("decimals", decimals);

        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(document.ToString()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("INF")]
    public void ReadFacts_RejectsDecimalsAttributeOnNilFacts(string decimals)
    {
        XNamespace synthetic = SyntheticProfile.Namespace;
        var nil = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Expenses"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = Revenue().Period,
            Unit = new XbrlUnit("QAR"),
            IsNil = true
        };
        var document = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { nil }));
        document.Root!.Element(synthetic + "Expenses")!.SetAttributeValue("decimals", decimals);

        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(document.ToString()));
    }

    [Fact]
    public void ReadFacts_UsesXmlSchemaBooleanLexicalValuesForNil()
    {
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        XNamespace synthetic = SyntheticProfile.Namespace;
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var nilFact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Expenses"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = Revenue().Period,
            Unit = new XbrlUnit("QAR"),
            IsNil = true
        };
        var xml = XmlSerializer.Serialize(Profile, new[] { nilFact });
        var document = XDocument.Parse(xml);
        var element = document.Root!.Elements(synthetic + "Expenses").Single();
        Assert.Equal("true", (string?)element.Attribute(xsi + "nil"));

        element.SetAttributeValue(xsi + "nil", "1");
        Assert.True(Assert.Single(XmlSerializer.ReadFacts(document.ToString())).IsNil);

        element.SetAttributeValue(xsi + "nil", "yes");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(document.ToString()));

        element.SetAttributeValue(xsi + "nil", "true");
        element.Value = "discarded";
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(document.ToString()));

        var legacyNilNamespace = XDocument.Parse(xml);
        legacyNilNamespace.Root!.Elements(synthetic + "Expenses").Single()
            .SetAttributeValue(xbrli + "nil", "true");
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(legacyNilNamespace.ToString()));
    }

    [Fact]
    public void ReadFacts_RequiresAnXbrlInstanceRoot()
    {
        var document = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { Revenue() }));
        document.Root!.Name = "notXbrl";
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(document.ToString()));
    }

    [Fact]
    public void XmlReader_PreservesNumericLookingUnitlessTextAsText()
    {
        var textFact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = Revenue().Period,
            TextValue = "123",
            Language = "en"
        };

        var reopened = Assert.Single(XmlSerializer.ReadFacts(XmlSerializer.Serialize(Profile, new[] { textFact })));

        Assert.Null(reopened.Value);
        Assert.Equal("123", reopened.TextValue);
        Assert.Equal("en", reopened.Language);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
