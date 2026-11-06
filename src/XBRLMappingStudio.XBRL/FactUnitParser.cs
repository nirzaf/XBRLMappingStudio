namespace XBRLMappingStudio.XBRL;

/// <summary>Parses FactConstruct unit expressions against the selected filing profile.</summary>
public static class FactUnitParser
{
    public const int MaximumMeasures = 64;

    /// <summary>Checks a parsed measure expression against both the profile and concept type.</summary>
    public static bool TryValidateForConcept(
        ConceptMetadata concept,
        FilingProfile profile,
        XbrlUnit? unit,
        CompoundUnit? compoundUnit,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(concept);
        ArgumentNullException.ThrowIfNull(profile);
        error = string.Empty;
        if (unit is not null && compoundUnit is not null)
            return Fail("A fact cannot have both a simple and a compound unit.", out error);

        if (unit is not null
            && (string.IsNullOrWhiteSpace(unit.Measure)
                || unit.Measure.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)
                    || character is '/' or '*' or '^')
                || unit.Power is 0 or < -MaximumMeasures or > MaximumMeasures
                || !profile.SupportsUnitMeasure(unit.Measure)))
            return Fail($"Unit measure '{unit.Measure}' or its power is invalid or unsupported by profile '{profile.ProfileId}'.", out error);

        if (compoundUnit is not null)
        {
            if (compoundUnit.Numerators is null || compoundUnit.Numerators.Count == 0
                || compoundUnit.Numerators.Count + compoundUnit.DenominatorList.Count > MaximumMeasures
                || compoundUnit.Numerators.Concat(compoundUnit.DenominatorList).Any(measure =>
                    string.IsNullOrWhiteSpace(measure)
                    || measure.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)
                        || character is '/' or '*' or '^')
                    || !profile.SupportsUnitMeasure(measure)))
                return Fail("Compound unit has missing, invalid, or unsupported measures.", out error);
            if (compoundUnit.DenominatorList.Intersect(compoundUnit.Numerators, StringComparer.Ordinal).Any())
                return Fail("Compound unit must cancel shared measures before division.", out error);
        }

        if (!concept.IsNumeric)
        {
            if (unit is not null || compoundUnit is not null)
                return Fail($"Non-numeric concept '{concept.QualifiedName}' cannot have a unit.", out error);
            return true;
        }
        if (unit is null && compoundUnit is null)
            return Fail($"Numeric concept '{concept.QualifiedName}' requires a unit.", out error);

        var typeName = concept.DataTypeLocalName;
        if (StringComparer.OrdinalIgnoreCase.Equals(typeName, "monetary"))
        {
            if (unit is null || compoundUnit is not null || unit.Power != 1
                || !profile.SupportedUnits.Contains(unit.Measure))
                return Fail($"Monetary concept '{concept.QualifiedName}' requires one currency measure supported by profile '{profile.ProfileId}'.", out error);
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(typeName, "shares"))
        {
            if (unit is not { Measure: "shares", Power: 1 } || compoundUnit is not null)
                return Fail($"Shares concept '{concept.QualifiedName}' requires one xbrli:shares measure.", out error);
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(typeName, "pure"))
        {
            if (unit is not { Measure: "pure", Power: 1 } || compoundUnit is not null)
                return Fail($"Pure concept '{concept.QualifiedName}' requires one xbrli:pure measure.", out error);
        }

        return true;
    }

    /// <summary>
    /// Parses a simple measure or a product/ratio expression such as <c>USD</c>,
    /// <c>shares*shares</c>, or <c>USD/shares</c>. Blank input means no unit.
    /// </summary>
    public static bool TryParse(
        string? expression,
        FilingProfile profile,
        out XbrlUnit? unit,
        out CompoundUnit? compoundUnit,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(profile);
        unit = null;
        compoundUnit = null;
        error = string.Empty;

        var input = expression?.Trim();
        if (string.IsNullOrEmpty(input)) return true;

        var sides = input.Split('/');
        if (sides.Length > 2 || sides.Any(string.IsNullOrEmpty))
            return Fail($"Unit expression '{input}' must contain at most one non-empty division separator.", out error);

        var numerators = sides[0].Split('*', StringSplitOptions.None);
        var denominators = sides.Length == 2
            ? sides[1].Split('*', StringSplitOptions.None)
            : Array.Empty<string>();
        if (numerators.Any(string.IsNullOrWhiteSpace) || denominators.Any(string.IsNullOrWhiteSpace))
            return Fail($"Unit expression '{input}' contains an empty measure.", out error);
        if (numerators.Length + denominators.Length > MaximumMeasures)
            return Fail($"Unit expression '{input}' exceeds the {MaximumMeasures}-measure limit.", out error);

        foreach (var measure in numerators.Concat(denominators))
        {
            if (measure.Any(char.IsWhiteSpace) || measure.Any(char.IsControl)
                || !profile.SupportsUnitMeasure(measure))
                return Fail($"Unit measure '{measure}' is not supported by profile '{profile.ProfileId}'.", out error);
        }
        numerators = numerators.Select(measure => NormalizeMeasure(measure, profile)).ToArray();
        denominators = denominators.Select(measure => NormalizeMeasure(measure, profile)).ToArray();
        if (denominators.Intersect(numerators, StringComparer.Ordinal).Any())
            return Fail($"Unit expression '{input}' must cancel shared measures before division.", out error);

        if (denominators.Length == 0 && numerators.Length == 1)
        {
            unit = new XbrlUnit(numerators[0]);
            return true;
        }
        if (denominators.Length == 0 && numerators.All(measure => StringComparer.Ordinal.Equals(measure, numerators[0])))
        {
            unit = new XbrlUnit(numerators[0], numerators.Length);
            return true;
        }
        if (numerators.Length == 1 && StringComparer.Ordinal.Equals(numerators[0], "pure")
            && denominators.Length > 0
            && denominators.All(measure => StringComparer.Ordinal.Equals(measure, denominators[0])))
        {
            unit = new XbrlUnit(denominators[0], -denominators.Length);
            return true;
        }

        compoundUnit = new CompoundUnit(numerators, denominators);
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static string NormalizeMeasure(string measure, FilingProfile profile)
    {
        var separator = measure.IndexOf(':');
        if (separator <= 0) return measure;
        var prefix = measure[..separator];
        var localName = measure[(separator + 1)..];
        return prefix == "iso4217" && profile.SupportedUnits.Contains(localName)
            || prefix == "xbrli" && profile.SupportsUnitMeasure(localName)
                ? localName
                : measure;
    }
}
