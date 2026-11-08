using System.Text.Json;

namespace XBRLMappingStudio.XBRL.Tests;

/// <summary>Same hand-authored network/expectation table is independently checked by pinned Arelle.</summary>
public sealed class HypercubeOracleTests
{
    [Fact]
    public void SharedRelationshipFixturesMatchIndependentExpectedResults()
    {
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(FixturePaths.Read("explicit-hypercubes.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(20, fixtures.Length);
        foreach (var fixture in fixtures)
        {
            var source = SyntheticProfile.Build();
            var members = fixture.Links.SelectMany(link => new[] { link.ParentQName, link.ChildQName })
                .Concat(fixture.Dimensions.Select(dimension => dimension.MemberName)).Distinct().ToArray();
            var axes = fixture.Links.Where(link => link.ArcRoleUri == XbrlArcRoles.HypercubeDimension)
                .Select(link => link.ChildQName).Concat(fixture.Dimensions.Select(dimension => dimension.Name)).Distinct();
            var profile = new FilingProfile
            {
                ProfileId = source.ProfileId, Digest = source.Digest, Concepts = source.Concepts,
                SupportedUnits = source.SupportedUnits, DefinitionLinks = fixture.Links,
                DimensionDomains = axes.ToDictionary(axis => axis, _ => (IReadOnlyList<string>)members)
            };
            var fact = new XbrlFact
            {
                ConceptName = "synthetic:Revenue", EntityScheme = "urn:test:entity", EntityIdentifier = "one",
                Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
                Value = 10m, Unit = new XbrlUnit("QAR"), Dimensions = fixture.Dimensions
            };
            var outcome = Assert.Single(new FilingRuleValidator(profile).ValidateDimensionalAspects(fact),
                result => result.RuleId == "xbrl:dimension-hypercube-valid");
            Assert.True(outcome.Status == (fixture.Valid ? RuleStatus.Pass : RuleStatus.Fail),
                $"Fixture '{fixture.Scenario}' expected valid={fixture.Valid}, got {outcome.Status}: {outcome.Detail}");
        }
    }

    private sealed record Fixture(string Scenario, DefinitionLink[] Links, Dimension[] Dimensions, bool Valid);
}
