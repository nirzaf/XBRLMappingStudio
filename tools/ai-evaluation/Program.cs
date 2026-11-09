using System.Security.Cryptography;
using System.Text.Json;
using XBRLMappingStudio.AI;
using XBRLMappingStudio.XBRL;

const string datasetFileName = "synthetic-engineering-v1.json";
const string expectedDatasetDigest = "sha256:3d685b8d789f4be44e93e921bcb1ecec98e70b1227238d0eb08f75c1a5f27e13";
const string scope = "Synthetic engineering behavior only. This report is not a held-out accounting mapping evaluation, calibrated confidence measure, human acceptance study, or production-quality claim.";
var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", datasetFileName);
var outputPath = (string?)null;
var selfTest = false;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--self-test":
            selfTest = true;
            break;
        case "--fixture" when index + 1 < args.Length:
            fixturePath = Path.GetFullPath(args[++index]);
            break;
        case "--output" when index + 1 < args.Length:
            outputPath = Path.GetFullPath(args[++index]);
            break;
        default:
            Console.Error.WriteLine("Usage: XBRLMappingStudio.AIEvaluation [--self-test] [--fixture <path>] [--output <path>]");
            return 2;
    }
}

try
{
    var fixtureBytes = await File.ReadAllBytesAsync(fixturePath);
    var datasetDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant();
    if (selfTest && !StringComparer.Ordinal.Equals(datasetDigest, expectedDatasetDigest))
        throw new InvalidDataException($"Synthetic evaluation fixture digest changed: expected {expectedDatasetDigest}, got {datasetDigest}.");

    using var document = JsonDocument.Parse(fixtureBytes, new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    });
    var root = document.RootElement;
    var datasetId = ReadRequiredString(root, "datasetId");
    var declaredScope = ReadRequiredString(root, "scope");
    var topK = ReadRequiredInt(root, "topK");
    if (topK is < 1 or > LocalRetrievalRanker.MaximumResults)
        throw new InvalidDataException("Fixture topK is outside the supported candidate bound.");

    var profileElement = root.GetProperty("profile");
    var profileId = ReadRequiredString(profileElement, "profileId");
    var taxonomyVersion = ReadRequiredString(profileElement, "taxonomyVersion");
    var prefix = ReadRequiredString(profileElement, "namespacePrefix");
    var namespaceUri = ReadRequiredString(profileElement, "namespaceUri");
    var concepts = profileElement.GetProperty("concepts").EnumerateArray().Select(concept =>
        new ConceptMetadata(
            ReadRequiredString(concept, "qualifiedName"),
            namespaceUri,
            ReadRequiredString(concept, "localName"),
            ReadRequiredString(concept, "label"),
            ReadRequiredString(concept, "dataType"),
            ReadRequiredString(concept, "periodType"),
            IsAbstract: false)).ToArray();
    var aliases = profileElement.GetProperty("aliases").EnumerateObject()
        .ToDictionary(
            property => property.Name,
            property => property.Value.EnumerateArray().Select(value => value.GetString()
                ?? throw new InvalidDataException("Alias values must be strings.")).ToArray(),
            StringComparer.Ordinal);
    var units = profileElement.GetProperty("supportedUnits").EnumerateArray()
        .Select(unit => unit.GetString() ?? throw new InvalidDataException("Unit values must be strings."))
        .ToHashSet(StringComparer.Ordinal);
    var profileDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(profileElement))).ToLowerInvariant();
    var profile = new FilingProfile
    {
        ProfileId = profileId,
        Digest = profileDigest,
        TaxonomyVersion = taxonomyVersion,
        Concepts = concepts,
        SupportedUnits = units,
        NamespaceUris = new Dictionary<string, string>(StringComparer.Ordinal) { [prefix] = namespaceUri }
    };
    var ranker = new LocalRetrievalRanker(new TaxonomyIndex(profile), aliases);
    var cases = root.GetProperty("cases").EnumerateArray().Select(testCase =>
    {
        var expected = testCase.GetProperty("relevantConcepts").EnumerateArray()
            .Select(value => value.GetString() ?? throw new InvalidDataException("Relevant concepts must be strings."))
            .ToHashSet(StringComparer.Ordinal);
        var descriptor = new SourceFieldDescriptor(
            ReadRequiredString(testCase, "caseId"),
            ReadRequiredString(testCase, "label"),
            ReadRequiredString(testCase, "unit"),
            ReadRequiredString(testCase, "periodType"));
        var ranked = ranker.Rank(descriptor, topK);
        return new CaseResult(
            descriptor.Id,
            ReadRequiredString(testCase, "category"),
            descriptor.Name,
            expected.Count > 0,
            expected.Count == 0,
            expected.Count > 1,
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            ranked.Select(candidate => candidate.QualifiedName).ToArray());
    }).ToArray();

    if (cases.Length == 0 || cases.Select(result => result.CaseId).Distinct(StringComparer.Ordinal).Count() != cases.Length)
        throw new InvalidDataException("Evaluation cases must be non-empty and have unique IDs.");
    if (cases.Any(result => result.RelevantConcepts.Any(name => !concepts.Any(concept => concept.QualifiedName == name))))
        throw new InvalidDataException("An expected concept is absent from the fixture taxonomy.");

    var report = BuildReport(datasetId, declaredScope, datasetDigest, profileId, profileDigest, topK, cases);
    if (selfTest)
        VerifySelfTest(report);

    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    if (outputPath is null)
        Console.WriteLine(json);
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json);
        Console.WriteLine($"Wrote synthetic evaluation report to {outputPath}");
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
    or JsonException or InvalidDataException or InvalidOperationException or ArgumentException
    or KeyNotFoundException)
{
    Console.Error.WriteLine($"Synthetic AI evaluation failed: {exception.Message}");
    return 1;
}

static string ReadRequiredString(JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var property)
        || property.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(property.GetString()))
        throw new InvalidDataException($"Fixture field '{name}' must be a non-empty string.");
    return property.GetString()!;
}

static int ReadRequiredInt(JsonElement element, string name)
{
    if (!element.TryGetProperty(name, out var property)
        || property.ValueKind != JsonValueKind.Number
        || !property.TryGetInt32(out var value))
        throw new InvalidDataException($"Fixture field '{name}' must be an integer.");
    return value;
}

static EvaluationReport BuildReport(
    string datasetId,
    string datasetScope,
    string datasetDigest,
    string profileId,
    string profileDigest,
    int topK,
    IReadOnlyList<CaseResult> cases)
{
    var answerable = cases.Where(item => item.IsAnswerable).ToArray();
    var noMatch = cases.Where(item => item.IsNoMatch).ToArray();
    var topKHits = answerable.Count(item => item.Ranked.Take(topK).Any(item.RelevantConcepts.Contains));
    var topOneHits = answerable.Count(item => item.Ranked.FirstOrDefault() is { } top
        && item.RelevantConcepts.Contains(top));
    var wrongTopOne = answerable.Length - topOneHits;
    var candidateCount = answerable.Sum(item => item.Ranked.Count);
    var relevantCandidateCount = answerable.Sum(item => item.Ranked.Count(item.RelevantConcepts.Contains));
    var incorrectCandidateCount = candidateCount - relevantCandidateCount;
    var noMatchAbstentions = noMatch.Count(item => item.Ranked.Count == 0);
    var noMatchSuggestions = noMatch.Length - noMatchAbstentions;
    var reportScope = $"{scope} Dataset scope: {datasetScope}";
    return new EvaluationReport(
        "xms-ai-engineering-evaluation-v1",
        datasetId,
        datasetDigest,
        profileId,
        profileDigest,
        reportScope,
        HumanAcceptanceMetricsAvailable: false,
        topK,
        cases.Count,
        new EvaluationMetrics(
            Metric.From(topKHits, answerable.Length),
            Metric.From(topOneHits, answerable.Length),
            Metric.From(wrongTopOne, answerable.Length),
            Metric.From(relevantCandidateCount, candidateCount),
            Metric.From(incorrectCandidateCount, candidateCount),
            Metric.From(noMatchAbstentions, noMatch.Length),
            Metric.From(noMatchSuggestions, noMatch.Length),
            answerable.Length == 0 ? 0 : answerable.Average(item => Math.Min(item.Ranked.Count, topK)),
            cases.Count == 0 ? 0 : cases.Average(item => Math.Min(item.Ranked.Count, topK))),
        cases.OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.CaseId, StringComparer.Ordinal)
            .ToArray(),
        cases.GroupBy(item => item.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CategoryResult(
                group.Key,
                group.Count(),
                group.Count(item => item.IsAnswerable),
                group.Count(item => item.IsNoMatch),
                group.Count(item => item.Ranked.Count == 0),
                Metric.From(group.Where(item => item.IsAnswerable).Count(item => item.Ranked.Take(topK).Any(item.RelevantConcepts.Contains)),
                    group.Count(item => item.IsAnswerable)),
                Metric.From(group.Where(item => item.IsAnswerable).Count(item => item.Ranked.FirstOrDefault() is { } top && item.RelevantConcepts.Contains(top)),
                    group.Count(item => item.IsAnswerable)),
                Metric.From(
                    group.Where(item => item.IsAnswerable).Sum(item => item.Ranked.Count(item.RelevantConcepts.Contains)),
                    group.Where(item => item.IsAnswerable).Sum(item => item.Ranked.Count)),
                Metric.From(group.Count(item => item.IsNoMatch && item.Ranked.Count == 0), group.Count(item => item.IsNoMatch)),
                group.Average(item => Math.Min(item.Ranked.Count, topK))))
            .ToArray());
}

static void VerifySelfTest(EvaluationReport report)
{
    var metrics = report.Metrics;
    if (report.CaseCount != 8
        || metrics.TopKRecall.Successes != 5 || metrics.TopKRecall.Trials != 5
        || metrics.TopOneAccuracy.Successes != 5 || metrics.TopOneAccuracy.Trials != 5
        || metrics.WrongTopOneRate.Successes != 0 || metrics.WrongTopOneRate.Trials != 5
        || metrics.CandidatePrecision.Successes != 6 || metrics.CandidatePrecision.Trials != 9
        || metrics.IncorrectCandidateRate.Successes != 3 || metrics.IncorrectCandidateRate.Trials != 9
        || metrics.NoMatchAbstentionRate.Successes != 3 || metrics.NoMatchAbstentionRate.Trials != 3
        || metrics.NoMatchFalseSuggestionRate.Successes != 0 || metrics.NoMatchFalseSuggestionRate.Trials != 3
        || report.Cases.Count(item => item.IsAmbiguous && item.Ranked.Count >= 2) != 1)
        throw new InvalidDataException("Synthetic evaluation behavior or fixture sample counts changed; review the fixture and update its pinned digest and expectations together.");
}

internal sealed record EvaluationReport(
    string EvaluationId,
    string DatasetId,
    string DatasetDigest,
    string ProfileId,
    string ProfileDigest,
    string Scope,
    bool HumanAcceptanceMetricsAvailable,
    int TopK,
    int CaseCount,
    EvaluationMetrics Metrics,
    IReadOnlyList<CaseResult> Cases,
    IReadOnlyList<CategoryResult> Categories);

internal sealed record EvaluationMetrics(
    Metric TopKRecall,
    Metric TopOneAccuracy,
    Metric WrongTopOneRate,
    Metric CandidatePrecision,
    Metric IncorrectCandidateRate,
    Metric NoMatchAbstentionRate,
    Metric NoMatchFalseSuggestionRate,
    double MeanCandidatesPerAnswerableCase,
    double MeanCandidatesPerCase);

internal sealed record CaseResult(
    string CaseId,
    string Category,
    string SourceLabel,
    bool IsAnswerable,
    bool IsNoMatch,
    bool IsAmbiguous,
    IReadOnlyList<string> RelevantConcepts,
    IReadOnlyList<string> Ranked);

internal sealed record CategoryResult(
    string Category,
    int CaseCount,
    int AnswerableCount,
    int NoMatchCount,
    int AbstentionCount,
    Metric TopKRecall,
    Metric TopOneAccuracy,
    Metric CandidatePrecision,
    Metric NoMatchAbstentionRate,
    double MeanCandidates);

internal sealed record Metric(int Successes, int Trials, double Estimate, double Wilson95Lower, double Wilson95Upper)
{
    public static Metric From(int successes, int trials)
    {
        if (trials <= 0) return new Metric(successes, trials, 0, 0, 0);
        const double z = 1.959963984540054;
        var p = (double)successes / trials;
        var denominator = 1 + z * z / trials;
        var center = (p + z * z / (2 * trials)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / trials + z * z / (4d * trials * trials)) / denominator;
        var lower = successes == 0 ? 0 : Math.Max(0, center - margin);
        var upper = successes == trials ? 1 : Math.Min(1, center + margin);
        return new Metric(successes, trials, p, lower, upper);
    }
}
