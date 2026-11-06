using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.TaxonomyBenchmark;

internal static class Program
{
    private const string ScenarioId = "taxonomy-search-20k";
    private const string RoleUri = "urn:xbrlms:synthetic:roles:financial-statements";
    private const int DefaultConceptCount = 20_000;
    private const int DefaultSearchSamples = 100;
    private const int DefaultExpansionSamples = 100;
    private const int DefaultPageSize = 25;
    private const int MaximumConceptCount = 500_000;
    private const string GeneratorVersion = "synthetic-taxonomy-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                PrintUsage();
                return 0;
            }

            if (args.Length == 1 && args[0] == "--self-test")
            {
                await RunSelfTestAsync();
                Console.WriteLine($"Performance fixture self-test passed ({DefaultConceptCount.ToString(CultureInfo.InvariantCulture)} concepts; graph sizes 100, 500, 1000, and 2000).");
                return 0;
            }

            if (args.Length == 2 && args[0] == "--generate-graph-fixtures")
            {
                GraphCanvasFixtureGenerator.Generate(args[1]);
                Console.WriteLine($"Wrote four deterministic graph-canvas fixtures and their manifest to {Path.GetFullPath(args[1])}");
                return 0;
            }

            var options = ParseOptions(args);
            var report = await RunBenchmarkAsync(options);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            if (options.OutputPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                WriteNewFile(options.OutputPath, json);
                Console.WriteLine($"Wrote synthetic taxonomy benchmark record: {Path.GetFullPath(options.OutputPath)}");
            }

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Taxonomy benchmark failed: {exception.Message}");
            return 2;
        }
    }

    private static async Task<BenchmarkReport> RunBenchmarkAsync(BenchmarkOptions options)
    {
        var fixtureTimer = Stopwatch.StartNew();
        var profile = SyntheticTaxonomyFixture.Create(options.ConceptCount);
        fixtureTimer.Stop();

        var indexTimer = Stopwatch.StartNew();
        var index = new TaxonomyIndex(profile);
        indexTimer.Stop();

        var search = MeasureWarmSearch(index, options.SearchSamples);

        var networkTimer = Stopwatch.StartNew();
        var network = index.Network(RoleUri);
        networkTimer.Stop();
        var expansion = MeasureRoleExpansion(network, options.ConceptCount, options.ExpansionSamples, options.PageSize);

        var recreationTimer = Stopwatch.StartNew();
        var recreatedIndex = new TaxonomyIndex(profile);
        recreationTimer.Stop();
        var exactQuery = ExactLabelQuery(options.ConceptCount);
        var recreationMatches = recreatedIndex.IsCurrentFor(profile)
            && recreatedIndex.AllConcepts.Count == index.AllConcepts.Count
            && recreatedIndex.Search(exactQuery, pageSize: 1).Items.Single().QualifiedName
                == index.Search(exactQuery, pageSize: 1).Items.Single().QualifiedName;
        if (!recreationMatches)
            throw new InvalidOperationException("Recreated taxonomy index did not preserve its profile identity and search result.");

        await VerifyPreCancelledSearchAsync(index);

        return new BenchmarkReport
        {
            SchemaVersion = 1,
            ScenarioId = options.ConceptCount == DefaultConceptCount ? ScenarioId : "taxonomy-search-custom",
            MeasurementStatus = "measured_synthetic_only",
            ThresholdPassClaimed = false,
            CapturedUtc = DateTimeOffset.UtcNow,
            Runtime = new RuntimeRecord
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                OperatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorDescription = ProcessorDescription(),
                ProcessorCount = Environment.ProcessorCount,
                Configuration = IsReleaseBuild() ? "Release" : "non-Release",
                StopwatchHighResolution = Stopwatch.IsHighResolution,
                StopwatchFrequency = Stopwatch.Frequency
            },
            Fixture = new FixtureRecord
            {
                Generator = GeneratorVersion,
                ProfileId = profile.ProfileId,
                ProfileDigest = profile.Digest,
                ConceptCount = profile.Concepts.Count,
                RelationshipCount = profile.PresentationLinks.Count,
                RoleUri = RoleUri,
                GroupCount = SyntheticTaxonomyFixture.GroupCountFor(options.ConceptCount),
                DigestBinding = "sha256 of version, profile identity, ordered concepts and relationships, supported units, and namespace bindings"
            },
            Measurements = new MeasurementRecord
            {
                FixtureGenerationMs = Milliseconds(fixtureTimer.Elapsed),
                IndexConstructionMs = Milliseconds(indexTimer.Elapsed),
                RoleNetworkConstructionMs = Milliseconds(networkTimer.Elapsed),
                IndexRecreationMs = Milliseconds(recreationTimer.Elapsed),
                WarmSearch = search,
                LazyRoleExpansion = expansion
            },
            Integrity = new IntegrityRecord
            {
                FixtureAndIndexIdentityVerified = index.IsCurrentFor(profile),
                RecreatedIndexMatches = recreationMatches,
                PreCancelledSearchObserved = true,
                RapidSupersedingQueryCoverage = "covered by TaxonomyExplorerTests.RapidSearchCriteriaChanges_LeaveOnlyTheNewestTwentyThousandConceptQuery"
            },
            Interpretation = new InterpretationRecord
            {
                Notes = new[]
                {
                    "These measurements cover the synthetic TaxonomyIndex and PresentationNetwork only.",
                    "They do not measure desktop rendering, a licensed taxonomy, target hardware, or any product threshold.",
                    "Warm search uses a broad label query and exact-label query in alternating order; role expansion requests bounded pages after network construction."
                }
            }
        };
    }

    private static SearchMeasurement MeasureWarmSearch(TaxonomyIndex index, int sampleCount)
    {
        const int warmupCount = 5;
        var exactQuery = ExactLabelQuery(index.AllConcepts.Count);
        for (var i = 0; i < warmupCount; i++)
            _ = index.Search(i % 2 == 0 ? "Disclosure" : exactQuery, pageSize: 1);

        var samples = new List<SearchSample>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            var broad = i % 2 == 0;
            var query = broad ? "Disclosure" : exactQuery;
            var started = Stopwatch.GetTimestamp();
            var result = index.Search(query, pageSize: 1);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var expectedCount = broad ? index.AllConcepts.Count : 1;
            if (result.TotalCount != expectedCount || result.Items.Count != 1)
                throw new InvalidOperationException($"The synthetic search query '{query}' returned an unexpected result count.");
            samples.Add(new SearchSample(i, broad ? "broad" : "exact_label", query, Milliseconds(elapsed), result.TotalCount));
        }

        var raw = samples.Select(sample => sample.ElapsedMs).ToArray();
        return new SearchMeasurement
        {
            WarmupCount = warmupCount,
            SampleCount = samples.Count,
            P50Ms = Percentile(raw, 0.50),
            P95Ms = Percentile(raw, 0.95),
            MaximumMs = raw.Max(),
            RawSamples = samples
        };
    }

    private static ExpansionMeasurement MeasureRoleExpansion(
        PresentationNetwork network,
        int conceptCount,
        int sampleCount,
        int pageSize)
    {
        var groupCount = SyntheticTaxonomyFixture.GroupCountFor(conceptCount);
        var leafCount = conceptCount - groupCount - 1;
        var samples = new List<ExpansionSample>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            var groupOrdinal = i % groupCount;
            var qname = SyntheticTaxonomyFixture.QualifiedName(groupOrdinal + 1);
            var page = i / groupCount % 2;
            var expectedChildren = leafCount / groupCount + (groupOrdinal < leafCount % groupCount ? 1 : 0);
            var started = Stopwatch.GetTimestamp();
            var result = network.ChildrenPage(qname, page, pageSize);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var expectedItems = Math.Max(0, Math.Min(pageSize, expectedChildren - page * pageSize));
            if (result.TotalCount != expectedChildren || result.Items.Count != expectedItems)
                throw new InvalidOperationException("A synthetic role expansion page did not match its expected bounded result.");
            samples.Add(new ExpansionSample(i, qname, page, Milliseconds(elapsed), result.Items.Count, result.TotalCount));
        }

        var raw = samples.Select(sample => sample.ElapsedMs).ToArray();
        return new ExpansionMeasurement
        {
            SampleCount = samples.Count,
            PageSize = pageSize,
            P50Ms = Percentile(raw, 0.50),
            P95Ms = Percentile(raw, 0.95),
            MaximumMs = raw.Max(),
            RawSamples = samples
        };
    }

    private static async Task RunSelfTestAsync()
    {
        var first = SyntheticTaxonomyFixture.Create(DefaultConceptCount);
        var second = SyntheticTaxonomyFixture.Create(DefaultConceptCount);
        if (first.Digest != second.Digest)
            throw new InvalidOperationException("Synthetic taxonomy fixture digest is not stable across recreation.");

        var report = await RunBenchmarkAsync(new BenchmarkOptions(
            DefaultConceptCount, DefaultSearchSamples, DefaultExpansionSamples, DefaultPageSize, null));
        if (report.Fixture.ConceptCount != DefaultConceptCount
            || report.Measurements.WarmSearch.SampleCount != DefaultSearchSamples
            || report.Measurements.LazyRoleExpansion.SampleCount != DefaultExpansionSamples
            || report.Measurements.WarmSearch.RawSamples.Count != DefaultSearchSamples
            || report.Measurements.LazyRoleExpansion.RawSamples.Count != DefaultExpansionSamples
            || report.ThresholdPassClaimed)
        {
            throw new InvalidOperationException("Synthetic taxonomy benchmark self-test did not produce the required raw measurements.");
        }

        GraphCanvasFixtureGenerator.RunSelfTest();
    }

    private static async Task VerifyPreCancelledSearchAsync(TaxonomyIndex index)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await index.SearchAsync("Disclosure", cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Taxonomy search did not observe a pre-cancelled request.");
    }

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(values));
        var sorted = values.Order().ToArray();
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Length));
        return sorted[rank - 1];
    }

    private static double Milliseconds(TimeSpan duration) => duration.TotalMilliseconds;

    private static string ExactLabelQuery(int conceptCount)
        => $"Disclosure {Math.Min(conceptCount - 1, 1_999):D5}";

    private static bool IsReleaseBuild()
    {
#if RELEASE
        return true;
#else
        return false;
#endif
    }

    private static string ProcessorDescription()
    {
        var windowsDescription = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(windowsDescription)) return windowsDescription.Trim();
        try
        {
            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    var separator = line.IndexOf(':');
                    if (separator <= 0) continue;
                    var key = line[..separator].Trim();
                    if (key is "model name" or "Hardware" or "Processor")
                    {
                        var value = line[(separator + 1)..].Trim();
                        if (value.Length > 0) return value;
                    }
                }
            }
        }
        catch (IOException)
        {
            // CPU model is supplementary; keep the record usable if host metadata is unavailable.
        }

        return "unavailable";
    }

    private static BenchmarkOptions ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Invalid option sequence near '{args[i]}'. Use --help for usage.");
            if (!values.TryAdd(args[i], args[++i]))
                throw new ArgumentException($"Option '{args[i - 1]}' was specified more than once.");
        }

        var conceptCount = ReadInt(values, "--concept-count", DefaultConceptCount, 3, MaximumConceptCount);
        var searchSamples = ReadInt(values, "--search-samples", DefaultSearchSamples, 1, 100_000);
        var expansionSamples = ReadInt(values, "--expansion-samples", DefaultExpansionSamples, 1, 100_000);
        var pageSize = ReadInt(values, "--page-size", DefaultPageSize, 1, 10_000);
        var outputPath = values.TryGetValue("--output", out var path) ? path : null;
        var unknown = values.Keys.Except(new[]
        {
            "--concept-count", "--search-samples", "--expansion-samples", "--page-size", "--output"
        }, StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null) throw new ArgumentException($"Unknown option '{unknown}'. Use --help for usage.");
        if (outputPath is not null && string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("--output must name a new output file.");

        return new BenchmarkOptions(conceptCount, searchSamples, expansionSamples, pageSize, outputPath);
    }

    private static int ReadInt(Dictionary<string, string> options, string name, int fallback, int minimum, int maximum)
    {
        if (!options.TryGetValue(name, out var value)) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"{name} must be an integer between {minimum} and {maximum}.");
        return parsed;
    }

    private static void WriteNewFile(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(content);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Synthetic performance fixtures (measurements never claim a threshold passed).");
        Console.WriteLine("  dotnet run --project tools/performance/taxonomy-benchmark -c Release -- [options]");
        Console.WriteLine("  --concept-count N       Synthetic concept count (default 20000; range 3..500000)");
        Console.WriteLine("  --search-samples N       Warm search samples (default 100)");
        Console.WriteLine("  --expansion-samples N    Lazy role-page samples (default 100)");
        Console.WriteLine("  --page-size N            Role expansion page size (default 25)");
        Console.WriteLine("  --output PATH            Write a new JSON record; existing files are refused");
        Console.WriteLine("  --generate-graph-fixtures DIR  Write 100/500/1000/2000-node .xmap fixtures and SHA-256 manifest to a new directory");
        Console.WriteLine("  --self-test              Check taxonomy benchmarks and graph fixture generation/round trips");
    }

    private sealed record BenchmarkOptions(
        int ConceptCount,
        int SearchSamples,
        int ExpansionSamples,
        int PageSize,
        string? OutputPath);

    private sealed class BenchmarkReport
    {
        public required int SchemaVersion { get; init; }
        public required string ScenarioId { get; init; }
        public required string MeasurementStatus { get; init; }
        public required bool ThresholdPassClaimed { get; init; }
        public required DateTimeOffset CapturedUtc { get; init; }
        public required RuntimeRecord Runtime { get; init; }
        public required FixtureRecord Fixture { get; init; }
        public required MeasurementRecord Measurements { get; init; }
        public required IntegrityRecord Integrity { get; init; }
        public required InterpretationRecord Interpretation { get; init; }
    }

    private sealed class RuntimeRecord
    {
        public required string Framework { get; init; }
        public required string OperatingSystem { get; init; }
        public required string OperatingSystemArchitecture { get; init; }
        public required string ProcessArchitecture { get; init; }
        public required string ProcessorDescription { get; init; }
        public required int ProcessorCount { get; init; }
        public required string Configuration { get; init; }
        public required bool StopwatchHighResolution { get; init; }
        public required long StopwatchFrequency { get; init; }
    }

    private sealed class FixtureRecord
    {
        public required string Generator { get; init; }
        public required string ProfileId { get; init; }
        public required string ProfileDigest { get; init; }
        public required int ConceptCount { get; init; }
        public required int RelationshipCount { get; init; }
        public required string RoleUri { get; init; }
        public required int GroupCount { get; init; }
        public required string DigestBinding { get; init; }
    }

    private sealed class MeasurementRecord
    {
        public required double FixtureGenerationMs { get; init; }
        public required double IndexConstructionMs { get; init; }
        public required double RoleNetworkConstructionMs { get; init; }
        public required double IndexRecreationMs { get; init; }
        public required SearchMeasurement WarmSearch { get; init; }
        public required ExpansionMeasurement LazyRoleExpansion { get; init; }
    }

    private sealed class SearchMeasurement
    {
        public required int WarmupCount { get; init; }
        public required int SampleCount { get; init; }
        public required double P50Ms { get; init; }
        public required double P95Ms { get; init; }
        public required double MaximumMs { get; init; }
        public required IReadOnlyList<SearchSample> RawSamples { get; init; }
    }

    private sealed record SearchSample(int Index, string QueryKind, string Query, double ElapsedMs, int ResultCount);

    private sealed class ExpansionMeasurement
    {
        public required int SampleCount { get; init; }
        public required int PageSize { get; init; }
        public required double P50Ms { get; init; }
        public required double P95Ms { get; init; }
        public required double MaximumMs { get; init; }
        public required IReadOnlyList<ExpansionSample> RawSamples { get; init; }
    }

    private sealed record ExpansionSample(
        int Index,
        string ParentQName,
        int Page,
        double ElapsedMs,
        int ReturnedCount,
        int TotalChildren);

    private sealed class IntegrityRecord
    {
        public required bool FixtureAndIndexIdentityVerified { get; init; }
        public required bool RecreatedIndexMatches { get; init; }
        public required bool PreCancelledSearchObserved { get; init; }
        public required string RapidSupersedingQueryCoverage { get; init; }
    }

    private sealed class InterpretationRecord
    {
        public required IReadOnlyList<string> Notes { get; init; }
    }
}

internal static class SyntheticTaxonomyFixture
{
    private const string GeneratorVersion = "synthetic-taxonomy-v1";
    private const string NamespaceUri = "urn:xbrlms:synthetic:taxonomy:financial-disclosures";
    private const string ProfileId = "synthetic-financial-disclosures-v1";

    public static int GroupCountFor(int conceptCount) => Math.Min(200, Math.Max(1, (conceptCount - 1) / 2));

    public static string QualifiedName(int index) => $"t{index % 4}:Disclosure{index:D5}";

    public static FilingProfile Create(int conceptCount)
    {
        var concepts = new List<ConceptMetadata>(conceptCount);
        for (var i = 0; i < conceptCount; i++)
        {
            var localName = $"Disclosure{i:D5}";
            var namespaceIndex = i % 4;
            var label = $"Disclosure {i:D5}";
            var isAbstract = i == 0 || i <= GroupCountFor(conceptCount);
            concepts.Add(new ConceptMetadata(
                $"t{namespaceIndex}:{localName}",
                $"{NamespaceUri}:{namespaceIndex}",
                localName,
                label,
                "monetary",
                "duration",
                isAbstract)
            {
                SupportedLanguages = new[] { "en" },
                LabelsByLanguage = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = label }
            });
        }

        var groupCount = GroupCountFor(conceptCount);
        var links = new List<PresentationLink>(conceptCount - 1);
        for (var group = 0; group < groupCount; group++)
            links.Add(new PresentationLink(ProgramRoleUri, QualifiedName(0), QualifiedName(group + 1), group + 1));

        var firstLeaf = groupCount + 1;
        for (var leaf = firstLeaf; leaf < conceptCount; leaf++)
        {
            var groupIndex = (leaf - firstLeaf) % groupCount;
            links.Add(new PresentationLink(
                ProgramRoleUri,
                QualifiedName(groupIndex + 1),
                QualifiedName(leaf),
                leaf));
        }

        var digest = ComputeDigest(conceptCount, concepts, links);
        return new FilingProfile
        {
            ProfileId = ProfileId,
            Digest = digest,
            TaxonomyVersion = GeneratorVersion,
            Concepts = concepts.AsReadOnly(),
            SupportedUnits = new HashSet<string>(StringComparer.Ordinal) { "USD", "EUR" },
            PresentationLinks = links.AsReadOnly(),
            NamespaceUris = Enumerable.Range(0, 4).ToDictionary(
                index => $"t{index}",
                index => $"{NamespaceUri}:{index}",
                StringComparer.Ordinal)
        };
    }

    private static string ProgramRoleUri => "urn:xbrlms:synthetic:roles:financial-statements";

    private static string ComputeDigest(int conceptCount, IReadOnlyList<ConceptMetadata> concepts, IReadOnlyList<PresentationLink> links)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPart(hash, "synthetic-taxonomy-profile-digest-v1");
        AppendPart(hash, ProfileId);
        AppendPart(hash, GeneratorVersion);
        AppendPart(hash, ProgramRoleUri);
        AppendPart(hash, conceptCount.ToString(CultureInfo.InvariantCulture));
        foreach (var unit in new[] { "EUR", "USD" }) AppendPart(hash, unit);
        for (var namespaceIndex = 0; namespaceIndex < 4; namespaceIndex++)
        {
            AppendPart(hash, $"t{namespaceIndex}");
            AppendPart(hash, $"{NamespaceUri}:{namespaceIndex}");
        }
        foreach (var concept in concepts)
        {
            AppendPart(hash, concept.QualifiedName);
            AppendPart(hash, concept.Namespace);
            AppendPart(hash, concept.LocalName);
            AppendPart(hash, concept.Label);
            AppendPart(hash, concept.DataType);
            AppendPart(hash, concept.PeriodType);
            AppendPart(hash, concept.IsAbstract ? "1" : "0");
        }
        foreach (var link in links)
        {
            AppendPart(hash, link.RoleUri);
            AppendPart(hash, link.ParentQName);
            AppendPart(hash, link.ChildQName);
            AppendPart(hash, link.Order.ToString(CultureInfo.InvariantCulture));
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
