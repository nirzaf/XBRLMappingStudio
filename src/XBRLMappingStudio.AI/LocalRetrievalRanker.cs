using System.Text.RegularExpressions;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI;

/// <summary>Auditable identity and filing metadata retained for a ranked concept.</summary>
public sealed record TaxonomyCandidateEvidence(
    string ProfileId,
    string ProfileDigest,
    string TaxonomyVersion,
    string QualifiedName,
    string NamespaceUri,
    string LocalName,
    string Label,
    string DataType,
    string PeriodType,
    IReadOnlyList<string> PresentationRoles,
    int TotalPresentationRoleCount);

/// <summary>One ranked taxonomy candidate for a source field (AI02).</summary>
public sealed record RankedCandidate(
    string QualifiedName,
    int Score,
    string Basis,
    TaxonomyCandidateEvidence Evidence);

/// <summary>
/// Deterministic, fully offline lexical retrieval over the selected <see cref="TaxonomyIndex"/>.
/// Ranking is a pure function of the field, reviewed aliases, taxonomy metadata and context.
/// Ties break on qualified name in ordinal order, keeping identical labels in separate
/// namespaces distinct. Abstract, unsupported-type, invalid-period and incompatible
/// unit/period candidates are excluded before a suggestion is created.
/// </summary>
public sealed class LocalRetrievalRanker
{
    /// <summary>Persisted AIM-08 identity for this deterministic scoring contract. Bump when ranking semantics change.</summary>
    public const string PolicyRevision = "offline-lexical/v1";
    /// <summary>File name for a reviewed alias table persisted inside the project directory.</summary>
    public const string AliasFileName = "ai_aliases.json";
    public const int MaximumResults = 100;
    private const int MaximumRoleEvidence = 16;
    private const int MaximumLabelEvidenceLength = 512;

    private static readonly Regex CamelCaseBoundary = new(
        "(?<=[a-z0-9])(?=[A-Z])", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(
        "[\\p{L}\\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string[]> NoAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal);
    private readonly record struct CandidatePriority(int Score, string QualifiedName);
    private sealed class WorstCandidateFirstComparer : IComparer<CandidatePriority>
    {
        public int Compare(CandidatePriority left, CandidatePriority right)
        {
            var score = left.Score.CompareTo(right.Score);
            return score != 0
                ? score
                : StringComparer.Ordinal.Compare(right.QualifiedName, left.QualifiedName);
        }
    }
    private static readonly IComparer<CandidatePriority> CandidatePriorityComparer =
        new WorstCandidateFirstComparer();

    private readonly TaxonomyIndex _index;
    private readonly IReadOnlyDictionary<string, string[]> _aliasesByConcept;

    public LocalRetrievalRanker(
        TaxonomyIndex index,
        IReadOnlyDictionary<string, string[]>? aliasesByConcept = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _aliasesByConcept = aliasesByConcept
            ?? (StringComparer.Ordinal.Equals(index.ProfileId, SyntheticProfile.ProfileId)
                ? DefaultAliases
                : NoAliases);
    }

    /// <summary>Whether this ranker uses the exact selected profile version.</summary>
    public bool IsCurrentFor(TaxonomyIndex index) =>
        index is not null
        && StringComparer.Ordinal.Equals(_index.ProfileId, index.ProfileId)
        && StringComparer.Ordinal.Equals(_index.ProfileDigest, index.ProfileDigest);

    /// <summary>Curated default alias table for the synthetic profile only.</summary>
    public static IReadOnlyDictionary<string, string[]> DefaultAliases { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [SyntheticProfile.Qualified("Revenue")] = new[] { "sales", "turnover", "revenues", "income_statement_revenue" },
            [SyntheticProfile.Qualified("Expenses")] = new[] { "cost", "costs", "expenditures", "spend" },
            [SyntheticProfile.Qualified("NetIncome")] = new[] { "profit", "net_profit", "earnings", "bottom_line" },
            [SyntheticProfile.Qualified("Assets")] = new[] { "asset", "total_assets", "balance_sheet_assets" },
            [SyntheticProfile.Qualified("Liabilities")] = new[] { "liability", "debt", "obligations" },
        };

    /// <summary>
    /// Loads explicit project alias overrides from <c>ai_aliases.json</c>. The profile-aware
    /// overload applies built-in aliases on an absent file only for the synthetic profile;
    /// keys and values use the JSON shape
    /// { "concept:QName": ["alias", ...] }.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> LoadAliases(string projectDirectory)
        => LoadAliases(projectDirectory, profileId: null);

    /// <summary>Loads project aliases, using defaults on an absent file only for synthetic profile callers.</summary>
    public static IReadOnlyDictionary<string, string[]> LoadAliases(string projectDirectory, string? profileId)
    {
        var path = Path.Combine(projectDirectory, AliasFileName);
        if (!File.Exists(path))
            return StringComparer.Ordinal.Equals(profileId, SyntheticProfile.ProfileId)
                ? DefaultAliases
                : NoAliases;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            result[prop.Name] = prop.Value.EnumerateArray()
                .Where(value => value.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(value => value.GetString()!)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        return result;
    }

    /// <summary>Rank concepts by label alone, retaining the legacy API for callers without context.</summary>
    public IReadOnlyList<RankedCandidate> Rank(string fieldLabel, int top = 5)
        => RankCore(fieldLabel, unit: null, periodType: null, top);

    /// <summary>Rank concepts using the field's known unit and period context.</summary>
    public IReadOnlyList<RankedCandidate> Rank(SourceFieldDescriptor field, int top = 5)
    {
        ArgumentNullException.ThrowIfNull(field);
        return RankCore(field.Name, field.Unit, field.PeriodType, top);
    }

    private IReadOnlyList<RankedCandidate> RankCore(string fieldLabel, string? unit, string? periodType, int top)
    {
        ArgumentNullException.ThrowIfNull(fieldLabel);
        if (top <= 0) return Array.Empty<RankedCandidate>();
        var resultLimit = Math.Min(top, MaximumResults);
        var tokens = Tokenize(fieldLabel);
        if (tokens.Count == 0) return Array.Empty<RankedCandidate>();

        var scored = new PriorityQueue<RankedCandidate, CandidatePriority>(CandidatePriorityComparer);
        foreach (var concept in _index.AllConcepts)
        {
            if (!IsSuggestionTargetable(concept)
                || (periodType is not null && !StringComparer.Ordinal.Equals(concept.PeriodType, periodType))
                || !HasCompatibleUnit(concept, unit))
                continue;

            int score = 0;
            var basis = new List<string>();

            if (_aliasesByConcept.TryGetValue(concept.QualifiedName, out var aliases))
            {
                foreach (var alias in aliases.OrderBy(value => value, StringComparer.Ordinal))
                {
                    if (Tokenize(alias).SetEquals(tokens))
                    {
                        score += 4;
                        basis.Add($"alias:{alias}");
                        break;
                    }
                }
            }

            var localTokens = Tokenize(concept.LocalName);
            if (tokens.Intersect(localTokens).Any())
            {
                score += 3;
                basis.Add("local-name");
            }

            foreach (var language in concept.LabelsByLanguage.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (tokens.Intersect(Tokenize(concept.LabelsByLanguage[language])).Any())
                {
                    score += 2;
                    basis.Add($"label:{language}");
                    break;
                }
            }
            if (concept.Label.Length > 0 && tokens.Intersect(Tokenize(concept.Label)).Any()
                && !basis.Any(value => value.StartsWith("label:", StringComparison.Ordinal)))
            {
                score += 2;
                basis.Add("label:default");
            }

            if (tokens.Any(token => concept.QualifiedName.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score += 1;
                basis.Add("qname");
            }

            if (score > 0)
            {
                var priority = new CandidatePriority(score, concept.QualifiedName);
                if (scored.Count < resultLimit)
                    scored.Enqueue(CreateCandidate(concept, score, basis), priority);
                else if (scored.TryPeek(out _, out var worstPriority)
                    && CandidatePriorityComparer.Compare(priority, worstPriority) > 0)
                {
                    scored.Dequeue();
                    scored.Enqueue(CreateCandidate(concept, score, basis), priority);
                }
            }
        }

        return scored.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.QualifiedName, StringComparer.Ordinal)
            .Take(resultLimit)
            .ToList();
    }

    private bool HasCompatibleUnit(ConceptMetadata concept, string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return true;
        if (concept.DataType != "monetary") return false;
        return _index.SupportedUnits.Contains(unit);
    }

    private static bool IsSuggestionTargetable(ConceptMetadata concept) =>
        TaxonomyIndex.IsFactTargetable(concept)
        && concept.PeriodType is "instant" or "duration"
        && concept.DataType is "monetary" or "string";

    private TaxonomyCandidateEvidence CreateEvidence(ConceptMetadata concept)
    {
        var roles = _index.PresentationRolesFor(concept.QualifiedName);
        return new TaxonomyCandidateEvidence(
            _index.ProfileId,
            _index.ProfileDigest,
            _index.TaxonomyVersion,
            concept.QualifiedName,
            concept.ExpandedName.NamespaceUri,
            concept.ExpandedName.LocalName,
            concept.Label.Length <= MaximumLabelEvidenceLength
                ? concept.Label
                : concept.Label[..MaximumLabelEvidenceLength],
            concept.DataType,
            concept.PeriodType,
            roles.Take(MaximumRoleEvidence).ToArray(),
            roles.Count);
    }

    private RankedCandidate CreateCandidate(ConceptMetadata concept, int score, IReadOnlyList<string> basis) =>
        new(concept.QualifiedName, score, string.Join("+", basis), CreateEvidence(concept));

    /// <summary>Unicode letter/digit tokens; punctuation, underscores and camel case are boundaries.</summary>
    private static HashSet<string> Tokenize(string value)
    {
        var separated = CamelCaseBoundary.Replace(value, " ");
        return TokenPattern.Matches(separated).Cast<Match>()
            .Select(match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }
}
