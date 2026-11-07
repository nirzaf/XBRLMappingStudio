using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Period kind per XBRL 2.1 / OIM: either an instant or a duration (start..end).
/// </summary>
public enum PeriodKind
{
    Instant,
    Duration
}

/// <summary>Structured aspect category for a rejected fact.</summary>
public enum FactValidationIssue
{
    None,
    InvalidConcept,
    InvalidEntity,
    InvalidPeriod,
    PeriodTypeMismatch,
    InvalidUnit,
    InvalidDecimals,
    InvalidDimensions,
    InvalidLanguage,
    InvalidValue
}

/// <summary>
/// XBRL period value. Dates are stored as <see cref="DateOnly"/> in the ISO proleptic
/// Gregorian calendar. For an instant, <see cref="Instant"/> is set and start/end are null.
/// For a duration, start (inclusive) and end (exclusive per OIM) are populated.
/// </summary>
public sealed record XbrlPeriod(
    PeriodKind Kind,
    DateOnly? Instant,
    DateOnly? Start,
    DateOnly? End)
{
    public static XbrlPeriod OfInstant(DateOnly d) => new(PeriodKind.Instant, d, null, null);

    /// <summary>
    /// Duration where <paramref name="startInclusive"/> and <paramref name="endExclusive"/>
    /// follow the OIM convention (per section 5.2 fixture: calendar year ends at midnight
    /// of the following Jan 1).
    /// </summary>
    public static XbrlPeriod OfDuration(DateOnly startInclusive, DateOnly endExclusive)
        => new(PeriodKind.Duration, null, startInclusive, endExclusive);

    public string ToIsoString() => Kind switch
    {
        PeriodKind.Instant => Instant?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        PeriodKind.Duration => $"{Start:yyyy-MM-dd}/{End:yyyy-MM-dd}",
        _ => throw new InvalidOperationException()
    };

    public override string ToString() => ToIsoString();
}

/// <summary>
/// A single unit measure. Compound multiply/divide units use <see cref="CompoundUnit"/>.
/// </summary>
public sealed record XbrlUnit(
    string Measure,
    int Power = 1)
{
    public override string ToString() => Power == 1 ? Measure : $"{Measure}^{Power}";
}

/// <summary>
/// Immutable fact value per XBR04, including numeric and textual facts with typed aspects.
/// </summary>
public sealed class XbrlFact
{
    public required string ConceptName { get; init; }         // qualified name: "synthetic:Revenue"
    public required string EntityScheme { get; init; }
    public required string EntityIdentifier { get; init; }
    public required XbrlPeriod Period { get; init; }
    public XbrlUnit? Unit { get; init; }
    /// <summary>Compound (multiply/divide) unit for measures such as USD per share (XBR04).
    /// Monetary facts use a single currency measure; other numeric concepts may use a compound unit.</summary>
    public CompoundUnit? CompoundUnit { get; init; }
    public decimal? Value { get; init; }
    public string? TextValue { get; init; }
    public bool IsNil { get; init; }
    public int? Decimals { get; init; }
    public string? Language { get; init; }

    /// <summary>Explicit dimensional aspects carried by this fact (XBR04).</summary>
    public IReadOnlyList<Dimension> Dimensions { get; init; } = Array.Empty<Dimension>();

    /// <summary>Set of source references contributing to this fact.</summary>
    public IReadOnlyList<SourceReference> ContributingSources { get; init; }
        = Array.Empty<SourceReference>();

    /// <summary>
    /// Row-phase mapping node IDs that reach this fact's FactConstruct, in deterministic order.
    /// This fact-level union does not identify a separate transformation path for each source.
    /// </summary>
    public IReadOnlyList<string> TransformationIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Connected row- and supported set-phase transformation IDs grouped by source
    /// occurrence. These paths describe graph connectivity; nodes that merge alternatives
    /// may include inputs that did not determine the selected value.
    /// </summary>
    public IReadOnlyList<SourceTransformationPath> SourceTransformationPaths { get; init; }
        = Array.Empty<SourceTransformationPath>();

    public bool ValidateValueAgainst(ConceptMetadata meta, out string reason)
        => ValidateValueAgainst(meta, out reason, out _);

    public bool ValidateValueAgainst(ConceptMetadata meta, out string reason, out FactValidationIssue issue)
    {
        if (Value is not null && TextValue is not null)
        {
            return Fail(FactValidationIssue.InvalidValue, "A fact cannot have both a numeric and text value.", out reason, out issue);
        }
        if (IsNil && (Value is not null || TextValue is not null))
        {
            return Fail(FactValidationIssue.InvalidValue, "A nil fact cannot also carry a value.", out reason, out issue);
        }
        if (!IsNil && Value is null && TextValue is null)
        {
            return Fail(FactValidationIssue.InvalidValue, "A non-nil fact must carry a value.", out reason, out issue);
        }
        if (Unit is not null && CompoundUnit is not null)
        {
            return Fail(FactValidationIssue.InvalidUnit, "A fact cannot have both a simple and compound unit.", out reason, out issue);
        }
        if (Unit is not null
            && (string.IsNullOrWhiteSpace(Unit.Measure) || Unit.Measure.Any(character =>
                    char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '*' or '^')
                || Unit.Power is 0 or < -FactUnitParser.MaximumMeasures or > FactUnitParser.MaximumMeasures))
        {
            return Fail(FactValidationIssue.InvalidUnit, "A simple unit has an invalid measure or power.", out reason, out issue);
        }
        if (CompoundUnit is not null
            && (CompoundUnit.Numerators is null
                || CompoundUnit.Numerators.Count == 0
                || CompoundUnit.Numerators.Count + CompoundUnit.DenominatorList.Count > FactUnitParser.MaximumMeasures
                || CompoundUnit.Numerators.Concat(CompoundUnit.DenominatorList).Any(measure =>
                    string.IsNullOrWhiteSpace(measure) || measure.Any(char.IsWhiteSpace) || measure.Any(char.IsControl)
                    || measure.Any(character => character is '/' or '*' or '^'))
                || CompoundUnit.DenominatorList.Intersect(CompoundUnit.Numerators, StringComparer.Ordinal).Any()))
        {
            return Fail(FactValidationIssue.InvalidUnit, "A compound unit has missing or invalid measures.", out reason, out issue);
        }
        if (Decimals is not null && (IsNil || Value is null))
        {
            return Fail(FactValidationIssue.InvalidDecimals, "Decimals are only valid for a non-nil numeric fact.", out reason, out issue);
        }
        if (Decimals is < -30 or > 30)
        {
            return Fail(FactValidationIssue.InvalidDecimals, "Decimals are outside the supported range [-30, 30].", out reason, out issue);
        }
        var dataType = GetDataTypeLocalName(meta.DataType);
        if (meta.IsNumeric && TextValue is not null)
        {
            return Fail(FactValidationIssue.InvalidValue, $"Numeric concept '{ConceptName}' requires a numeric value.", out reason, out issue);
        }
        if (!meta.IsNumeric && Value is not null)
        {
            return Fail(FactValidationIssue.InvalidValue, $"Non-numeric concept '{ConceptName}' cannot carry a numeric value.", out reason, out issue);
        }
        if (meta.IsNumeric && Language is not null)
        {
            return Fail(FactValidationIssue.InvalidLanguage, "A numeric fact cannot carry a language tag.", out reason, out issue);
        }
        if (StringComparer.OrdinalIgnoreCase.Equals(dataType, "monetary") && Unit is null)
        {
            return Fail(FactValidationIssue.InvalidUnit, $"Concept '{ConceptName}' is monetary and requires a unit.", out reason, out issue);
        }
        if (meta.IsNumeric && Unit is null && CompoundUnit is null)
        {
            return Fail(FactValidationIssue.InvalidUnit, "A numeric fact must carry a unit, including when it is nil.", out reason, out issue);
        }
        if (!meta.IsNumeric && (Unit is not null || CompoundUnit is not null || Decimals is not null))
        {
            return Fail(FactValidationIssue.InvalidUnit, "A non-numeric fact cannot carry a unit or decimals.", out reason, out issue);
        }
        if (StringComparer.OrdinalIgnoreCase.Equals(dataType, "monetary")
            && (CompoundUnit is not null || Unit is { Power: not 1 }))
        {
            return Fail(FactValidationIssue.InvalidUnit, $"Monetary concept '{ConceptName}' requires one currency measure, not a powered unit.", out reason, out issue);
        }
        if (StringComparer.OrdinalIgnoreCase.Equals(dataType, "shares")
            && (CompoundUnit is not null || Unit is not { Measure: "shares", Power: 1 }))
        {
            return Fail(FactValidationIssue.InvalidUnit, $"Shares concept '{ConceptName}' requires one xbrli:shares measure.", out reason, out issue);
        }
        if (StringComparer.OrdinalIgnoreCase.Equals(dataType, "pure")
            && (CompoundUnit is not null || Unit is not { Measure: "pure", Power: 1 }))
        {
            return Fail(FactValidationIssue.InvalidUnit, $"Pure concept '{ConceptName}' requires one xbrli:pure measure.", out reason, out issue);
        }
        if (!meta.IsNumeric && !IsNil && TextValue is null)
        {
            return Fail(FactValidationIssue.InvalidValue, $"Non-numeric concept '{ConceptName}' requires a text value.", out reason, out issue);
        }
        if (Language is not null && meta.SupportedLanguages.Count > 0
            && !meta.SupportedLanguages.Contains(Language.ToLowerInvariant(), StringComparer.Ordinal))
        {
            return Fail(FactValidationIssue.InvalidLanguage, $"Language '{Language}' is not supported by concept '{ConceptName}'.", out reason, out issue);
        }
        reason = "";
        issue = FactValidationIssue.None;
        return true;
    }

    /// <summary>Verify the fact value and period against a taxonomy concept.</summary>
    public bool ValidateAgainst(ConceptMetadata meta, out string reason)
        => ValidateAgainst(meta, out reason, out _);

    public bool ValidateAgainst(ConceptMetadata meta, out string reason, out FactValidationIssue issue)
    {
        if (string.IsNullOrWhiteSpace(EntityScheme)
            || !Uri.TryCreate(EntityScheme, UriKind.Absolute, out _)
            || string.IsNullOrWhiteSpace(EntityIdentifier))
            return Fail(FactValidationIssue.InvalidEntity, "Fact entity scheme or identifier is missing or invalid.", out reason, out issue);

        if (Period is null)
            return Fail(FactValidationIssue.InvalidPeriod, "Fact period is incomplete or invalid.", out reason, out issue);

        var validPeriod = Period.Kind switch
        {
            PeriodKind.Instant => Period.Instant.HasValue && Period.Start is null && Period.End is null,
            PeriodKind.Duration => Period.Instant is null && Period.Start.HasValue && Period.End.HasValue
                && Period.Start.Value < Period.End.Value,
            _ => false
        };
        if (!validPeriod)
            return Fail(FactValidationIssue.InvalidPeriod, "Fact period is incomplete or invalid.", out reason, out issue);

        if (Dimensions?.Any(dimension => !dimension.IsExplicit) == true)
            return Fail(FactValidationIssue.InvalidDimensions,
                "Typed dimensions require filing-profile domain metadata and are unsupported.", out reason, out issue);

        if (Dimensions is null || Dimensions.Count > FactDimensionParser.MaximumDimensions
            || Dimensions.Any(dimension => string.IsNullOrWhiteSpace(dimension.Name)
                || string.IsNullOrWhiteSpace(dimension.MemberName))
            || Dimensions.Select(dimension => dimension.Name).Distinct(StringComparer.Ordinal).Count() != Dimensions.Count)
            return Fail(FactValidationIssue.InvalidDimensions, "Fact dimensions are empty, duplicated, or exceed the supported limit.", out reason, out issue);

        if (!ValidateValueAgainst(meta, out reason, out issue)) return false;
        if (meta.IsAbstract)
        {
            return Fail(FactValidationIssue.InvalidConcept, $"Concept '{ConceptName}' is abstract and cannot carry a fact value.", out reason, out issue);
        }
        if (meta.PeriodType == "instant" && Period.Kind != PeriodKind.Instant)
        {
            return Fail(FactValidationIssue.PeriodTypeMismatch, $"Concept '{ConceptName}' requires an instant period.", out reason, out issue);
        }
        if (meta.PeriodType == "duration" && Period.Kind != PeriodKind.Duration)
        {
            return Fail(FactValidationIssue.PeriodTypeMismatch, $"Concept '{ConceptName}' requires a duration period (start..end).", out reason, out issue);
        }
        reason = "";
        issue = FactValidationIssue.None;
        return true;
    }

    private static bool Fail(FactValidationIssue failure, string message, out string reason, out FactValidationIssue issue)
    {
        reason = message;
        issue = failure;
        return false;
    }

    private static string GetDataTypeLocalName(string dataType)
    {
        var typeName = dataType[(dataType.LastIndexOf(':') + 1)..];
        return typeName.EndsWith("ItemType", StringComparison.OrdinalIgnoreCase)
            ? typeName[..^"ItemType".Length]
            : typeName;
    }

    /// <summary>
    /// Order-independent semantic identity used for multiset equivalence across
    /// xBRL-JSON and xBRL-XML round-trips (XBR05) and report-to-record import (XBR06).
    /// Layout/provenance are deliberately excluded.
    /// </summary>
    public string SemanticKey()
    {
        var unit = GetSemanticUnitKey();
        var dims = string.Join(",", Dimensions
            .OrderBy(d => d.Name, System.StringComparer.Ordinal)
            .Select(d => $"{EscapeSemanticComponent(d.Name, "|,=")}="
                + $"{EscapeSemanticComponent(d.MemberName, "|,=")}:{d.IsExplicit}"));
        var val = IsNil ? "nil"
            : Value is not null ? "n:" + Value.Value.ToString(CultureInfo.InvariantCulture)
            : TextValue is not null ? "s:" + EscapeSemanticComponent(TextValue, "|")
            : "(none)";
        var dec = Decimals?.ToString(CultureInfo.InvariantCulture) ?? "INF";
        return string.Join('|',
            EscapeSemanticComponent(ConceptName, "|"),
            $"{EscapeSemanticComponent(EntityScheme, "|")}:{EscapeSemanticComponent(EntityIdentifier, "|:")}",
            Period.ToIsoString(),
            unit,
            dims,
            Language is null ? "" : EscapeSemanticComponent(Language.ToLowerInvariant(), "|"),
            val,
            dec);
    }

    private string GetSemanticUnitKey()
    {
        if (CompoundUnit is not null)
        {
            if (CompoundUnit.Numerators is null || CompoundUnit.Numerators.Count == 0
                || CompoundUnit.Numerators.Concat(CompoundUnit.DenominatorList).Any(string.IsNullOrWhiteSpace))
                return "cu:invalid";
            var denominators = CompoundUnit.DenominatorList;
            if (denominators.Count == 0
                && CompoundUnit.Numerators.All(measure => StringComparer.Ordinal.Equals(measure, CompoundUnit.Numerators[0])))
                return FormatUnitKey(CompoundUnit.Numerators[0], CompoundUnit.Numerators.Count);
            if (CompoundUnit.Numerators.Count == 1
                && StringComparer.Ordinal.Equals(CompoundUnit.Numerators[0], "pure")
                && denominators.Count > 0
                && denominators.All(measure => StringComparer.Ordinal.Equals(measure, denominators[0])))
                return FormatUnitKey(denominators[0], -denominators.Count);
            return "cu:" + EscapeSemanticComponent(CompoundUnit.CanonicalKey, "|");
        }
        if (Unit is null) return "-";
        if (StringComparer.Ordinal.Equals(Unit.Measure, "pure") && Unit.Power == 1) return "-";
        return FormatUnitKey(Unit.Measure, Unit.Power);
    }

    private static string FormatUnitKey(string measure, int power)
        => "u:" + EscapeSemanticComponent(measure, "|^") + (power != 1 ? $"^{power}" : "");

    private static string EscapeSemanticComponent(string value, string delimiters)
    {
        // Percent itself is escaped so the encoding remains one-to-one. Ordinary
        // values keep their historical key, while delimiters inside lexical values
        // cannot alias another fact's aspect boundary.
        var result = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == '%' || delimiters.Contains(character))
            {
                result.Append('%');
                result.Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }
}

/// <summary>
/// Intermediate representation of a fact emitted by FactConstruct during execution.
/// The Engine emits PendingFacts; the XBRL/serialization stage validates and folds
/// them into final <see cref="XbrlFact"/> instances.
/// </summary>
public sealed class PendingFact
{
    public string? ConceptName { get; set; }
    public string? EntityScheme { get; set; }
    public string? EntityIdentifier { get; set; }
    public XbrlPeriod? Period { get; set; }
    public XbrlUnit? Unit { get; set; }
    public CompoundUnit? CompoundUnit { get; set; }
    public decimal? Value { get; set; }
    public string? TextValue { get; set; }
    public bool IsNil { get; set; }
    public int? Decimals { get; set; }
    public string? Language { get; set; }
    public IReadOnlyList<Dimension> Dimensions { get; set; } = Array.Empty<Dimension>();
    public List<SourceReference> Sources { get; } = new();
    public List<string> TransformationIds { get; } = new();
    public List<SourceTransformationPath> SourceTransformationPaths { get; } = new();
}

/// <summary>
/// Metadata about a concept in the taxonomy (XBR03).
/// </summary>
public sealed record ConceptMetadata(
    string QualifiedName,
    string Namespace,
    string LocalName,
    string Label,
    string DataType,       // "monetary", "string", "boolean", etc.
    string PeriodType,     // "instant" | "duration"
    bool IsAbstract)
{
    [JsonIgnore]
    public string DataTypeLocalName
    {
        get
        {
            var typeName = DataType[(DataType.LastIndexOf(':') + 1)..];
            return typeName.EndsWith("ItemType", StringComparison.OrdinalIgnoreCase)
                ? typeName[..^"ItemType".Length]
                : typeName;
        }
    }

    /// <summary>Whether the concept's taxonomy type is one of the numeric types emitted by FactConstruct.</summary>
    [JsonIgnore]
    public bool IsNumeric
    {
        get
        {
            return DataTypeLocalName.ToLowerInvariant() is
                "monetary" or "decimal" or "integer" or "positiveinteger" or "negativeinteger"
                or "nonpositiveinteger" or "nonnegativeinteger" or "long" or "int" or "short"
                or "byte" or "unsignedlong" or "unsignedint" or "unsignedshort" or "unsignedbyte"
                or "float" or "double" or "shares" or "pure" or "percent" or "percentage";
        }
    }

    public IReadOnlyList<string> SupportedLanguages { get; init; } = new[] { "en" };
    public IReadOnlyDictionary<string, string> LabelsByLanguage { get; init; }
        = new Dictionary<string, string>();
    [JsonIgnore]
    public ExpandedConceptName ExpandedName => new(Namespace, LocalName);
}

/// <summary>Stable taxonomy identity independent of a package's chosen QName prefix.</summary>
public readonly record struct ExpandedConceptName(string NamespaceUri, string LocalName);

/// <summary>
/// Immutable agreement about what constitutes an acceptable deliverable for one
/// collector and reporting cycle (section 5.1). For G1 the profile is synthetic.
/// </summary>
public sealed class FilingProfile
{
    private static readonly HashSet<string> BuiltInUnitMeasures = new(StringComparer.Ordinal)
    {
        "shares", "pure"
    };

    public required string ProfileId { get; init; }
    public required string Digest { get; init; }             // "sha256:..."
    public required IReadOnlyList<ConceptMetadata> Concepts { get; init; }
    public required IReadOnlySet<string> SupportedUnits { get; init; }
    /// <summary>Version label for the offline taxonomy package set.</summary>
    public string TaxonomyVersion { get; init; } = "";
    /// <summary>Dimension QName to its allowed member QNames, when declared by the package.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionDomains { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    /// <summary>Ordered, role-specific presentation arcs for this exact taxonomy version.</summary>
    public IReadOnlyList<PresentationLink> PresentationLinks { get; init; } = Array.Empty<PresentationLink>();
    /// <summary>Ordered summation-item arcs for this exact taxonomy version.</summary>
    public IReadOnlyList<CalculationLink> CalculationLinks { get; init; } = Array.Empty<CalculationLink>();
    /// <summary>Ordered, arcrole-specific definition arcs for this exact taxonomy version.</summary>
    public IReadOnlyList<DefinitionLink> DefinitionLinks { get; init; } = Array.Empty<DefinitionLink>();
    public string OutputFormat { get; init; } = "xbrl-json";
    public IReadOnlyDictionary<string, string> NamespaceUris { get; init; }
        = new Dictionary<string, string>();
    public IReadOnlyList<string> TaxonomyUrls { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> PackageHashes { get; init; }
        = new Dictionary<string, string>();
    /// <summary>Plugin identifiers required by the exact installed filing profile.</summary>
    public IReadOnlySet<string> RequiredPlugins { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>XBR07 check-completeness: rule identifiers the profile declares as
    /// mandatory. A full-file validation must produce at least one outcome for every
    /// declared required check; if a rule never fires (for example, an empty fact set
    /// cannot exercise per-fact rules), <see cref="FilingRuleValidator"/> records an
    /// <see cref="RuleStatus.Incomplete"/> outcome with <c>XBR014</c> so the run
    /// resolves to <see cref="RunOutcome.IncompleteConfiguration"/> instead of a
    /// silent pass. Conditional rules such as <c>dimension-member-valid</c> are
    /// intentionally omitted because they apply only when facts exercise them.</summary>
    public IReadOnlySet<string> RequiredChecks { get; init; }
        = new HashSet<string>(StringComparer.Ordinal);

    public ConceptMetadata? FindConcept(string qualifiedName)
        => Concepts.FirstOrDefault(c => c.QualifiedName == qualifiedName);

    /// <summary>True for a profile currency, a standard XBRL unit measure, or a declared QName.</summary>
    public bool SupportsUnitMeasure(string measure)
    {
        if (string.IsNullOrWhiteSpace(measure)) return false;
        if (SupportedUnits.Contains(measure) || BuiltInUnitMeasures.Contains(measure)) return true;

        var separator = measure.IndexOf(':');
        if (separator <= 0 || separator != measure.LastIndexOf(':'))
            return false;
        var prefix = measure[..separator];
        var localName = measure[(separator + 1)..];
        if (prefix == "iso4217" && SupportedUnits.Contains(localName)) return true;
        if (prefix == "xbrli" && BuiltInUnitMeasures.Contains(localName)) return true;
        if (!NamespaceUris.ContainsKey(prefix)) return false;
        try
        {
            XmlConvert.VerifyNCName(prefix);
            XmlConvert.VerifyNCName(localName);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
