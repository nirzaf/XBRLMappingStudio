using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Fixtures;

/// <summary>
/// Immutable, hand-authored source and report fixtures shared by the application test
/// suites. The manifest digest is pinned in code so changing an expected file digest
/// also requires an explicit, reviewable fixture revision.
/// </summary>
public static class IndependentAcceptanceFixtures
{
    public const string ManifestSha256 = "sha256:1040bceb1bd12510c10c611e41c37d124a31eaaa5881a3953a5d964575a9da39";
    private const string ResourcePrefix = "XBRLMappingStudio.Fixtures.AcceptanceFixtures.";

    public static byte[] ReadBytes(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!IsSimpleFileName(fileName))
            throw new ArgumentException("Acceptance fixture names must be simple file names.", nameof(fileName));

        using var stream = typeof(IndependentAcceptanceFixtures).Assembly
            .GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new FileNotFoundException($"Acceptance fixture '{fileName}' is not embedded.", fileName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string ReadText(string fileName)
        => Encoding.UTF8.GetString(ReadBytes(fileName));

    public static string Materialize(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, ReadBytes(fileName));
        return path;
    }

    public static string Digest(ReadOnlySpan<byte> bytes)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static AcceptanceFactOracle ReadFactOracle(string name)
    {
        using var manifest = JsonDocument.Parse(ReadBytes("fixture-manifest.json"));
        var fact = manifest.RootElement.GetProperty("expectedFacts").GetProperty(name);
        var period = fact.GetProperty("period");
        var source = fact.TryGetProperty("source", out var sourceElement) ? sourceElement : (JsonElement?)null;
        var transformation = fact.TryGetProperty("transformation", out var transformationElement)
            ? transformationElement
            : (JsonElement?)null;

        return new AcceptanceFactOracle(
            fact.GetProperty("concept").GetString()!,
            fact.GetProperty("entityScheme").GetString()!,
            fact.GetProperty("entityIdentifier").GetString()!,
            DateOnly.ParseExact(period.GetProperty("start").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(period.GetProperty("end").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            period.GetProperty("oimLexical").GetString()!,
            fact.GetProperty("unit").GetString()!,
            fact.GetProperty("valueLexical").GetString()!,
            fact.GetProperty("decimals").GetInt32(),
            source?.GetProperty("field").GetString(),
            source?.GetProperty("locator").GetString(),
            source?.GetProperty("valueLexical").GetString(),
            source?.GetProperty("workbookDisplayLexical").GetString(),
            transformation?.GetProperty("factor").GetString());
    }

    public static IReadOnlyList<string> CompareFact(
        AcceptanceFactOracle expected,
        XbrlFact actual,
        bool requireSourceEvidence = true)
    {
        var errors = new List<string>();
        if (!StringComparer.Ordinal.Equals(expected.Concept, actual.ConceptName)) errors.Add("concept");
        if (!StringComparer.Ordinal.Equals(expected.EntityScheme, actual.EntityScheme)) errors.Add("entity scheme");
        if (!StringComparer.Ordinal.Equals(expected.EntityIdentifier, actual.EntityIdentifier)) errors.Add("entity identifier");
        if (actual.Period.Kind != PeriodKind.Duration
            || actual.Period.Start != expected.PeriodStart
            || actual.Period.End != expected.PeriodEnd) errors.Add("period");
        if (!StringComparer.Ordinal.Equals(expected.Unit, actual.Unit?.Measure) || actual.Unit?.Power != 1) errors.Add("unit");
        if (actual.Value != expected.Value) errors.Add("numeric value");
        if (actual.Decimals != expected.Decimals) errors.Add("decimals");
        if (requireSourceEvidence && expected.SourceLocator is not null
            && !actual.ContributingSources.Any(source => StringComparer.Ordinal.Equals(source.Locator, expected.SourceLocator)))
            errors.Add("source locator");
        return errors;
    }

    public static IReadOnlyList<string> CompareSerializedOim(AcceptanceFactOracle expected, string reportJson)
    {
        var errors = new List<string>();
        var semanticFacts = OimSerializer.ReadFacts(reportJson);
        if (semanticFacts.Count != 1)
            errors.Add($"semantic fact count (expected 1, got {semanticFacts.Count})");
        else
            errors.AddRange(CompareFact(expected, semanticFacts[0], requireSourceEvidence: false)
                .Select(aspect => $"semantic {aspect}"));

        using var document = JsonDocument.Parse(reportJson);
        var facts = document.RootElement.GetProperty("facts").EnumerateObject().ToArray();
        if (facts.Length != 1)
        {
            errors.Add($"fact count (expected 1, got {facts.Length})");
            return errors;
        }

        var fact = facts[0].Value;
        if (!StringComparer.Ordinal.Equals(expected.ValueLexical, fact.GetProperty("value").GetString()))
            errors.Add("value lexical");
        if (!StringComparer.Ordinal.Equals(expected.OimPeriodLexical,
                fact.GetProperty("dimensions").GetProperty("period").GetString()))
            errors.Add("period lexical");
        if (fact.GetProperty("decimals").GetInt32() != expected.Decimals) errors.Add("decimals");
        return errors;
    }

    public static IReadOnlyList<string> VerifyIntegrity()
        => VerifyIntegrity(ReadBytes);

    public static IReadOnlyList<string> VerifyIntegrity(string directory)
    {
        var root = Path.GetFullPath(directory);
        return VerifyIntegrity(fileName =>
        {
            if (!IsSimpleFileName(fileName))
                throw new InvalidDataException($"Acceptance fixture manifest contains an unsafe file name '{fileName}'.");
            return File.ReadAllBytes(Path.Combine(root, fileName));
        });
    }

    private static IReadOnlyList<string> VerifyIntegrity(Func<string, byte[]> readBytes)
    {
        var errors = new List<string>();
        var manifestBytes = readBytes("fixture-manifest.json");
        if (!StringComparer.Ordinal.Equals(Digest(manifestBytes), ManifestSha256))
            errors.Add("fixture-manifest.json digest differs from its pinned value.");

        using var manifest = JsonDocument.Parse(manifestBytes);
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            if (!IsSimpleFileName(entry.Name))
            {
                errors.Add($"{entry.Name} is not a safe acceptance fixture file name.");
                continue;
            }
            var actual = Digest(readBytes(entry.Name));
            if (!StringComparer.Ordinal.Equals(actual, entry.Value.GetString()))
                errors.Add($"{entry.Name} digest differs from fixture-manifest.json.");
        }

        return errors;
    }

    private static bool IsSimpleFileName(string fileName)
        => !string.IsNullOrWhiteSpace(fileName)
            && !fileName.Contains('/')
            && !fileName.Contains('\\')
            && !fileName.Contains("..", StringComparison.Ordinal)
            && StringComparer.Ordinal.Equals(Path.GetFileName(fileName), fileName);
}

public sealed record AcceptanceFactOracle(
    string Concept,
    string EntityScheme,
    string EntityIdentifier,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string OimPeriodLexical,
    string Unit,
    string ValueLexical,
    int Decimals,
    string? SourceField,
    string? SourceLocator,
    string? SourceValueLexical,
    string? WorkbookDisplayLexical,
    string? ScaleFactorLexical)
{
    public decimal Value => decimal.Parse(ValueLexical, NumberStyles.Number, CultureInfo.InvariantCulture);
}
