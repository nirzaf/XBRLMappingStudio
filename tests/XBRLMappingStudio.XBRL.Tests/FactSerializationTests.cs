using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>
/// XBR04 — the golden fact matrix (currency + scale, accuracy/decimals, instant and
/// duration dates, nil, zero, language tag, explicit dimension, and a compound unit
/// <c>USD per share</c>) must survive a round-trip through both xBRL-JSON and xBRL-XML,
/// preserving semantic identity.
/// </summary>
public sealed class FactSerializationTests
{
    private static FilingProfile Profile => TestFilingProfiles.WithNumericCompoundUnitConcept();

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
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
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
    public void OimJson_FactLocatorsAreStableAndDistinctForDuplicateOccurrences()
    {
        var template = GoldenFacts()[0];
        XbrlFact Copy() => new()
        {
            ConceptName = template.ConceptName,
            EntityScheme = template.EntityScheme,
            EntityIdentifier = template.EntityIdentifier,
            Period = template.Period,
            Unit = template.Unit,
            Value = template.Value,
            Decimals = template.Decimals
        };
        var facts = new[] { Copy(), Copy() };

        var json = OimSerializer.Serialize(Profile, facts);
        var entries = OimSerializer.ReadFactEntries(json);
        var locators = OimSerializer.GetFactLocators(facts);
        var locatorMap = OimSerializer.GetFactLocatorMap(facts);

        Assert.Equal(2, locators.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(locators, OimSerializer.GetFactLocators(facts));
        Assert.Equal(locators, entries.Select(entry => entry.Locator));
        Assert.Equal(2, locatorMap.Count);

        var reversed = facts.Reverse().ToArray();
        var reversedLocators = OimSerializer.GetFactLocators(reversed);
        Assert.Equal(locators.Order(StringComparer.Ordinal), reversedLocators.Order(StringComparer.Ordinal));
        Assert.Equal(reversedLocators,
            OimSerializer.ReadFactEntries(OimSerializer.Serialize(Profile, reversed))
                .Select(entry => entry.Locator));
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
    public void JsonAndXml_PreserveIndependentOccurrenceOracleAndExactNumericLexemes()
    {
        var period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var repeatedRevenue = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ACME-01",
            Period = period,
            Unit = new XbrlUnit("QAR"),
            Value = 1.2300m,
            Decimals = 4,
            Dimensions = new[] { new Dimension("synthetic:Region", "synthetic:EMEA") }
        };
        var facts = new[]
        {
            repeatedRevenue,
            repeatedRevenue,
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "ACME-01",
                Period = period,
                TextValue = "Revenue | statement: Q1",
                Language = "en"
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("NetIncome"),
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "ACME-01",
                Period = period,
                Unit = new XbrlUnit("EUR"),
                Value = 0m,
                Decimals = 0
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Expenses"),
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "ACME-01",
                Period = period,
                Unit = new XbrlUnit("QAR"),
                IsNil = true
            },
            new XbrlFact
            {
                ConceptName = TestFilingProfiles.UnitsPerShareConcept,
                EntityScheme = "http://xbrl.org/entity/",
                EntityIdentifier = "ACME-01",
                Period = period,
                CompoundUnit = new CompoundUnit(new[] { "USD" }, new[] { "shares" }),
                Value = 12.5000m,
                Decimals = 4
            }
        };

        var expected = new[]
        {
            new FactOccurrence("synthetic:Revenue", "http://xbrl.org/entity/", "ACME-01",
                "2025-01-01/2026-01-01", "QAR", 1, null, "1.2300", false, 4, null,
                new[] { "synthetic:Region=synthetic:EMEA:True" }),
            new FactOccurrence("synthetic:Revenue", "http://xbrl.org/entity/", "ACME-01",
                "2025-01-01/2026-01-01", "QAR", 1, null, "1.2300", false, 4, null,
                new[] { "synthetic:Region=synthetic:EMEA:True" }),
            new FactOccurrence("synthetic:EntityRegistrantName", "http://xbrl.org/entity/", "ACME-01",
                "2025-01-01/2026-01-01", null, null, null, "Revenue | statement: Q1", false,
                null, "en", Array.Empty<string>()),
            new FactOccurrence("synthetic:NetIncome", "http://xbrl.org/entity/", "ACME-01",
                "2025-01-01/2026-01-01", "EUR", 1, null, "0", false, 0, null,
                Array.Empty<string>()),
            new FactOccurrence("synthetic:Expenses", "http://xbrl.org/entity/", "ACME-01",
                "2025-01-01/2026-01-01", "QAR", 1, null, null, true, null, null,
                Array.Empty<string>()),
            new FactOccurrence(TestFilingProfiles.UnitsPerShareConcept, "http://xbrl.org/entity/", "ACME-01",
                "2025-01-01/2026-01-01", null, null, "USD/shares", "12.5000", false, 4,
                null, Array.Empty<string>())
        };

        var json = OimSerializer.Serialize(Profile, facts);
        var xml = XmlSerializer.Serialize(Profile, facts);
        var fromJson = OimSerializer.ReadFacts(json);
        var fromXml = XmlSerializer.ReadFacts(xml);

        Assert.Equal(OccurrenceKeys(expected), OccurrenceKeys(fromJson.Select(ToOccurrence)));
        Assert.Equal(OccurrenceKeys(expected), OccurrenceKeys(fromXml.Select(ToOccurrence)));

        using var jsonDocument = JsonDocument.Parse(json);
        var jsonRevenueOccurrences = jsonDocument.RootElement.GetProperty("facts").EnumerateObject()
            .Where(property => property.Value.GetProperty("dimensions").GetProperty("concept").GetString()
                == "synthetic:Revenue")
            .ToArray();
        Assert.Equal(2, jsonRevenueOccurrences.Length);
        Assert.Equal(2, jsonRevenueOccurrences.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var occurrence in jsonRevenueOccurrences)
        {
            Assert.Equal("1.2300", occurrence.Value.GetProperty("value").GetString());
            Assert.Equal(4, occurrence.Value.GetProperty("decimals").GetInt32());
            Assert.Equal("iso4217:QAR", occurrence.Value.GetProperty("dimensions").GetProperty("unit").GetString());
            Assert.Equal("synthetic:EMEA", occurrence.Value.GetProperty("dimensions").GetProperty("synthetic:Region").GetString());
        }

        XNamespace synthetic = SyntheticProfile.Namespace;
        var xmlRoot = XDocument.Parse(xml).Root!;
        var xmlRevenueOccurrences = xmlRoot.Elements(synthetic + "Revenue")
            .Where(element => (string?)element.Attribute("decimals") == "4")
            .ToArray();
        Assert.Equal(2, xmlRevenueOccurrences.Length);
        Assert.All(xmlRevenueOccurrences, element => Assert.Equal("1.2300", element.Value));
        var xmlText = Assert.Single(xmlRoot.Elements(synthetic + "EntityRegistrantName"));
        Assert.Equal("en", (string?)xmlText.Attribute(XNamespace.Xml + "lang"));
        Assert.Equal("Revenue | statement: Q1", xmlText.Value);
        var xmlNil = Assert.Single(xmlRoot.Elements(synthetic + "Expenses"));
        Assert.Equal("true", (string?)xmlNil.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "nil"));
    }

    [Fact]
    public void SemanticKey_DistinguishesEntitySchemeAndIdentifierBoundaries()
    {
        var valid = GoldenFacts()[0];
        var first = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = "urn:foo:bar",
            EntityIdentifier = "baz",
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };
        var second = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = "urn:foo",
            EntityIdentifier = "bar:baz",
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };

        Assert.NotEqual(first.SemanticKey(), second.SemanticKey());

        var fromXml = XmlSerializer.ReadFacts(XmlSerializer.Serialize(Profile, new[] { first, second }));
        Assert.Equal(2, fromXml.Count);
        Assert.Equal(new[] { first.SemanticKey(), second.SemanticKey() }.Order(StringComparer.Ordinal),
            fromXml.Select(fact => fact.SemanticKey()).Order(StringComparer.Ordinal));
        var fromJson = OimSerializer.ReadFacts(OimSerializer.Serialize(Profile, new[] { first, second }));
        Assert.Equal(new[] { first.SemanticKey(), second.SemanticKey() }.Order(StringComparer.Ordinal),
            fromJson.Select(fact => fact.SemanticKey()).Order(StringComparer.Ordinal));

        var unitWithCaret = new XbrlFact
        {
            ConceptName = first.ConceptName,
            EntityScheme = first.EntityScheme,
            EntityIdentifier = first.EntityIdentifier,
            Period = first.Period,
            Unit = new XbrlUnit("USD^2"),
            Value = first.Value,
            Decimals = first.Decimals
        };
        var poweredUnit = new XbrlFact
        {
            ConceptName = first.ConceptName,
            EntityScheme = first.EntityScheme,
            EntityIdentifier = first.EntityIdentifier,
            Period = first.Period,
            Unit = new XbrlUnit("USD", 2),
            Value = first.Value,
            Decimals = first.Decimals
        };
        Assert.NotEqual(unitWithCaret.SemanticKey(), poweredUnit.SemanticKey());
    }

    [Fact]
    public void OimJson_RejectsMalformedOrWhitespaceEntityLocalParts()
    {
        var valid = GoldenFacts()[0];
        var whitespaceIdentifier = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = valid.EntityScheme,
            EntityIdentifier = "ACME INC",
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { whitespaceIdentifier }));

        var json = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { valid }))!;
        var facts = json["facts"]!.AsObject();
        facts.Single().Value!["dimensions"]!["entity"] = "entity:";
        Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(json.ToJsonString()));
    }

    [Fact]
    public void XmlSerializer_DoesNotMergeContextsAtEmbeddedSeparatorBoundaries()
    {
        var valid = GoldenFacts()[0];
        var separator = "\u241F";
        var first = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = "urn:foo" + separator + "bar",
            EntityIdentifier = "baz",
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };
        var second = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = "urn:foo",
            EntityIdentifier = "bar" + separator + "baz",
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };

        var xml = XmlSerializer.Serialize(Profile, new[] { first, second });
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(2, root.Elements(xbrli + "context").Count());

        var reopened = XmlSerializer.ReadFacts(xml);
        var expectedEntities = new[] { first, second }
            .Select(fact => JsonSerializer.Serialize(new[] { fact.EntityScheme, fact.EntityIdentifier }))
            .Order(StringComparer.Ordinal);
        var actualEntities = reopened
            .Select(fact => JsonSerializer.Serialize(new[] { fact.EntityScheme, fact.EntityIdentifier }))
            .Order(StringComparer.Ordinal);
        Assert.Equal(expectedEntities, actualEntities);
    }

    [Fact]
    public void CompoundUnit_SerializesAsDivide_AndReadsBack()
    {
        var cu = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
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
    public void ProductUnit_SerializesAsMultipleMeasures_AndReadsBack()
    {
        var product = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            CompoundUnit = new CompoundUnit(new[] { "USD", "shares" }),
            Value = 12.5m,
            Decimals = 1
        };

        var xml = XmlSerializer.Serialize(Profile, new[] { product });
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        var unit = XDocument.Parse(xml).Root!.Element(xbrli + "unit")!;
        Assert.Equal(2, unit.Elements(xbrli + "measure").Count());
        Assert.Empty(unit.Elements(xbrli + "divide"));

        var reopened = Assert.Single(XmlSerializer.ReadFacts(xml));
        Assert.Equal("USD*shares", reopened.CompoundUnit?.CanonicalKey);
    }

    [Fact]
    public void PoweredUnitsUseStandardMeasureProductsAndRatios()
    {
        var fact = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("shares", -2),
            Value = 12.5m,
            Decimals = 1
        };

        var xml = XmlSerializer.Serialize(Profile, new[] { fact });
        Assert.DoesNotContain(" power=", xml, StringComparison.OrdinalIgnoreCase);
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        var divide = XDocument.Parse(xml).Root!.Element(xbrli + "unit")!.Element(xbrli + "divide")!;
        Assert.Equal("xbrli:pure", divide.Element(xbrli + "unitNumerator")!
            .Element(xbrli + "measure")!.Value);
        Assert.Equal(2, divide.Element(xbrli + "unitDenominator")!.Elements(xbrli + "measure").Count());
        var fromXml = Assert.Single(XmlSerializer.ReadFacts(xml));
        Assert.Equal(fact.SemanticKey(), fromXml.SemanticKey());

        var json = OimSerializer.Serialize(Profile, new[] { fact });
        using var document = JsonDocument.Parse(json);
        var unit = document.RootElement.GetProperty("facts").EnumerateObject().Single().Value
            .GetProperty("dimensions").GetProperty("unit").GetString();
        Assert.Equal("xbrli:pure/xbrli:shares*xbrli:shares", unit);
        var fromJson = Assert.Single(OimSerializer.ReadFacts(json));
        Assert.Equal(fact.SemanticKey(), fromJson.SemanticKey());
    }

    [Fact]
    public void PureUnitUsesOimOmissionAndRoundTripsWithSelectedProfile()
    {
        var fact = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("pure"),
            Value = 0.125m,
            Decimals = 3
        };

        var json = OimSerializer.Serialize(Profile, new[] { fact });
        using (var document = JsonDocument.Parse(json))
        {
            var dimensions = document.RootElement.GetProperty("facts").EnumerateObject().Single().Value
                .GetProperty("dimensions");
            Assert.False(dimensions.TryGetProperty("unit", out _));
        }

        var fromJson = Assert.Single(OimSerializer.ReadFacts(json, Profile));
        Assert.Equal("pure", fromJson.Unit?.Measure);
        Assert.Equal(fact.SemanticKey(), fromJson.SemanticKey());

        var xml = XmlSerializer.Serialize(Profile, new[] { fact });
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        Assert.Equal("xbrli:pure", XDocument.Parse(xml).Root!.Element(xbrli + "unit")!
            .Element(xbrli + "measure")!.Value);
        var fromXml = Assert.Single(XmlSerializer.ReadFacts(xml));
        Assert.Equal(fact.SemanticKey(), fromXml.SemanticKey());
    }

    [Fact]
    public void ProfileAwareOimReadNormalizesNamespaceAliasesForConceptsAndDimensions()
    {
        var fact = new XbrlFact
        {
            ConceptName = TestFilingProfiles.UnitsPerShareConcept,
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            CompoundUnit = new CompoundUnit(new[] { "USD" }, new[] { "shares" }),
            Value = 12.5m,
            Decimals = 1,
            Dimensions = new[]
            {
                new Dimension(SyntheticProfile.Qualified("Region"), SyntheticProfile.Qualified("EMEA"))
            }
        };
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { fact }))!.AsObject();
        var namespaceMap = root["documentInfo"]!["namespaces"]!.AsObject();
        var originalPrefix = namespaceMap.First(pair =>
            StringComparer.Ordinal.Equals(pair.Value!.GetValue<string>(), SyntheticProfile.Namespace)).Key;
        var originalNamespace = namespaceMap[originalPrefix]!.GetValue<string>();
        namespaceMap.Remove(originalPrefix);
        namespaceMap["alias"] = originalNamespace;

        foreach (var factEntry in root["facts"]!.AsObject())
        {
            var dimensions = factEntry.Value!["dimensions"]!.AsObject();
            dimensions["concept"] = ReplacePrefix(dimensions["concept"]!.GetValue<string>());
            var dimension = dimensions.First(pair => pair.Key.StartsWith(originalPrefix + ":", StringComparison.Ordinal));
            var dimensionLocalName = dimension.Key[(originalPrefix.Length + 1)..];
            var member = ReplacePrefix(dimension.Value!.GetValue<string>());
            dimensions.Remove(dimension.Key);
            dimensions["alias:" + dimensionLocalName] = member;
        }

        var reopened = Assert.Single(OimSerializer.ReadFacts(root.ToJsonString(), Profile));
        Assert.Equal(fact.ConceptName, reopened.ConceptName);
        Assert.Equal(fact.Dimensions, reopened.Dimensions);

        string ReplacePrefix(string qname)
            => qname.StartsWith(originalPrefix + ":", StringComparison.Ordinal)
                ? "alias:" + qname[(originalPrefix.Length + 1)..]
                : qname;
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
    public void OimJson_PreservesExactDecimalScaleAndAccuracyLexicalValue()
    {
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 1.2300m,
            Decimals = 4
        };

        var json = OimSerializer.Serialize(Profile, new[] { fact });
        using var document = JsonDocument.Parse(json);
        var serialized = Assert.Single(document.RootElement.GetProperty("facts").EnumerateObject()).Value;
        Assert.Equal("1.2300", serialized.GetProperty("value").GetString());
        Assert.Equal(4, serialized.GetProperty("decimals").GetInt32());

        var reopened = Assert.Single(OimSerializer.ReadFacts(json));
        Assert.Equal("1.2300", reopened.Value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(4, reopened.Decimals);
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

    [Theory]
    [InlineData("report")]
    [InlineData("documentInfo")]
    [InlineData("fact")]
    public void OimJson_RejectsUnpreservedExtensionProperties(string location)
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { GoldenFacts()[0] }))!.AsObject();
        const string extensionName = "acme:sourceAnnotation";
        root["documentInfo"]!["namespaces"]!["acme"] = "urn:example:report-extension";
        switch (location)
        {
            case "report":
                root[extensionName] = "transaction details";
                break;
            case "documentInfo":
                root["documentInfo"]![extensionName] = "source package metadata";
                break;
            case "fact":
                root["facts"]!.AsObject().First().Value![extensionName] = "original ledger row";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(location));
        }

        var error = Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));

        Assert.Contains("not supported by this mapping model", error.Message, StringComparison.Ordinal);
        Assert.Contains(extensionName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OimJson_RejectsUnsupportedStandardDocumentInfoFeatures()
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { GoldenFacts()[0] }))!.AsObject();
        root["documentInfo"]!["features"] = new JsonObject();

        var error = Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));

        Assert.Contains("documentInfo property 'features'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void OimJson_ReadsSupportedDecimalsRangeBoundaries(int decimals)
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { GoldenFacts()[0] }))!.AsObject();
        var fact = root["facts"]!.AsObject().First().Value!.AsObject();
        fact["decimals"] = decimals;

        var reopened = Assert.Single(OimSerializer.ReadFacts(root.ToJsonString()));

        Assert.Equal(decimals, reopened.Decimals);
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(31)]
    public void OimJson_RejectsDecimalsOutsideSupportedRange(int decimals)
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { GoldenFacts()[0] }))!.AsObject();
        var fact = root["facts"]!.AsObject().First().Value!.AsObject();
        fact["decimals"] = decimals;

        Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));
    }

    [Fact]
    public void OimJson_RejectsDecimalsOnNilFacts()
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
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { nil }))!.AsObject();
        var fact = root["facts"]!.AsObject().First().Value!.AsObject();
        fact["decimals"] = 0;

        Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));
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
            Concepts = basis.Concepts.Append(new ConceptMetadata(
                "synthetic:RevenueByRegion", "urn:synthetic:finance:1.0", "RevenueByRegion",
                "Revenue by Region", "monetary", "duration", false)).ToArray(),
            SupportedUnits = basis.SupportedUnits,
            NamespaceUris = basis.NamespaceUris,
            TaxonomyUrls = new[] { "xbrl-cross-format-taxonomy.xsd" },
            PackageHashes = basis.PackageHashes,
            DimensionDomains = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["synthetic:RegionAxis"] = new[] { "synthetic:EMEA" }
            }
        };
        var duration = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var facts = new[]
        {
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = duration,
                Unit = new XbrlUnit("USD"),
                Value = 123.45m,
                Decimals = 2
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Revenue"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = duration,
                Unit = new XbrlUnit("USD"),
                Value = 123.450m,
                Decimals = 3
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = duration,
                TextValue = "ABC INC",
                Language = "en"
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("NetIncome"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = duration,
                Unit = new XbrlUnit("USD"),
                Value = 0m,
                Decimals = 0
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Expenses"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = duration,
                Unit = new XbrlUnit("QAR"),
                IsNil = true
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("Assets"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = XbrlPeriod.OfInstant(new DateOnly(2025, 12, 31)),
                Unit = new XbrlUnit("QAR"),
                Value = 500.00m,
                Decimals = 2
            },
            new XbrlFact
            {
                ConceptName = SyntheticProfile.Qualified("RevenueByRegion"),
                EntityScheme = "http://standards.iso.org/iso/17442",
                EntityIdentifier = "00EHHQ2ZHDCFXJCPCL49",
                Period = duration,
                Unit = new XbrlUnit("USD"),
                Value = 72.25m,
                Decimals = 2,
                Dimensions = new[]
                {
                    new Dimension("synthetic:RegionAxis", "synthetic:EMEA")
                }
            }
        };

        var fixtureJsonFacts = OimSerializer.ReadFacts(FixturePaths.Read("product-generated-pass.xbrl.json"));
        var xmlFixtureFacts = XmlSerializer.ReadFacts(FixturePaths.Read("product-generated-pass.xbrl.xml"));
        var generatedJsonFacts = OimSerializer.ReadFacts(OimSerializer.Serialize(profile, facts));
        var generatedXmlFacts = XmlSerializer.ReadFacts(XmlSerializer.Serialize(profile, facts));
        var expectedOccurrences = OccurrenceKeys(facts.Select(ToOccurrence));
        Assert.Equal(expectedOccurrences, OccurrenceKeys(fixtureJsonFacts.Select(ToOccurrence)));
        Assert.Equal(expectedOccurrences, OccurrenceKeys(xmlFixtureFacts.Select(ToOccurrence)));
        Assert.Equal(expectedOccurrences, OccurrenceKeys(generatedJsonFacts.Select(ToOccurrence)));
        Assert.Equal(expectedOccurrences, OccurrenceKeys(generatedXmlFacts.Select(ToOccurrence)));
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

    [Fact]
    public void FactValidationRejectsMalformedEntityPeriodDimensionsAndDecimals()
    {
        var valid = GoldenFacts()[0];

        var invalidEntity = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = "",
            EntityIdentifier = valid.EntityIdentifier,
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };
        Assert.False(invalidEntity.ValidateAgainst(Profile.FindConcept(invalidEntity.ConceptName)!, out _, out var entityIssue));
        Assert.Equal(FactValidationIssue.InvalidEntity, entityIssue);
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { invalidEntity }));

        var malformedScheme = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = "not a uri",
            EntityIdentifier = valid.EntityIdentifier,
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };
        Assert.False(malformedScheme.ValidateAgainst(Profile.FindConcept(malformedScheme.ConceptName)!, out _, out var schemeIssue));
        Assert.Equal(FactValidationIssue.InvalidEntity, schemeIssue);
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { malformedScheme }));

        var uriStyleEntity = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = "https://example.test/entity-scheme",
            EntityIdentifier = "https://example.test/entities/ACME Holdings",
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };
        Assert.True(uriStyleEntity.ValidateAgainst(Profile.FindConcept(uriStyleEntity.ConceptName)!, out var uriEntityReason), uriEntityReason);
        Assert.Contains("https://example.test/entities/ACME Holdings", XmlSerializer.Serialize(Profile, new[] { uriStyleEntity }));

        var invalidPeriod = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = valid.EntityScheme,
            EntityIdentifier = valid.EntityIdentifier,
            Period = new XbrlPeriod(PeriodKind.Duration, null, new DateOnly(2026, 1, 1), new DateOnly(2025, 1, 1)),
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals
        };
        Assert.False(invalidPeriod.ValidateAgainst(Profile.FindConcept(invalidPeriod.ConceptName)!, out _, out var periodIssue));
        Assert.Equal(FactValidationIssue.InvalidPeriod, periodIssue);
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { invalidPeriod }));

        var duplicateDimensions = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = valid.EntityScheme,
            EntityIdentifier = valid.EntityIdentifier,
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals,
            Dimensions = new[]
            {
                new Dimension("synthetic:Region", "synthetic:EMEA"),
                new Dimension("synthetic:Region", "synthetic:Domestic")
            }
        };
        Assert.False(duplicateDimensions.ValidateAgainst(Profile.FindConcept(duplicateDimensions.ConceptName)!, out _, out var dimensionsIssue));
        Assert.Equal(FactValidationIssue.InvalidDimensions, dimensionsIssue);
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { duplicateDimensions }));

        var typedDimension = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = valid.EntityScheme,
            EntityIdentifier = valid.EntityIdentifier,
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = valid.Decimals,
            Dimensions = new[] { new Dimension("synthetic:TypedAxis", "https://example.test/typed-value", IsExplicit: false) }
        };
        Assert.False(typedDimension.ValidateAgainst(Profile.FindConcept(typedDimension.ConceptName)!, out var typedReason, out var typedIssue));
        Assert.Equal(FactValidationIssue.InvalidDimensions, typedIssue);
        Assert.Contains("typed dimensions", typedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { typedDimension }));
        Assert.Throws<XbrlExportException>(() => XmlSerializer.Serialize(Profile, new[] { typedDimension }));

        var invalidDecimals = new XbrlFact
        {
            ConceptName = valid.ConceptName,
            EntityScheme = valid.EntityScheme,
            EntityIdentifier = valid.EntityIdentifier,
            Period = valid.Period,
            Unit = valid.Unit,
            Value = valid.Value,
            Decimals = 31
        };
        Assert.False(invalidDecimals.ValidateAgainst(Profile.FindConcept(invalidDecimals.ConceptName)!, out _, out var decimalsIssue));
        Assert.Equal(FactValidationIssue.InvalidDecimals, decimalsIssue);
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { invalidDecimals }));
    }

    [Theory]
    [InlineData("EN-us")]
    [InlineData("zh-cmn-Hans-CN-u-ca-chinese")]
    [InlineData("x-private")]
    [InlineData("i-default")]
    public void FactValidationAcceptsWellFormedBcp47LanguageTags(string language)
    {
        var fact = CreateTextFact(language);
        var concept = Profile.FindConcept(fact.ConceptName)! with
        {
            SupportedLanguages = Array.Empty<string>()
        };

        Assert.True(fact.ValidateAgainst(concept, out var reason), reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("en_US")]
    [InlineData("en--US")]
    [InlineData("x")]
    [InlineData("en-u")]
    [InlineData("en-u-ca-u-nu")]
    [InlineData("en-1901-1901")]
    public void FactValidationRejectsMalformedBcp47LanguageTags(string language)
    {
        var fact = CreateTextFact(language);
        var concept = Profile.FindConcept(fact.ConceptName)! with
        {
            SupportedLanguages = Array.Empty<string>()
        };

        Assert.False(fact.ValidateAgainst(concept, out _, out var issue));
        Assert.Equal(FactValidationIssue.InvalidLanguage, issue);
        Assert.Throws<XbrlExportException>(() => OimSerializer.Serialize(Profile, new[] { fact }));
    }

    [Fact]
    public void FactValidationMatchesProfileLanguageTagsWithoutCaseSensitivity()
    {
        var fact = CreateTextFact("EN");
        var concept = Profile.FindConcept(fact.ConceptName)! with
        {
            SupportedLanguages = new[] { "en" }
        };

        Assert.True(fact.ValidateAgainst(concept, out var reason), reason);
    }

    [Theory]
    [InlineData("en--US")]
    [InlineData("en_US")]
    public void OimJsonReaderRejectsMalformedLanguageTagsWithoutProfile(string language)
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { CreateTextFact("en") }))!.AsObject();
        var fact = root["facts"]!.AsObject().Single().Value!.AsObject();
        fact["dimensions"]!.AsObject()["language"] = language;

        Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));
    }

    [Theory]
    [InlineData("EN")]
    [InlineData("en-US")]
    public void OimJsonReaderRejectsNonLowercaseLanguageTagsWithoutProfile(string language)
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { CreateTextFact("en") }))!.AsObject();
        var fact = root["facts"]!.AsObject().Single().Value!.AsObject();
        fact["dimensions"]!.AsObject()["language"] = language;

        var error = Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));
        Assert.Contains("lowercase", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OimJsonReaderRejectsTypedDimensionsWithoutProfile()
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { CreateTextFact("en") }))!.AsObject();
        var fact = root["facts"]!.AsObject().Single().Value!.AsObject();
        fact["dimensions"]!.AsObject()["synthetic:TypedAxis"] = "Electric Motor Vehicles";

        var error = Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));
        Assert.Contains("typed dimension", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SupportedXsStringTypedDimensionRoundTripsThroughOimAndXml()
    {
        var baseProfile = Profile;
        var profile = new FilingProfile
        {
            ProfileId = baseProfile.ProfileId,
            Digest = baseProfile.Digest,
            Concepts = baseProfile.Concepts,
            SupportedUnits = baseProfile.SupportedUnits,
            NamespaceUris = baseProfile.NamespaceUris.Concat(new[]
            {
                new KeyValuePair<string, string>("xs", "http://www.w3.org/2001/XMLSchema")
            }).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            TaxonomyUrls = baseProfile.TaxonomyUrls,
            TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
            {
                ["synthetic:TypedAxis"] = new("synthetic:TypedMember", "xs:string")
            }
        };
        var textFact = CreateTextFact("en");
        var fact = new XbrlFact
        {
            ConceptName = textFact.ConceptName,
            EntityScheme = textFact.EntityScheme,
            EntityIdentifier = textFact.EntityIdentifier,
            Period = textFact.Period,
            TextValue = textFact.TextValue,
            Language = textFact.Language,
            Dimensions = [new Dimension("synthetic:TypedAxis", "", IsExplicit: false)
                { TypedValue = "synthetic:QNameLookingText" }]
        };
        Assert.True(fact.ValidateAgainst(profile, profile.FindConcept(fact.ConceptName)!, out var reason), reason);

        var oim = OimSerializer.Serialize(profile, [fact]);
        var fromOim = Assert.Single(OimSerializer.ReadFacts(oim, profile));
        Assert.False(Assert.Single(fromOim.Dimensions).IsExplicit);
        Assert.Equal("synthetic:QNameLookingText", Assert.Single(fromOim.Dimensions).TypedValue);
        Assert.Equal(fact.SemanticKey(), fromOim.SemanticKey());

        var xml = XmlSerializer.Serialize(profile, [fact]);
        var fromXml = Assert.Single(XmlSerializer.ReadFacts(xml, profile));
        Assert.False(Assert.Single(fromXml.Dimensions).IsExplicit);
        Assert.Equal("synthetic:QNameLookingText", Assert.Single(fromXml.Dimensions).TypedValue);
        Assert.Equal(fact.SemanticKey(), fromXml.SemanticKey());
        Assert.Contains("typedMember", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void OimJsonReaderRejectsDuplicateDimensionPropertiesWithoutProfile()
    {
        var json = OimSerializer.Serialize(Profile, new[] { CreateTextFact("en") });
        const string languageProperty = "\"language\": \"en\"";
        Assert.Contains(languageProperty, json, StringComparison.Ordinal);
        json = json.Replace(languageProperty,
            languageProperty + ",\n          \"language\": \"fr\"", StringComparison.Ordinal);

        var error = Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(json));
        Assert.Contains("repeats key 'language'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OimJsonReaderRejectsDimensionAliasesForTheSameAxisWithoutProfile()
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { CreateTextFact("en") }))!.AsObject();
        var namespaces = root["documentInfo"]!["namespaces"]!.AsObject();
        namespaces["alternate"] = namespaces["synthetic"]!.DeepClone();
        var dimensions = root["facts"]!.AsObject().Single().Value!["dimensions"]!.AsObject();
        dimensions["synthetic:Region"] = "synthetic:EMEA";
        dimensions["alternate:Region"] = "alternate:EMEA";

        var error = Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));
        Assert.Contains("duplicates another dimension", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OimJsonReaderEnforcesTaxonomyDimensionCountWithoutProfile()
    {
        var root = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { CreateTextFact("en") }))!.AsObject();
        var dimensions = root["facts"]!.AsObject().Single().Value!["dimensions"]!.AsObject();
        for (var index = 0; index <= FactDimensionParser.MaximumDimensions; index++)
        {
            var suffix = index.ToString("D2", CultureInfo.InvariantCulture);
            dimensions[$"synthetic:Axis{suffix}"] = $"synthetic:Member{suffix}";
        }

        var error = Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(root.ToJsonString()));
        Assert.Contains($"at most {FactDimensionParser.MaximumDimensions}", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.234567890123456789012345678901")]
    [InlineData("0.00000000000000000000000000001")]
    public void JsonAndXmlReadersRejectNumericValuesThatRequireRoundingOrUnderflow(string lexical)
    {
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 1m,
            Decimals = 0
        };

        var json = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { fact }))!.AsObject();
        var jsonFact = json["facts"]!.AsObject().Single().Value!.AsObject();
        jsonFact["value"] = lexical;
        Assert.Throws<XbrlExportException>(() => OimSerializer.ReadFacts(json.ToJsonString()));

        var xml = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { fact }));
        var xmlFact = Assert.Single(xml.Descendants(), element => element.Name.LocalName == "Revenue");
        xmlFact.Value = lexical;
        Assert.Throws<XbrlExportException>(() => XmlSerializer.ReadFacts(xml.ToString(SaveOptions.DisableFormatting)));
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    public void JsonAndXmlReadersAcceptExactClrDecimalBoundaries(string lexical)
    {
        var fact = new XbrlFact
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ABC-Corp",
            Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
            Unit = new XbrlUnit("QAR"),
            Value = 1m,
            Decimals = 0
        };

        var json = JsonNode.Parse(OimSerializer.Serialize(Profile, new[] { fact }))!.AsObject();
        var jsonFact = json["facts"]!.AsObject().Single().Value!.AsObject();
        jsonFact["value"] = lexical;
        var parsedJsonFact = Assert.Single(OimSerializer.ReadFacts(json.ToJsonString()));
        Assert.True(parsedJsonFact.Value.HasValue);
        Assert.Equal(decimal.Parse(lexical, CultureInfo.InvariantCulture), parsedJsonFact.Value.GetValueOrDefault());

        var xml = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { fact }));
        var xmlFact = Assert.Single(xml.Descendants(), element => element.Name.LocalName == "Revenue");
        xmlFact.Value = lexical;
        var parsedXmlFact = Assert.Single(XmlSerializer.ReadFacts(xml.ToString(SaveOptions.DisableFormatting)));
        Assert.True(parsedXmlFact.Value.HasValue);
        Assert.Equal(decimal.Parse(lexical, CultureInfo.InvariantCulture), parsedXmlFact.Value.GetValueOrDefault());
    }

    [Theory]
    [InlineData("en--US")]
    [InlineData("en_US")]
    public void XmlReaderRejectsMalformedLanguageTagsWithoutProfile(string language)
    {
        var document = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { CreateTextFact("en") }));
        var fact = Assert.Single(document.Descendants(),
            element => element.Attribute(XNamespace.Xml + "lang") is not null);
        fact.SetAttributeValue(XNamespace.Xml + "lang", language);

        Assert.Throws<XbrlExportException>(() =>
            XmlSerializer.ReadFacts(document.ToString(SaveOptions.DisableFormatting)));
    }

    [Fact]
    public void XmlReaderRejectsDimensionAliasesForTheSameAxisWithoutProfile()
    {
        XNamespace xbrli = "http://www.xbrl.org/2003/instance";
        XNamespace xbrldi = "http://xbrl.org/2006/xbrldi";
        var document = XDocument.Parse(XmlSerializer.Serialize(Profile, new[] { CreateTextFact("en") }));
        var root = document.Root!;
        root.SetAttributeValue(XNamespace.Xmlns + "alternate", root.GetNamespaceOfPrefix("synthetic")!.NamespaceName);
        root.SetAttributeValue(XNamespace.Xmlns + "xbrldi", xbrldi.NamespaceName);
        var context = Assert.Single(root.Elements(xbrli + "context"));
        var entity = context.Element(xbrli + "entity")!;
        entity.Add(new XElement(xbrli + "segment",
            new XElement(xbrldi + "explicitMember", new XAttribute("dimension", "synthetic:Region"), "synthetic:EMEA"),
            new XElement(xbrldi + "explicitMember", new XAttribute("dimension", "alternate:Region"), "alternate:EMEA")));

        var error = Assert.Throws<XbrlExportException>(() =>
            XmlSerializer.ReadFacts(document.ToString(SaveOptions.DisableFormatting)));
        Assert.Contains("duplicates another dimension", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static XbrlFact CreateTextFact(string language) => new()
    {
        ConceptName = SyntheticProfile.Qualified("EntityRegistrantName"),
        EntityScheme = "http://xbrl.org/entity/",
        EntityIdentifier = "ABC-Corp",
        Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
        TextValue = "Example",
        Language = language
    };

    private static void AssertMultisetEquivalent(IReadOnlyList<XbrlFact> expected, IReadOnlyList<XbrlFact> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        var a = expected.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal);
        var b = actual.Select(f => f.SemanticKey()).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(a.ToList(), b.ToList());
    }

    private static string[] OccurrenceKeys(IEnumerable<FactOccurrence> occurrences)
        => occurrences.Select(occurrence => JsonSerializer.Serialize(occurrence))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static FactOccurrence ToOccurrence(XbrlFact fact)
        => new(
            fact.ConceptName,
            fact.EntityScheme,
            fact.EntityIdentifier,
            fact.Period.ToIsoString(),
            fact.Unit?.Measure,
            fact.Unit?.Power,
            fact.CompoundUnit?.CanonicalKey,
            fact.IsNil ? null : fact.Value?.ToString(CultureInfo.InvariantCulture) ?? fact.TextValue,
            fact.IsNil,
            fact.Decimals,
            fact.Language?.ToLowerInvariant(),
            fact.Dimensions
                .Select(dimension => $"{dimension.Name}={dimension.MemberName}:{dimension.IsExplicit}")
                .Order(StringComparer.Ordinal)
                .ToArray());

    private sealed record FactOccurrence(
        string ConceptName,
        string EntityScheme,
        string EntityIdentifier,
        string Period,
        string? UnitMeasure,
        int? UnitPower,
        string? CompoundUnit,
        string? Value,
        bool IsNil,
        int? Decimals,
        string? Language,
        string[] Dimensions);
}
