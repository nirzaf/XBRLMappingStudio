using System.Collections;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class ProvenanceAssessmentTests
{
    private static readonly InputIdentity Input = new("binding", "file", "sha256:verified", false, "source.csv");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutFactFindings_EnumeratesOnceAndNeverBuildsALocatorMap(bool documentFinding)
    {
        var facts = new EnumerationOnlyFacts(new[] { Fact(1), Fact(2), Fact(3) });
        var diagnostics = documentFinding ? new[] { DocumentFinding() } : Array.Empty<Diagnostic>();

        Assert.Equal(RunProvenanceStatus.Complete,
            MappingJobService.AssessProvenanceStatus(facts, new[] { Input }, diagnostics));
        Assert.Equal(1, facts.EnumerationCount);
        Assert.True(facts.EnumeratorDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FactFindings_MatchCopiedSourcePathsAndExactDuplicateOccurrences(bool diskBacked)
    {
        // Identical semantic facts have different source occurrences, with another
        // semantic key interleaved to exercise the spool's occurrence ordering.
        var facts = new[] { Fact(1), Fact(2, value: 7m), Fact(3) };
        var locators = OimSerializer.GetFactLocators(facts);
        var findings = facts.Select((fact, index) => Finding(fact, locators[index])).ToArray();
        var root = NewDirectory();
        try
        {
            using var artifact = CreateArtifact(root, facts);
            RunProvenanceStatus Assess(IReadOnlyList<Diagnostic> diagnostics)
                => diskBacked
                    ? MappingJobService.AssessProvenanceStatus(artifact, new[] { Input }, diagnostics)
                    : MappingJobService.AssessProvenanceStatus(facts, new[] { Input }, diagnostics);

            Assert.NotEqual(locators[0], locators[2]);
            Assert.Equal(RunProvenanceStatus.Complete, Assess(findings));
            // Repeated findings may reference the same fact, but a duplicate fact's
            // paths cannot stand in for the requested occurrence's source evidence.
            Assert.Equal(RunProvenanceStatus.Complete, Assess(new[] { findings[2], findings[2] }));
            Assert.Equal(RunProvenanceStatus.Incomplete, Assess(new[]
            {
                findings[0] with { SourceTransformationPaths = findings[2].SourceTransformationPaths }
            }));
            Assert.Equal(RunProvenanceStatus.Incomplete, Assess(new[]
            {
                findings[0] with { FactLocator = "f-not-in-report" }
            }));
            Assert.Equal(RunProvenanceStatus.Incomplete, Assess(new[]
            {
                findings[0] with
                {
                    SourceTransformationPaths = new[]
                    {
                        new SourceTransformationPath(facts[0].SourceTransformationPaths[0].Source,
                            new[] { "fact", "source" })
                    }
                }
            }));
            Assert.Equal(RunProvenanceStatus.Incomplete, Assess(new[]
            {
                findings[0] with { SourceTransformationPaths = null }
            }));
            Assert.Equal(RunProvenanceStatus.Incomplete, Assess(new[]
            {
                findings[0] with { Source = null }
            }));
            Assert.Equal(RunProvenanceStatus.Incomplete, Assess(new[]
            {
                findings[0] with { FactLocator = null, Stage = DiagnosticStage.Worker }
            }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LargeAssessment_ValidatesAllFactsEvenWithoutFactFindings()
    {
        var root = NewDirectory();
        try
        {
            var facts = new[] { Fact(1), Fact(2, withPaths: false) };
            using var artifact = CreateArtifact(root, facts);
            Assert.Equal(RunProvenanceStatus.Incomplete,
                MappingJobService.AssessProvenanceStatus(artifact, new[] { Input }, Array.Empty<Diagnostic>()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Assessment_RequiresNonemptyFactsAndVerifiedInputs(bool diskBacked)
    {
        var root = NewDirectory();
        try
        {
            var facts = new[] { Fact(1) };
            using var artifact = CreateArtifact(root, facts);
            var unverified = new[] { Input with { Digest = null } };
            Assert.Equal(RunProvenanceStatus.Incomplete, diskBacked
                ? MappingJobService.AssessProvenanceStatus(artifact, unverified, Array.Empty<Diagnostic>())
                : MappingJobService.AssessProvenanceStatus(facts, unverified, Array.Empty<Diagnostic>()));
            Assert.Equal(RunProvenanceStatus.Incomplete, diskBacked
                ? MappingJobService.AssessProvenanceStatus(artifact, Array.Empty<InputIdentity>(), Array.Empty<Diagnostic>())
                : MappingJobService.AssessProvenanceStatus(facts, Array.Empty<InputIdentity>(), Array.Empty<Diagnostic>()));
        }
        finally { Directory.Delete(root, recursive: true); }

        root = NewDirectory();
        try
        {
            using var empty = CreateArtifact(root, Array.Empty<XbrlFact>());
            Assert.Equal(RunProvenanceStatus.Incomplete, diskBacked
                ? MappingJobService.AssessProvenanceStatus(empty, new[] { Input }, Array.Empty<Diagnostic>())
                : MappingJobService.AssessProvenanceStatus(Array.Empty<XbrlFact>(), new[] { Input }, Array.Empty<Diagnostic>()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CancellationDuringFactScan_StopsAndDisposesTheEnumerator()
    {
        using var cancellation = new CancellationTokenSource();
        var facts = new EnumerationOnlyFacts(new[] { Fact(1), Fact(2) }, cancellation.Cancel);
        Assert.Throws<OperationCanceledException>(() => MappingJobService.AssessProvenanceStatus(
            facts, new[] { Input }, Array.Empty<Diagnostic>(), cancellation.Token));
        Assert.Equal(1, facts.EnumerationCount);
        Assert.True(facts.EnumeratorDisposed);
    }

    [Fact]
    public void LargeAssessment_ObservesCancellationBeforeScanningTheSpool()
    {
        var root = NewDirectory();
        try
        {
            using var artifact = CreateArtifact(root, new[] { Fact(1), Fact(2) });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => MappingJobService.AssessProvenanceStatus(
                artifact, new[] { Input }, Array.Empty<Diagnostic>(), cancellation.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static XbrlFact Fact(int row, decimal value = 12m, bool withPaths = true) => new()
    {
        ConceptName = "synthetic:Revenue",
        EntityScheme = "https://example.com/entity",
        EntityIdentifier = "ACME",
        Period = XbrlPeriod.OfDuration(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1)),
        Unit = new XbrlUnit("USD"),
        Value = value,
        SourceTransformationPaths = withPaths ? new[]
        {
            new SourceTransformationPath(new SourceReference("binding", $"row={row}"), new[] { "source", "fact" })
        } : Array.Empty<SourceTransformationPath>()
    };

    private static Diagnostic DocumentFinding() => new()
    {
        Code = "XBR001", Subsystem = DiagnosticSubsystem.XBRL, Severity = DiagnosticSeverity.Warning,
        Stage = DiagnosticStage.Validation, Message = "Document-level finding"
    };

    private static Diagnostic Finding(XbrlFact fact, string locator) => DocumentFinding() with
    {
        FactLocator = locator,
        // A worker may supply a primary report location; it need not equal the
        // source location recovered from the spool's transformation paths.
        Source = new SourceReference("worker", "report.json#" + locator),
        SourceTransformationPaths = fact.SourceTransformationPaths.Select(path =>
            new SourceTransformationPath(path.Source with { }, path.TransformationIds.ToArray())).ToArray()
    };

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "xbrl-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static LargeRunArtifact CreateArtifact(string root, IEnumerable<XbrlFact> facts)
    {
        var directory = Path.Combine(root, "artifact");
        Directory.CreateDirectory(directory);
        var store = new LargeRunFactStore(Path.Combine(directory, "facts.db"), 256, 8 * 1024 * 1024);
        try
        {
            foreach (var fact in facts) store.Add(fact, 1024 * 1024);
            store.Finish();
            return new LargeRunArtifact(directory, store, Path.Combine(directory, "report.json"), "sha256:test", 0);
        }
        catch { store.Dispose(); throw; }
    }

    private sealed class EnumerationOnlyFacts(IReadOnlyList<XbrlFact> facts, Action? beforeYield = null)
        : IReadOnlyList<XbrlFact>
    {
        public int Count => facts.Count;
        public int EnumerationCount { get; private set; }
        public bool EnumeratorDisposed { get; private set; }
        public XbrlFact this[int index] => throw new InvalidOperationException("Random fact access is forbidden.");
        public IEnumerator<XbrlFact> GetEnumerator()
        {
            if (++EnumerationCount != 1) throw new InvalidOperationException("A second fact scan is forbidden.");
            try
            {
                foreach (var fact in facts)
                {
                    beforeYield?.Invoke();
                    yield return fact;
                }
            }
            finally { EnumeratorDisposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
