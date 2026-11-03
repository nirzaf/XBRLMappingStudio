using System.Diagnostics;
using System.Text.Json;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Worker;

/// <summary>
/// Abstract worker surface used by the engine and CLI. Two implementations exist:
///   - <see cref="LocalSyntheticWorker"/> for the G1 synthetic profile. Validates facts
///     directly in .NET with no Python dependency, enabling reproducible CI.
///   - <see cref="ArelleWorker"/> for real filing profiles where Arelle's taxonomy
///     resolution and validation is required (XBR02).
/// </summary>
public interface IWorkerClient : IAsyncDisposable
{
    Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken);
    Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// In-process validation for the synthetic profile. Implements the same protocol so
/// the CLI and Desktop do not branch on worker implementation.
/// </summary>
public sealed class LocalSyntheticWorker : IWorkerClient
{
    private readonly FilingProfile _profile;

    public LocalSyntheticWorker(FilingProfile profile)
    {
        _profile = profile;
    }

    public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
        => Task.FromResult(new WorkerHandshake(
            ProtocolVersion: WorkerProtocol.CurrentVersion,
            EngineVersion: "local-synthetic/1.0",
            Capabilities: new[] { WorkerOperations.LoadTaxonomy, WorkerOperations.ValidateReport }));

    public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request.Operation switch
        {
            WorkerOperations.LoadTaxonomy => Task.FromResult(new WorkerResponse(
                request.OperationId,
                WorkerStatus.Success,
                JsonSerializer.SerializeToElement(new { concepts = _profile.Concepts.Count }),
                Diagnostics: Array.Empty<Diagnostic>(),
                Error: null)),

            WorkerOperations.ValidateReport => ValidateReport(request, cancellationToken),

            _ => Task.FromResult(new WorkerResponse(
                request.OperationId,
                WorkerStatus.DomainFailure,
                null,
                Array.Empty<Diagnostic>(),
                $"Operation '{request.Operation}' is not supported by the local synthetic worker."))
        };
    }

    private Task<WorkerResponse> ValidateReport(WorkerRequest request, CancellationToken cancellationToken)
    {
        // Payload carries the xBRL-JSON text; we parse and validate each fact against the profile.
        var payload = request.Payload is { } p
            ? p.Deserialize<string>(WorkerJson.Options)
            : null;
        if (string.IsNullOrEmpty(payload))
        {
            return Task.FromResult(new WorkerResponse(
                request.OperationId,
                WorkerStatus.DomainFailure,
                null,
                Array.Empty<Diagnostic>(),
                "Missing report payload."));
        }

        var diags = new List<Diagnostic>();
        var facts = OimSerializer.ReadFacts(payload);
        int i = 0;
        foreach (var fact in facts)
        {
            i++;
            var concept = _profile.FindConcept(fact.ConceptName);
            if (concept is null)
            {
                diags.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.XbrConceptUnknown,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true,
                    Message = $"Concept '{fact.ConceptName}' is not defined in profile '{_profile.ProfileId}'.",
                    OperationId = request.OperationId,
                    RuleIdentifier = "synthetic:concept-exists"
                });
                continue;
            }
            if (concept.IsAbstract)
            {
                diags.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.XbrConceptUnknown,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true,
                    Message = $"Concept '{fact.ConceptName}' is abstract and cannot carry a fact value.",
                    OperationId = request.OperationId,
                    RuleIdentifier = "synthetic:abstract-concept-not-used"
                });
            }
            if (concept.DataType == "monetary" && fact.Unit is null)
            {
                diags.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.XbrUnitMissing,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true,
                    Message = $"Fact {i} on monetary concept '{fact.ConceptName}' has no unit.",
                    OperationId = request.OperationId,
                    RuleIdentifier = "synthetic:monetary-unit-required"
                });
            }
            if (fact.Unit is not null && !_profile.SupportedUnits.Contains(fact.Unit.Measure))
            {
                diags.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.XbrUnitMissing,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true,
                    Message = $"Unit '{fact.Unit.Measure}' is not supported by profile '{_profile.ProfileId}'.",
                    OperationId = request.OperationId,
                    RuleIdentifier = "synthetic:unit-allowed"
                });
            }
            if (concept.PeriodType == "instant" && fact.Period.Kind != PeriodKind.Instant)
            {
                diags.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.XbrPeriodTypeMismatch,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true,
                    Message = $"Fact {i} on instant concept '{fact.ConceptName}' carries a duration period.",
                    OperationId = request.OperationId,
                    RuleIdentifier = "synthetic:period-type-match"
                });
            }
            if (concept.PeriodType == "duration" && fact.Period.Kind != PeriodKind.Duration)
            {
                diags.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.XbrPeriodTypeMismatch,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true,
                    Message = $"Fact {i} on duration concept '{fact.ConceptName}' carries an instant period.",
                    OperationId = request.OperationId,
                    RuleIdentifier = "synthetic:period-type-match"
                });
            }
        }

        var status = diags.Any(d => d.IsBlocking) ? WorkerStatus.DomainFailure : WorkerStatus.Success;
        return Task.FromResult(new WorkerResponse(
            request.OperationId,
            status,
            JsonSerializer.SerializeToElement(new { factCount = facts.Count, errorCount = diags.Count(d => d.IsBlocking) }),
            diags,
            Error: null));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Hosts a bundled Python worker (Arelle) as a child process and speaks the framed
/// protocol. Requires a real Python install with arelle-release. Not used by the G1
/// synthetic slice; kept as the integration point (XBR02 acceptance) so the
/// architecture is exercised without forcing Python into the build.
/// </summary>
public sealed class ArelleWorker : IWorkerClient
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;

    public string ExecutablePath { get; }

    public ArelleWorker(string executablePath, string[]? args = null)
    {
        ExecutablePath = executablePath;
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var a in args ?? Array.Empty<string>())
            psi.ArgumentList.Add(a);
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start worker at {executablePath}.");
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
    }

    public async Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
    {
        var req = new WorkerRequest(
            WorkerOperations.Handshake,
            OperationId: "h-" + Guid.NewGuid().ToString("N"),
            JobId: "system",
            ProfileDigest: "-",
            DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(15),
            Payload: null);
        var resp = await SendAsync(req, cancellationToken);
        if (resp.Status != WorkerStatus.Success)
            throw new InvalidOperationException($"Worker handshake failed: {resp.Error}");
        return resp.Result!.Value.Deserialize<WorkerHandshake>(WorkerJson.Options)!;
    }

    public async Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync(request, cancellationToken);
    }

    private async Task<WorkerResponse> SendAsync(WorkerRequest req, CancellationToken ct)
    {
        var msg = req.Encode();
        WorkerProtocol.ValidateByteLimit(msg);
        await _stdin.WriteAsync(msg);
        await _stdin.FlushAsync(ct);
        var line = await _stdout.ReadLineAsync(ct);
        if (line is null)
        {
            return new WorkerResponse(req.OperationId, WorkerStatus.Crash, null,
                Array.Empty<Diagnostic>(),
                "Worker terminated unexpectedly.");
        }
        return WorkerResponse.Decode(line);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch { /* best effort */ }
        _process.Dispose();
    }
}
