using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.XBRL.Tests;

public class FactDimensionParserTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Domains =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["ex:RegionAxis"] = new[] { "ex:Domestic", "ex:International" },
            ["ex:ProductAxis"] = new[] { "ex:Hardware" }
        };

    [Fact]
    public void ParsesAndCanonicalizesDeclaredExplicitDimensions()
    {
        var ok = FactDimensionParser.TryParse(
            "ex:ProductAxis=ex:Hardware\r\nex:RegionAxis=ex:Domestic",
            Domains,
            out var dimensions,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(new[] { "ex:ProductAxis", "ex:RegionAxis" }, dimensions.Select(dimension => dimension.Name));
        Assert.Equal("ex:ProductAxis=ex:Hardware\nex:RegionAxis=ex:Domestic",
            FactDimensionParser.Format(dimensions));
    }

    [Theory]
    [InlineData("ex:RegionAxis", "axisQName=memberQName")]
    [InlineData("ex:MissingAxis=ex:Domestic", "not declared")]
    [InlineData("ex:RegionAxis=ex:Missing", "not declared for dimension axis")]
    [InlineData("ex:RegionAxis=ex:Domestic\nex:RegionAxis=ex:International", "more than once")]
    [InlineData("ex:RegionAxis=ex:Domestic=extra", "axisQName=memberQName")]
    public void RejectsMalformedOrUndeclaredDimensions(string text, string expectedError)
    {
        var ok = FactDimensionParser.TryParse(text, Domains, out var dimensions, out var error);

        Assert.False(ok);
        Assert.Empty(dimensions);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsEmptyDimensionsAndEnforcesMaximumCount()
    {
        Assert.True(FactDimensionParser.TryParse(" \r\n ", Domains, out var empty, out var error), error);
        Assert.Empty(empty);

        var tooMany = string.Join(Environment.NewLine,
            Enumerable.Repeat("ex:RegionAxis=ex:Domestic", FactDimensionParser.MaximumDimensions + 1));
        Assert.False(FactDimensionParser.TryParse(tooMany, Domains, out _, out error));
        Assert.Contains("at most", error, StringComparison.OrdinalIgnoreCase);
    }
}
