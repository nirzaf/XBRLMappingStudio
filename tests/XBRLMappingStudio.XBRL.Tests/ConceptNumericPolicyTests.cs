using System.Globalization;
using System.Text.Json;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.XBRL.Tests;

public sealed class ConceptNumericPolicyTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private sealed record FinancialFixture(string Scenario, string Value, int? Decimals, ConceptNumericPolicy Policy, bool Valid);

    [Fact]
    public void FinancialFixturesEnforcePoliciesWithoutChangingValues()
    {
        var fixtures = JsonSerializer.Deserialize<FinancialFixture[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "concept-numeric-policies.json")), Options)!;
        foreach (var fixture in fixtures)
        {
            var value = decimal.Parse(fixture.Value, CultureInfo.InvariantCulture);
            var fact = Fact(value, fixture.Decimals);
            var concept = Concept(fixture.Policy);
            Assert.True(fact.ValidateValueAgainst(concept, out _) == fixture.Valid, fixture.Scenario);
            Assert.Equal(value, fact.Value);
            Assert.Equal(fixture.Decimals, fact.Decimals);
        }
    }

    [Fact]
    public void AbsentPolicyPreservesLegacyValuesAndNilBypassesValueConstraints()
    {
        Assert.True(Fact(1.255m, 2).ValidateValueAgainst(Concept(null), out _));
        var nil = new XbrlFact { ConceptName = "test:Revenue", EntityScheme = "urn:entity", EntityIdentifier = "a",
            Period = XbrlPeriod.OfInstant(new DateOnly(2025, 12, 31)), Unit = new XbrlUnit("USD"), IsNil = true };
        Assert.True(nil.ValidateValueAgainst(Concept(new() { AllowInfiniteAccuracy = false }), out _));
    }

    [Fact]
    public void InvalidPoliciesFailClosed()
    {
        foreach (var policy in new ConceptNumericPolicy[] {
            new() { MaximumSignificantDigits = 0 }, new() { MaximumSignificantDigits = 30 },
            new() { RoundingDecimals = -29 }, new() { RoundingMode = "nearest" },
            new() { MinimumDecimals = 3, MaximumDecimals = 2 }, new() { MaximumDecimals = 31 } })
            Assert.False(Fact(1m, 0).ValidateValueAgainst(Concept(policy), out _));
        Assert.False(Fact(1m, 0).ValidateValueAgainst(Concept(new()) with { DataType = "string" }, out _));
    }

    [Fact]
    public void PolicyRoundTripsAndChangesManifestDigest()
    {
        var manifest = new FilingProfileManifest {
            SchemaVersion = 6, ProfileId = "test", Version = "1", EntryPoint = "test.xsd", ProfileDigest = "",
            Concepts = new() { Concept(new() { RoundingDecimals = 2 }) }, SupportedUnits = new() { "USD" },
            PackageNames = new(), PackageFiles = new() };
        var restored = JsonSerializer.Deserialize<FilingProfileManifest>(manifest.ToJson(), Options)!;
        Assert.Equal(manifest.ComputeDigest(), restored.ComputeDigest());
        var changed = manifest with { Concepts = new() { Concept(new() { RoundingDecimals = 3 }) } };
        Assert.NotEqual(manifest.ComputeDigest(), changed.ComputeDigest());
    }

    private static ConceptMetadata Concept(ConceptNumericPolicy? policy) =>
        new("test:Revenue", "urn:test", "Revenue", "Revenue", "monetary", "instant", false) { NumericPolicy = policy };
    private static XbrlFact Fact(decimal value, int? decimals) => new() {
        ConceptName = "test:Revenue", EntityScheme = "urn:entity", EntityIdentifier = "a",
        Period = XbrlPeriod.OfInstant(new DateOnly(2025, 12, 31)), Unit = new XbrlUnit("USD"), Value = value, Decimals = decimals };
}
