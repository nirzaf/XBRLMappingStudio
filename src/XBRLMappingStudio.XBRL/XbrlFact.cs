using System.Globalization;
using System.Text.Json.Serialization;
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
/// Simple unit measure for the G1 synthetic profile. Compound units (divide/multiply)
/// are deferred to post-G1 (XBR04 supports compound where profile requires).
/// </summary>
public sealed record XbrlUnit(
    string Measure,
    int Power = 1)
{
    public override string ToString() => Power == 1 ? Measure : $"{Measure}^{Power}";
}

/// <summary>
/// Immutable fact value per XBR04. The G1 synthetic profile supports:
/// monetary instant/duration facts with decimals accuracy and ISO language tags.
/// </summary>
public sealed class XbrlFact
{
    public required string ConceptName { get; init; }         // qualified name: "synthetic:Revenue"
    public required string EntityScheme { get; init; }
    public required string EntityIdentifier { get; init; }
    public required XbrlPeriod Period { get; init; }
    public XbrlUnit? Unit { get; init; }
    /// <summary>Compound (multiply/divide) unit for measures such as USD per share (XBR04).
    /// Exactly one of <see cref="Unit"/> or <see cref="CompoundUnit"/> is set for a monetary fact.</summary>
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
    /// Connected row-phase transformation IDs grouped by source occurrence. These paths
    /// describe graph connectivity; nodes that merge alternatives may include inputs
    /// that did not determine the selected value.
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
        if (Decimals is not null && (IsNil || Value is null))
        {
            return Fail(FactValidationIssue.InvalidDecimals, "Decimals are only valid for a non-nil numeric fact.", out reason, out issue);
        }
        if (Decimals is < -30 or > 30)
        {
            return Fail(FactValidationIssue.InvalidDecimals, "Decimals are outside the supported range [-30, 30].", out reason, out issue);
        }
        if (Value is not null && Language is not null)
        {
            return Fail(FactValidationIssue.InvalidLanguage, "A numeric fact cannot carry a language tag.", out reason, out issue);
        }
        if (Value is not null && Unit is null && CompoundUnit is null)
        {
            return Fail(FactValidationIssue.InvalidUnit, "A numeric fact must carry a unit.", out reason, out issue);
        }
        if (TextValue is not null && (Unit is not null || CompoundUnit is not null))
        {
            return Fail(FactValidationIssue.InvalidUnit, "A text fact cannot carry a unit.", out reason, out issue);
        }
        if (TextValue is not null && Decimals is not null)
        {
            return Fail(FactValidationIssue.InvalidDecimals, "A text fact cannot carry decimals.", out reason, out issue);
        }
        if (meta.DataType == "monetary" && Unit is null && CompoundUnit is null)
        {
            return Fail(FactValidationIssue.InvalidUnit, $"Concept '{ConceptName}' is monetary and requires a unit.", out reason, out issue);
        }
        if (meta.DataType == "monetary" && TextValue is not null)
        {
            return Fail(FactValidationIssue.InvalidValue, $"Monetary concept '{ConceptName}' requires a numeric value.", out reason, out issue);
        }
        if (meta.DataType == "string" && (Value is not null || Unit is not null || CompoundUnit is not null || Decimals is not null))
        {
            return Fail(FactValidationIssue.InvalidValue, $"String concept '{ConceptName}' cannot carry a numeric value, unit, or decimals.", out reason, out issue);
        }
        if (meta.DataType == "string" && !IsNil && TextValue is null)
        {
            return Fail(FactValidationIssue.InvalidValue, $"String concept '{ConceptName}' requires a text value.", out reason, out issue);
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

    /// <summary>
    /// Order-independent semantic identity used for multiset equivalence across
    /// xBRL-JSON and xBRL-XML round-trips (XBR05) and report-to-record import (XBR06).
    /// Layout/provenance are deliberately excluded.
    /// </summary>
    public string SemanticKey()
    {
        var unit = CompoundUnit is not null
            ? "cu:" + EscapeSemanticComponent(CompoundUnit.CanonicalKey, "|")
            : Unit is not null ? "u:" + EscapeSemanticComponent(Unit.Measure, "|^")
                + (Unit.Power != 1 ? $"^{Unit.Power}" : "") : "-";
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
    public required string ProfileId { get; init; }
    public required string Digest { get; init; }             // "sha256:..."
    public required IReadOnlyList<ConceptMetadata> Concepts { get; init; }
    public required IReadOnlySet<string> SupportedUnits { get; init; }
    /// <summary>Version label for the offline taxonomy package set.</summary>
    public string TaxonomyVersion { get; init; } = "";
    /// <summary>Dimension QName to its allowed member QNames, when declared by the package.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DimensionDomains { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
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
}
