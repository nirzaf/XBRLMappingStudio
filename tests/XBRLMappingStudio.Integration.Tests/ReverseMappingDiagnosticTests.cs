using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

public sealed class ReverseMappingDiagnosticTests
{
    [Fact]
    public void ReportSource_RecordsNonBlockingReconstructionLimitation()
    {
        var diagnostics = new DiagnosticBag();

        MappingJobService.AddReverseMappingLossWarning(
            diagnostics,
            new SourceConnection("report-binding", "xbrl-report", "synthetic-report.json"),
            "operation-123");

        var warning = Assert.Single(diagnostics.Items);
        Assert.Equal(DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.False(warning.IsBlocking);
        Assert.Equal("operation-123", warning.OperationId);
        Assert.Equal(new SourceReference("report-binding", "fact-occurrences"), warning.Source);
        Assert.Equal("source-detail-not-present-in-report", warning.Details!["limitation"]);
        Assert.Contains("cannot reconstruct source detail absent from the report", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonReportSource_DoesNotRecordReverseMappingLimitation()
    {
        var diagnostics = new DiagnosticBag();

        MappingJobService.AddReverseMappingLossWarning(
            diagnostics,
            new SourceConnection("workbook-binding", "excel", "synthetic.xlsx"),
            "operation-123");

        Assert.Empty(diagnostics.Items);
    }

    [Fact]
    public async Task RunAsync_PersistsReverseMappingLimitationWithSourceFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xbrl-reverse-warning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var reportPath = Path.Combine(directory, "input.json");
            await File.WriteAllTextAsync(reportPath, "not-json");
            var project = TestFixtures.BuildRevenueMappingProject(reportPath);
            var binding = project.Bindings.Single();
            project.Bindings[0] = binding with
            {
                Kind = "xbrl-report",
                Configuration = new Dictionary<string, object?>(),
                SchemaFingerprint = null
            };

            var result = await new MappingJobService(SyntheticProfile.Build(), new SuccessfulWorker()).RunAsync(
                project,
                new Dictionary<string, SourceConnection>
                {
                    [binding.BindingId] = new SourceConnection(binding.BindingId, "xbrl-report", reportPath)
                },
                new Dictionary<string, string>(),
                preview: false,
                CancellationToken.None);

            Assert.Equal(RunOutcome.DataError, result.Manifest.Outcome);
            Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.IoReverseMappingCannotRecoverAbsentSourceDetail
                && diagnostic.Severity == DiagnosticSeverity.Warning
                && !diagnostic.IsBlocking
                && diagnostic.Source?.SourceId == binding.BindingId);
            Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
                diagnostic.Code == DiagnosticCodes.IoUnsupportedEncoding && diagnostic.IsBlocking);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SuccessfulWorker : IWorkerClient
    {
        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new WorkerHandshake(
                WorkerProtocol.CurrentVersion,
                "reverse-warning-test/1.0",
                new[] { WorkerOperations.LoadTaxonomy, WorkerOperations.ValidateReport }));

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new WorkerResponse(
                request.OperationId,
                WorkerStatus.Success,
                Result: null,
                Diagnostics: Array.Empty<Diagnostic>(),
                Error: null));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
