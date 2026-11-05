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
    public async Task MappingJobReportsWorkerAndSourceProgressStages()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new FakeWorker(req => new WorkerResponse(
            req.OperationId, WorkerStatus.Success, Result: null, Array.Empty<Diagnostic>(), Error: null));
        var progress = new RecordingProgress<MappingJobProgress>();
        var service = new MappingJobService(SyntheticProfile.Build(), worker);

        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None, progress);

        Assert.True(result.IsPublishable);
        Assert.Contains(progress.Items, item => item.Stage == "Starting validation worker");
        Assert.Contains(progress.Items, item => item.Stage == "Loading filing taxonomy in validation worker");
        Assert.Contains(progress.Items, item => item.Stage == "Recording source identities");
        Assert.Contains(progress.Items, item => item.Stage.StartsWith("Inspecting source ", StringComparison.Ordinal));
        Assert.Contains(progress.Items, item => item.Stage.StartsWith("Read source ", StringComparison.Ordinal));
        Assert.Contains(progress.Items, item => item.Stage == "Executing mapping");
        Assert.Contains(progress.Items, item => item.Stage == "Validating generated report in validation worker");
        Assert.Contains(progress.Items, item => item.Stage == "Run ended: Success");
    }

    [Fact]
    public async Task MappingJobCancellationDuringWorkerStartupIsTerminalAndNonPublishable()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var worker = new CancellationWaitingWorker();
        var service = new MappingJobService(SyntheticProfile.Build(), worker);
        using var cancellation = new CancellationTokenSource();

        var run = service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, cancellation.Token);
        await worker.HandshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunOutcome.Cancelled, result.Manifest.Outcome);
        Assert.False(result.IsPublishable);
        Assert.Empty(result.Facts);
        Assert.Equal(string.Empty, result.XbrlJson);
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

    [Fact]
    public async Task StagedArtifactValidation_SendsExactStagedReportToProfileWorker()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var validationRequests = new List<WorkerRequest>();
        var worker = new FakeWorker(request =>
        {
            if (request.Operation == WorkerOperations.ValidateReport)
                validationRequests.Add(request);
            return new WorkerResponse(request.OperationId, WorkerStatus.Success, Result: null,
                Array.Empty<Diagnostic>(), null);
        });
        var service = new MappingJobService(SyntheticProfile.Build(), worker);
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);

        var stagedPath = Path.Combine(_dir, "report.xbrl.json.xbsl-staging-test");
        await File.WriteAllTextAsync(stagedPath, "altered-after-serialization", new System.Text.UTF8Encoding(false));
        var altered = await Assert.ThrowsAsync<PublicationException>(() => service.ValidateStagedArtifactAsync(
            stagedPath, Path.Combine(_dir, "report.xbrl.json"), result));
        Assert.Equal(DiagnosticCodes.PubDigestMismatch, altered.Code);
        Assert.Single(validationRequests);

        await File.WriteAllTextAsync(stagedPath, result.XbrlJson, new System.Text.UTF8Encoding(false));
        var validatedDigest = await service.ValidateStagedArtifactAsync(
            stagedPath, Path.Combine(_dir, "report.xbrl.json"), result);

        Assert.Equal(2, validationRequests.Count);
        Assert.Equal(result.XbrlJson, validationRequests[1].Payload!.Value.GetString());
        Assert.Equal(result.Manifest.Outputs.Single(output => output.Kind == "xbrl-json").Digest,
            validatedDigest);
        Assert.Equal(validatedDigest, result.Manifest.ValidatedArtifactDigest);
    }

    [Fact]
    public async Task StagedArtifactValidation_RejectsWorkerBlockingFinding()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var validationCount = 0;
        var worker = new FakeWorker(request =>
        {
            if (request.Operation != WorkerOperations.ValidateReport)
                return new WorkerResponse(request.OperationId, WorkerStatus.Success, Result: null,
                    Array.Empty<Diagnostic>(), null);

            validationCount++;
            var rejectsStaged = validationCount > 1;
            return new WorkerResponse(request.OperationId, WorkerStatus.Success, Result: null,
                rejectsStaged
                    ? new[]
                    {
                        new Diagnostic
                        {
                            Code = DiagnosticCodes.XbrValidationFailed,
                            Subsystem = DiagnosticSubsystem.XBRL,
                            Severity = DiagnosticSeverity.Error,
                            Stage = DiagnosticStage.Validation,
                            IsBlocking = true,
                            Message = "Staged report failed profile validation.",
                            OperationId = request.OperationId
                        }
                    }
                    : Array.Empty<Diagnostic>(),
                null);
        });
        var service = new MappingJobService(SyntheticProfile.Build(), worker);
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);

        var stagedPath = Path.Combine(_dir, "rejected-report.json");
        await File.WriteAllTextAsync(stagedPath, result.XbrlJson, new System.Text.UTF8Encoding(false));
        var ex = await Assert.ThrowsAsync<PublicationException>(() => service.ValidateStagedArtifactAsync(
            stagedPath, Path.Combine(_dir, "published-report.json"), result));

        Assert.Equal(DiagnosticCodes.XbrValidationFailed, ex.Code);
        Assert.Null(result.Manifest.ValidatedArtifactDigest);
    }

    [Fact]
    public async Task IncompleteStagedValidation_AbortsAndPreservesPriorDestination()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var validationCount = 0;
        var worker = new FakeWorker(request =>
        {
            if (request.Operation != WorkerOperations.ValidateReport)
                return new WorkerResponse(request.OperationId, WorkerStatus.Success, Result: null,
                    Array.Empty<Diagnostic>(), null);

            validationCount++;
            if (validationCount == 1)
                return new WorkerResponse(request.OperationId, WorkerStatus.Success, Result: null,
                    Array.Empty<Diagnostic>(), null);

            return new WorkerResponse(request.OperationId, WorkerStatus.IncompleteValidation, Result: null,
                new[]
                {
                    new Diagnostic
                    {
                        Code = DiagnosticCodes.XbrValidationIncomplete,
                        Subsystem = DiagnosticSubsystem.XBRL,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Validation,
                        IsBlocking = true,
                        Message = "Profile validation did not complete.",
                        OperationId = request.OperationId
                    }
                },
                "Arelle validation is incomplete.");
        });
        var profile = SyntheticProfile.Build();
        var service = new MappingJobService(profile, worker);
        var result = await service.RunAsync(project, Connections(project, _workbookPath),
            new Dictionary<string, string>(), preview: false, CancellationToken.None);
        Assert.True(result.IsPublishable);

        var destination = Path.Combine(_dir, "existing-report.json");
        var priorBytes = System.Text.Encoding.UTF8.GetBytes("prior-accepted-report");
        await File.WriteAllBytesAsync(destination, priorBytes);
        var journal = new RunJournal(Path.Combine(_dir, "incomplete-validation-journal"));
        var publisher = new PublicationService(journal);
        var ex = await Assert.ThrowsAsync<PublicationException>(() => publisher.PublishAuthorizedAsync(
            destination,
            System.Text.Encoding.UTF8.GetBytes(result.XbrlJson),
            _dir,
            project,
            profile,
            allowSyntheticProfile: true,
            result: result,
            validateStagedArtifactAsync: (stagedPath, token) => service.ValidateStagedArtifactAsync(
                stagedPath, destination, result, token),
            cancellationToken: CancellationToken.None));

        Assert.Equal(DiagnosticCodes.XbrValidationIncomplete, ex.Code);
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(destination));
        var entry = journal.Load(result.Manifest.RunId);
        Assert.NotNull(entry);
        Assert.Equal(JournalState.Aborted, entry!.State);
        Assert.Null(entry.ValidatedDigest);
        Assert.Empty(Directory.GetFiles(_dir, "*.xbsl-staging-*"));
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

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = new();
        public void Report(T value) => Items.Add(value);
    }

    private sealed class CancellationWaitingWorker : IWorkerClient
    {
        public TaskCompletionSource HandshakeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
        {
            HandshakeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The canceled handshake unexpectedly continued.");
        }

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No worker request should follow a canceled handshake.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
