using System.Text.Json;
using System.Text.Json.Serialization;
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

    [Fact]
    public void ParsesBoundedAdapterDimensionJsonAndRejectsTypedOrUnknownProperties()
    {
        const string json = "[{\"Name\":\"ex:RegionAxis\",\"MemberName\":\"ex:Domestic\",\"IsExplicit\":true}]";
        Assert.True(FactDimensionParser.TryParseJson(json, Domains, out var dimensions, out var error), error);
        Assert.Equal(new Dimension("ex:RegionAxis", "ex:Domestic"), Assert.Single(dimensions));

        Assert.False(FactDimensionParser.TryParseJson(
            "[{\"Name\":\"ex:RegionAxis\",\"MemberName\":\"ex:Domestic\",\"IsExplicit\":false}]",
            Domains, out dimensions, out error));
        Assert.Empty(dimensions);
        Assert.Contains("Typed dimensions", error, StringComparison.OrdinalIgnoreCase);

        Assert.False(FactDimensionParser.TryParseJson(
            "[{\"Name\":\"ex:RegionAxis\",\"MemberName\":\"ex:Domestic\",\"extra\":1}]",
            Domains, out dimensions, out error));
        Assert.Contains("unsupported", error, StringComparison.OrdinalIgnoreCase);

        Assert.False(FactDimensionParser.TryParseJson(new string('x', FactDimensionParser.MaximumJsonLength + 1),
            Domains, out dimensions, out error));
        Assert.Contains("limit", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FactInspectorAcceptsTypedStringJsonAndFormatsItWithoutOpaqueEvidence()
    {
        var profile = TypedStringProfile();
        const string json = "[{\"Name\":\"synthetic:TypedAxis\",\"IsExplicit\":false,\"TypedValue\":\"synthetic:QNameLookingText\"}]";

        Assert.True(FactDimensionParser.TryParseForProfile(json, profile, out var dimensions, out var error), error);
        var typed = Assert.Single(dimensions);
        Assert.False(typed.IsExplicit);
        Assert.Equal("synthetic:QNameLookingText", typed.TypedValue);

        var formatted = FactDimensionParser.Format(dimensions);
        Assert.Contains("TypedValue", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("TypedValueJson", formatted, StringComparison.Ordinal);
        Assert.True(FactDimensionParser.TryParseForProfile(formatted, profile, out var reparsed, out error), error);
        Assert.Equal(typed, Assert.Single(reparsed));
        Assert.False(FactDimensionParser.TryParseForProfile("[{\"Name\":\"synthetic:TypedAxis\",\"IsExplicit\":false,\"TypedValue\":\"bad\\u0001\"}]",
            profile, out _, out _));
    }

    [Fact]
    public void PromotesImportedTypedXmlEvidenceOnlyForMatchingProfileAxisAndDomain()
    {
        var profile = TypedStringProfile();
        var evidence = JsonSerializer.Serialize(new
        {
            Name = "{urn:synthetic:finance:1.0}TypedMember",
            Attributes = Array.Empty<object>(),
            Content = new[] { new { Kind = "text", Value = "North", Target = (string?)null, Data = (string?)null, Element = (object?)null } }
        });
        var dimensionsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                Name = "source:TypedAxis",
                MemberName = "",
                IsExplicit = false,
                TypedValueJson = evidence,
                AxisExpandedName = "{urn:synthetic:finance:1.0}TypedAxis"
            }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

        Assert.True(FactDimensionParser.TryParseJson(dimensionsJson, profile, out var dimensions, out var error), error);
        var dimension = Assert.Single(dimensions);
        Assert.Equal("synthetic:TypedAxis", dimension.Name);
        Assert.False(dimension.IsExplicit);
        Assert.Equal("North", dimension.TypedValue);
        Assert.Null(dimension.TypedValueJson);
    }

    [Fact]
    public void RejectsTypedEvidenceWithWrongDomainOrConflictingNormalizedValue()
    {
        var profile = TypedStringProfile();
        var wrongDomain = JsonSerializer.Serialize(new
        {
            Name = "{urn:synthetic:finance:1.0}WrongElement",
            Attributes = Array.Empty<object>(),
            Content = new[] { new { Kind = "text", Value = "North", Target = (string?)null, Data = (string?)null, Element = (object?)null } }
        });
        var conflicting = JsonSerializer.Serialize(new
        {
            Name = "{urn:synthetic:finance:1.0}TypedMember",
            Attributes = Array.Empty<object>(),
            Content = new[] { new { Kind = "text", Value = "South", Target = (string?)null, Data = (string?)null, Element = (object?)null } }
        });

        Assert.False(TryParseEvidence(wrongDomain, profile, out var error));
        Assert.Contains("does not match", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(TryParseEvidence(conflicting, profile, out error, normalizedValue: "North"));
        Assert.Contains("does not match", error, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseEvidence(
        string evidence,
        FilingProfile profile,
        out string error,
        string? normalizedValue = null)
    {
        var dimensionsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                Name = "source:TypedAxis",
                MemberName = "",
                IsExplicit = false,
                TypedValue = normalizedValue,
                TypedValueJson = evidence,
                AxisExpandedName = "{urn:synthetic:finance:1.0}TypedAxis"
            }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        return FactDimensionParser.TryParseJson(dimensionsJson, profile, out _, out error);
    }

    private static FilingProfile TypedStringProfile()
    {
        var source = SyntheticProfile.Build();
        return new FilingProfile
        {
            ProfileId = source.ProfileId,
            Digest = source.Digest,
            Concepts = source.Concepts,
            SupportedUnits = source.SupportedUnits,
            DimensionDomains = source.DimensionDomains,
            NamespaceUris = source.NamespaceUris.Concat(new[]
            {
                new KeyValuePair<string, string>("xs", "http://www.w3.org/2001/XMLSchema")
            }).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            TypedDimensionDomains = new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal)
            {
                ["synthetic:TypedAxis"] = new("synthetic:TypedMember", "xs:string")
            }
        };
    }
}
