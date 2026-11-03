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
    public decimal? Value { get; init; }
    public bool IsNil { get; init; }
    public int? Decimals { get; init; }
    public string? Language { get; init; }

    /// <summary>Set of source references contributing to this fact.</summary>
    public IReadOnlyList<SourceReference> ContributingSources { get; init; }
        = Array.Empty<SourceReference>();

    /// <summary>Verify period kind matches concept constraint. Returns false and populates reason.</summary>
    public bool ValidateAgainst(ConceptMetadata meta, out string reason)
    {
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
        if (meta.DataType == "monetary" && Unit is null)
        {
            reason = $"Concept '{ConceptName}' is monetary and requires a unit.";
            return false;
        }
        reason = "";
        return true;
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
    public IReadOnlyDictionary<string, string> PackageHashes { get; init; }
        = new Dictionary<string, string>();

    public ConceptMetadata? FindConcept(string qualifiedName)
        => Concepts.FirstOrDefault(c => c.QualifiedName == qualifiedName);
}
