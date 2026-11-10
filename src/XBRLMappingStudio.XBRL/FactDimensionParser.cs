using System.Text;
using System.Text.Json;
using System.Xml;

namespace XBRLMappingStudio.XBRL;

/// <summary>Parses explicit fact dimensions against the selected profile's declared domains.</summary>
public static class FactDimensionParser
{
    public const int MaximumDimensions = 64;
    public const int MaximumJsonLength = 256 * 1024;
    public const int MaximumTypedValueCharacters = XbrlFact.MaximumTypedDimensionValueCharacters;

    /// <summary>Parses bounded dimension JSON emitted by report adapters.</summary>
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

    /// <summary>
    /// Parses the fact-inspector representation: legacy axis=member lines, or a
    /// bounded JSON dimension array when typed values are needed.
    /// </summary>
    public static bool TryParseForProfile(
        string? text,
        FilingProfile profile,
        out IReadOnlyList<Dimension> dimensions,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            return TryParseJson(text, profile, out dimensions, out error);
        return TryParse(text, profile.DimensionDomains, out dimensions, out error);
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
            var resolvedAxes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return Fail("Each dimension JSON item must be an object.", out dimensions, out error);
                string? name = null;
                string? member = null;
                bool? isExplicit = null;
                string? typedValue = null;
                string? typedValueJson = null;
                string? axisExpandedName = null;
                string? memberExpandedName = null;
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
                        case "TypedValueJson" or "typedValueJson" when typedValueJson is null
                            && property.Value.ValueKind == JsonValueKind.String:
                            typedValueJson = property.Value.GetString();
                            break;
                        case "AxisExpandedName" or "axisExpandedName" when axisExpandedName is null
                            && property.Value.ValueKind == JsonValueKind.String:
                            axisExpandedName = property.Value.GetString();
                            break;
                        case "MemberExpandedName" or "memberExpandedName" when memberExpandedName is null
                            && property.Value.ValueKind == JsonValueKind.String:
                            memberExpandedName = property.Value.GetString();
                            break;
                        default:
                            return Fail($"Dimension JSON property '{property.Name}' is unsupported or malformed.", out dimensions, out error);
                    }
                }
                if (string.IsNullOrWhiteSpace(name) || !seenAxes.Add(name))
                    return Fail("Each dimension requires a unique, non-empty Name.", out dimensions, out error);
                if (isExplicit == false)
                {
                    if (!string.IsNullOrEmpty(member))
                        return Fail($"Typed dimensions such as '{name}' require a supported xs:string domain and a bounded TypedValue.", out dimensions, out error);
                    if (typedValueJson is not null)
                    {
                        if (!TryReadCanonicalXsString(typedValueJson, axisExpandedName,
                                typedDimensionDomains, namespaceUris, out var evidenceValue)
                            || typedValue is not null && !StringComparer.Ordinal.Equals(typedValue, evidenceValue))
                            return Fail($"Typed dimension evidence for '{name}' does not match the selected profile's simple xs:string domain.", out dimensions, out error);
                        typedValue ??= evidenceValue;
                    }
                    if (typedValue is null || typedValue.Length > MaximumTypedValueCharacters
                        || !HasValidXmlCharacters(typedValue)
                        || !TryFindTypedDimension(name, axisExpandedName, typedDimensionDomains,
                            namespaceUris, out var canonicalAxisName, out var typedMetadata)
                        || !IsSupportedXsString(typedMetadata, namespaceUris))
                        return Fail($"Typed dimensions such as '{name}' require a supported xs:string domain and a bounded TypedValue.", out dimensions, out error);
                    if (!resolvedAxes.Add(canonicalAxisName))
                        return Fail($"Dimension axis '{canonicalAxisName}' is duplicated after profile namespace resolution.", out dimensions, out error);
                    typed.Add(new Dimension(canonicalAxisName, string.Empty, IsExplicit: false) { TypedValue = typedValue });
                    continue;
                }
                if (typedValue is not null || typedValueJson is not null)
                    return Fail("Explicit dimensions cannot carry typed-value data.", out dimensions, out error);
                if (string.IsNullOrWhiteSpace(member))
                    return Fail("Each explicit dimension requires Name and MemberName QNames.", out dimensions, out error);
                if ((axisExpandedName is null) != (memberExpandedName is null))
                    return Fail("Expanded-name evidence for an explicit dimension must include both axis and member names.", out dimensions, out error);
                if (axisExpandedName is not null)
                {
                    if (!TryResolveExplicitNames(name, member, axisExpandedName, memberExpandedName!,
                            dimensionDomains, namespaceUris, out var canonicalAxisName, out var canonicalMemberName))
                        return Fail("Expanded-name evidence does not resolve to one member in the selected filing profile.", out dimensions, out error);
                    name = canonicalAxisName;
                    member = canonicalMemberName;
                }
                if (!resolvedAxes.Add(name))
                    return Fail($"Dimension axis '{name}' is duplicated after profile namespace resolution.", out dimensions, out error);
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

    private static bool TryFindTypedDimension(
        string importedAxisName,
        string? axisExpandedName,
        IReadOnlyDictionary<string, TypedDimensionDomainMetadata> typedDimensionDomains,
        IReadOnlyDictionary<string, string> namespaceUris,
        out string canonicalAxisName,
        out TypedDimensionDomainMetadata metadata)
    {
        canonicalAxisName = string.Empty;
        metadata = null!;
        if (typedDimensionDomains.TryGetValue(importedAxisName, out var direct))
        {
            if (axisExpandedName is not null
                && (!TryExpandProfileQName(importedAxisName, namespaceUris, out var directExpanded)
                    || !StringComparer.Ordinal.Equals(directExpanded, axisExpandedName)))
                return false;
            canonicalAxisName = importedAxisName;
            metadata = direct;
            return true;
        }
        if (axisExpandedName is null) return false;

        var matches = typedDimensionDomains
            .Where(entry => TryExpandProfileQName(entry.Key, namespaceUris, out var expanded)
                && StringComparer.Ordinal.Equals(expanded, axisExpandedName))
            .ToArray();
        if (matches.Length != 1) return false;
        canonicalAxisName = matches[0].Key;
        metadata = matches[0].Value;
        return true;
    }

    private static bool TryResolveExplicitNames(
        string importedAxisName,
        string importedMemberName,
        string axisExpandedName,
        string memberExpandedName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> dimensionDomains,
        IReadOnlyDictionary<string, string> namespaceUris,
        out string canonicalAxisName,
        out string canonicalMemberName)
    {
        canonicalAxisName = string.Empty;
        canonicalMemberName = string.Empty;
        var axisMatches = dimensionDomains.Keys
            .Where(axis => TryExpandProfileQName(axis, namespaceUris, out var expanded)
                && StringComparer.Ordinal.Equals(expanded, axisExpandedName))
            .ToArray();
        if (axisMatches.Length != 1) return false;
        var memberMatches = dimensionDomains[axisMatches[0]]
            .Where(member => TryExpandProfileQName(member, namespaceUris, out var expanded)
                && StringComparer.Ordinal.Equals(expanded, memberExpandedName))
            .ToArray();
        if (memberMatches.Length != 1) return false;

        // When the importer's lexical names already match the profile, require the
        // namespace evidence to agree. Aliased source prefixes are canonicalized above.
        if (StringComparer.Ordinal.Equals(importedAxisName, axisMatches[0])
            && StringComparer.Ordinal.Equals(importedMemberName, memberMatches[0]))
        {
            canonicalAxisName = importedAxisName;
            canonicalMemberName = importedMemberName;
        }
        else
        {
            canonicalAxisName = axisMatches[0];
            canonicalMemberName = memberMatches[0];
        }
        return true;
    }

    private static bool TryReadCanonicalXsString(
        string evidenceJson,
        string? axisExpandedName,
        IReadOnlyDictionary<string, TypedDimensionDomainMetadata> typedDimensionDomains,
        IReadOnlyDictionary<string, string> namespaceUris,
        out string? typedValue)
    {
        typedValue = null;
        if (axisExpandedName is null || evidenceJson.Length > MaximumJsonLength) return false;
        var metadataMatches = typedDimensionDomains
            .Where(entry => TryExpandProfileQName(entry.Key, namespaceUris, out var expanded)
                && StringComparer.Ordinal.Equals(expanded, axisExpandedName))
            .ToArray();
        if (metadataMatches.Length != 1
            || !IsSupportedXsString(metadataMatches[0].Value, namespaceUris)
            || !TryExpandProfileQName(metadataMatches[0].Value.DomainElementQName, namespaceUris,
                out var expectedElementName))
            return false;

        try
        {
            using var document = JsonDocument.Parse(evidenceJson, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? elementName = null;
            JsonElement attributes = default;
            JsonElement content = default;
            bool hasAttributes = false;
            bool hasContent = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "Name" when elementName is null && property.Value.ValueKind == JsonValueKind.String:
                        elementName = property.Value.GetString();
                        break;
                    case "Attributes" when !hasAttributes && property.Value.ValueKind == JsonValueKind.Array:
                        attributes = property.Value;
                        hasAttributes = true;
                        break;
                    case "Content" when !hasContent && property.Value.ValueKind == JsonValueKind.Array:
                        content = property.Value;
                        hasContent = true;
                        break;
                    default:
                        return false;
                }
            }
            if (!StringComparer.Ordinal.Equals(elementName, expectedElementName)
                || !hasAttributes || attributes.GetArrayLength() != 0 || !hasContent)
                return false;

            var value = new StringBuilder();
            foreach (var node in content.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) return false;
                string? kind = null;
                string? text = null;
                bool hasTarget = false;
                bool hasData = false;
                bool hasElement = false;
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "Kind" && kind is null && property.Value.ValueKind == JsonValueKind.String)
                        kind = property.Value.GetString();
                    else if (property.Name == "Value" && text is null && property.Value.ValueKind == JsonValueKind.String)
                        text = property.Value.GetString();
                    else if (property.Name == "Target" && !hasTarget
                        && property.Value.ValueKind == JsonValueKind.Null)
                        hasTarget = true;
                    else if (property.Name == "Data" && !hasData
                        && property.Value.ValueKind == JsonValueKind.Null)
                        hasData = true;
                    else if (property.Name == "Element" && !hasElement
                        && property.Value.ValueKind == JsonValueKind.Null)
                        hasElement = true;
                    else
                        return false;
                }
                if (!StringComparer.Ordinal.Equals(kind, "text") || text is null
                    || !hasTarget || !hasData || !hasElement) return false;
                value.Append(text);
                if (value.Length > MaximumTypedValueCharacters) return false;
            }
            var normalized = value.ToString();
            if (!HasValidXmlCharacters(normalized)) return false;
            typedValue = normalized;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExpandProfileQName(
        string qname,
        IReadOnlyDictionary<string, string> namespaceUris,
        out string expandedName)
    {
        expandedName = string.Empty;
        var separator = qname.IndexOf(':');
        if (separator <= 0 || separator != qname.LastIndexOf(':') || separator == qname.Length - 1)
            return false;
        var prefix = qname[..separator];
        var localName = qname[(separator + 1)..];
        if (!namespaceUris.TryGetValue(prefix, out var namespaceUri)) return false;
        try
        {
            XmlConvert.VerifyNCName(prefix);
            XmlConvert.VerifyNCName(localName);
        }
        catch (XmlException)
        {
            return false;
        }
        expandedName = $"{{{namespaceUri}}}{localName}";
        return true;
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
    {
        var ordered = dimensions.OrderBy(dimension => dimension.Name, StringComparer.Ordinal).ToArray();
        if (ordered.Any(dimension => !dimension.IsExplicit))
            return JsonSerializer.Serialize(ordered.Select(dimension => new
            {
                dimension.Name,
                dimension.MemberName,
                dimension.IsExplicit,
                dimension.TypedValue
            }));
        return string.Join("\n", ordered.Select(dimension => $"{dimension.Name}={dimension.MemberName}"));
    }
}
