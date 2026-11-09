using System.Text.Json;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class XbrlFactRecordMapperProvenanceTests
{
    private static readonly string ProfileDigest = SyntheticProfile.Build().Digest;

    [Fact]
    public void ProvenanceJson_StoresTwoSourceSpecificPathsWithoutFactWideUnion()
    {
        var numerator = new SourceReference("workbook", "Sheet1!B3", "Revenue row");
        var denominator = new SourceReference("workbook", "Sheet1!C3", "Revenue row");
        var fact = Fact(new[] { denominator, numerator }, new[]
        {
            Path(numerator, "excel-numerator", "ratio", "fact-construct"),
            Path(denominator, "excel-denominator", "ratio", "fact-construct")
        });

        var json = Assert.Single(XbrlFactRecordMapper.ToRecords(new[] { fact }, ProfileDigest))
            ["provenance_json"].AsString();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
        var entries = root.EnumerateArray().ToDictionary(
            entry => entry.GetProperty("Locator").GetString()!, StringComparer.Ordinal);
        AssertSourceFields(entries["Sheet1!B3"], numerator);
        AssertSourceFields(entries["Sheet1!C3"], denominator);
        Assert.Equal(new[] { new[] { "excel-numerator", "ratio", "fact-construct" } },
            ReadPaths(entries["Sheet1!B3"]));
        Assert.Equal(new[] { new[] { "excel-denominator", "ratio", "fact-construct" } },
            ReadPaths(entries["Sheet1!C3"]));
        Assert.DoesNotContain("excel-denominator", ReadPaths(entries["Sheet1!B3"]).SelectMany(path => path));
        Assert.DoesNotContain("excel-numerator", ReadPaths(entries["Sheet1!C3"]).SelectMany(path => path));
    }

    [Fact]
    public void ProvenanceJson_PreservesMultiplePathsForOneSourceAsSeparateArrays()
    {
        var source = new SourceReference("workbook", "Sheet1!B3", "Revenue row");
        var otherSource = new SourceReference("workbook", "Sheet1!C3", "Revenue row");
        var pathlessSource = new SourceReference("workbook", "Sheet1!D3", "Unused source");
        var fact = Fact(new[] { source, otherSource, pathlessSource }, new[]
        {
            Path(source, "excel-direct", "coalesce", "fact-construct"),
            Path(source, "excel-fallback", "coalesce", "fact-construct"),
            Path(otherSource, "excel-denominator", "coalesce", "fact-construct")
        });

        var json = Assert.Single(XbrlFactRecordMapper.ToRecords(new[] { fact }, ProfileDigest))
            ["provenance_json"].AsString();
        var reversedFact = Fact(new[] { source, otherSource, pathlessSource },
            fact.SourceTransformationPaths.Reverse().ToArray());
        var reversedJson = Assert.Single(XbrlFactRecordMapper.ToRecords(new[] { reversedFact }, ProfileDigest))
            ["provenance_json"].AsString();
        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.EnumerateArray().ToDictionary(
            entry => entry.GetProperty("Locator").GetString()!, StringComparer.Ordinal);

        Assert.Equal(new[]
            {
                new[] { "excel-direct", "coalesce", "fact-construct" },
                new[] { "excel-fallback", "coalesce", "fact-construct" }
            }, ReadPaths(entries["Sheet1!B3"]));
        Assert.Equal(new[] { new[] { "excel-denominator", "coalesce", "fact-construct" } },
            ReadPaths(entries["Sheet1!C3"]));
        Assert.False(entries["Sheet1!D3"].TryGetProperty("TransformationPaths", out _));
        Assert.Equal(json, reversedJson);
    }

    [Fact]
    public void DuplicateFactKeysRemainBoundToSourcePathsWhenInputOrderReverses()
    {
        var source = new SourceReference("workbook", "Sheet1!B3", "Revenue row");
        var direct = Fact(new[] { source }, new[] { Path(source, "field-direct", "fact-construct") });
        var fallback = Fact(new[] { source }, new[] { Path(source, "field-fallback", "fact-construct") });

        var original = XbrlFactRecordMapper.ToRecords(new[] { direct, fallback }, ProfileDigest);
        var reversed = XbrlFactRecordMapper.ToRecords(new[] { fallback, direct }, ProfileDigest);

        Assert.Equal(KeysByPath(original), KeysByPath(reversed));
    }

    [Fact]
    public void TypedDimensionValuesHaveDistinctOrderIndependentFactKeys()
    {
        var first = Fact(Array.Empty<SourceReference>(), Array.Empty<SourceTransformationPath>(), new[]
        {
            new Dimension("synthetic:CustomerAxis", "", IsExplicit: false)
            {
                TypedValueJson = "{\"Name\":\"{urn:customer}customer\",\"Attributes\":[],\"Content\":[{\"Kind\":\"text\",\"Value\":\"0017\"}]}"
            }
        });
        var second = Fact(Array.Empty<SourceReference>(), Array.Empty<SourceTransformationPath>(), new[]
        {
            new Dimension("synthetic:CustomerAxis", "", IsExplicit: false)
            {
                TypedValueJson = "{\"Name\":\"{urn:customer}customer\",\"Attributes\":[],\"Content\":[{\"Kind\":\"text\",\"Value\":\"0042\"}]}"
            }
        });

        static IReadOnlyDictionary<string, string> KeysByTypedValue(IReadOnlyList<XBRLMappingStudio.Engine.DataRecord> records)
            => records.ToDictionary(
                record => record["dimensions_json"].AsString(),
                record => record["fact_key"].AsString(),
                StringComparer.Ordinal);

        var original = XbrlFactRecordMapper.ToRecords(new[] { first, second }, ProfileDigest);
        var reversed = XbrlFactRecordMapper.ToRecords(new[] { second, first }, ProfileDigest);

        Assert.Equal(2, original.Select(record => record["fact_key"].AsString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(KeysByTypedValue(original), KeysByTypedValue(reversed));
    }

    [Fact]
    public void ProvenanceJsonWithoutTransformationPaths_IsByteForByteCompatible()
    {
        var sources = new[]
        {
            new SourceReference("z-source", "row=2,col=4"),
            new SourceReference("a-source", "Sheet1!B3", "Revenue row")
        };
        var fact = Fact(sources, Array.Empty<SourceTransformationPath>());
        var expected = JsonSerializer.Serialize(sources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .ThenBy(source => source.Locator, StringComparer.Ordinal)
            .ThenBy(source => source.DisplayName, StringComparer.Ordinal));

        var actual = Assert.Single(XbrlFactRecordMapper.ToRecords(new[] { fact }, ProfileDigest))
            ["provenance_json"].AsString();

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("TransformationPaths", actual, StringComparison.Ordinal);
    }

    private static XbrlFact Fact(
        IReadOnlyList<SourceReference> sources,
        IReadOnlyList<SourceTransformationPath> paths,
        IReadOnlyList<Dimension>? dimensions = null)
        => new()
        {
            ConceptName = SyntheticProfile.Qualified("Revenue"),
            EntityScheme = "http://xbrl.org/entity/",
            EntityIdentifier = "ACME",
            Period = XbrlPeriod.OfInstant(new DateOnly(2025, 12, 31)),
            Unit = new XbrlUnit("QAR"),
            Value = 100m,
            Decimals = 0,
            Dimensions = dimensions ?? Array.Empty<Dimension>(),
            ContributingSources = sources,
            SourceTransformationPaths = paths
        };

    private static SourceTransformationPath Path(SourceReference source, params string[] nodeIds)
        => new(source, nodeIds);

    private static string[][] ReadPaths(JsonElement sourceEntry)
        => sourceEntry.GetProperty("TransformationPaths")
            .EnumerateArray()
            .Select(path => path.EnumerateArray().Select(node => node.GetString()!).ToArray())
            .ToArray();

    private static IReadOnlyDictionary<string, string> KeysByPath(IReadOnlyList<XBRLMappingStudio.Engine.DataRecord> records)
        => records.ToDictionary(
            record => ReadPathsFromProvenance(record["provenance_json"].AsString())[0][0],
            record => record["fact_key"].AsString(), StringComparer.Ordinal);

    private static string[][] ReadPathsFromProvenance(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadPaths(document.RootElement[0]);
    }

    private static void AssertSourceFields(JsonElement entry, SourceReference expected)
    {
        Assert.Equal(expected.SourceId, entry.GetProperty("SourceId").GetString());
        Assert.Equal(expected.Locator, entry.GetProperty("Locator").GetString());
        Assert.Equal(expected.DisplayName, entry.GetProperty("DisplayName").GetString());
    }
}
