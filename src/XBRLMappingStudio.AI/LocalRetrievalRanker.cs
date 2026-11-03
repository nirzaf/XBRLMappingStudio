using System.Text.Json;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.AI;

/// <summary>One ranked taxonomy candidate for a source field (AI02).</summary>
public sealed record RankedCandidate(
    string QualifiedName,
    int Score,
    string Basis);

/// <summary>
/// Deterministic, fully offline lexical retrieval over a <see cref="TaxonomyIndex"/> (AI02).
/// Ranking is a pure function of (field label, alias table, concept table): the same input
/// always yields the same ordered candidate list. Ties break on the qualified name in
/// ordinal order so two namespaces carrying an identical label remain distinct, stable rows.
/// Abstract concepts are never proposed as fact targets.
/// </summary>
public sealed class LocalRetrievalRanker
{
    /// <summary>File name for the reviewed alias table persisted inside the project directory.</summary>
    public const string AliasFileName = "ai_aliases.json";

    private readonly TaxonomyIndex _index;
    private readonly IReadOnlyDictionary<string, string[]> _aliasesByConcept;

    public LocalRetrievalRanker(
        TaxonomyIndex index,
        IReadOnlyDictionary<string, string[]>? aliasesByConcept = null)
    {
        _index = index;
        _aliasesByConcept = aliasesByConcept ?? DefaultAliases;
    }

    /// <summary>Curated default alias table for the synthetic profile.</summary>
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
    /// Loads the reviewed alias table from <c>ai_aliases.json</c> in the project directory,
    /// falling back to the built-in defaults when the file is absent. Keys and values are
    /// compared with ordinal semantics; JSON object shape is { "concept:QName": ["alias", ...] }.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> LoadAliases(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, AliasFileName);
        if (!File.Exists(path)) return DefaultAliases;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            result[prop.Name] = prop.Value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        return result;
    }

    /// <summary>
    /// Rank concepts for one source field label. Score components: exact alias hit (4),
    /// local-name token hit (3), label token hit (2), qualified-name substring (1).
    /// Only concepts with a positive score are returned, ordered by score descending then
    /// qualified name ascending (ordinal) — fully deterministic.
    /// </summary>
    public IReadOnlyList<RankedCandidate> Rank(string fieldLabel, int top = 5)
    {
        var tokens = Tokenize(fieldLabel);
        if (tokens.Count == 0) return Array.Empty<RankedCandidate>();

        var scored = new List<RankedCandidate>();
        foreach (var c in _index.AllConcepts)
        {
            // Abstract concepts are structurally un-suggestable as fact targets (XBR03 rule).
            if (!TaxonomyIndex.IsFactTargetable(c)) continue;

            int score = 0;
            var basis = new List<string>();

            if (_aliasesByConcept.TryGetValue(c.QualifiedName, out var aliases))
            {
                foreach (var alias in aliases.OrderBy(a => a, StringComparer.Ordinal))
                {
                    if (Tokenize(alias).SetEquals(tokens))
                    {
                        score += 4;
                        basis.Add($"alias:{alias}");
                        break;
                    }
                }
            }

            var localTokens = Tokenize(c.LocalName);
            if (tokens.Intersect(localTokens).Any())
            {
                score += 3;
                basis.Add("local-name");
            }

            foreach (var lang in c.LabelsByLanguage.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var labelTokens = Tokenize(c.LabelsByLanguage[lang]);
                if (tokens.Intersect(labelTokens).Any())
                {
                    score += 2;
                    basis.Add($"label:{lang}");
                    break;
                }
            }
            if (c.Label.Length > 0 && tokens.Intersect(Tokenize(c.Label)).Any()
                && !basis.Any(b => b.StartsWith("label:", StringComparison.Ordinal)))
            {
                score += 2;
                basis.Add("label:default");
            }

            if (tokens.Any(t => c.QualifiedName.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                score += 1;
                basis.Add("qname");
            }

            if (score > 0)
                scored.Add(new RankedCandidate(c.QualifiedName, score, string.Join("+", basis)));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.QualifiedName, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    /// <summary>Lowercased alphanumeric token set; deterministic across runs and cultures.</summary>
    private static HashSet<string> Tokenize(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
}
