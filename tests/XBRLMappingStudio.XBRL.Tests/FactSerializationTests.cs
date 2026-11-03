namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR04 — the golden fact matrix (currency + scale, accuracy/decimals, instant and
/// duration dates, nil, zero, language tag, explicit dimension, and a compound unit
/// <c>USD per share</c>) must survive a round-trip through both xBRL-JSON and xBRL-XML,
/// preserving semantic identity.
/// </summary>
public sealed class FactSerializationTests
{
    private static FilingProfile Profile => SyntheticProfile.Build();

    private static IReadOnlyList<XbrlFact> GoldenFacts() => new[]
    {
        // Monetary duration with scale + accuracy (decimals -3, thousands).
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 1_250_000m,
            Decimals = -3
        },
        // Instant monetary fact.
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Assets"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfInstant(new DateOnly(2025, 12, 31)),
            Unit = new XbrlUnit("USD"),
            Value = 5_000_000m,
            Decimals = 0
        },
        // Zero value (distinct from nil).
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("NetIncome"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("EUR"),
            Value = 0m,
            Decimals = 0
        },
        // Nil fact (no value, decimals INF).
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Expenses"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            IsNil = true
        },
        // Language-tagged fact.
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("USD"),
            Value = 99m,
            Decimals = 0,
            Language = "en"
        },
        // Explicit dimension.
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 500m,
            Decimals = 0,
            Dimensions = new[] { new Dimension(SyntheticProfile.Qualified("Region"), SyntheticProfile.Qualified("EMEA")) }
        },
        // Compound unit: USD per share.
        new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("NetIncome"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            CompoundUnit = new CompoundUnit(new[] { "USD" }, new[] { "shares" }),
            Value = 12.5m,
            Decimals = 1
        }
    };

    [Fact]
    public void OimJson_RoundTrip_PreservesSemanticIdentity()
    {
        var facts = GoldenFacts();
        var json = OimSerializer.Serialize(Profile, facts);
        var reopened = OimSerializer.ReadFacts(json);
        AssertMultisetEquivalent(facts, reopened);
    }

    [Fact]
    public void Xml_RoundTrip_PreservesSemanticIdentity()
    {
        var facts = GoldenFacts();
        var xml = XmlSerializer.Serialize(Profile, facts);
        var reopened = XmlSerializer.ReadFacts(xml);
        AssertMultisetEquivalent(facts, reopened);
    }

    [Fact]
    public void Json_AndXml_AreSemanticallyEquivalentToEachOther()
    {
        var facts = GoldenFacts();
        var fromJson = OimSerializer.ReadFacts(OimSerializer.Serialize(Profile, facts));
        var fromXml = XmlSerializer.ReadFacts(XmlSerializer.Serialize(Profile, facts));
        AssertMultisetEquivalent(fromJson, fromXml);
    }

    [Fact]
    public void CompoundUnit_SerializesAsDivide_AndReadsBack()
    {
        var cu = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("NetIncome"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            CompoundUnit = new CompoundUnit(new[] { "USD" }, new[] { "shares" }),
            Value = 12.5m,
            Decimals = 1
        };
        var json = OimSerializer.Serialize(Profile, new[] { cu });
        Assert.Contains("\"divide\"", json);
        Assert.Contains("numerators", json);
        Assert.Contains("denominators", json);

        var read = OimSerializer.ReadFacts(json).Single();
        Assert.NotNull(read.CompoundUnit);
        Assert.Null(read.Unit);
        Assert.Equal("USD/shares", read.CompoundUnit!.CanonicalKey);
    }

    [Fact]
    public void Dimension_RoundTripsThroughBothEncodings()
    {
        var dim = new Dimension(SyntheticProfile.Qualified("Region"), SyntheticProfile.Qualified("EMEA"));
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 500m,
            Decimals = 0,
            Dimensions = new[] { dim }
        };
        var jsonFact = OimSerializer.ReadFacts(OimSerializer.Serialize(Profile, new[] { fact })).Single();
        var xmlFact = XmlSerializer.ReadFacts(XmlSerializer.Serialize(Profile, new[] { fact })).Single();
        Assert.Equal(dim.ToString(), jsonFact.Dimensions.Single().ToString());
        Assert.Equal(dim.ToString(), xmlFact.Dimensions.Single().ToString());
    }

    [Fact]
    public void NilFact_ReadsBackWithNoValue()
    {
        var nil = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Expenses"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            IsNil = true
        };
        var json = OimSerializer.ReadFacts(OimSerializer.Serialize(Profile, new[] { nil })).Single();
        var xml = XmlSerializer.ReadFacts(XmlSerializer.Serialize(Profile, new[] { nil })).Single();
        Assert.True(json.IsNil);
        Assert.Null(json.Value);
        Assert.True(xml.IsNil);
        Assert.Null(xml.Value);
        // nil is distinguishable from a zero value by the semantic key.
        Assert.Contains("|nil|", nil.SemanticKey());
    }

    private static void AssertMultisetEquivalent(IReadOnlyList<XbrlFact> expected, IReadOnlyList<XbrlFact> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        var a = expected.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal);
        var b = actual.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(a.ToList(), b.ToList());
    }
}
