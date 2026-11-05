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

    // XBR08 timeout path: a per-request deadline that elapses without any response
    // must yield a distinct terminal outcome (OperationalFailure), a blocking WKR004
    // diagnostic, and no publication. This is not cancellation and not a crash.
    [Fact]
    public async Task WorkerTimedOut_IsOperationalFailure_WithWkrTimeoutDiagnostic_AndNotPublishable()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new FakeWorker(req => new WorkerResponse(
            req.OperationId, WorkerStatus.TimedOut, Result: null,
            // Simulates a hung worker that never dies and returns no diagnostics of its own.
            Array.Empty<Diagnostic>(),
            Error: "Per-request deadline elapsed."));

        var svc = new MappingJobService(SyntheticProfile.Build(), worker);
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.OperationalFailure, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        var timeout = Assert.Single(result.Manifest.Diagnostics,
            d => d.Code == DiagnosticCodes.WkrTimeout);
        Assert.True(timeout.IsBlocking);
        Assert.Equal(DiagnosticStage.Worker, timeout.Stage);
    }

    // XBR08 crash path: a worker that terminated unexpectedly yields WKR003 and is
    // never publishable, distinct from the timeout path above.
    [Fact]
    public async Task WorkerCrash_IsOperationalFailure_WithWkrCrashDiagnostic_AndNotPublishable()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new FakeWorker(req => new WorkerResponse(
            req.OperationId, WorkerStatus.Crash, Result: null,
            Array.Empty<Diagnostic>(),
            Error: "Worker terminated without a response."));

        var svc = new MappingJobService(SyntheticProfile.Build(), worker);
        var result = await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.OperationalFailure, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        Assert.Contains(result.Manifest.Diagnostics,
            d => d.Code == DiagnosticCodes.WkrCrash && d.IsBlocking);
    }

    [Fact]
    public async Task WorkerStartupTimeout_IsOperationalFailure_WithWkrTimeoutDiagnostic()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new StartupFailureWorker(WorkerStatus.TimedOut, "Handshake deadline elapsed.");
        var result = await new MappingJobService(SyntheticProfile.Build(), worker).RunAsync(
            project, Connections(project, _workbookPath), new Dictionary<string, string>(),
            preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.OperationalFailure, result.Manifest.Outcome);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
            diagnostic.Code == DiagnosticCodes.WkrTimeout && diagnostic.IsBlocking);
    }

    [Fact]
    public async Task WorkerStartupProtocolMismatch_UsesDistinctDiagnostic()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new StartupFailureWorker(WorkerStatus.DomainFailure,
            "No mutually supported protocol version.");
        var result = await new MappingJobService(SyntheticProfile.Build(), worker).RunAsync(
            project, Connections(project, _workbookPath), new Dictionary<string, string>(),
            preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
            diagnostic.Code == DiagnosticCodes.WkrProtocolVersionMismatch && diagnostic.IsBlocking);
    }

    [Fact]
    public async Task WorkerSuccessWithUnsupportedProtocol_IsIncompleteAndNotPublishable()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new FakeWorker(_ => throw new InvalidOperationException(
            "Request must not be sent after an unsupported handshake."),
            new WorkerHandshake(WorkerProtocol.CurrentVersion + 1, "fake/unsupported",
                new[] { WorkerOperations.ValidateReport }));

        var result = await new MappingJobService(SyntheticProfile.Build(), worker).RunAsync(
            project, Connections(project, _workbookPath), new Dictionary<string, string>(),
            preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
            diagnostic.Code == DiagnosticCodes.WkrProtocolVersionMismatch && diagnostic.IsBlocking);
    }

    [Fact]
    public async Task TaxonomyPreflightFailureStopsBeforeMissingSourceIsRead()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var missingPath = Path.Combine(_dir, "missing-source.xlsx");
        var worker = new FakeWorker(request => new WorkerResponse(
            request.OperationId, WorkerStatus.DomainFailure, null, Array.Empty<Diagnostic>(),
            "Taxonomy entry point could not be loaded."));

        var result = await new MappingJobService(SyntheticProfile.Build(), worker).RunAsync(
            project, Connections(project, missingPath), new Dictionary<string, string>(),
            preview: false, CancellationToken.None);

        Assert.Equal(RunOutcome.IncompleteConfiguration, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        Assert.Contains(result.Manifest.Diagnostics, diagnostic =>
            diagnostic.Code == DiagnosticCodes.WkrHandshakeFailed && diagnostic.IsBlocking);
    }

    // Deadline is propagated on the request sent by MappingJobService (XBR08).
    [Fact]
    public async Task RequestDeadline_IsInTheFutureAndCarriesProfileDigest()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        WorkerRequest? captured = null;
        var worker = new FakeWorker(req =>
        {
            captured = req;
            return new WorkerResponse(req.OperationId, WorkerStatus.Success, Result: null,
                Array.Empty<Diagnostic>(), null);
        });
        var profile = SyntheticProfile.Build();

        var svc = new MappingJobService(profile, worker);
        await svc.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(WorkerOperations.ValidateReport, captured!.Operation);
        Assert.Equal(profile.Digest, captured.ProfileDigest);
        Assert.True(captured.DeadlineUtc > DateTimeOffset.UtcNow,
            "Validate-report deadline must be in the future so a hung worker is bounded (XBR08).");
    }

    /// <summary>Configurable in-process worker: answers every request via <paramref name="responder"/>.</summary>
    private sealed class FakeWorker : IWorkerClient
    {
        private readonly Func<WorkerRequest, WorkerResponse> _responder;
        private readonly WorkerHandshake _handshake;

        public FakeWorker(Func<WorkerRequest, WorkerResponse> responder, WorkerHandshake? handshake = null)
        {
            _responder = responder;
            _handshake = handshake ?? new WorkerHandshake(WorkerProtocol.CurrentVersion, "fake/1.0",
                new[] { WorkerOperations.LoadTaxonomy, WorkerOperations.ValidateReport });
        }

        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => Task.FromResult(_handshake);

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StartupFailureWorker : IWorkerClient
    {
        private readonly WorkerStatus _status;
        private readonly string _message;

        public StartupFailureWorker(WorkerStatus status, string message)
        {
            _status = status;
            _message = message;
        }

        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
            => Task.FromException<WorkerHandshake>(new WorkerStartupException(_status, _message));

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Request should not be sent after a startup failure.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
