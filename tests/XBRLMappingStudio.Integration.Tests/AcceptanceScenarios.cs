using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

/// <summary>
/// Section 10 acceptance scenarios:
///   - Financial correctness of the emitted fact (XBR04/XBR05).
///   - Deliberately wrong mapping produces a useful diagnostic.
///   - Two independent runs (representing Desktop and CLI paths) yield semantically
///     equivalent output (OPS01 parity).
/// </summary>
public sealed class AcceptanceScenarios : IAsyncLifetime
{
    private string _dir = "";
    private string _workbookPath = "";

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xbrl-g1-int-" + Guid.NewGuid().ToString("N"));
        _workbookPath = TestFixtures.MaterializeWorkbook(_dir);
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static MappingJobService NewService()
    {
        var profile = SyntheticProfile.Build();
        var worker = new LocalSyntheticWorker(profile);
        return new MappingJobService(profile, worker);
    }

    private static IReadOnlyDictionary<string, SourceConnection> Connections(MappingProject project, string path)
    {
        var b = project.Bindings[0];
        var cfg = b.Configuration?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value?.ToString() ?? "")
            ?? new Dictionary<string, string>();
        return new Dictionary<string, SourceConnection>
        {
            [b.BindingId] = new SourceConnection(b.BindingId, "excel", path, cfg)
        };
    }

    [Fact]
    public async Task FinancialCorrectness_RevenueMatchesGoldenOracle()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var svc = NewService();
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);
        Assert.Single(result.Facts);

        var expected = TestFixtures.GoldenRevenueFacts().Single();
        var actual = result.Facts.Single();

        Assert.Equal(expected.ConceptName, actual.ConceptName);
        Assert.Equal(expected.EntityIdentifier, actual.EntityIdentifier);
        Assert.Equal(expected.Period, actual.Period);
        Assert.Equal(expected.Unit, actual.Unit);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Decimals, actual.Decimals);
    }

    [Fact]
    public async Task WrongPeriodBinding_ProducesUsefulDiagnostic()
    {
        // Bind the revenue duration flow to the instant-only "Assets" concept.
        var project = TestFixtures.BuildRevenueMappingProject(
            _workbookPath, concept: SyntheticProfile.Qualified("Assets"));

        var svc = NewService();
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.NotEqual(RunOutcome.Success, result.Manifest.Outcome);
        Assert.Contains(result.Manifest.Diagnostics,
            d => d.Code == DiagnosticCodes.XbrPeriodTypeMismatch && d.IsBlocking);
    }

    [Fact]
    public async Task DesktopAndCli_ProduceSemanticallyIdenticalOutput()
    {
        // Same project file, same workbook; two independent executions must be equivalent.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var svc = NewService();

        var desktop = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        var cli = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.Success, desktop.Manifest.Outcome);
        Assert.Equal(RunOutcome.Success, cli.Manifest.Outcome);

        // Normalize the two xBRL-JSON documents by stripping run-scoped ids (Fact ids) and
        // re-serializing fact content: same facts means same set of tuples.
        var a = Canonicalize(desktop.XbrlJson);
        var b = Canonicalize(cli.XbrlJson);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task RunManifest_RecordsInputHashAndProfileDigest()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var svc = NewService();
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        var manifest = result.Manifest;
        Assert.Equal(project.ProjectId, manifest.ProjectId);
        Assert.Equal(SyntheticProfile.Build().Digest, manifest.ProfileDigest);
        Assert.NotEmpty(manifest.Inputs);
        Assert.All(manifest.Inputs, i => Assert.NotNull(i.Digest));
        Assert.NotEmpty(manifest.Outputs);
        Assert.All(manifest.Outputs, o => Assert.StartsWith("sha256:", o.Digest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task XbrlJson_RoundTrips_BackToFacts()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var svc = NewService();
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        var reopened = OimSerializer.ReadFacts(result.XbrlJson);
        Assert.Single(reopened);
        Assert.Equal(result.Facts.Single().Value, reopened.Single().Value);
        Assert.Equal(result.Facts.Single().ConceptName, reopened.Single().ConceptName);
    }

    [Fact]
    public async Task Worker_LoadTaxonomy_OfflineSucceeds()
    {
        var profile = SyntheticProfile.Build();
        var worker = new LocalSyntheticWorker(profile);
        var resp = await worker.RequestAsync(new WorkerRequest(
            WorkerOperations.LoadTaxonomy,
            OperationId: "op-1",
            JobId: "job-1",
            ProfileDigest: profile.Digest,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            Payload: JsonElement.Parse("{}")), CancellationToken.None);

        Assert.Equal(WorkerStatus.Success, resp.Status);
    }

    [Fact]
    public async Task ExcelAdapter_ReadsHeaderAndComputesFingerprint()
    {
        var adapter = new ExcelAdapter();
        var conn = new SourceConnection("b1", "excel", _workbookPath,
            new Dictionary<string, string> { ["sheet"] = TestFixtures.SheetName, ["headerRow"] = "3" });

        var schema = await adapter.InspectSchemaAsync(conn, CancellationToken.None);
        Assert.StartsWith("sha256:", schema.Fingerprint, StringComparison.Ordinal);
        Assert.Contains(schema.Columns, c => c.Name == "Current");
        Assert.Contains(schema.Columns, c => c.Name == "Prior");

        var rows = new List<DataRecord>();
        await foreach (var r in adapter.ReadFullAsync(conn, schema, CancellationToken.None))
            rows.Add(r);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task Cancellation_YieldsCancelledOutcome_AndNoOutput()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var svc = NewService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, cts.Token);

        // The service checks cancellation before loading sources; the outcome must not
        // publish an output document.
        Assert.NotEqual(RunOutcome.Success, result.Manifest.Outcome);
        Assert.Empty(result.XbrlJson);
    }

    /// <summary>
    /// Produces a canonical form of an xBRL-JSON document: fact keys are stripped (they
    /// contain run-scoped identifiers) and facts are sorted by semantic content.
    /// </summary>
    private static string Canonicalize(string xbrlJson)
    {
        using var doc = JsonDocument.Parse(xbrlJson);
        var facts = new List<string>();
        if (doc.RootElement.TryGetProperty("facts", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in f.EnumerateObject())
            {
                // Re-serialize each fact value sorted by property name for stable comparison.
                facts.Add(SortJson(prop.Value));
            }
        }
        facts.Sort(StringComparer.Ordinal);
        return string.Join("\n", facts);
    }

    private static string SortJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return el.GetRawText();
        var parts = el.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}:{SortJson(p.Value)}");
        return "{" + string.Join(",", parts) + "}";
    }
}
