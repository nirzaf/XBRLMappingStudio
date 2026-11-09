using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        var keys = ComputeFactKeys(facts, profileDigest);
        return facts.Select((fact, index) => ToRecord(fact, profileDigest, keys[index])).ToArray();
    }

    private static DataRecord ToRecord(XbrlFact fact, string profileDigest, string key)
    {
        ArgumentNullException.ThrowIfNull(fact);
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
            ["provenance_json"] = DataValue.StringValue(SerializeProvenance(fact))
        };

        return new DataRecord
        {
            RowId = key,
            Fields = fields,
            HasStableRowIdentity = true
        };
    }

    private static string[] ComputeFactKeys(IReadOnlyList<XbrlFact> facts, string profileDigest)
    {
        var keys = facts.Select(fact =>
        {
            ArgumentNullException.ThrowIfNull(fact);
            return ComputeAspectFactKey(fact, profileDigest);
        }).ToArray();
        foreach (var group in Enumerable.Range(0, keys.Length)
                     .GroupBy(index => keys[index], StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var duplicateOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            var keepsHistoricalKey = true;
            var occurrenceSignatures = group.ToDictionary(index => index,
                index => ComputeOccurrenceSignature(facts[index]));
            foreach (var index in group.OrderBy(index => occurrenceSignatures[index], StringComparer.Ordinal)
                         .ThenBy(index => index))
            {
                var signature = occurrenceSignatures[index];
                duplicateOrdinals.TryGetValue(signature, out var ordinal);
                ordinal++;
                duplicateOrdinals[signature] = ordinal;
                if (keepsHistoricalKey)
                {
                    // Keep the established aspect key for the first occurrence. Only
                    // additional colliding occurrences receive a deterministic suffix.
                    keepsHistoricalKey = false;
                    continue;
                }
                keys[index] = ComputeOccurrenceFactKey(keys[index], signature, ordinal);
            }
        }

        return keys;
    }

    private static string ComputeAspectFactKey(XbrlFact fact, string profileDigest)
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
            .ThenBy(dimension => dimension.TypedValueJson, StringComparer.Ordinal)
            .ToArray();
        builder.Append(dimensions.Length).Append(':');
        foreach (var dimension in dimensions)
        {
            AppendPart(builder, dimension.Name);
            AppendPart(builder, dimension.MemberName);
            AppendPart(builder, dimension.IsExplicit ? "1" : "0");
            // Typed values are part of the dimensional aspect identity. Keep the
            // historical explicit-dimension key contract unchanged.
            if (!dimension.IsExplicit)
                AppendPart(builder, dimension.TypedValueJson ?? "");
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
        return "sha256:" + digest;
    }

    private static string ComputeOccurrenceSignature(XbrlFact fact)
    {
        var builder = new StringBuilder();
        AppendPart(builder, fact.SemanticKey());
        var sources = fact.ContributingSources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .ThenBy(source => source.Locator, StringComparer.Ordinal)
            .ThenBy(source => source.DisplayName, StringComparer.Ordinal)
            .ToArray();
        builder.Append(sources.Length).Append(':');
        foreach (var source in sources)
        {
            AppendPart(builder, source.SourceId);
            AppendPart(builder, source.Locator);
            AppendPart(builder, source.DisplayName ?? "");
        }

        var paths = fact.SourceTransformationPaths
            .OrderBy(path => path.Source.SourceId, StringComparer.Ordinal)
            .ThenBy(path => path.Source.Locator, StringComparer.Ordinal)
            .ThenBy(path => path.Source.DisplayName, StringComparer.Ordinal)
            .ThenBy(path => path.TransformationIds,
                Comparer<IReadOnlyList<string>>.Create(CompareTransformationPaths))
            .ToArray();
        if (paths.Length > 0)
        {
            // Keep pathless occurrence signatures byte-compatible with the historical
            // key contract while binding traced occurrences to their source paths.
            AppendPart(builder, "source-transformation-paths-v1");
            builder.Append(paths.Length).Append(':');
            foreach (var path in paths)
            {
                AppendPart(builder, path.Source.SourceId);
                AppendPart(builder, path.Source.Locator);
                AppendPart(builder, path.Source.DisplayName ?? "");
                builder.Append(path.TransformationIds.Count).Append(':');
                foreach (var nodeId in path.TransformationIds)
                    AppendPart(builder, nodeId);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static string ComputeOccurrenceFactKey(string aspectKey, string occurrenceSignature, int ordinal)
    {
        var builder = new StringBuilder("xbrl-fact-occurrence-v1");
        AppendPart(builder, aspectKey);
        AppendPart(builder, occurrenceSignature);
        AppendPart(builder, ordinal.ToString(CultureInfo.InvariantCulture));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
        return "sha256:" + digest;
    }

    private static string SerializeDimensions(IReadOnlyList<Dimension> dimensions)
        => JsonSerializer.Serialize(dimensions
            .OrderBy(dimension => dimension.Name, StringComparer.Ordinal)
            .ThenBy(dimension => dimension.MemberName, StringComparer.Ordinal)
            .ThenBy(dimension => dimension.IsExplicit));

    private static string SerializeProvenance(XbrlFact fact)
    {
        var orderedSources = fact.ContributingSources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .ThenBy(source => source.Locator, StringComparer.Ordinal)
            .ThenBy(source => source.DisplayName, StringComparer.Ordinal)
            .ToList();
        if (fact.SourceTransformationPaths.Count == 0)
            return JsonSerializer.Serialize(orderedSources);

        var pathsBySource = fact.SourceTransformationPaths
            .GroupBy(path => path.Source)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string[]>)group.Select(path => path.TransformationIds.ToArray())
                    .OrderBy(path => path, Comparer<IReadOnlyList<string>>.Create(CompareTransformationPaths))
                    .ToArray());

        // A well-formed fact normally lists every path source in ContributingSources.
        // Keep an inconsistent path-only source visible instead of silently dropping it.
        foreach (var source in pathsBySource.Keys)
            if (!orderedSources.Contains(source))
                orderedSources.Add(source);
        orderedSources = orderedSources
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .ThenBy(source => source.Locator, StringComparer.Ordinal)
            .ThenBy(source => source.DisplayName, StringComparer.Ordinal)
            .ToList();

        return JsonSerializer.Serialize(orderedSources.Select(source => new SourceProvenanceEntry(
            source.SourceId,
            source.Locator,
            source.DisplayName,
            pathsBySource.TryGetValue(source, out var paths) ? paths : null)));
    }

    private sealed record SourceProvenanceEntry(
        string SourceId,
        string Locator,
        string? DisplayName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string[]>? TransformationPaths);

    private static int CompareTransformationPaths(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var sharedLength = Math.Min(left.Count, right.Count);
        for (var index = 0; index < sharedLength; index++)
        {
            var comparison = StringComparer.Ordinal.Compare(left[index], right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Count.CompareTo(right.Count);
    }

    private static DataValue NullableText(string? value)
        => value is null ? DataValue.ExplicitNil(PortShape.String) : DataValue.StringValue(value);

    private static void AppendPart(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);
}
