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

    /// <summary>Stable, hand-authored concepts for the first vertical slice.</summary>
    public static IReadOnlyList<ConceptMetadata> Concepts { get; } = new[]
    {
        new ConceptMetadata(
            Qualified("Assets"), Namespace, "Assets",
            Label: "Total Assets",
            DataType: "monetary",
            PeriodType: "instant",
            IsAbstract: false),

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
            IsAbstract: false),

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
            Qualified("FinancialStatements"), Namespace, "FinancialStatements",
            Label: "Financial Statements",
            DataType: "non-float",
            PeriodType: "duration",
            IsAbstract: true)
    };

    public static IReadOnlySet<string> SupportedUnits { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "EUR", "USD", "QAR" };

    public static string Qualified(string local) => $"{Prefix}:{local}";

    public static FilingProfile Build()
    {
        // Digest depends only on stable concept and unit content; independent of order.
        var sb = new StringBuilder();
        sb.Append(ProfileId).Append('|');
        foreach (var c in Concepts.OrderBy(c => c.QualifiedName, StringComparer.Ordinal))
        {
            sb.Append(c.QualifiedName).Append('/')
              .Append(c.DataType).Append('/')
              .Append(c.PeriodType).Append('/')
              .Append(c.IsAbstract ? "A" : "C").Append(';');
        }
        sb.Append('|');
        foreach (var u in SupportedUnits.OrderBy(u => u, StringComparer.Ordinal))
        {
            sb.Append(u).Append(',');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        var digest = "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();

        return new FilingProfile
        {
            ProfileId = ProfileId,
            Digest = digest,
            Concepts = Concepts,
            SupportedUnits = SupportedUnits,
            OutputFormat = "xbrl-json",
            PackageHashes = new Dictionary<string, string>
            {
                ["profile-concept-table"] = digest
            }
        };
    }
}
