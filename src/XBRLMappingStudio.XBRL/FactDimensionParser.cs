namespace XBRLMappingStudio.XBRL;

/// <summary>Parses explicit fact dimensions against the selected profile's declared domains.</summary>
public static class FactDimensionParser
{
    public const int MaximumDimensions = 64;

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
