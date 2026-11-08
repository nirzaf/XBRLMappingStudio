using System.Text;
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
/// IG01: unit/scale/period heading anchors must be bound into the Excel schema
/// fingerprint, so that relabelling a banner from e.g. "QAR thousands" to "QAR units"
/// (while leaving column names and shapes untouched) is detected as schema drift —
/// combined with OG01 fail-closed publishing, the previously accepted export is
/// preserved rather than silently re-emitted at the wrong scale.
/// </summary>
public sealed class SchemaDriftTests : IAsyncLifetime
{
    private string _dir = "";
    private string _workbookPath = "";

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xbrl-drift-" + Guid.NewGuid().ToString("N"));
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
        var cfg = b.Configuration?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "")
            ?? new Dictionary<string, string>();
        return new Dictionary<string, SourceConnection>
        {
            [b.BindingId] = new SourceConnection(b.BindingId, "excel", path, cfg)
        };
    }

    private async Task<string> FingerprintForWorkbook(string workbookPath)
    {
        var adapter = new ExcelAdapter();
        var conn = new SourceConnection("probe", "excel", workbookPath,
            new Dictionary<string, string> { ["sheet"] = TestFixtures.SheetName, ["headerRow"] = "3" });
        var schema = await adapter.InspectSchemaAsync(conn, CancellationToken.None);
        return schema.Fingerprint;
    }

    [Fact]
    public async Task Fingerprint_ChangesWhenOnlyTheUnitHeadingChanges()
    {
        // Same sheet, same columns, same inferred shapes — only the banner text above
        // the header row differs. This must not look like the same schema.
        var thousandsPath = TestFixtures.MaterializeWorkbook(
            Path.Combine(_dir, "thousands"), "QAR thousands");
        var unitsPath = TestFixtures.MaterializeWorkbook(
            Path.Combine(_dir, "units"), "QAR units");

        var thousandsFingerprint = await FingerprintForWorkbook(thousandsPath);
        var unitsFingerprint = await FingerprintForWorkbook(unitsPath);

        Assert.NotEqual(thousandsFingerprint, unitsFingerprint);

        // ... and it must be stable across an identical re-read (no false positives).
        Assert.Equal(thousandsFingerprint, await FingerprintForWorkbook(thousandsPath));
    }

    [Fact]
    public async Task UnitHeadingDrift_BlocksRemapAndPreservesPriorAcceptedOutput()
    {
        // 1. Record the fingerprint the mapping was last validated against (thousands).
        var originalFingerprint = await FingerprintForWorkbook(_workbookPath);

        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        project.Bindings[0] = project.Bindings[0] with { SchemaFingerprint = originalFingerprint };

        var svc = NewService();

        // 2. Publish a first, accepted run while the workbook still says "QAR thousands".
        var dest = Path.Combine(_dir, "published.json");
        var firstRun = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.Equal(RunOutcome.Success, firstRun.Manifest.Outcome);
        Assert.True(firstRun.IsPublishable);

        var journal = new RunJournal(Path.Combine(_dir, "journal"));
        new PublicationService(journal).Publish(
            dest, Encoding.UTF8.GetBytes(firstRun.XbrlJson), firstRun.Manifest.RunId);
        var acceptedBytes = File.ReadAllBytes(dest);

        // 3. Someone re-labels the same workbook for next quarter: same file path,
        //    same columns, only the unit/scale banner changes to "QAR units".
        TestFixtures.MaterializeWorkbook(_dir, "QAR units");

        var driftRun = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        // The stale fingerprint no longer matches: execution must block before the
        // graph runs, and it must not be publishable.
        Assert.Equal(RunOutcome.DataError, driftRun.Manifest.Outcome);
        Assert.False(driftRun.IsPublishable);
        Assert.Contains(driftRun.Diagnostics.Items,
            d => d.Code == DiagnosticCodes.IoSchemaMismatch && d.IsBlocking);

        // Fail-closed: since the result is not publishable, a caller following the
        // contract (as the CLI now does) never touches the destination.
        Assert.Equal(acceptedBytes, File.ReadAllBytes(dest));
        Assert.Equal(JournalState.Finalized, journal.Load(firstRun.Manifest.RunId)!.State);
        Assert.Null(journal.Load(driftRun.Manifest.RunId));
    }

    [Fact]
    public async Task MissingMappedColumn_BlocksBeforePreviewOrExecutionCanEmitNoFacts()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        project.Bindings[0] = project.Bindings[0] with { SchemaFingerprint = null };
        using (var workbook = new ClosedXML.Excel.XLWorkbook(_workbookPath))
        {
            workbook.Worksheet(TestFixtures.SheetName).Cell("B3").Value = "Current Amount";
            workbook.Save();
        }

        var result = await NewService().RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: true, CancellationToken.None);

        Assert.Equal(RunOutcome.DataError, result.Manifest.Outcome);
        Assert.Empty(result.Facts);
        Assert.False(result.IsPublishable);
        var diagnostic = Assert.Single(result.Diagnostics.Items,
            item => item.Code == DiagnosticCodes.IoSchemaMismatch && item.IsBlocking);
        Assert.Contains(".column='Current'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Rebind the source fields", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreviewedPdfInputReturnsBlockingDataErrorWithoutRunningMapping()
    {
        var profile = SyntheticProfile.Build();
        var sourcePath = Path.Combine(_dir, "unreviewed.pdf");
        await File.WriteAllBytesAsync(sourcePath, Encoding.ASCII.GetBytes("%PDF synthetic source"));
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var binding = project.Bindings[0] with { Kind = "pdf-digital", SchemaFingerprint = null };
        project.Bindings[0] = binding;
        var connections = new Dictionary<string, SourceConnection>
        {
            [binding.BindingId] = new SourceConnection(binding.BindingId, "pdf-digital", sourcePath)
        };
        var service = new MappingJobService(profile, new LocalSyntheticWorker(profile),
            applicationDatabaseFactory: () => new LocalApplicationDatabase(Path.Combine(_dir, "pdf-state.db")));

        var result = await service.RunAsync(project, connections,
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.DataError, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        Assert.Contains(result.Diagnostics.Items,
            diagnostic => diagnostic.Code == DiagnosticCodes.IoPdfReviewRequired && diagnostic.IsBlocking);
        Assert.NotNull(result.Manifest.CompletedUtc);
    }
}
