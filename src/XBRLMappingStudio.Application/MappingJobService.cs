using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Csv;
using XBRLMappingStudio.Adapters.Excel;
using XBRLMappingStudio.Adapters.Json;
using XBRLMappingStudio.Adapters.Xml;
using XBRLMappingStudio.Adapters.Xbrl;
using XBRLMappingStudio.Adapters.Sqlite;
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
        var profileDiagnostics = PublicationGate.EvaluateProfileIdentity(
            project, _profile, DiagnosticStage.Validation);
        if (profileDiagnostics.HasBlockingErrors)
            return CompileOutcome.Invalid(profileDiagnostics);

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
        var manifest = new RunManifest
        {
            RunId = runId,
            OperationId = opId,
            ProjectId = project.ProjectId,
            SemanticRevision = project.SemanticRevision,
            ProjectSemanticDigest = project.ComputeSemanticDigest(),
            ProfileDigest = _profile.Digest,
            EngineVersion = "1.0.0",
            StartedUtc = DateTimeOffset.UtcNow,
            IsPreview = preview,
            FrozenParameters = new Dictionary<string, string>(parameters)
        };

        // Reject missing or substituted profiles before hashing or opening any source.
        var profileDiagnostics = PublicationGate.EvaluateProfileIdentity(
            project, _profile, DiagnosticStage.Validation, opId);
        if (profileDiagnostics.HasBlockingErrors)
        {
            manifest.Outcome = RunOutcome.IncompleteConfiguration;
            manifest.Diagnostics = profileDiagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifest.Inputs = await RecordInputIdentities(connections, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
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
                manifest.Diagnostics = context.Diagnostics.Items;
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
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }

            var records = new List<DataRecord>();
            await foreach (var r in adapter.ReadFullAsync(conn, schema, cancellationToken))
            {
                records.Add(r);
                if (preview && records.Count >= context.PreviewRowLimit) break;
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(conn.Kind, "sqlite"))
            {
                var digest = DigestRecords(conn, records);
                var hasDeclaredPrimaryKey = schema.Columns.Any(column => column.PrimaryKeyOrdinal > 0);
                var identityStrategy = hasDeclaredPrimaryKey
                    ? SqliteSourceAdapter.StablePrimaryKeyIdentityStrategy
                    : SqliteSourceAdapter.PositionalIdentityStrategy;
                if (!hasDeclaredPrimaryKey)
                {
                    context.Diagnostics.Warn(
                        DiagnosticCodes.IoStableRowIdentityUnavailable,
                        DiagnosticSubsystem.Adapter,
                        DiagnosticStage.Execution,
                        "The selected SQLite table has no declared primary key. "
                        + "SQLite does not guarantee row ordering, so positional row provenance may shift between reads even when visible rows are unchanged; "
                        + "use a declared primary key or immutable extract if durable row-level identity is required.",
                        operationId: opId);
                }
                manifest.Inputs = manifest.Inputs.Select(input => input.SourceId == binding.BindingId
                    ? input with
                    {
                        Kind = preview ? "sqlite-preview" : "sqlite-table",
                        Digest = digest,
                        IsReplayable = !preview,
                        RowIdentityStrategy = identityStrategy
                    }
                    : input).ToArray();
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

        string json;
        try
        {
            json = OimSerializer.Serialize(_profile, facts);
        }
        catch (XbrlExportException ex)
        {
            context.Diagnostics.Error(ex.Code, DiagnosticSubsystem.XBRL, DiagnosticStage.Serialization,
                ex.Message, operationId: opId);
            manifest.Outcome = RunOutcome.InvalidMapping;
            manifest.Diagnostics = context.Diagnostics.Items;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            return JobResult.FromManifest(manifest, facts, "");
        }

        if (context.Diagnostics.HasBlockingErrors || exec.Outcome != RunOutcome.Success)
        {
            manifest.Outcome = context.Diagnostics.HasBlockingErrors
                ? RunOutcome.InvalidMapping
                : exec.Outcome;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, facts, "");
        }

        // Validate through the worker (XBR05 acceptance).
        WorkerHandshake handshake;
        try
        {
            handshake = await _worker.HandshakeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error(DiagnosticCodes.WkrHandshakeFailed,
                DiagnosticSubsystem.Worker, DiagnosticStage.Worker,
                $"Validation worker handshake failed: {ex.Message}", operationId: opId);
            manifest.Outcome = RunOutcome.IncompleteConfiguration;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        if (handshake.ProtocolVersion < WorkerProtocol.MinimumVersion
            || handshake.ProtocolVersion > WorkerProtocol.CurrentVersion
            || !handshake.Capabilities.Contains(WorkerOperations.ValidateReport, StringComparer.Ordinal))
        {
            context.Diagnostics.Error(DiagnosticCodes.WkrProtocolVersionMismatch,
                DiagnosticSubsystem.Worker, DiagnosticStage.Worker,
                $"Validation worker '{handshake.EngineVersion}' has an incompatible protocol or lacks report validation.",
                operationId: opId);
            manifest.Outcome = RunOutcome.IncompleteConfiguration;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        var validateReq = new WorkerRequest(
            WorkerOperations.ValidateReport,
            OperationId: opId + "-validate",
            JobId: runId,
            ProfileDigest: _profile.Digest,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(2),
            Payload: JsonSerializer.SerializeToElement(json));
        var workerResp = await _worker.RequestAsync(validateReq, cancellationToken);

        // XBR05 fail-closed worker adjudication: a run may be labelled successful only
        // when the worker explicitly reports Success AND returns no blocking diagnostics.
        // Every other terminal state — cancellation, incomplete validation, a domain
        // failure, a crash, or a nominal Success that still carries blocking findings —
        // must prevent publication. A real Arelle backend can exit "success" while its
        // report contains errors, so the status alone is not sufficient evidence of a
        // compliant filing; this guard makes the run's publishability depend on content.
        var workerBlocking = (workerResp.Diagnostics ?? Array.Empty<Diagnostic>()).Any(d => d.IsBlocking);
        manifest.Outcome = workerResp.Status switch
        {
            WorkerStatus.Success when !workerBlocking
                => preview || context.Diagnostics.Items.Any(d => d.Severity == DiagnosticSeverity.Warning)
                    ? RunOutcome.SuccessWithWarnings
                    : RunOutcome.Success,
            WorkerStatus.Cancelled => RunOutcome.Cancelled,
            WorkerStatus.IncompleteValidation => RunOutcome.IncompleteConfiguration,
            WorkerStatus.TimedOut => RunOutcome.OperationalFailure,
            WorkerStatus.Crash => RunOutcome.OperationalFailure,
            // DomainFailure, and Success-with-blocking-diagnostics, are invalid results.
            _ => RunOutcome.InvalidMapping,
        };

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
        "sqlite" => new SqliteSourceAdapter(),
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
            if (StringComparer.OrdinalIgnoreCase.Equals(kv.Value.Kind, "sqlite"))
            {
                list.Add(new InputIdentity(kv.Key, "sqlite-table", null, IsReplayable: false, kv.Value.Path));
                continue;
            }
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

    private static string DigestRecords(SourceConnection connection, IReadOnlyList<DataRecord> records)
    {
        var builder = new StringBuilder();
        AppendPart(builder, connection.Configuration is not null
            && connection.Configuration.TryGetValue("table", out var table) ? table : "");
        builder.Append(records.Count).Append(':');
        var rows = records.Select(CanonicalRecord).Order(StringComparer.Ordinal);
        foreach (var row in rows) AppendPart(builder, row);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string CanonicalRecord(DataRecord record)
    {
        var row = new StringBuilder();
        row.Append(record.Fields.Count).Append(':');
        foreach (var field in record.Fields.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            AppendPart(row, field.Key);
            AppendPart(row, field.Value.Shape.ToString());
            row.Append((int)field.Value.Presence).Append(':');
            AppendPart(row, field.Value.LexicalSource ?? "");
            AppendPart(row, field.Value.Raw switch
            {
                null => "",
                string value => value,
                decimal value => value.ToString(CultureInfo.InvariantCulture),
                DateOnly value => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeOffset value => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                IFormattable value => value.ToString(null, CultureInfo.InvariantCulture) ?? "",
                var value => value.ToString() ?? ""
            });
        }
        return row.ToString();
    }

    private static void AppendPart(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value);
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
    /// <summary>
    /// OG01 fail-closed publishing: only a completed, full-run (non-preview) execution
    /// whose outcome is an unqualified success or success-with-warnings is eligible to
    /// be written to a durable destination. Every other terminal state (cancelled,
    /// invalid mapping, data error, incomplete configuration, authorization failure,
    /// operational failure, unknown, or any preview) must leave a prior accepted
    /// destination untouched and issue no publication receipt.
    /// </summary>
    public bool IsPublishable =>
        !Manifest.IsPreview
        && Manifest.Outcome is RunOutcome.Success or RunOutcome.SuccessWithWarnings;

    internal static JobResult FromManifest(RunManifest manifest, IReadOnlyList<XbrlFact> facts, string json,
        IReadOnlyList<SampledTotal>? sampledTotals = null)
    {
        var bag = new DiagnosticBag();
        foreach (var d in manifest.Diagnostics) bag.Add(d);
        return new JobResult(manifest, facts, json, bag, sampledTotals ?? Array.Empty<SampledTotal>());
    }
}
