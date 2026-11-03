using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;
using RunContext = XBRLMappingStudio.Engine.ExecutionContext;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Shared application service used by Desktop and CLI (OPS01). Executes the same
/// graph in-process; callers supply the parameters and destinations.
/// </summary>
public sealed class MappingJobService
{
    private readonly FilingProfile _profile;
    private readonly FunctionCatalog _catalog;
    private readonly IWorkerClient _worker;

    public MappingJobService(FilingProfile profile, IWorkerClient worker, FunctionCatalog? catalog = null)
    {
        _profile = profile;
        _worker = worker;
        _catalog = catalog ?? FunctionCatalog.Default;
    }

    /// <summary>Validate + compile the graph in a project. Does not execute.</summary>
    public CompileOutcome Validate(MappingProject project)
    {
        var graph = new MappingGraph(project);
        var validator = new GraphValidator(_catalog);
        var diag = validator.Validate(graph);
        if (diag.HasBlockingErrors)
            return CompileOutcome.Invalid(diag);
        var compiler = new PlanCompiler(_catalog);
        var result = compiler.Compile(graph, _profile);
        if (!result.Success || result.Plan is null)
            return CompileOutcome.Invalid(result.Diagnostics);
        return CompileOutcome.Ok(result.Plan, graph, diag);
    }

    /// <summary>
    /// Preflight + execute the project against the given source connections.
    /// When <paramref name="preview"/> is true, only a bounded sample is processed
    /// and the outcome is labelled partial.
    /// </summary>
    public async Task<JobResult> RunAsync(
        MappingProject project,
        IReadOnlyDictionary<string, SourceConnection> connections,
        IReadOnlyDictionary<string, string> parameters,
        bool preview,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        var opId = Guid.NewGuid().ToString("N");
        RunManifest manifest;
        try
        {
            manifest = new RunManifest
            {
                RunId = runId,
                OperationId = opId,
                ProjectId = project.ProjectId,
                SemanticRevision = project.SemanticRevision,
                ProfileDigest = _profile.Digest,
                EngineVersion = "1.0.0",
                StartedUtc = DateTimeOffset.UtcNow,
                FrozenParameters = new Dictionary<string, string>(parameters),
                Inputs = await RecordInputIdentities(connections, cancellationToken)
            };
        }
        catch (OperationCanceledException)
        {
            var m = new RunManifest
            {
                RunId = runId,
                OperationId = opId,
                ProjectId = project.ProjectId,
                SemanticRevision = project.SemanticRevision,
                ProfileDigest = _profile.Digest,
                EngineVersion = "1.0.0",
                StartedUtc = DateTimeOffset.UtcNow,
                FrozenParameters = new Dictionary<string, string>(parameters)
            };
            m.Outcome = RunOutcome.Cancelled;
            return JobResult.FromManifest(m, Array.Empty<XbrlFact>(), "");
        }

        var compile = Validate(project);
        if (!compile.Success || compile.Plan is null)
        {
            manifest.Outcome = RunOutcome.InvalidMapping;
            manifest.Diagnostics = compile.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        var context = new RunContext
        {
            OperationId = opId,
            Manifest = manifest,
            Parameters = parameters,
            IsPreview = preview,
            CancellationToken = cancellationToken
        };

        // Load each source binding into the context.
        foreach (var binding in project.Bindings)
        {
            if (!connections.TryGetValue(binding.BindingId, out var conn))
            {
                context.Diagnostics.Error(
                    DiagnosticCodes.IoSchemaMismatch,
                    DiagnosticSubsystem.Adapter,
                    DiagnosticStage.Introspection,
                    $"No connection supplied for binding '{binding.BindingId}'.",
                    operationId: opId);
                manifest.Outcome = RunOutcome.IncompleteConfiguration;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }

            var adapter = BuildAdapter(conn);
            var schema = await adapter.InspectSchemaAsync(conn, cancellationToken);

            if (!string.IsNullOrEmpty(binding.SchemaFingerprint)
                && !string.Equals(binding.SchemaFingerprint, schema.Fingerprint, StringComparison.Ordinal))
            {
                context.Diagnostics.Error(
                    DiagnosticCodes.IoSchemaMismatch,
                    DiagnosticSubsystem.Adapter,
                    DiagnosticStage.Introspection,
                    $"Source '{binding.DisplayName}' schema changed since the mapping was saved. Remap before running.",
                    operationId: opId);
                manifest.Outcome = RunOutcome.DataError;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }

            var records = new List<DataRecord>();
            await foreach (var r in adapter.ReadFullAsync(conn, schema, cancellationToken))
            {
                records.Add(r);
                if (preview && records.Count >= context.PreviewRowLimit) break;
            }
            context.LoadedSources[binding.BindingId] = records;
        }

        if (context.Diagnostics.HasBlockingErrors)
        {
            manifest.Outcome = RunOutcome.DataError;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        var exec = await compile.Plan.ExecuteAsync(context);

        // Fold PendingFacts into validated XbrlFacts, then emit xBRL-JSON.
        var facts = new List<XbrlFact>();
        foreach (var pf in exec.Facts)
        {
            if (pf.ConceptName is null || pf.EntityIdentifier is null || pf.Period is null)
            {
                context.Diagnostics.Error(
                    DiagnosticCodes.XbrPeriodInvalid,
                    DiagnosticSubsystem.XBRL,
                    DiagnosticStage.Serialization,
                    "Fact is missing required aspects (concept, entity, or period).",
                    operationId: opId);
                continue;
            }
            facts.Add(new XbrlFact
            {
                ConceptName = pf.ConceptName,
                EntityScheme = pf.EntityScheme!,
                EntityIdentifier = pf.EntityIdentifier,
                Period = pf.Period,
                Unit = pf.Unit,
                Value = pf.Value,
                IsNil = pf.IsNil,
                Decimals = pf.Decimals,
                Language = pf.Language,
                ContributingSources = pf.Sources
            });
        }

        var json = OimSerializer.Serialize(_profile, facts);

        if (context.Diagnostics.HasBlockingErrors || exec.Outcome != RunOutcome.Success)
        {
            manifest.Outcome = context.Diagnostics.HasBlockingErrors
                ? RunOutcome.InvalidMapping
                : exec.Outcome;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, facts, "");
        }

        // Validate through the worker (XBR05 acceptance).
        var validateReq = new WorkerRequest(
            WorkerOperations.ValidateReport,
            OperationId: opId + "-validate",
            JobId: runId,
            ProfileDigest: _profile.Digest,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(2),
            Payload: JsonSerializer.SerializeToElement(json));
        var workerResp = await _worker.RequestAsync(validateReq, cancellationToken);

        if (workerResp.Status is WorkerStatus.DomainFailure or WorkerStatus.Crash
            or WorkerStatus.IncompleteValidation)
        {
            manifest.Outcome = workerResp.Status == WorkerStatus.IncompleteValidation
                ? RunOutcome.IncompleteConfiguration
                : RunOutcome.InvalidMapping;
        }
        else
        {
            manifest.Outcome = preview ? RunOutcome.SuccessWithWarnings : RunOutcome.Success;
        }

        manifest.CompletedUtc = DateTimeOffset.UtcNow;
        manifest.Diagnostics = context.Diagnostics.Items
            .Concat(workerResp.Diagnostics ?? Array.Empty<Diagnostic>()).ToList();
        manifest.Outputs = new List<OutputIdentity>
        {
            new OutputIdentity(
                Path: "<memory>",
                Digest: RunManifest.ComputeSha256(json),
                Bytes: System.Text.Encoding.UTF8.GetByteCount(json),
                Kind: "xbrl-json")
        };

        return JobResult.FromManifest(manifest, facts, json, context.SampledTotals);
    }

    private static ISourceAdapter BuildAdapter(SourceConnection conn) => conn.Kind switch
    {
        "excel" => new ExcelAdapter(),
        "csv" => new CsvAdapter(CsvAdapter.Options.FromConfiguration(conn.Configuration)),
        "json" => new JsonAdapter(JsonAdapter.Options.FromConfiguration(conn.Configuration)),
        "xml" => new XmlAdapter(),
        "xbrl-report" => new XbrlReportAdapter(),
        _ => throw new NotSupportedException($"Adapter kind '{conn.Kind}' is not supported in this build.")
    };

    /// <summary>
    /// Resolve the write-path adapter for a destination connection and enforce the IO01
    /// direction gate: an adapter whose declared capabilities do not permit a staged
    /// write or publication cannot be bound as a target. A read-only adapter (e.g. the
    /// Excel or JSON/XML source readers) throws <see cref="AdapterException"/> with
    /// <c>IO008</c> before any bytes are touched.
    /// </summary>
    public static ITargetAdapter BuildTargetAdapter(SourceConnection conn)
    {
        ITargetAdapter adapter = conn.Kind switch
        {
            "csv" => new CsvAdapter(CsvAdapter.Options.FromConfiguration(conn.Configuration)),
            "excel" => new ExcelOutputAdapter(),
            _ => throw new NotSupportedException($"No target adapter for kind '{conn.Kind}'.")
        };
        AssertDirection(AdapterDirection.Write, adapter.Capabilities, adapter.Kind);
        return adapter;
    }

    /// <summary>
    /// IO01 direction gate. A component resolved for one direction must not be bound for
    /// the other when its declared capabilities forbid it. Read-only source adapters may
    /// never serve as a write target; write-only sinks may never serve as a read source.
    /// </summary>
    public static void AssertDirection(AdapterDirection direction, AdapterCapabilities caps, string kind)
    {
        bool permitted = direction switch
        {
            AdapterDirection.Write => caps.AllowsWriteTarget,
            AdapterDirection.Read => caps.SupportsReading,
            _ => false
        };
        if (!permitted)
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                $"Adapter '{kind}' cannot be bound for {direction} use with its declared capabilities.",
                DiagnosticStage.Publication);
    }

    private static async Task<IReadOnlyList<InputIdentity>> RecordInputIdentities(
        IReadOnlyDictionary<string, SourceConnection> connections,
        CancellationToken ct)
    {
        var list = new List<InputIdentity>();
        foreach (var kv in connections)
        {
            ct.ThrowIfCancellationRequested();
            if (kv.Value.Path is not null && File.Exists(kv.Value.Path))
            {
                await using var fs = File.OpenRead(kv.Value.Path);
                var digest = RunManifest.ComputeSha256(fs);
                list.Add(new InputIdentity(kv.Key, "file", digest, IsReplayable: true, kv.Value.Path));
            }
            else
            {
                list.Add(new InputIdentity(kv.Key, kv.Value.Kind, null, IsReplayable: false, kv.Value.Path));
            }
        }
        return list;
    }
}

public sealed record CompileOutcome(
    bool Success,
    ExecutionPlan? Plan,
    MappingGraph? Graph,
    DiagnosticBag Diagnostics)
{
    public static CompileOutcome Ok(ExecutionPlan p, MappingGraph g, DiagnosticBag d) => new(true, p, g, d);
    public static CompileOutcome Invalid(DiagnosticBag d) => new(false, null, null, d);
}

public sealed record JobResult(
    RunManifest Manifest,
    IReadOnlyList<XbrlFact> Facts,
    string XbrlJson,
    DiagnosticBag Diagnostics,
    // MAP08: aggregates computed during a preview run, each flagged IsPartial so a
    // reviewer cannot mistake a sampled total for a full-run figure.
    IReadOnlyList<SampledTotal> SampledTotals)
{
    internal static JobResult FromManifest(RunManifest manifest, IReadOnlyList<XbrlFact> facts, string json,
        IReadOnlyList<SampledTotal>? sampledTotals = null)
    {
        var bag = new DiagnosticBag();
        foreach (var d in manifest.Diagnostics) bag.Add(d);
        return new JobResult(manifest, facts, json, bag, sampledTotals ?? Array.Empty<SampledTotal>());
    }
}
