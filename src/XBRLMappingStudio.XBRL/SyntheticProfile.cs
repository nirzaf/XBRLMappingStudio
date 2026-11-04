using System.Security.Cryptography;
using System.Text;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Synthetic OIM-compatible filing profile for G1 (BASE01, XBR01).
/// Namespace: <c>urn:synthetic:finance:1.0</c> with prefix <c>synthetic</c>.
/// Provides five monetary concepts and three currency units. No regulator dependency;
/// no external taxonomy package required for offline resolution.
/// </summary>
public static class SyntheticProfile
{
    public const string ProfileId = "synthetic-oim-v1";
    public const string Namespace = "urn:synthetic:finance:1.0";
    public const string Prefix = "synthetic";
    public const string TaxonomyFileName = "arelle-test-taxonomy.xsd";
    public const string TaxonomyPackageDigest = "sha256:e898d8ef578e9e7d7015265b289b7bb63f3df8b2dadc35c11776c32a243d14c9";

    /// <summary>Stable, hand-authored concepts for the first vertical slice.</summary>
    public static IReadOnlyList<ConceptMetadata> Concepts { get; } = new[]
    {
        new ConceptMetadata(
            Qualified("Assets"), Namespace, "Assets",
            Label: "Total Assets",
            DataType: "monetary",
            PeriodType: "instant",
            IsAbstract: false)
        {
            SupportedLanguages = new[] { "en", "fr" },
            LabelsByLanguage = new Dictionary<string, string>
            {
                ["en"] = "Total Assets",
                ["fr"] = "Actif total"
            }
        },

        new ConceptMetadata(
            Qualified("Liabilities"), Namespace, "Liabilities",
            Label: "Total Liabilities",
            DataType: "monetary",
            PeriodType: "instant",
            IsAbstract: false),

        new ConceptMetadata(
            Qualified("Revenue"), Namespace, "Revenue",
            Label: "Revenue",
            DataType: "monetary",
            PeriodType: "duration",
            IsAbstract: false)
        {
            SupportedLanguages = new[] { "en", "fr", "ar" },
            LabelsByLanguage = new Dictionary<string, string>
            {
                ["en"] = "Revenue",
                ["fr"] = "Produit",
                ["ar"] = "إيرادات"
            }
        },

        new ConceptMetadata(
            Qualified("Expenses"), Namespace, "Expenses",
            Label: "Expenses",
            DataType: "monetary",
            PeriodType: "duration",
            IsAbstract: false),

        new ConceptMetadata(
            Qualified("NetIncome"), Namespace, "NetIncome",
            Label: "Net Income",
            DataType: "monetary",
            PeriodType: "duration",
            IsAbstract: false),

        new ConceptMetadata(
            Qualified("EntityRegistrantName"), Namespace, "EntityRegistrantName",
            Label: "Entity Registrant Name",
            DataType: "string",
            PeriodType: "duration",
            IsAbstract: false)
        {
            SupportedLanguages = new[] { "en", "fr", "ar" },
            LabelsByLanguage = new Dictionary<string, string>
            {
                ["en"] = "Entity Registrant Name",
                ["fr"] = "Nom du titulaire de l'entité",
                ["ar"] = "اسم الكيان"
            }
        },

        new ConceptMetadata(
            Qualified("FinancialStatements"), Namespace, "FinancialStatements",
            Label: "Financial Statements",
            DataType: "non-float",
            PeriodType: "duration",
            IsAbstract: true)
    };

    public static IReadOnlySet<string> SupportedUnits { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "EUR", "USD", "QAR" };

    /// <summary>Default presentation role (xBRL standard summary role).</summary>
    public const string PresentationRole =
        "http://xbrl.info/xbrl/synthetic/role/presentation";

    /// <summary>XBR07: unconditional rules that every non-empty filing validation must
    /// exercise. If a rule never fires (empty fact set, or a filtered-out concept class),
    /// <see cref="FilingRuleValidator"/> records an <c>Incomplete</c> outcome so the run
    /// maps to <see cref="Diagnostics.RunOutcome.IncompleteConfiguration"/>.</summary>
    public static IReadOnlySet<string> RequiredChecks { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "synthetic:concept-exists",
        "synthetic:abstract-concept-not-used",
        "synthetic:monetary-unit-required",
        "synthetic:unit-allowed",
        "synthetic:fact-value-contract",
        "synthetic:period-type-match",
        "synthetic:no-duplicate-facts",
    };

    /// <summary>Financial-statement hierarchy used to build the presentation network (XBR03).
    /// The instant and duration children both hang under the abstract FinancialStatements
    /// concept so a concept appears across roles.</summary>
    public static IReadOnlyList<PresentationLink> PresentationLinks { get; } = new[]
    {
        new PresentationLink(PresentationRole, Qualified("FinancialStatements"), Qualified("Revenue"), 1),
        new PresentationLink(PresentationRole, Qualified("FinancialStatements"), Qualified("Expenses"), 2),
        new PresentationLink(PresentationRole, Qualified("FinancialStatements"), Qualified("NetIncome"), 3),
        new PresentationLink(PresentationRole, Qualified("FinancialStatements"), Qualified("Assets"), 4),
        new PresentationLink(PresentationRole, Qualified("FinancialStatements"), Qualified("Liabilities"), 5),
        // A second role reuses Revenue, proving a concept may appear in multiple networks.
        new PresentationLink(PresentationRole + "/income-statement", Qualified("NetIncome"), Qualified("Revenue"), 1),
    };

    public static string Qualified(string local) => $"{Prefix}:{local}";

    public static FilingProfile Build()
    {
        // Profile identity binds the complete concept metadata, units and the exact
        // offline taxonomy bytes. Reordering concepts, labels or units does not change it.
        var concepts = new StringBuilder();
        foreach (var c in Concepts.OrderBy(c => c.QualifiedName, StringComparer.Ordinal))
        {
            AppendPart(concepts, "concept");
            AppendPart(concepts, c.QualifiedName);
            AppendPart(concepts, c.Namespace);
            AppendPart(concepts, c.LocalName);
            AppendPart(concepts, c.Label);
            AppendPart(concepts, c.DataType);
            AppendPart(concepts, c.PeriodType);
            AppendPart(concepts, c.IsAbstract ? "abstract" : "concrete");
            var languages = c.SupportedLanguages.Order(StringComparer.Ordinal).ToArray();
            concepts.Append("languages:").Append(languages.Length).Append(':');
            foreach (var language in languages)
                AppendPart(concepts, language);
            var labels = c.LabelsByLanguage.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();
            concepts.Append("labels:").Append(labels.Length).Append(':');
            foreach (var (language, label) in labels)
            {
                AppendPart(concepts, language);
                AppendPart(concepts, label);
            }
            concepts.Append(';');
        }
        var conceptDigest = Hash(concepts.ToString());

        var packageHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile-concept-table"] = conceptDigest,
            [TaxonomyFileName] = TaxonomyPackageDigest
        };

        var identity = new StringBuilder();
        AppendPart(identity, ProfileId);
        AppendPart(identity, Namespace);
        AppendPart(identity, TaxonomyFileName);
        AppendPart(identity, TaxonomyPackageDigest);
        AppendPart(identity, conceptDigest);
        var units = SupportedUnits.OrderBy(u => u, StringComparer.Ordinal).ToArray();
        identity.Append("units:").Append(units.Length).Append(':');
        foreach (var u in units)
            AppendPart(identity, u);
        var digest = Hash(identity.ToString());

        return new FilingProfile
        {
            ProfileId = ProfileId,
            Digest = digest,
            Concepts = Concepts,
            SupportedUnits = SupportedUnits,
            TaxonomyUrls = new[] { TaxonomyFileName },
            NamespaceUris = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Prefix] = Namespace
            },
            OutputFormat = "xbrl-json",
            PackageHashes = packageHashes,
            RequiredChecks = SyntheticProfile.RequiredChecks
        };
    }

    private static void AppendPart(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);

    private static string Hash(string value)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
