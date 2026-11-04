using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Integration.Tests;

/// <summary>
/// XBR05 fail-closed worker adjudication. Even when the mapping executes and serializes
/// cleanly, publication depends on the worker's validation *content*, not merely on a
/// process-level "success" flag:
///   - a Success that still carries blocking diagnostics is not a pass (the real Arelle
///     backend can exit successfully while its report contains errors);
///   - a cancelled worker result must never be read as success.
/// Both states must leave the run non-publishable.
/// </summary>
public sealed class WorkerAdjudicationTests : IAsyncLifetime
{
    private string _dir = "";
    private string _workbookPath = "";

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xbrl-worker-adj-" + Guid.NewGuid().ToString("N"));
        _workbookPath = TestFixtures.MaterializeWorkbook(_dir);
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
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

    [Fact]
    public async Task WorkerSuccess_WithBlockingDiagnostic_IsInvalid_AndNotPublishable()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new FakeWorker(req => new WorkerResponse(
            req.OperationId,
            WorkerStatus.Success, // nominal success...
            Result: null,
            new[]
            {
                new Diagnostic
                {
                    Code = DiagnosticCodes.XbrValidationFailed,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true, // ...but the report contains a blocking error.
                    Message = "Simulated Arelle blocking validation error.",
                    OperationId = req.OperationId,
                }
            },
            Error: null));

        var svc = new MappingJobService(SyntheticProfile.Build(), worker);
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.InvalidMapping, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        Assert.Contains(result.Manifest.Diagnostics,
            d => d.IsBlocking && d.Code == DiagnosticCodes.XbrValidationFailed);
    }

    [Fact]
    public async Task WorkerCancelled_IsNotSuccess_AndNotPublishable()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new FakeWorker(req => new WorkerResponse(
            req.OperationId, WorkerStatus.Cancelled, Result: null,
            Array.Empty<Diagnostic>(), "Simulated worker cancellation."));

        var svc = new MappingJobService(SyntheticProfile.Build(), worker);
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.Cancelled, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
    }

    [Fact]
    public async Task WorkerSuccess_NoBlockingDiagnostic_IsPublishable()
    {
        // Guards against over-correction: a genuinely clean validation still publishes.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new FakeWorker(req => new WorkerResponse(
            req.OperationId, WorkerStatus.Success, Result: null,
            new[]
            {
                new Diagnostic
                {
                    Code = DiagnosticCodes.XbrValidationIncomplete,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Warning,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = false,
                    Message = "Non-blocking advisory.",
                    OperationId = req.OperationId,
                }
            },
            Error: null));

        var svc = new MappingJobService(SyntheticProfile.Build(), worker);
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.Success, result.Manifest.Outcome);
        Assert.True(result.IsPublishable);
    }

    /// <summary>Configurable in-process worker: answers every request via <paramref name="responder"/>.</summary>
    private sealed class FakeWorker : IWorkerClient
    {
        private readonly Func<WorkerRequest, WorkerResponse> _responder;
        public FakeWorker(Func<WorkerRequest, WorkerResponse> responder) => _responder = responder;

        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new WorkerHandshake(WorkerProtocol.CurrentVersion, "fake/1.0", Array.Empty<string>()));

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
