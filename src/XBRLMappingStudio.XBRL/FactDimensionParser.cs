using System.Text.Json;
using System.Xml;

namespace XBRLMappingStudio.XBRL;

/// <summary>Parses explicit fact dimensions against the selected profile's declared domains.</summary>
public static class FactDimensionParser
{
    public const int MaximumDimensions = 64;
    public const int MaximumJsonLength = 256 * 1024;
    public const int MaximumTypedValueCharacters = XbrlFact.MaximumTypedDimensionValueCharacters;

    /// <summary>Parses the bounded explicit-dimension JSON emitted by report adapters.</summary>
    public static bool TryParseJson(
        string? json,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dimensionDomains,
        out IReadOnlyList<Dimension> dimensions,
        out string error)
        => TryParseJson(json, dimensionDomains,
            new Dictionary<string, TypedDimensionDomainMetadata>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal), out dimensions, out error);

    public static bool TryParseJson(
        string? json,
        FilingProfile profile,
        out IReadOnlyList<Dimension> dimensions,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return TryParseJson(json, profile.DimensionDomains, profile.TypedDimensionDomains,
            profile.NamespaceUris, out dimensions, out error);
    }

    public static bool TryParseJson(
        string? json,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dimensionDomains,
        IReadOnlyDictionary<string, TypedDimensionDomainMetadata> typedDimensionDomains,
        IReadOnlyDictionary<string, string> namespaceUris,
        out IReadOnlyList<Dimension> dimensions,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(dimensionDomains);
        ArgumentNullException.ThrowIfNull(typedDimensionDomains);
        ArgumentNullException.ThrowIfNull(namespaceUris);
        if (string.IsNullOrWhiteSpace(json))
        {
            dimensions = Array.Empty<Dimension>();
            error = string.Empty;
            return true;
        }
        if (json.Length > MaximumJsonLength)
        {
            dimensions = Array.Empty<Dimension>();
            error = $"Dimension JSON exceeds the {MaximumJsonLength}-character limit.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Fail("Dimension JSON must be an array.", out dimensions, out error);
            var array = document.RootElement;
            if (array.GetArrayLength() > MaximumDimensions)
                return Fail($"A fact may have at most {MaximumDimensions} explicit dimensions.", out dimensions, out error);

            var lines = new List<string>(array.GetArrayLength());
            var typed = new List<Dimension>();
            var seenAxes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return Fail("Each dimension JSON item must be an object.", out dimensions, out error);
                string? name = null;
                string? member = null;
                bool? isExplicit = null;
                string? typedValue = null;
                foreach (var property in item.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "Name" or "name" when name is null && property.Value.ValueKind == JsonValueKind.String:
                            name = property.Value.GetString();
                            break;
                        case "MemberName" or "memberName" when member is null && property.Value.ValueKind == JsonValueKind.String:
                            member = property.Value.GetString();
                            break;
                        case "IsExplicit" or "isExplicit" when isExplicit is null
                            && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                            isExplicit = property.Value.GetBoolean();
                            break;
                        case "TypedValue" or "typedValue" when typedValue is null
                            && property.Value.ValueKind == JsonValueKind.String:
                            typedValue = property.Value.GetString();
                            break;
                        default:
                            return Fail($"Dimension JSON property '{property.Name}' is unsupported or malformed.", out dimensions, out error);
                    }
                }
                if (string.IsNullOrWhiteSpace(name) || !seenAxes.Add(name))
                    return Fail("Each dimension requires a unique, non-empty Name.", out dimensions, out error);
                if (isExplicit == false)
                {
                    if (!string.IsNullOrEmpty(member) || typedValue is null
                        || typedValue.Length > MaximumTypedValueCharacters
                        || !HasValidXmlCharacters(typedValue)
                        || !typedDimensionDomains.TryGetValue(name, out var typedMetadata)
                        || !IsSupportedXsString(typedMetadata, namespaceUris))
                        return Fail($"Typed dimensions such as '{name}' require a supported xs:string domain and a bounded TypedValue.", out dimensions, out error);
                    typed.Add(new Dimension(name, string.Empty, IsExplicit: false) { TypedValue = typedValue });
                    continue;
                }
                if (typedValue is not null && isExplicit != false)
                    return Fail("Explicit dimensions cannot also carry TypedValue.", out dimensions, out error);
                if (string.IsNullOrWhiteSpace(member))
                    return Fail("Each explicit dimension requires Name and MemberName QNames.", out dimensions, out error);
                lines.Add($"{name}={member}");
            }
            if (typed.Count + lines.Count > MaximumDimensions)
                return Fail($"A fact may have at most {MaximumDimensions} dimensions.", out dimensions, out error);
            if (!TryParse(string.Join("\n", lines), dimensionDomains, out var explicitDimensions, out error))
            {
                dimensions = Array.Empty<Dimension>();
                return false;
            }
            dimensions = Array.AsReadOnly(typed.Concat(explicitDimensions)
                .OrderBy(dimension => dimension.Name, StringComparer.Ordinal).ToArray());
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return Fail("Dimension JSON is malformed.", out dimensions, out error);
        }
    }

    private static bool IsSupportedXsString(
        TypedDimensionDomainMetadata? metadata,
        IReadOnlyDictionary<string, string> namespaceUris)
    {
        if (metadata is null) return false;
        var separator = metadata.DataTypeQName.IndexOf(':');
        return separator > 0 && separator == metadata.DataTypeQName.LastIndexOf(':')
            && separator < metadata.DataTypeQName.Length - 1
            && namespaceUris.TryGetValue(metadata.DataTypeQName[..separator], out var uri)
            && StringComparer.Ordinal.Equals(uri, "http://www.w3.org/2001/XMLSchema")
            && StringComparer.Ordinal.Equals(metadata.DataTypeQName[(separator + 1)..], "string");
    }

    private static bool HasValidXmlCharacters(string value)
    {
        try { XmlConvert.VerifyXmlChars(value); return true; }
        catch (XmlException) { return false; }
    }

    private static bool Fail(string message, out IReadOnlyList<Dimension> dimensions, out string error)
    {
        dimensions = Array.Empty<Dimension>();
        error = message;
        return false;
    }

    public static bool TryParse(
        string? text,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dimensionDomains,
        out IReadOnlyList<Dimension> dimensions,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(dimensionDomains);
        if (string.IsNullOrWhiteSpace(text))
        {
            dimensions = Array.Empty<Dimension>();
            error = string.Empty;
            return true;
        }

        var lines = text.Split(
            new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaximumDimensions)
        {
            dimensions = Array.Empty<Dimension>();
            error = $"A fact may have at most {MaximumDimensions} explicit dimensions.";
            return false;
        }

        var parsed = new List<Dimension>(lines.Length);
        var seenAxes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator != line.LastIndexOf('='))
            {
                dimensions = Array.Empty<Dimension>();
                error = $"Dimension line {index + 1} must use axisQName=memberQName.";
                return false;
            }

            var axis = line[..separator].Trim();
            var member = line[(separator + 1)..].Trim();
            if (axis.Length == 0 || member.Length == 0)
            {
                dimensions = Array.Empty<Dimension>();
                error = $"Dimension line {index + 1} requires both an axis and a member QName.";
                return false;
            }
            if (!seenAxes.Add(axis))
            {
                dimensions = Array.Empty<Dimension>();
                error = $"Dimension axis '{axis}' is declared more than once.";
                return false;
            }
            if (!dimensionDomains.TryGetValue(axis, out var members))
            {
                dimensions = Array.Empty<Dimension>();
                error = $"Dimension axis '{axis}' is not declared by the selected filing profile.";
                return false;
            }
            if (!members.Contains(member, StringComparer.Ordinal))
            {
                dimensions = Array.Empty<Dimension>();
                error = $"Member '{member}' is not declared for dimension axis '{axis}'.";
                return false;
            }

            parsed.Add(new Dimension(axis, member));
        }

        dimensions = Array.AsReadOnly(parsed.OrderBy(dimension => dimension.Name, StringComparer.Ordinal).ToArray());
        error = string.Empty;
        return true;
    }

    public static string Format(IEnumerable<Dimension> dimensions)
        => string.Join("\n", dimensions
            .OrderBy(dimension => dimension.Name, StringComparer.Ordinal)
            .Select(dimension => $"{dimension.Name}={dimension.MemberName}"));
}
