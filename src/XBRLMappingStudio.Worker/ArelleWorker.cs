using System.Diagnostics;
using System.Text;
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
            Capabilities: new[] { WorkerOperations.LoadTaxonomy, WorkerOperations.ValidateReport })
        {
            ProfileId = _profile.ProfileId,
            ProfileDigest = _profile.Digest,
            RequiredPlugins = _profile.RequiredPlugins.Order(StringComparer.Ordinal).ToArray(),
            RequiredChecks = _profile.RequiredChecks.Order(StringComparer.Ordinal).ToArray()
        });

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
        var knownDimensionMembers = _profile.DimensionDomains.Values
            .SelectMany(members => members).ToHashSet(StringComparer.Ordinal);
        var filingValidation = new FilingRuleValidator(_profile, knownDimensionMembers).Validate(facts);
        var requiredRules = CompletionItems(_profile.RequiredChecks,
            filingValidation.Outcomes.Select(outcome => new ValidationCheckCompletion(
                outcome.RuleId,
                outcome.Status switch
                {
                    RuleStatus.Pass => ValidationCheckStatus.Pass,
                    RuleStatus.Fail => ValidationCheckStatus.Fail,
                    _ => ValidationCheckStatus.Unavailable
                },
                outcome.Detail)));
        var requiredPlugins = _profile.RequiredPlugins.Order(StringComparer.Ordinal)
            .Select(plugin => new ValidationCheckCompletion(plugin, ValidationCheckStatus.Unavailable,
                "The local synthetic worker does not load Arelle plugins."))
            .ToArray();
        var completion = new ValidationCompletionReport(
            _profile.ProfileId,
            _profile.Digest,
            requiredRules,
            requiredPlugins);
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
            if (!fact.ValidateValueAgainst(concept, out var valueReason)
                && !valueReason.EndsWith("is monetary and requires a unit.", StringComparison.Ordinal))
            {
                diags.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.XbrUnsupportedConstruct,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Validation,
                    IsBlocking = true,
                    Message = valueReason,
                    OperationId = request.OperationId,
                    RuleIdentifier = "synthetic:fact-value-contract"
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

        var completionFailed = requiredRules.Any(item => item.Status == ValidationCheckStatus.Fail);
        var completionIncomplete = requiredRules.Any(item => item.Status is ValidationCheckStatus.Skipped or ValidationCheckStatus.Unavailable)
            || requiredPlugins.Any(item => item.Status is ValidationCheckStatus.Skipped or ValidationCheckStatus.Unavailable);
        var status = diags.Any(d => d.IsBlocking) || completionFailed
            ? WorkerStatus.DomainFailure
            : completionIncomplete ? WorkerStatus.IncompleteValidation : WorkerStatus.Success;
        return Task.FromResult(new WorkerResponse(
            request.OperationId,
            status,
            JsonSerializer.SerializeToElement(new
            {
                factCount = facts.Count,
                errorCount = diags.Count(d => d.IsBlocking),
                validationCompletion = completion
            }, WorkerJson.Options),
            diags,
            Error: null));

        static ValidationCheckCompletion[] CompletionItems(
            IReadOnlySet<string> required,
            IEnumerable<ValidationCheckCompletion> outcomes)
        {
            var byId = outcomes.GroupBy(item => item.Identifier, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(item => item.Status switch
                    {
                        ValidationCheckStatus.Fail => 0,
                        ValidationCheckStatus.Unavailable => 1,
                        ValidationCheckStatus.Skipped => 2,
                        _ => 3
                    }).First(), StringComparer.Ordinal);
            return required.Order(StringComparer.Ordinal)
                .Select(identifier => byId.TryGetValue(identifier, out var outcome)
                    ? outcome
                    : new ValidationCheckCompletion(identifier, ValidationCheckStatus.Unavailable,
                        "The required check did not produce a completion outcome."))
                .ToArray();
        }
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
    private readonly WorkerProcessTree _processTree;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Task _stderrDrain;
    private readonly string? _expectedProfileId;
    private readonly int? _expectedProfileSchemaVersion;
    private readonly string? _expectedProfileVersion;
    private readonly string? _expectedProfileDigest;
    private readonly string? _expectedTaxonomyEntryPoint;
    private readonly IReadOnlyList<string>? _expectedRequiredPlugins;
    private readonly IReadOnlyList<string>? _expectedRequiredChecks;
    private int _disposed;
    private int _disposeStarted;

    public string ExecutablePath { get; }

    public ArelleWorker(string executablePath, string[]? args = null,
        string? expectedProfileId = null,
        int? expectedProfileSchemaVersion = null,
        string? expectedProfileVersion = null,
        string? expectedProfileDigest = null,
        string? expectedTaxonomyEntryPoint = null,
        IReadOnlyList<string>? expectedRequiredPlugins = null,
        IReadOnlyCollection<string>? expectedRequiredChecks = null)
    {
        ExecutablePath = executablePath;
        _expectedProfileId = expectedProfileId;
        _expectedProfileSchemaVersion = expectedProfileSchemaVersion;
        _expectedProfileVersion = expectedProfileVersion;
        _expectedProfileDigest = expectedProfileDigest;
        _expectedTaxonomyEntryPoint = expectedTaxonomyEntryPoint;
        _expectedRequiredPlugins = expectedRequiredPlugins?.ToArray();
        _expectedRequiredChecks = expectedRequiredChecks?.Order(StringComparer.Ordinal).ToArray();
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (var a in args ?? Array.Empty<string>())
            psi.ArgumentList.Add(a);
        var started = WorkerProcessTree.Start(psi);
        _process = started.Process;
        _processTree = started.Tree;
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _stderrDrain = DrainAsync(_process.StandardError);
    }

    public async Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
    {
        var req = new WorkerRequest(
            WorkerOperations.Handshake,
            OperationId: "h-" + Guid.NewGuid().ToString("N"),
            JobId: "system",
            ProfileDigest: "-",
            DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(15),
            Payload: JsonSerializer.SerializeToElement(new
            {
                minimumProtocolVersion = WorkerProtocol.MinimumVersion,
                maximumProtocolVersion = WorkerProtocol.CurrentVersion
            }));
        var resp = await SendAsync(req, cancellationToken);
        if (resp.Status != WorkerStatus.Success)
            throw new WorkerStartupException(resp.Status,
                resp.Error ?? "Worker returned a non-success status during handshake.",
                resp.Diagnostics ?? Array.Empty<Diagnostic>());
        var handshake = resp.Result!.Value.Deserialize<WorkerHandshake>(WorkerJson.Options)
            ?? throw new InvalidOperationException("Worker returned an empty handshake.");
        if (handshake.ProtocolVersion < WorkerProtocol.MinimumVersion
            || handshake.ProtocolVersion > WorkerProtocol.CurrentVersion)
        {
            const string message = "Arelle worker returned an unsupported protocol version.";
            throw new WorkerStartupException(WorkerStatus.DomainFailure, message,
                new[] { StartupDiagnostic(DiagnosticCodes.WkrProtocolVersionMismatch, message) });
        }
        if (!handshake.Capabilities.Contains(WorkerOperations.ValidateReport, StringComparer.Ordinal))
        {
            const string message = "Arelle worker does not support report validation.";
            throw new WorkerStartupException(WorkerStatus.DomainFailure, message,
                new[] { StartupDiagnostic(DiagnosticCodes.WkrHandshakeFailed, message) });
        }
        if (handshake.ArelleAvailable == false)
            throw new WorkerStartupException(WorkerStatus.IncompleteValidation,
                "The Arelle runtime is not installed in the configured worker environment.",
                new[] { StartupDiagnostic(DiagnosticCodes.XbrWorkerUnavailable,
                    "The Arelle runtime is not installed in the configured worker environment.") });
        if (_expectedProfileId is not null
            && (_expectedProfileSchemaVersion != handshake.ProfileSchemaVersion
                || !StringComparer.Ordinal.Equals(_expectedProfileId, handshake.ProfileId)
                || !StringComparer.Ordinal.Equals(_expectedProfileVersion, handshake.ProfileVersion)
                || !StringComparer.Ordinal.Equals(_expectedProfileDigest, handshake.ProfileDigest)
                || !StringComparer.Ordinal.Equals(_expectedTaxonomyEntryPoint, handshake.TaxonomyEntryPoint)
                || !(_expectedRequiredPlugins ?? Array.Empty<string>()).SequenceEqual(
                    handshake.RequiredPlugins, StringComparer.Ordinal)
                || !(_expectedRequiredChecks ?? Array.Empty<string>()).SequenceEqual(
                    handshake.RequiredChecks.Order(StringComparer.Ordinal), StringComparer.Ordinal)))
        {
            const string message = "Arelle worker profile metadata does not match the hash-verified installation.";
            throw new WorkerStartupException(WorkerStatus.DomainFailure, message,
                new[] { StartupDiagnostic(DiagnosticCodes.XbrProfileMismatch, message) });
        }
        return handshake;
    }

    private static Diagnostic StartupDiagnostic(string code, string message) => new()
    {
        Code = code,
        Subsystem = DiagnosticSubsystem.Worker,
        Severity = DiagnosticSeverity.Error,
        Stage = DiagnosticStage.Worker,
        IsBlocking = true,
        Message = message
    };

    public async Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        return await SendAsync(request, cancellationToken);
    }

    private async Task<WorkerResponse> SendAsync(WorkerRequest req, CancellationToken ct)
    {
        try { await _requestLock.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new WorkerResponse(req.OperationId, WorkerStatus.Cancelled, null, Array.Empty<Diagnostic>(), "Request cancelled before it was sent.");
        }
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return Failure(req.OperationId, WorkerStatus.Crash, "Worker is disposed.");
            if (_process.HasExited)
                return Failure(req.OperationId, WorkerStatus.Crash, ProcessUnavailableMessage());
            var msg = req.Encode();
            try { WorkerProtocol.ValidateByteLimit(msg); }
            catch (InvalidOperationException ex)
            {
                return new WorkerResponse(req.OperationId, WorkerStatus.DomainFailure, null, Array.Empty<Diagnostic>(), ex.Message);
            }
            var remaining = req.DeadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return Failure(req.OperationId, WorkerStatus.TimedOut, "Worker request deadline elapsed before send.");
            using var deadline = new CancellationTokenSource(remaining);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try
            {
                await _stdin.WriteAsync(msg.AsMemory(), linked.Token);
                await _stdin.FlushAsync(linked.Token);
                var line = await ReadBoundedLineAsync(_stdout, linked.Token);
                if (line is null)
                {
                    // EOF can arrive just before Process.HasExited becomes true.
                    // Wait within the same request deadline so crash reports keep
                    // the exact exit code without turning a closed-pipe hang into
                    // an unbounded wait.
                    await _process.WaitForExitAsync(linked.Token);
                    return Failure(req.OperationId, WorkerStatus.Crash,
                        $"Worker terminated without a response (exit code {_process.ExitCode}).");
                }
                var response = WorkerResponse.Decode(line);
                if (!string.Equals(response.OperationId, req.OperationId, StringComparison.Ordinal))
                {
                    await TerminateAsync();
                    return Failure(req.OperationId, WorkerStatus.Crash, "Worker response operation ID does not match the request.");
                }
                if (response.Status == WorkerStatus.Crash) await TerminateAsync();
                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || deadline.IsCancellationRequested)
            {
                await TerminateAsync();
                return Failure(req.OperationId, ct.IsCancellationRequested ? WorkerStatus.Cancelled : WorkerStatus.TimedOut,
                    ct.IsCancellationRequested ? "Worker request was cancelled." : "Worker request exceeded its deadline.");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                await TerminateAsync();
                return Failure(req.OperationId, WorkerStatus.Crash, $"Worker protocol failed: {ex.Message}");
            }
        }
        finally { _requestLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        Volatile.Write(ref _disposed, 1);
        await TerminateAsync();
        await _requestLock.WaitAsync();
        try
        {
            try { await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _process.Dispose();
        }
        finally
        {
            _processTree.Dispose();
            _requestLock.Release();
        }
    }

    private static WorkerResponse Failure(string operationId, WorkerStatus status, string message) => new(
        operationId, status, null, Array.Empty<Diagnostic>(), message);

    private string ProcessUnavailableMessage()
        => $"Worker process exited with code {_process.ExitCode}.";

    private async Task TerminateAsync()
    {
        try
        {
            _processTree.Kill(_process);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { /* process termination is best-effort after its pipes have failed */ }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        // stderr is intentionally discarded, but it must still be consumed so a
        // noisy worker cannot block on the pipe. Read fixed-size chunks rather
        // than lines so one newline-free diagnostic cannot allocate without bound.
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0) { }
        }
        catch (IOException) { }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new char[4096];
        var bytes = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) return line.Length == 0 ? null : line.ToString();
            for (var i = 0; i < count; i++)
            {
                var ch = buffer[i];
                if (ch == '\n') return line.Length > 0 && line[^1] == '\r'
                    ? line.ToString(0, line.Length - 1)
                    : line.ToString();
                bytes += ch <= 0x7f ? 1 : ch <= 0x7ff || char.IsSurrogate(ch) ? 2 : 3;
                if (bytes > WorkerProtocol.MaxMessageBytes)
                    throw new InvalidOperationException($"Worker response exceeds {WorkerProtocol.MaxMessageBytes} bytes.");
                line.Append(ch);
            }
        }
    }
}
