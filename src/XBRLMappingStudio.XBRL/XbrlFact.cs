using System.Globalization;
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

    public bool ValidateValueAgainst(ConceptMetadata meta, out string reason)
    {
        if (Value is not null && TextValue is not null)
        {
            reason = "A fact cannot have both a numeric and text value.";
            return false;
        }
        if (IsNil && (Value is not null || TextValue is not null))
        {
            reason = "A nil fact cannot also carry a value.";
            return false;
        }
        if (!IsNil && Value is null && TextValue is null)
        {
            reason = "A non-nil fact must carry a value.";
            return false;
        }
        if (Unit is not null && CompoundUnit is not null)
        {
            reason = "A fact cannot have both a simple and compound unit.";
            return false;
        }
        if (Decimals is not null && (IsNil || Value is null))
        {
            reason = "Decimals are only valid for a non-nil numeric fact.";
            return false;
        }
        if (Value is not null && Language is not null)
        {
            reason = "A numeric fact cannot carry a language tag.";
            return false;
        }
        if (Value is not null && Unit is null && CompoundUnit is null)
        {
            reason = "A numeric fact must carry a unit.";
            return false;
        }
        if (TextValue is not null && (Unit is not null || CompoundUnit is not null || Decimals is not null))
        {
            reason = "A text fact cannot carry a unit or decimals.";
            return false;
        }
        if (meta.DataType == "monetary" && Unit is null && CompoundUnit is null)
        {
            reason = $"Concept '{ConceptName}' is monetary and requires a unit.";
            return false;
        }
        if (meta.DataType == "monetary" && TextValue is not null)
        {
            reason = $"Monetary concept '{ConceptName}' requires a numeric value.";
            return false;
        }
        if (meta.DataType == "string" && (Value is not null || Unit is not null || CompoundUnit is not null || Decimals is not null))
        {
            reason = $"String concept '{ConceptName}' cannot carry a numeric value, unit, or decimals.";
            return false;
        }
        if (meta.DataType == "string" && !IsNil && TextValue is null)
        {
            reason = $"String concept '{ConceptName}' requires a text value.";
            return false;
        }
        if (Language is not null && meta.SupportedLanguages.Count > 0
            && !meta.SupportedLanguages.Contains(Language.ToLowerInvariant(), StringComparer.Ordinal))
        {
            reason = $"Language '{Language}' is not supported by concept '{ConceptName}'.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Verify the fact value and period against a taxonomy concept.</summary>
    public bool ValidateAgainst(ConceptMetadata meta, out string reason)
    {
        if (!ValidateValueAgainst(meta, out reason)) return false;
        if (meta.IsAbstract)
        {
            reason = $"Concept '{ConceptName}' is abstract and cannot carry a fact value.";
            return false;
        }
        if (meta.PeriodType == "instant" && Period.Kind != PeriodKind.Instant)
        {
            reason = $"Concept '{ConceptName}' requires an instant period.";
            return false;
        }
        if (meta.PeriodType == "duration" && Period.Kind != PeriodKind.Duration)
        {
            reason = $"Concept '{ConceptName}' requires a duration period (start..end).";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Order-independent semantic identity used for multiset equivalence across
    /// xBRL-JSON and xBRL-XML round-trips (XBR05) and report-to-record import (XBR06).
    /// Layout/provenance are deliberately excluded.
    /// </summary>
    public string SemanticKey()
    {
        var unit = CompoundUnit is not null
            ? "cu:" + CompoundUnit.CanonicalKey
            : Unit is not null ? "u:" + Unit.Measure + (Unit.Power != 1 ? $"^{Unit.Power}" : "") : "-";
        var dims = string.Join(",", Dimensions
            .OrderBy(d => d.Name, System.StringComparer.Ordinal)
            .Select(d => $"{d.Name}={d.MemberName}:{d.IsExplicit}"));
        var val = IsNil ? "nil"
            : Value is not null ? "n:" + Value.Value.ToString(CultureInfo.InvariantCulture)
            : TextValue is not null ? "s:" + TextValue
            : "(none)";
        var dec = Decimals?.ToString(CultureInfo.InvariantCulture) ?? "INF";
        return string.Join('|',
            ConceptName,
            $"{EntityScheme}:{EntityIdentifier}",
            Period.ToIsoString(),
            unit,
            dims,
            Language?.ToLowerInvariant() ?? "",
            val,
            dec);
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
    public bool IsNil { get; set; }
    public int? Decimals { get; set; }
    public string? Language { get; set; }
    public List<SourceReference> Sources { get; } = new();
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
}

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
    public string OutputFormat { get; init; } = "xbrl-json";
    public IReadOnlyDictionary<string, string> NamespaceUris { get; init; }
        = new Dictionary<string, string>();
    public IReadOnlyList<string> TaxonomyUrls { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> PackageHashes { get; init; }
        = new Dictionary<string, string>();

    public ConceptMetadata? FindConcept(string qualifiedName)
        => Concepts.FirstOrDefault(c => c.QualifiedName == qualifiedName);
}
