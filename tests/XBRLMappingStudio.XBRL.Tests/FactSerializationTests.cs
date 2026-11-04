using System.Text.Json;
using System.Text.Json.Nodes;

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
            ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            TextValue = "Revenue reported in English",
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
        using var document = JsonDocument.Parse(json);
        var fact = document.RootElement.GetProperty("facts").EnumerateObject().Single().Value;
        Assert.Equal("iso4217:USD/xbrli:shares", fact.GetProperty("dimensions").GetProperty("unit").GetString());

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

    [Fact]
    public void OimJson_UsesStandardEnvelopeLexicalValuesAndBoundaryDates()
    {
        var fact = GoldenFacts()[0];
        using var document = JsonDocument.Parse(OimSerializer.Serialize(Profile, new[] { fact }));
        var root = document.RootElement;
        Assert.Equal("https://xbrl.org/2021/xbrl-json",
            root.GetProperty("documentInfo").GetProperty("documentType").GetString());
        var output = root.GetProperty("facts").EnumerateObject().Single().Value;
        Assert.Equal("1250000", output.GetProperty("value").GetString());
        Assert.Equal(-3, output.GetProperty("decimals").GetInt32());
        Assert.Equal("2025-01-01T00:00:00/2026-01-01T00:00:00",
            output.GetProperty("dimensions").GetProperty("period").GetString());
        Assert.Equal("iso4217:QAR", output.GetProperty("dimensions").GetProperty("unit").GetString());
        Assert.Equal("synthetic:Revenue", output.GetProperty("dimensions").GetProperty("concept").GetString());
        Assert.DoesNotContain("$schema", root.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("report", root.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OimJson_FactIdsAreContentDerivedAndRepeatedFactsRemainDistinct()
    {
        var facts = new[] { GoldenFacts()[0], GoldenFacts()[1], GoldenFacts()[0] };
        using var first = JsonDocument.Parse(OimSerializer.Serialize(Profile, facts));
        using var reordered = JsonDocument.Parse(OimSerializer.Serialize(Profile, facts.Reverse().ToArray()));
        var firstIds = first.RootElement.GetProperty("facts").EnumerateObject().Select(p => p.Name).Order().ToArray();
        var reorderedIds = reordered.RootElement.GetProperty("facts").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(firstIds, reorderedIds);
        Assert.Equal(3, firstIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OimJson_RejectsLegacyEnvelopeAndDuplicateKeys()
    {
        Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts("""
            { "$schema": "https://xbrl.org/2021/json/xbrl-json-1.0.json", "facts": {} }
            """));
        Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts("""
            { "documentInfo": { "documentType": "https://xbrl.org/2021/xbrl-json", "documentType": "bad", "namespaces": {}, "taxonomy": [] }, "facts": {} }
            """));
    }

    [Fact]
    public void OimJson_ReadsXbrlInternationalExampleIncludingTextFact()
    {
        var facts = OimSerializer.ReadFacts(FixturePaths.Read("xbrl-international-simple-report.json"));
        Assert.Equal(2, facts.Count);
        Assert.Equal(1_230_000m, facts[0].Value);
        Assert.Equal(new DateOnly(2019, 12, 31), facts[0].Period.Instant);
        Assert.Equal("ABC INC", facts[1].TextValue);
        Assert.Equal("en", facts[1].Language);
        Assert.Equal(XbrlPeriod.OfDuration(new DateOnly(2019, 1, 1), new DateOnly(2020, 1, 1)), facts[1].Period);
    }

    [Fact]
    public void OimJson_PreservesTextAndLanguageWithoutInventingDecimals()
    {
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            TextValue = "for year ended",
            Language = "EN"
        };
        var json = OimSerializer.Serialize(Profile, new[] { fact });
        using var document = JsonDocument.Parse(json);
        var output = document.RootElement.GetProperty("facts").EnumerateObject().Single().Value;
        Assert.Equal("for year ended", output.GetProperty("value").GetString());
        Assert.False(output.TryGetProperty("decimals", out _));
        Assert.Equal("en", output.GetProperty("dimensions").GetProperty("language").GetString());
        Assert.Equal(fact.SemanticKey(), OimSerializer.ReadFacts(json).Single().SemanticKey());
    }

    [Fact]
    public void OimJson_MatchesProductFixtureValidatedIndependentlyByArelle()
    {
        var basis = Profile;
        var profile = new FilingProfile
        {
            ProfileId = basis.ProfileId,
            Digest = basis.Digest,
            Concepts = basis.Concepts,
            SupportedUnits = basis.SupportedUnits,
            NamespaceUris = basis.NamespaceUris,
            TaxonomyUrls = new[] { "arelle-test-taxonomy.xsd" },
            PackageHashes = basis.PackageHashes
        };
        var facts = new[]
        {
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
                Unit = new XbrlUnit("USD"),
                Value = 123.45m,
                Decimals = 2
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
                TextValue = "ABC INC",
                Language = "en"
            }
        };

        var expected = JsonNode.Parse(FixturePaths.Read("product-generated-pass.xbrl.json"));
        var actual = JsonNode.Parse(OimSerializer.Serialize(profile, facts));
        Assert.True(JsonNode.DeepEquals(expected, actual), "C# serializer output changed from the Arelle-validated fixture.");
    }

    [Fact]
    public void OimJson_TextThatLooksNumeric_RemainsTextWithoutAUnit()
    {
        var report = """
            {"documentInfo":{"documentType":"https://xbrl.org/2021/xbrl-json","namespaces":{"synthetic":"urn:synthetic:finance:1.0","entity":"urn:entity"},"taxonomy":[]},"facts":{"f1":{"value":"123","dimensions":{"concept":"synthetic:EntityRegistrantName","entity":"entity:ABC","period":"2025-01-01T00:00:00/2026-01-01T00:00:00"}}}}
            """;

        var fact = Assert.Single(OimSerializer.ReadFacts(report));

        Assert.Null(fact.Value);
        Assert.Equal("123", fact.TextValue);
    }

    [Fact]
    public void Serializers_RejectNilFactsThatAlsoCarryAValue()
    {
        var malformed = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("USD"),
            Value = 1m,
            IsNil = true
        };

        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { malformed }));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.Serialize(Profile, new[] { malformed }));
    }

    private static void AssertMultisetEquivalent(IReadOnlyList<XbrlFact> expected, IReadOnlyList<XbrlFact> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        var a = expected.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal);
        var b = actual.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(a.ToList(), b.ToList());
    }
}
