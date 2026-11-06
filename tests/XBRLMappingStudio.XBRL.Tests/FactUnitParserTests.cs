namespace XBRLMappingStudio.XBRL.Tests;

public sealed class FactUnitParserTests
{
    private static FilingProfile Profile => SyntheticProfile.Build();

    [Theory]
    [InlineData("USD", "USD", null)]
    [InlineData("USD/shares", null, "USD/shares")]
    [InlineData("USD*QAR/shares", null, "QAR*USD/shares")]
    [InlineData("shares*shares", "shares^2", null)]
    [InlineData("pure/shares", "shares^-1", null)]
    [InlineData("iso4217:USD", "USD", null)]
    [InlineData("xbrli:shares", "shares", null)]
    public void ParsesSupportedSimpleAndCompoundExpressions(string expression, string? expectedUnit, string? expectedCompound)
    {
        Assert.True(FactUnitParser.TryParse(expression, Profile, out var unit, out var compound, out var error), error);
        Assert.Equal(expectedUnit, unit?.ToString());
        Assert.Equal(expectedCompound, compound?.CanonicalKey);
    }

    [Theory]
    [InlineData("USD/")]
    [InlineData("/shares")]
    [InlineData("USD//shares")]
    [InlineData("USD**shares")]
    [InlineData("USD/shares/pure")]
    [InlineData("USD/ shares")]
    [InlineData("USD/unsupported")]
    public void RejectsMalformedOrUnsupportedExpressions(string expression)
    {
        Assert.False(FactUnitParser.TryParse(expression, Profile, out var unit, out var compound, out var error));
        Assert.Null(unit);
        Assert.Null(compound);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ValidatesUnitsAgainstNumericConceptTypes()
    {
        var profile = TestFilingProfiles.WithNumericCompoundUnitConcept();
        var revenue = profile.FindConcept(SyntheticProfile.Qualified("Revenue"))!;
        var compoundTarget = profile.FindConcept(TestFilingProfiles.UnitsPerShareConcept)!;

        Assert.True(FactUnitParser.TryValidateForConcept(revenue, profile, new XbrlUnit("USD"), null, out _));
        Assert.False(FactUnitParser.TryValidateForConcept(revenue, profile, null,
            new CompoundUnit(new[] { "USD" }, new[] { "shares" }), out _));
        Assert.True(FactUnitParser.TryValidateForConcept(compoundTarget, profile, null,
            new CompoundUnit(new[] { "USD" }, new[] { "shares" }), out _));
        Assert.False(FactUnitParser.TryParse("xbrli:integer", profile, out _, out _, out _));
    }

    [Fact]
    public void RejectsExpressionsOverTheMeasureLimit()
    {
        var expression = string.Join("*", Enumerable.Repeat("shares", FactUnitParser.MaximumMeasures + 1));

        Assert.False(FactUnitParser.TryParse(expression, Profile, out _, out _, out var error));
        Assert.Contains("measure limit", error, StringComparison.OrdinalIgnoreCase);
    }
}
