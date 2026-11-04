using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Converts validated XBRL facts to the stable row shape used by durable destinations.
/// Decimal values remain CLR decimals so each writer can preserve their invariant
/// lexical representation without narrowing financial values.
/// </summary>
public static class XbrlFactRecordMapper
{
    public static IReadOnlyList<string> OutputColumns { get; } = Array.AsReadOnly(new[]
    {
        "fact_key", "profile_digest", "concept", "entity_scheme", "entity_identifier",
        "period_kind", "instant_date", "period_start", "period_end", "unit",
        "compound_unit", "value", "text_value", "is_nil", "decimals", "language",
        "dimensions_json", "provenance_json"
    });

    public static IReadOnlyList<DataRecord> ToRecords(
        IReadOnlyList<XbrlFact> facts,
        string profileDigest)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDigest);

        return facts.Select(fact => ToRecord(fact, profileDigest)).ToArray();
    }

    private static DataRecord ToRecord(XbrlFact fact, string profileDigest)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var key = ComputeFactKey(fact, profileDigest);
        var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal)
        {
            ["fact_key"] = DataValue.StringValue(key),
            ["profile_digest"] = DataValue.StringValue(profileDigest),
            ["concept"] = DataValue.StringValue(fact.ConceptName),
            ["entity_scheme"] = DataValue.StringValue(fact.EntityScheme),
            ["entity_identifier"] = DataValue.StringValue(fact.EntityIdentifier),
            ["period_kind"] = DataValue.StringValue(fact.Period.Kind.ToString().ToLowerInvariant()),
            ["instant_date"] = NullableText(fact.Period.Instant?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["period_start"] = NullableText(fact.Period.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["period_end"] = NullableText(fact.Period.End?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ["unit"] = NullableText(fact.Unit?.ToString()),
            ["compound_unit"] = NullableText(fact.CompoundUnit?.CanonicalKey),
            ["value"] = fact.Value is { } number
                ? DataValue.DecimalValue(number)
                : DataValue.ExplicitNil(PortShape.Decimal),
            ["text_value"] = NullableText(fact.TextValue),
            ["is_nil"] = DataValue.BoolValue(fact.IsNil),
            ["decimals"] = fact.Decimals is { } decimals
                ? DataValue.IntegerValue(decimals)
                : DataValue.ExplicitNil(PortShape.Integer),
            ["language"] = NullableText(fact.Language),
            ["dimensions_json"] = DataValue.StringValue(SerializeDimensions(fact.Dimensions)),
            ["provenance_json"] = DataValue.StringValue(SerializeProvenance(fact.ContributingSources))
        };

        return new DataRecord
        {
            RowId = key,
            Fields = fields,
            HasStableRowIdentity = true
        };
    }

    private static string ComputeFactKey(XbrlFact fact, string profileDigest)
    {
        var builder = new StringBuilder();
        AppendPart(builder, profileDigest);
        AppendPart(builder, fact.ConceptName);
        AppendPart(builder, fact.EntityScheme);
        AppendPart(builder, fact.EntityIdentifier);
        AppendPart(builder, fact.Period.Kind.ToString());
        AppendPart(builder, fact.Period.Instant?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        AppendPart(builder, fact.Period.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        AppendPart(builder, fact.Period.End?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        AppendPart(builder, fact.Unit?.ToString() ?? "");
        AppendPart(builder, fact.CompoundUnit?.CanonicalKey ?? "");
        AppendPart(builder, fact.Language ?? "");

        var dimensions = fact.Dimensions
            .OrderBy(dimension => dimension.Name, StringComparer.Ordinal)
            .ThenBy(dimension => dimension.MemberName, StringComparer.Ordinal)
            .ThenBy(dimension => dimension.IsExplicit)
            .ToArray();
        builder.Append(dimensions.Length).Append(':');
        foreach (var dimension in dimensions)
        {
            AppendPart(builder, dimension.Name);
            AppendPart(builder, dimension.MemberName);
            AppendPart(builder, dimension.IsExplicit ? "1" : "0");
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
        return "sha256:" + digest;
    }

    private static string SerializeDimensions(IReadOnlyList<Dimension> dimensions)
        => JsonSerializer.Serialize(dimensions
            .OrderBy(dimension => dimension.Name, StringComparer.Ordinal)
            .ThenBy(dimension => dimension.MemberName, StringComparer.Ordinal)
            .ThenBy(dimension => dimension.IsExplicit));

    private static string SerializeProvenance(IReadOnlyList<SourceReference> sources)
        => JsonSerializer.Serialize(sources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .ThenBy(source => source.Locator, StringComparer.Ordinal)
            .ThenBy(source => source.DisplayName, StringComparer.Ordinal));

    private static DataValue NullableText(string? value)
        => value is null ? DataValue.ExplicitNil(PortShape.String) : DataValue.StringValue(value);

    private static void AppendPart(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);
}
