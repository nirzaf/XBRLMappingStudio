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
using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;
using RunContext = XBRLMappingStudio.Engine.ExecutionContext;

namespace XBRLMappingStudio.Application;

/// <summary>Observable stage progress for a mapping job. Worker work may be indeterminate.</summary>
public sealed record MappingJobProgress(string Stage, int? Current = null, int? Total = null);

/// <summary>
/// Shared application service used by Desktop and CLI (OPS01). Executes the same
/// graph in-process; callers supply the parameters and destinations.
/// </summary>
public sealed class MappingJobService
{
    private readonly FilingProfile _profile;
    private readonly FunctionCatalog _catalog;
    private readonly IWorkerClient _worker;
    private readonly Func<LocalApplicationDatabase> _applicationDatabaseFactory;
    private readonly ExecutionBudget _executionBudget;

    public MappingJobService(
        FilingProfile profile,
        IWorkerClient worker,
        FunctionCatalog? catalog = null,
        Func<LocalApplicationDatabase>? applicationDatabaseFactory = null,
        ExecutionBudget? executionBudget = null)
    {
        _profile = profile;
        _worker = worker;
        _catalog = catalog ?? FunctionCatalog.Default;
        _applicationDatabaseFactory = applicationDatabaseFactory ?? (() => new LocalApplicationDatabase());
        _executionBudget = executionBudget ?? ExecutionBudget.Default;
        _executionBudget.Validate();
    }

    /// <summary>Validate + compile the graph in a project. Does not execute.</summary>
    public CompileOutcome Validate(MappingProject project)
        => ValidateProject(project, _profile, _catalog);

    /// <summary>Profile-specific graph validation that does not require a worker process.</summary>
    public static CompileOutcome ValidateProject(
        MappingProject project,
        FilingProfile profile,
        FunctionCatalog? catalog = null)
    {
        var profileDiagnostics = PublicationGate.EvaluateProfileIdentity(
            project, profile, DiagnosticStage.Validation);
        if (profileDiagnostics.HasBlockingErrors)
            return CompileOutcome.Invalid(profileDiagnostics);

        var destinationDiagnostics = DestinationPlanValidator.Validate(project.Destination);
        if (destinationDiagnostics.HasBlockingErrors)
            return CompileOutcome.Invalid(destinationDiagnostics);

        var graph = new MappingGraph(project);
        var effectiveCatalog = catalog ?? FunctionCatalog.Default;
        var validator = new GraphValidator(effectiveCatalog);
        var diag = validator.Validate(graph);
        if (diag.HasBlockingErrors)
            return CompileOutcome.Invalid(diag);
        var compiler = new PlanCompiler(effectiveCatalog);
        var result = compiler.Compile(graph, profile);
        if (!result.Success || result.Plan is null)
            return CompileOutcome.Invalid(result.Diagnostics);
        return CompileOutcome.Ok(result.Plan, graph, diag);
    }

    private static JobResult PreflightFailure(
        RunManifest manifest,
        RunOutcome outcome,
        string fallbackCode,
        string fallbackMessage,
        IEnumerable<Diagnostic>? diagnostics = null)
    {
        var items = diagnostics?.Select(diagnostic => diagnostic with
        {
            OperationId = manifest.OperationId
        }).ToList() ?? new List<Diagnostic>();
        if (!items.Any(diagnostic => diagnostic.IsBlocking))
        {
            items.Add(new Diagnostic
            {
                Code = fallbackCode,
                Subsystem = DiagnosticSubsystem.Worker,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Worker,
                IsBlocking = true,
                Message = fallbackMessage,
                OperationId = manifest.OperationId
            });
        }

        manifest.Outcome = outcome;
        manifest.CompletedUtc = DateTimeOffset.UtcNow;
        manifest.Diagnostics = items;
        return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
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
        CancellationToken cancellationToken,
        IProgress<MappingJobProgress>? progress = null)
    {
        progress?.Report(new MappingJobProgress("Checking filing profile and mapping"));
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

        // Validate the saved destination and graph before hashing or opening any
        // source. A malformed target plan must fail closed without touching inputs.
        var compile = Validate(project);
        if (!compile.Success || compile.Plan is null)
        {
            manifest.Outcome = RunOutcome.InvalidMapping;
            manifest.Diagnostics = compile.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        // Verify the selected worker and load the exact profile taxonomy before
        // hashing, inspecting, or reading any source data.
        WorkerHandshake handshake;
        try
        {
            progress?.Report(new MappingJobProgress("Starting validation worker"));
            handshake = await _worker.HandshakeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        catch (WorkerStartupException ex)
        {
            var outcome = ex.Status switch
            {
                WorkerStatus.Cancelled => RunOutcome.Cancelled,
                WorkerStatus.Crash or WorkerStatus.TimedOut => RunOutcome.OperationalFailure,
                _ => RunOutcome.IncompleteConfiguration
            };
            var fallbackCode = ex.Status switch
            {
                WorkerStatus.TimedOut => DiagnosticCodes.WkrTimeout,
                WorkerStatus.Crash => DiagnosticCodes.WkrCrash,
                WorkerStatus.IncompleteValidation => DiagnosticCodes.XbrWorkerUnavailable,
                WorkerStatus.DomainFailure => DiagnosticCodes.WkrProtocolVersionMismatch,
                _ => DiagnosticCodes.WkrHandshakeFailed
            };
            return PreflightFailure(manifest, outcome, fallbackCode,
                $"Validation worker startup failed: {ex.Message}", ex.Diagnostics);
        }
        catch (Exception ex)
        {
            return PreflightFailure(manifest, RunOutcome.IncompleteConfiguration,
                DiagnosticCodes.WkrHandshakeFailed,
                $"Validation worker handshake failed: {ex.Message}");
        }

        if (handshake.ProtocolVersion < WorkerProtocol.MinimumVersion
            || handshake.ProtocolVersion > WorkerProtocol.CurrentVersion)
            return PreflightFailure(manifest, RunOutcome.IncompleteConfiguration,
                DiagnosticCodes.WkrProtocolVersionMismatch,
                $"Validation worker '{handshake.EngineVersion}' returned an unsupported protocol version.");

        var requiredCapabilities = new[] { WorkerOperations.LoadTaxonomy, WorkerOperations.ValidateReport };
        var missingCapabilities = requiredCapabilities
            .Where(capability => !handshake.Capabilities.Contains(capability, StringComparer.Ordinal))
            .ToArray();
        if (missingCapabilities.Length > 0)
            return PreflightFailure(manifest, RunOutcome.IncompleteConfiguration,
                DiagnosticCodes.WkrHandshakeFailed,
                $"Validation worker '{handshake.EngineVersion}' lacks required capabilities: {string.Join(", ", missingCapabilities)}.");

        var isSynthetic = StringComparer.Ordinal.Equals(_profile.ProfileId, SyntheticProfile.ProfileId);
        if (!isSynthetic
            && (!StringComparer.Ordinal.Equals(_profile.ProfileId, handshake.ProfileId)
                || !StringComparer.Ordinal.Equals(_profile.Digest, handshake.ProfileDigest)
                || handshake.ProfileSchemaVersion is null
                || string.IsNullOrWhiteSpace(handshake.TaxonomyEntryPoint)
                || !handshake.RequiredPlugins.Order(StringComparer.Ordinal)
                    .SequenceEqual(_profile.RequiredPlugins.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || !handshake.RequiredChecks.Order(StringComparer.Ordinal)
                    .SequenceEqual(_profile.RequiredChecks.Order(StringComparer.Ordinal), StringComparer.Ordinal)))
            return PreflightFailure(manifest, RunOutcome.IncompleteConfiguration,
                DiagnosticCodes.XbrProfileMismatch,
                "Validation worker profile identity or taxonomy metadata does not match the selected profile.");

        var taxonomyEntryPoint = handshake.TaxonomyEntryPoint;
        if (string.IsNullOrWhiteSpace(taxonomyEntryPoint) && isSynthetic)
            taxonomyEntryPoint = _profile.TaxonomyUrls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(taxonomyEntryPoint))
            return PreflightFailure(manifest, RunOutcome.IncompleteConfiguration,
                DiagnosticCodes.XbrProfileMismatch,
                "Selected profile does not expose a taxonomy entry point for worker preflight.");

        WorkerResponse taxonomyResponse;
        try
        {
            progress?.Report(new MappingJobProgress("Loading filing taxonomy in validation worker"));
            taxonomyResponse = await _worker.RequestAsync(new WorkerRequest(
                WorkerOperations.LoadTaxonomy,
                OperationId: opId + "-taxonomy",
                JobId: runId,
                ProfileDigest: _profile.Digest,
                DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(2),
                Payload: JsonSerializer.SerializeToElement(new { taxonomyPath = taxonomyEntryPoint })),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        var taxonomyDiagnostics = taxonomyResponse.Diagnostics ?? Array.Empty<Diagnostic>();
        if (taxonomyResponse.Status != WorkerStatus.Success
            || taxonomyDiagnostics.Any(diagnostic => diagnostic.IsBlocking))
        {
            var outcome = taxonomyResponse.Status switch
            {
                WorkerStatus.Cancelled => RunOutcome.Cancelled,
                WorkerStatus.Crash or WorkerStatus.TimedOut => RunOutcome.OperationalFailure,
                WorkerStatus.Success when taxonomyDiagnostics.Any(diagnostic => diagnostic.IsBlocking)
                    => RunOutcome.InvalidMapping,
                _ => RunOutcome.IncompleteConfiguration
            };
            var fallbackCode = taxonomyResponse.Status switch
            {
                WorkerStatus.TimedOut => DiagnosticCodes.WkrTimeout,
                WorkerStatus.Crash => DiagnosticCodes.WkrCrash,
                WorkerStatus.IncompleteValidation => DiagnosticCodes.XbrWorkerUnavailable,
                _ => DiagnosticCodes.WkrHandshakeFailed
            };
            return PreflightFailure(manifest, outcome, fallbackCode,
                taxonomyResponse.Error ?? "The selected taxonomy did not load successfully.", taxonomyDiagnostics);
        }

        if (!isSynthetic)
        {
            WorkerTaxonomyLoadResult? loadedProfile;
            try
            {
                loadedProfile = taxonomyResponse.Result?.Deserialize<WorkerTaxonomyLoadResult>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException)
            {
                loadedProfile = null;
            }

            if (loadedProfile is null
                || !loadedProfile.Completed
                || loadedProfile.Capabilities is null
                || loadedProfile.RequiredPlugins is null
                || loadedProfile.RequiredChecks is null
                || loadedProfile.ProfileSchemaVersion != handshake.ProfileSchemaVersion
                || !StringComparer.Ordinal.Equals(loadedProfile.ProfileId, handshake.ProfileId)
                || !StringComparer.Ordinal.Equals(loadedProfile.ProfileVersion, handshake.ProfileVersion)
                || !StringComparer.Ordinal.Equals(loadedProfile.ProfileDigest, _profile.Digest)
                || !StringComparer.Ordinal.Equals(loadedProfile.TaxonomyEntryPoint, taxonomyEntryPoint)
                || !loadedProfile.RequiredPlugins.SequenceEqual(handshake.RequiredPlugins, StringComparer.Ordinal)
                || !loadedProfile.RequiredChecks.SequenceEqual(handshake.RequiredChecks, StringComparer.Ordinal)
                || !loadedProfile.Capabilities.Contains(WorkerOperations.ValidateReport, StringComparer.Ordinal))
                return PreflightFailure(manifest, RunOutcome.IncompleteConfiguration,
                    DiagnosticCodes.XbrProfileMismatch,
                    "Loaded taxonomy metadata does not match the selected profile handshake.");
        }

        var identityConnections = new Dictionary<string, SourceConnection>(StringComparer.Ordinal);
        foreach (var binding in project.Bindings)
        {
            if (!connections.TryGetValue(binding.BindingId, out var connection)) continue;
            identityConnections.TryAdd(binding.BindingId, connection);
            if (StringComparer.OrdinalIgnoreCase.Equals(connection.Kind, "sqlite")) continue;
            if (IsBoundedInMemorySource(connection, out var sourceLimitReason)) continue;

            manifest.Outcome = RunOutcome.IncompleteConfiguration;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = new[]
            {
                new Diagnostic
                {
                    Code = DiagnosticCodes.IoNumberOutOfBudget,
                    Subsystem = DiagnosticSubsystem.Adapter,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Introspection,
                    IsBlocking = true,
                    Message = sourceLimitReason,
                    OperationId = opId
                }
            };
            progress?.Report(new MappingJobProgress("Run ended: IncompleteConfiguration"));
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MappingJobProgress("Recording source identities"));
            manifest.Inputs = await RecordInputIdentities(identityConnections, cancellationToken,
                _executionBudget.MaxInMemorySourceBytes);
        }
        catch (OperationCanceledException)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
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

        // Bounded probe. Small and preview runs retain the existing in-memory plan;
        // only a single streamed SQLite source may enter the qualified large path.
        await using var sourceReadersOwner = new BufferedSourceReaderSet();
        var sourceReaders = sourceReadersOwner.Readers;
        var sourceConnections = new Dictionary<string, SourceConnection>(StringComparer.Ordinal);
        var sourceSchemas = new Dictionary<string, ISourceSchema>(StringComparer.Ordinal);
        var bindingIndex = 0;
        foreach (var binding in project.Bindings)
        {
            bindingIndex++;
            if (!connections.TryGetValue(binding.BindingId, out var conn))
            {
                context.Diagnostics.Error(DiagnosticCodes.IoSchemaMismatch,
                    DiagnosticSubsystem.Adapter, DiagnosticStage.Introspection,
                    $"No connection supplied for binding '{binding.BindingId}'.", operationId: opId);
                manifest.Outcome = RunOutcome.IncompleteConfiguration;
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }

            try
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(conn.Kind, "sqlite")
                    && !IsBoundedInMemorySource(conn, out var sourceLimitReason))
                {
                    context.Diagnostics.Error(DiagnosticCodes.IoNumberOutOfBudget,
                        DiagnosticSubsystem.Adapter, DiagnosticStage.Introspection,
                        sourceLimitReason, operationId: opId);
                    manifest.Outcome = RunOutcome.IncompleteConfiguration;
                    manifest.CompletedUtc = DateTimeOffset.UtcNow;
                    manifest.Diagnostics = context.Diagnostics.Items;
                    return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                }
                progress?.Report(new MappingJobProgress(
                    $"Inspecting source {binding.DisplayName}", bindingIndex, project.Bindings.Count));
                var adapter = BuildExecutionAdapter(conn);
                var schema = await adapter.InspectSchemaAsync(conn, cancellationToken);
                if (!string.IsNullOrEmpty(binding.SchemaFingerprint)
                    && !string.Equals(binding.SchemaFingerprint, schema.Fingerprint, StringComparison.Ordinal))
                {
                    context.Diagnostics.Error(DiagnosticCodes.IoSchemaMismatch,
                        DiagnosticSubsystem.Adapter, DiagnosticStage.Introspection,
                        $"Source '{binding.DisplayName}' schema changed since the mapping was saved. Remap before running.",
                        operationId: opId);
                    manifest.Outcome = RunOutcome.DataError;
                    manifest.CompletedUtc = DateTimeOffset.UtcNow;
                    manifest.Diagnostics = context.Diagnostics.Items;
                    return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                }
                sourceConnections.Add(binding.BindingId, conn);
                sourceSchemas.Add(binding.BindingId, schema);
                sourceReadersOwner.Add(binding.BindingId, adapter.ReadFullAsync(conn, schema, cancellationToken), cancellationToken);
            }
            catch (AdapterException ex)
            {
                context.Diagnostics.Add(ex.ToDiagnostic(binding.BindingId, opId));
                manifest.Outcome = RunOutcome.DataError;
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                manifest.Outcome = RunOutcome.Cancelled;
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Diagnostics = context.Diagnostics.Items;
                progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }
        }

        var isLargeRun = false;
        long probedBytes = 0;
        string? activeBindingId = null;
        try
        {
            if (preview)
            {
                foreach (var binding in project.Bindings)
                {
                    activeBindingId = binding.BindingId;
                    var rows = await sourceReaders[binding.BindingId]
                        .ReadBatchAsync(context.PreviewRowLimit, cancellationToken);
                    context.LoadedSources[binding.BindingId] = rows;
                    progress?.Report(new MappingJobProgress($"Read source {binding.DisplayName}", rows.Count));
                }
            }
            else
            {
                var probedRecords = 0;
                while (probedRecords <= _executionBudget.InMemorySourceRecordLimit)
                {
                    var progressed = false;
                    foreach (var binding in project.Bindings)
                    {
                        activeBindingId = binding.BindingId;
                        var reader = sourceReaders[binding.BindingId];
                        if (reader.IsEnded) continue;
                        if (await reader.BufferNextAsync(cancellationToken))
                        {
                            probedRecords++;
                            var rowBytes = BufferedSourceReader.EstimateRecordBytes(reader.Buffer[^1]);
                            if (rowBytes > _executionBudget.MaxRowBytes)
                            {
                                context.Diagnostics.Error(DiagnosticCodes.IoNumberOutOfBudget,
                                    DiagnosticSubsystem.Adapter, DiagnosticStage.Introspection,
                                    $"A source row exceeded the {_executionBudget.MaxRowBytes} byte row budget.",
                                    operationId: opId);
                                manifest.Outcome = RunOutcome.IncompleteConfiguration;
                                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                                manifest.Diagnostics = context.Diagnostics.Items;
                                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                            }
                            probedBytes = checked(probedBytes + rowBytes);
                            progressed = true;
                            if (probedRecords > _executionBudget.InMemorySourceRecordLimit
                                || probedBytes > _executionBudget.MaxProbeBytes)
                            {
                                isLargeRun = true;
                                break;
                            }
                        }
                    }
                    if (isLargeRun || !progressed) break;
                }

                if (!isLargeRun)
                {
                    foreach (var binding in project.Bindings)
                    {
                        activeBindingId = binding.BindingId;
                        context.LoadedSources[binding.BindingId] = sourceReaders[binding.BindingId].Buffer;
                        progress?.Report(new MappingJobProgress(
                            $"Read source {binding.DisplayName}", sourceReaders[binding.BindingId].Buffer.Count));
                    }
                }
            }
        }
        catch (AdapterException ex)
        {
            context.Diagnostics.Add(ex.ToDiagnostic(activeBindingId, opId));
            manifest.Outcome = RunOutcome.DataError;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        if (isLargeRun)
        {
            var binding = project.Bindings.SingleOrDefault();
            if (binding is null || project.Bindings.Count != 1
                || compile.Plan!.SetSteps.Count != 0
                || !StringComparer.OrdinalIgnoreCase.Equals(sourceConnections[binding.BindingId].Kind, "sqlite")
                || project.Destination is not null
                || !StringComparer.Ordinal.Equals(_profile.OutputFormat, "xbrl-json"))
            {
                context.Diagnostics.Error(DiagnosticCodes.IoUnsupportedOperation,
                    DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                    "Large-run execution is currently qualified only for one streamed SQLite source, a row-scoped plan, no configured destination, and xBRL-JSON output.",
                    operationId: opId);
                manifest.Outcome = RunOutcome.IncompleteConfiguration;
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }

            var connection = sourceConnections[binding.BindingId];
            var schema = sourceSchemas[binding.BindingId];
            var hasPrimaryKey = schema.Columns.Any(column => column.PrimaryKeyOrdinal > 0);
            if (!hasPrimaryKey)
            {
                context.Diagnostics.Warn(DiagnosticCodes.IoStableRowIdentityUnavailable,
                    DiagnosticSubsystem.Adapter, DiagnosticStage.Execution,
                    "The selected SQLite table has no declared primary key. SQLite does not guarantee row ordering, so positional row provenance may shift between reads even when visible rows are unchanged; use a declared primary key or immutable extract if durable row-level identity is required.",
                    operationId: opId);
            }
            context.Diagnostics.SetMaximumRetainedItems(255);
            return await RunLargeRowScopedAsync(project, binding, connection, schema,
                sourceReaders[binding.BindingId], compile.Plan!, context, manifest, runId, opId, probedBytes,
                progress, cancellationToken, hasPrimaryKey);
        }

        if (preview)
        {
            foreach (var binding in project.Bindings)
            {
                var connection = sourceConnections[binding.BindingId];
                if (StringComparer.Ordinal.Equals(connection.Kind, "pdf-digital")
                    && !await PdfInputsMatchRecordedIdentityAsync(binding.BindingId, connection, manifest.Inputs, cancellationToken))
                {
                    context.Diagnostics.Error(DiagnosticCodes.IoPdfReviewManifestInvalid,
                        DiagnosticSubsystem.Adapter, DiagnosticStage.Validation,
                        "The PDF source or review manifest changed during this run. Retry so the approved input identity can be recorded.",
                        source: new SourceReference(binding.BindingId, "pdf-input-identity"), operationId: opId);
                    manifest.Outcome = RunOutcome.DataError;
                    manifest.CompletedUtc = DateTimeOffset.UtcNow;
                    manifest.Diagnostics = context.Diagnostics.Items;
                    return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                }
                if (StringComparer.OrdinalIgnoreCase.Equals(connection.Kind, "sqlite"))
                {
                    AddSqliteRowIdentityWarning(context, sourceSchemas[binding.BindingId], opId);
                    UpdateSqliteInputIdentity(manifest, binding.BindingId, connection,
                        DigestRecords(connection, context.LoadedSources[binding.BindingId]), schema: sourceSchemas[binding.BindingId], preview: true);
                }
            }
        }
        else
        {
            foreach (var binding in project.Bindings)
            {
                var connection = sourceConnections[binding.BindingId];
                if (StringComparer.Ordinal.Equals(connection.Kind, "pdf-digital")
                    && !await PdfInputsMatchRecordedIdentityAsync(binding.BindingId, connection, manifest.Inputs, cancellationToken))
                {
                    context.Diagnostics.Error(DiagnosticCodes.IoPdfReviewManifestInvalid,
                        DiagnosticSubsystem.Adapter, DiagnosticStage.Validation,
                        "The PDF source or review manifest changed during this run. Retry so the approved input identity can be recorded.",
                        source: new SourceReference(binding.BindingId, "pdf-input-identity"), operationId: opId);
                    manifest.Outcome = RunOutcome.DataError;
                    manifest.CompletedUtc = DateTimeOffset.UtcNow;
                    manifest.Diagnostics = context.Diagnostics.Items;
                    return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                }
                if (StringComparer.OrdinalIgnoreCase.Equals(connection.Kind, "sqlite"))
                {
                    AddSqliteRowIdentityWarning(context, sourceSchemas[binding.BindingId], opId);
                    UpdateSqliteInputIdentity(manifest, binding.BindingId, connection,
                        DigestRecords(connection, context.LoadedSources[binding.BindingId]), schema: sourceSchemas[binding.BindingId], preview: false);
                }
            }
        }

        if (context.Diagnostics.HasBlockingErrors)
        {
            manifest.Outcome = RunOutcome.DataError;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

        progress?.Report(new MappingJobProgress("Executing mapping"));
        var exec = await compile.Plan.ExecuteAsync(context);
        if (exec.Outcome == RunOutcome.Cancelled || cancellationToken.IsCancellationRequested)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }

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
                TextValue = pf.TextValue,
                IsNil = pf.IsNil,
                Decimals = pf.Decimals,
                Language = pf.Language,
                Dimensions = pf.Dimensions,
                ContributingSources = pf.Sources
            });
        }

        if (facts.Count == 0)
        {
            context.Diagnostics.Error(
                DiagnosticCodes.XbrRequiredCheckMissing,
                DiagnosticSubsystem.XBRL,
                DiagnosticStage.Validation,
                "The mapping produced no XBRL facts. Verify the selected source rows and reporting-year filters before validation.",
                operationId: opId);
            manifest.Outcome = RunOutcome.IncompleteConfiguration;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, facts, "");
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

        // Validate the staged report through the already preflighted worker (XBR05).
        var validateReq = new WorkerRequest(
            WorkerOperations.ValidateReport,
            OperationId: opId + "-validate",
            JobId: runId,
            ProfileDigest: _profile.Digest,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(2),
            Payload: JsonSerializer.SerializeToElement(json))
        {
            ValidationRequirements = CreateValidationRequirements()
        };
        progress?.Report(new MappingJobProgress("Validating generated report in validation worker"));
        var workerResp = await _worker.RequestAsync(validateReq, cancellationToken);

        // XBR05 fail-closed worker adjudication: a run may be labelled successful only
        // when the worker explicitly reports Success AND returns no blocking diagnostics.
        // Every other terminal state — cancellation, incomplete validation, a domain
        // failure, a crash, or a nominal Success that still carries blocking findings —
        // must prevent publication. A real Arelle backend can exit "success" while its
        // report contains errors, so the status alone is not sufficient evidence of a
        // compliant filing; this guard makes the run's publishability depend on content.
        // XBR08 additionally distinguishes a per-request timeout from a crash and from
        // cancellation so the run manifest carries an unambiguous terminal cause: an
        // unresponsive worker that never dies must surface as WKR004 with the run
        // outcome OperationalFailure, never as Success-with-warnings or a silent pass.
        var completion = ValidationCompletionReportValidator.Evaluate(
            workerResp.Result, _profile,
            StringComparer.Ordinal.Equals(_profile.ProfileId, SyntheticProfile.ProfileId) ? facts : null);
        var workerBlocking = (workerResp.Diagnostics ?? Array.Empty<Diagnostic>()).Any(d => d.IsBlocking);
        manifest.Outcome = workerResp.Status switch
        {
            _ when completion.State == ValidationCompletionState.Failed => RunOutcome.InvalidMapping,
            WorkerStatus.Success when workerBlocking => RunOutcome.InvalidMapping,
            WorkerStatus.Success when completion.State == ValidationCompletionState.Incomplete
                => RunOutcome.IncompleteConfiguration,
            WorkerStatus.Success
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

        // Attach a WKR diagnostic for a terminal timeout or crash so downstream consumers
        // (support bundle, inspect-run-result, scheduler alerting) see the specific cause
        // even when the worker returned no diagnostics of its own (fake or truncated pipe).
        var workerDiags = (workerResp.Diagnostics ?? Array.Empty<Diagnostic>()).ToList();
        if (completion.State == ValidationCompletionState.Failed
            || (completion.State == ValidationCompletionState.Incomplete
                && workerResp.Status is (WorkerStatus.Success or WorkerStatus.IncompleteValidation)))
            workerDiags.Add(CompletionDiagnostic(completion, opId));
        if (workerResp.Status is WorkerStatus.TimedOut or WorkerStatus.Crash
            && !workerDiags.Any(d => d.Code is DiagnosticCodes.WkrTimeout or DiagnosticCodes.WkrCrash))
        {
            var code = workerResp.Status == WorkerStatus.TimedOut
                ? DiagnosticCodes.WkrTimeout
                : DiagnosticCodes.WkrCrash;
            var reason = workerResp.Status == WorkerStatus.TimedOut
                ? "Worker exceeded its per-request deadline and did not respond."
                : "Worker terminated unexpectedly; no response was received.";
            workerDiags.Add(new Diagnostic
            {
                Code = code,
                Subsystem = DiagnosticSubsystem.Worker,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Worker,
                IsBlocking = true,
                Message = workerResp.Error is null ? reason : $"{reason} Worker message: {workerResp.Error}",
                OperationId = opId,
            });
        }

        manifest.CompletedUtc = DateTimeOffset.UtcNow;
        manifest.Diagnostics = context.Diagnostics.Items.Concat(workerDiags).ToList();
        manifest.Outputs = new List<OutputIdentity>
        {
            new OutputIdentity(
                Path: "<memory>",
                Digest: RunManifest.ComputeSha256(json),
                Bytes: System.Text.Encoding.UTF8.GetByteCount(json),
                Kind: "xbrl-json")
        };

        progress?.Report(new MappingJobProgress($"Run ended: {manifest.Outcome}"));

        return JobResult.FromManifest(manifest, facts, json, context.SampledTotals);
    }

    private async Task<JobResult> RunLargeRowScopedAsync(
        MappingProject project,
        SourceBinding binding,
        SourceConnection connection,
        ISourceSchema schema,
        BufferedSourceReader reader,
        ExecutionPlan plan,
        RunContext context,
        RunManifest manifest,
        string runId,
        string operationId,
        long probeBytes,
        IProgress<MappingJobProgress>? progress,
        CancellationToken cancellationToken,
        bool hasPrimaryKey)
    {
        var directory = Path.Combine(Path.GetTempPath(), "xms-large-" + Guid.NewGuid().ToString("N"));
        LargeRunFactStore? store = null;
        LargeRunArtifact? artifact = null;
        var ownershipTransferred = false;
        try
        {
            CreatePrivateArtifactDirectory(directory);
            store = new LargeRunFactStore(Path.Combine(directory, "facts.sqlite"),
                _executionBudget.SqliteCacheKiB, _executionBudget.MaxSpoolBytes);
            long sourceRows = 0;
            var table = connection.Configuration is not null
                && connection.Configuration.TryGetValue("table", out var configuredTable) ? configuredTable : "";

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = await reader.ReadBatchAsync(_executionBudget.RowBatchSize,
                    _executionBudget.MaxBatchBytes, BufferedSourceReader.EstimateRecordBytes, cancellationToken);
                if (rows.Count == 0) break;
                sourceRows = checked(sourceRows + rows.Count);
                foreach (var row in rows)
                    store.AddSourceDigestRecord(CanonicalRecord(row), _executionBudget.MaxRowBytes);

                context.LoadedSources[binding.BindingId] = rows;
                var execution = await plan.ExecuteAsync(context);
                if (execution.Outcome == RunOutcome.Cancelled || cancellationToken.IsCancellationRequested)
                {
                    manifest.Outcome = RunOutcome.Cancelled;
                    manifest.CompletedUtc = DateTimeOffset.UtcNow;
                    manifest.Diagnostics = context.Diagnostics.Items;
                    progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
                    return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                }
                if (execution.Outcome != RunOutcome.Success || context.Diagnostics.HasBlockingErrors)
                {
                    manifest.Outcome = context.Diagnostics.HasBlockingErrors
                        ? RunOutcome.InvalidMapping : execution.Outcome;
                    manifest.CompletedUtc = DateTimeOffset.UtcNow;
                    manifest.Diagnostics = context.Diagnostics.Items;
                    return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                }

                foreach (var pending in execution.Facts)
                {
                    if (pending.ConceptName is null || pending.EntityIdentifier is null || pending.Period is null)
                    {
                        context.Diagnostics.Error(DiagnosticCodes.XbrPeriodInvalid,
                            DiagnosticSubsystem.XBRL, DiagnosticStage.Serialization,
                            "Fact is missing required aspects (concept, entity, or period).", operationId: operationId);
                        manifest.Outcome = RunOutcome.InvalidMapping;
                        manifest.CompletedUtc = DateTimeOffset.UtcNow;
                        manifest.Diagnostics = context.Diagnostics.Items;
                        return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
                    }
                    store.Add(new XbrlFact
                    {
                        ConceptName = pending.ConceptName,
                        EntityScheme = pending.EntityScheme!,
                        EntityIdentifier = pending.EntityIdentifier,
                        Period = pending.Period,
                        Unit = pending.Unit,
                        Value = pending.Value,
                        TextValue = pending.TextValue,
                        IsNil = pending.IsNil,
                        Decimals = pending.Decimals,
                        Language = pending.Language,
                        Dimensions = pending.Dimensions,
                        ContributingSources = pending.Sources
                    }, _executionBudget.MaxFactBytes);
                }

                context.Provenance.Clear();
                context.LoadedSources.Remove(binding.BindingId);
                progress?.Report(new MappingJobProgress("Executing bounded row batch", checked((int)Math.Min(sourceRows, int.MaxValue))));
            }

            if (sourceRows <= _executionBudget.InMemorySourceRecordLimit
                && probeBytes <= _executionBudget.MaxProbeBytes)
                throw new InvalidOperationException("The large-run path was selected without exceeding its probe threshold.");
            context.Provenance.Clear();
            context.LoadedSources.Remove(binding.BindingId);
            if (context.Diagnostics.HasBlockingErrors)
            {
                manifest.Outcome = RunOutcome.InvalidMapping;
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }
            if (store.Count == 0)
            {
                context.Diagnostics.Error(DiagnosticCodes.XbrRequiredCheckMissing,
                    DiagnosticSubsystem.XBRL, DiagnosticStage.Validation,
                    "The mapping produced no XBRL facts. Verify the selected source rows and reporting-year filters before validation.",
                    operationId: operationId);
                manifest.Outcome = RunOutcome.IncompleteConfiguration;
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }

            progress?.Report(new MappingJobProgress("Finalizing bounded source spool indexes"));
            store.Finish(cancellationToken);
            progress?.Report(new MappingJobProgress("Computing bounded source digest"));
            var digest = store.ComputeSourceDigest(table, cancellationToken);
            UpdateSqliteInputIdentity(manifest, binding.BindingId, connection, digest, schema, preview: false, hasPrimaryKey);
            var reportPath = Path.Combine(directory, "report.json");
            long reportBytes;
            try
            {
                using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    64 * 1024, FileOptions.SequentialScan);
                reportBytes = OimSerializer.WriteLargeReport(_profile, store.InInputOrder(cancellationToken),
                    store.InSemanticOrderWithOccurrences(cancellationToken),
                    report, _executionBudget.MaxReportBytes, _executionBudget.MaxNamespaces, cancellationToken);
                report.Flush(flushToDisk: true);
            }
            catch (XbrlExportException ex)
            {
                context.Diagnostics.Error(ex.Code, DiagnosticSubsystem.XBRL, DiagnosticStage.Serialization,
                    ex.Message, operationId: operationId);
                manifest.Outcome = RunOutcome.InvalidMapping;
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }
            catch (InvalidDataException ex)
            {
                context.Diagnostics.Error(DiagnosticCodes.IoNumberOutOfBudget,
                    DiagnosticSubsystem.XBRL, DiagnosticStage.Serialization,
                    ex.Message, operationId: operationId);
                manifest.Outcome = RunOutcome.IncompleteConfiguration;
                manifest.CompletedUtc = DateTimeOffset.UtcNow;
                manifest.Diagnostics = context.Diagnostics.Items;
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }

            using (var report = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan))
            {
                var actualDigest = RunManifest.ComputeSha256(report);
                var actualBytes = new FileInfo(reportPath).Length;
                if (actualBytes != reportBytes)
                    throw new InvalidDataException("The streamed report byte count changed before worker validation.");
                artifact = new LargeRunArtifact(directory, store, reportPath, actualDigest, actualBytes);
                store = null;
            }

            var validatePayload = JsonSerializer.SerializeToElement(new
            {
                xbrlJsonPath = artifact.XbrlJsonPath,
                sha256 = artifact.Sha256
            });
            var request = new WorkerRequest(WorkerOperations.ValidateReport,
                OperationId: operationId + "-validate", JobId: runId,
                ProfileDigest: _profile.Digest, DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(2),
                Payload: validatePayload)
            {
                ValidationRequirements = CreateValidationRequirements()
            };
            progress?.Report(new MappingJobProgress("Validating staged report artifact in validation worker"));
            var workerResponse = await _worker.RequestAsync(request, cancellationToken).ConfigureAwait(false);
            var workerDiagnostics = (workerResponse.Diagnostics ?? Array.Empty<Diagnostic>()).ToList();
            var completion = ValidationCompletionReportValidator.Evaluate(workerResponse.Result, _profile);
            var workerBlocking = workerDiagnostics.Any(diagnostic => diagnostic.IsBlocking);
            manifest.Outcome = workerResponse.Status switch
            {
                _ when completion.State == ValidationCompletionState.Failed => RunOutcome.InvalidMapping,
                WorkerStatus.Success when workerBlocking => RunOutcome.InvalidMapping,
                WorkerStatus.Success when completion.State == ValidationCompletionState.Incomplete
                    => RunOutcome.IncompleteConfiguration,
                WorkerStatus.Success
                    => context.Diagnostics.Items.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
                        ? RunOutcome.SuccessWithWarnings : RunOutcome.Success,
                WorkerStatus.Cancelled => RunOutcome.Cancelled,
                WorkerStatus.IncompleteValidation => RunOutcome.IncompleteConfiguration,
                WorkerStatus.TimedOut or WorkerStatus.Crash => RunOutcome.OperationalFailure,
                _ => RunOutcome.InvalidMapping
            };
            if (completion.State == ValidationCompletionState.Failed
                || (completion.State == ValidationCompletionState.Incomplete
                    && workerResponse.Status is (WorkerStatus.Success or WorkerStatus.IncompleteValidation)))
                workerDiagnostics.Add(CompletionDiagnostic(completion, operationId));
            if (workerResponse.Status is WorkerStatus.TimedOut or WorkerStatus.Crash
                && !workerDiagnostics.Any(diagnostic => diagnostic.Code is DiagnosticCodes.WkrTimeout or DiagnosticCodes.WkrCrash))
            {
                var timeout = workerResponse.Status == WorkerStatus.TimedOut;
                workerDiagnostics.Add(new Diagnostic
                {
                    Code = timeout ? DiagnosticCodes.WkrTimeout : DiagnosticCodes.WkrCrash,
                    Subsystem = DiagnosticSubsystem.Worker,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Worker,
                    IsBlocking = true,
                    Message = timeout
                        ? "Worker exceeded its per-request deadline and did not respond."
                        : "Worker terminated unexpectedly; no response was received.",
                    OperationId = operationId
                });
            }
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items.Concat(workerDiagnostics).ToArray();
            if (workerResponse.Status != WorkerStatus.Success || workerBlocking
                || completion.State != ValidationCompletionState.Complete)
            {
                manifest.Outputs = Array.Empty<OutputIdentity>();
                progress?.Report(new MappingJobProgress($"Run ended: {manifest.Outcome}"));
                return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
            }
            manifest.ValidatedArtifactDigest = artifact.Sha256;
            manifest.Outputs = new[]
            {
                new OutputIdentity(artifact.XbrlJsonPath, artifact.Sha256, artifact.Bytes, "xbrl-json")
            };
            progress?.Report(new MappingJobProgress($"Run ended: {manifest.Outcome}"));
            ownershipTransferred = true;
            return JobResult.FromManifest(manifest, artifact.Facts, "", context.SampledTotals, artifact);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            manifest.Outcome = RunOutcome.Cancelled;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            progress?.Report(new MappingJobProgress("Run ended: Cancelled"));
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        catch (AdapterException ex)
        {
            context.Diagnostics.Add(ex.ToDiagnostic(binding.BindingId, operationId));
            manifest.Outcome = RunOutcome.DataError;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        catch (LargeRunBudgetExceededException ex)
        {
            context.Diagnostics.Error(DiagnosticCodes.IoNumberOutOfBudget,
                DiagnosticSubsystem.Mapping, DiagnosticStage.Execution,
                ex.Message, operationId: operationId);
            manifest.Outcome = RunOutcome.IncompleteConfiguration;
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            manifest.Diagnostics = context.Diagnostics.Items;
            return JobResult.FromManifest(manifest, Array.Empty<XbrlFact>(), "");
        }
        finally
        {
            if (!ownershipTransferred)
            {
                artifact?.Dispose();
                store?.Dispose();
                try { Directory.Delete(directory, recursive: true); }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void UpdateSqliteInputIdentity(RunManifest manifest, string bindingId,
        SourceConnection connection, string digest, ISourceSchema schema, bool preview, bool? hasPrimaryKey = null)
    {
        var declaredPrimaryKey = hasPrimaryKey ?? schema.Columns.Any(column => column.PrimaryKeyOrdinal > 0);
        manifest.Inputs = manifest.Inputs.Select(input => input.SourceId == bindingId
            ? input with
            {
                Kind = preview ? "sqlite-preview" : "sqlite-table",
                Digest = digest,
                IsReplayable = false,
                RowIdentityStrategy = declaredPrimaryKey
                    ? SqliteSourceAdapter.StablePrimaryKeyIdentityStrategy
                    : SqliteSourceAdapter.PositionalIdentityStrategy,
                SnapshotStrategy = "sqlite-read-transaction"
            }
            : input).ToArray();
    }

    private static void AddSqliteRowIdentityWarning(RunContext context, ISourceSchema schema, string operationId)
    {
        if (schema.Columns.Any(column => column.PrimaryKeyOrdinal > 0)) return;
        context.Diagnostics.Warn(DiagnosticCodes.IoStableRowIdentityUnavailable,
            DiagnosticSubsystem.Adapter, DiagnosticStage.Execution,
            "The selected SQLite table has no declared primary key. SQLite does not guarantee row ordering, so positional row provenance may shift between reads even when visible rows are unchanged; use a declared primary key or immutable extract if durable row-level identity is required.",
            operationId: operationId);
    }

    private static void CreatePrivateArtifactDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private bool IsBoundedInMemorySource(SourceConnection connection, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(connection.Path))
        {
            reason = $"Source '{connection.SourceId}' has no file path that can be size-checked before schema inspection.";
            return false;
        }
        try
        {
            var path = connection.Path;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                reason = $"Source '{connection.SourceId}' is missing and cannot be size-checked before schema inspection.";
                return false;
            }
            if (info.Length > _executionBudget.MaxInMemorySourceBytes)
            {
                reason = $"Source '{connection.SourceId}' exceeds the {_executionBudget.MaxInMemorySourceBytes} byte in-memory source limit; use a streamed SQLite source for large runs.";
                return false;
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(connection.Kind, "excel"))
            {
                using var file = File.OpenRead(path);
                using var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read);
                long expandedBytes = 0;
                foreach (var entry in archive.Entries)
                {
                    expandedBytes = checked(expandedBytes + entry.Length);
                    if (expandedBytes > _executionBudget.MaxExpandedInMemorySourceBytes)
                    {
                        reason = $"Excel source '{connection.SourceId}' exceeds the {_executionBudget.MaxExpandedInMemorySourceBytes} byte expanded in-memory source limit.";
                        return false;
                    }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            reason = $"Source '{connection.SourceId}' could not be size-checked safely before schema inspection: {ex.Message}";
            return false;
        }
    }

    private ValidationRequirements CreateValidationRequirements() => new(
        _profile.ProfileId,
        _profile.Digest,
        _profile.RequiredChecks.Order(StringComparer.Ordinal).ToArray(),
        _profile.RequiredPlugins.Order(StringComparer.Ordinal).ToArray());

    private static Diagnostic CompletionDiagnostic(
        ValidationCompletionAssessment assessment,
        string operationId)
    {
        var code = assessment.State == ValidationCompletionState.Failed
            ? DiagnosticCodes.XbrValidationFailed
            : assessment.IsPlugin
                ? DiagnosticCodes.XbrWorkerUnavailable
                : assessment.Identifier is not null
                    ? DiagnosticCodes.XbrRequiredCheckMissing
                    : DiagnosticCodes.XbrValidationIncomplete;
        return new Diagnostic
        {
            Code = code,
            Subsystem = DiagnosticSubsystem.Worker,
            Severity = DiagnosticSeverity.Error,
            Stage = DiagnosticStage.Validation,
            IsBlocking = true,
            Message = assessment.Detail ?? "Required profile validation did not provide completion evidence.",
            OperationId = operationId,
            RuleIdentifier = assessment.Identifier
        };
    }

    /// <summary>
    /// Validate the exact bytes reread from the publisher's private staging file. This
    /// is a second profile-worker adjudication at the final publication boundary; the
    /// returned digest is checked again by <see cref="PublicationService"/> before the
    /// staged file can replace the destination.
    /// </summary>
    public async Task<string> ValidateStagedArtifactAsync(
        string stagedPath,
        string destinationPath,
        JobResult result,
        CancellationToken cancellationToken = default)
    {
        var manifest = result.Manifest;
        var output = manifest.Outputs.SingleOrDefault(identity => identity.Kind == "xbrl-json");
        if (output is null)
            throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                "The completed run has no xBRL-JSON output identity to validate.",
                manifest.RunId, destinationPath);

        string digest;
        JsonElement workerPayload;
        string? workerScratch = null;
        IReadOnlyList<XbrlFact>? localSyntheticStagedFacts = null;
        if (result.LargeArtifact is not null)
        {
            try
            {
                await using var stagedStream = new FileStream(stagedPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                digest = RunManifest.ComputeSha256(stagedStream);
                var stagedBytes = new FileInfo(stagedPath).Length;
                if (!StringComparer.Ordinal.Equals(output.Digest, digest) || output.Bytes != stagedBytes)
                    throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                        "Staged report bytes do not match the artifact recorded by the completed run.",
                        manifest.RunId, destinationPath);

                workerScratch = Path.Combine(Path.GetTempPath(), "xms-large-stage-" + Guid.NewGuid().ToString("N"));
                CreatePrivateArtifactDirectory(workerScratch);
                var workerPath = Path.Combine(workerScratch, "report.json");
                await using var source = new FileStream(stagedPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var target = new FileStream(workerPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied = checked(copied + read);
                    if (copied > output.Bytes)
                        throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                            "Staged report grew while it was copied for profile validation.", manifest.RunId, destinationPath);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (copied != output.Bytes)
                    throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                        "Staged report changed while it was copied for profile validation.", manifest.RunId, destinationPath);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                workerPayload = JsonSerializer.SerializeToElement(new { xbrlJsonPath = workerPath, sha256 = digest });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (workerScratch is not null) TryDeleteDirectory(workerScratch);
                throw;
            }
            catch (PublicationException)
            {
                if (workerScratch is not null) TryDeleteDirectory(workerScratch);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (workerScratch is not null) TryDeleteDirectory(workerScratch);
                throw new PublicationException(DiagnosticCodes.PubStagingFailed,
                    $"Could not stream the staged artifact for profile validation: {ex.Message}",
                    manifest.RunId, destinationPath);
            }
        }
        else
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(stagedPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PublicationException(DiagnosticCodes.PubStagingFailed,
                    $"Could not read the staged artifact for profile validation: {ex.Message}",
                    manifest.RunId, destinationPath);
            }
            digest = RunManifest.ComputeSha256(bytes);
            if (!StringComparer.Ordinal.Equals(output.Digest, digest) || output.Bytes != bytes.Length)
                throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                    "Staged report bytes do not match the artifact recorded by the completed run.",
                    manifest.RunId, destinationPath);
            string reportJson;
            try
            {
                reportJson = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                    $"Staged xBRL-JSON is not valid UTF-8: {ex.Message}", manifest.RunId, destinationPath);
            }
            if (StringComparer.Ordinal.Equals(_profile.ProfileId, SyntheticProfile.ProfileId))
            {
                try
                {
                    // Adjudicate exactly what the worker and publisher are validating. The
                    // in-memory JobResult facts are useful for the run but are not the staged
                    // artifact's trust boundary.
                    localSyntheticStagedFacts = OimSerializer.ReadFacts(reportJson);
                }
                catch (Exception ex) when (ex is JsonException or XbrlExportException)
                {
                    throw new PublicationException(DiagnosticCodes.XbrValidationIncomplete,
                        $"Staged xBRL-JSON could not be evaluated by the local synthetic rules: {ex.Message}",
                        manifest.RunId, destinationPath);
                }
            }
            workerPayload = JsonSerializer.SerializeToElement(reportJson);
        }

        if (!StringComparer.Ordinal.Equals(output.Digest, digest))
            throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                "Staged report bytes do not match the artifact recorded by the completed run.", manifest.RunId, destinationPath);

        var operationId = manifest.OperationId + "-staged-" + Guid.NewGuid().ToString("N");
        WorkerResponse response;
        try
        {
            response = await _worker.RequestAsync(new WorkerRequest(
                WorkerOperations.ValidateReport,
                OperationId: operationId,
                JobId: manifest.RunId,
                ProfileDigest: _profile.Digest,
                DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(2),
                Payload: workerPayload)
            {
                ValidationRequirements = CreateValidationRequirements()
            }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new PublicationException(DiagnosticCodes.WkrTimeout,
                $"Staged artifact validation was interrupted: {ex.Message}", manifest.RunId, destinationPath);
        }
        catch (Exception ex)
        {
            throw new PublicationException(DiagnosticCodes.WkrCrash,
                $"Validation worker failed while checking the staged artifact: {ex.Message}",
                manifest.RunId, destinationPath);
        }
        finally
        {
            if (workerScratch is not null) TryDeleteDirectory(workerScratch);
        }

        if (!StringComparer.Ordinal.Equals(response.OperationId, operationId))
            throw new PublicationException(DiagnosticCodes.XbrValidationIncomplete,
                "Validation worker response did not match the staged-artifact request.",
                manifest.RunId, destinationPath);

        var diagnostics = response.Diagnostics ?? Array.Empty<Diagnostic>();
        var blocking = diagnostics.FirstOrDefault(diagnostic => diagnostic.IsBlocking);
        var completion = ValidationCompletionReportValidator.Evaluate(
            response.Result, _profile, localSyntheticStagedFacts);
        if (completion.State == ValidationCompletionState.Failed)
        {
            var completionDiagnostic = CompletionDiagnostic(completion, operationId);
            manifest.Diagnostics = manifest.Diagnostics.Concat(diagnostics).Append(completionDiagnostic).ToArray();
            throw new PublicationException(completionDiagnostic.Code,
                $"The staged report failed profile validation: {completion.Detail}", manifest.RunId, destinationPath);
        }
        if (response.Status == WorkerStatus.Success && blocking is null
            && completion.State == ValidationCompletionState.Complete)
        {
            manifest.ValidatedArtifactDigest = digest;
            manifest.Diagnostics = manifest.Diagnostics.Concat(diagnostics).ToArray();
            return digest;
        }

        if (response.Status == WorkerStatus.Cancelled)
            throw new OperationCanceledException(
                response.Error ?? "The validation worker cancelled staged-artifact validation.",
                cancellationToken);

        var code = response.Status switch
        {
            WorkerStatus.TimedOut => DiagnosticCodes.WkrTimeout,
            WorkerStatus.Crash => DiagnosticCodes.WkrCrash,
            WorkerStatus.IncompleteValidation => diagnostics.FirstOrDefault(diagnostic =>
                diagnostic.IsBlocking && diagnostic.Code is DiagnosticCodes.XbrWorkerUnavailable
                    or DiagnosticCodes.XbrValidationIncomplete)?.Code ?? DiagnosticCodes.XbrValidationIncomplete,
            WorkerStatus.Success or WorkerStatus.DomainFailure => blocking?.Code ?? DiagnosticCodes.XbrValidationFailed,
            _ => DiagnosticCodes.XbrValidationIncomplete
        };
        if (response.Status is (WorkerStatus.Success or WorkerStatus.IncompleteValidation)
            && completion.State != ValidationCompletionState.Complete)
        {
            var completionDiagnostic = CompletionDiagnostic(completion, operationId);
            manifest.Diagnostics = manifest.Diagnostics.Concat(diagnostics).Append(completionDiagnostic).ToArray();
            if (response.Status == WorkerStatus.Success || code == DiagnosticCodes.XbrValidationIncomplete)
                code = completionDiagnostic.Code;
        }
        var detail = blocking?.Message ?? completion.Detail ?? response.Error ??
            $"Validation worker ended with status '{response.Status}'.";
        throw new PublicationException(code,
            $"The staged report failed profile validation: {detail}", manifest.RunId, destinationPath);
    }

    /// <summary>
    /// Compare a successful full-run result with the selected SQLite target using a
    /// read-only destination snapshot. No publication receipt or database mutation is made.
    /// </summary>
    public async Task<SqliteTargetChangePreview> PreviewDestinationChangesAsync(
        MappingProject project,
        JobResult result,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var prepared = PrepareSqliteDestination(project, result, workspaceRoot);
        SqliteTargetTableSchema schema;
        try
        {
            schema = await new SqliteDestinationInspector().InspectTableAsync(
                prepared.Path, prepared.Plan.Table, cancellationToken);
        }
        catch (AdapterException ex)
        {
            return BlockedDestinationPreview(prepared.Records.Count, ex.Message);
        }

        var schemaDiagnostics = DestinationPlanValidator.ValidateSchema(prepared.Plan, schema);
        if (schemaDiagnostics.HasBlockingErrors)
            return BlockedDestinationPreview(prepared.Records.Count,
                string.Join(" ", schemaDiagnostics.Items.Select(diagnostic => diagnostic.Message)));

        var destination = new SourceConnection("destination", DestinationPlanValidator.SupportedKind,
            prepared.Path, new Dictionary<string, string>(StringComparer.Ordinal) { ["table"] = prepared.Plan.Table });
        return await new SqliteTargetPublisher().PreviewAsync(
            destination, prepared.Records, prepared.Plan.BusinessKey, cancellationToken: cancellationToken);
    }

    private static SqliteTargetChangePreview BlockedDestinationPreview(int candidateRows, string discrepancy)
        => new(candidateRows, 0, 0, 0, 0, 0, SqliteTargetPublisher.DefaultPreviewSampleLimit,
            Array.Empty<SqliteTargetChangeSample>(), new[] { discrepancy });

    /// <summary>
    /// Publish a previously validated, full run through the versioned SQLite
    /// destination plan. The call rechecks approval and run identity at the write
    /// boundary; callers cannot publish preview or stale project output.
    /// </summary>
    public async Task<SqlitePublishReceipt> PublishDestinationAsync(
        MappingProject project,
        JobResult result,
        string workspaceRoot,
        bool allowSyntheticProfile,
        CancellationToken cancellationToken = default)
    {
        var prepared = PrepareSqliteDestination(project, result, workspaceRoot);
        var plan = prepared.Plan;
        var manifest = result.Manifest;
        var gate = PublicationGate.Evaluate(project, _profile, allowSyntheticProfile, manifest.OperationId);
        if (gate.HasBlockingErrors)
        {
            var first = gate.Items.First(diagnostic => diagnostic.IsBlocking);
            throw new PublicationException(first.Code,
                string.Join(Environment.NewLine, gate.Items.Select(diagnostic => diagnostic.Message)),
                manifest.RunId, plan.Locator);
        }

        var operationFingerprint = ComputeDestinationOperationFingerprint(
            project, result, plan, prepared.Path, prepared.ReportDigest);
        var operationKey = "xms-" + operationFingerprint["sha256:".Length..];
        var destination = new SourceConnection(
            "destination",
            DestinationPlanValidator.SupportedKind,
            prepared.Path,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["table"] = plan.Table });

        var receipt = await new SqliteTargetPublisher().PublishAsync(
            destination,
            prepared.Records,
            plan.BusinessKey,
            operationKey,
            operationFingerprint,
            cancellationToken);

        manifest.Outputs = manifest.Outputs
            .Where(output => output.Kind != "db-receipt" || output.Path != prepared.Path)
            .Append(new OutputIdentity(prepared.Path, receipt.IntentFingerprint, 0, "db-receipt")
            {
                RowsWritten = receipt.WrittenRows,
                OperationKey = receipt.OperationKey,
                Table = plan.Table
            })
            .ToArray();
        manifest.CompletedUtc = DateTimeOffset.UtcNow;
        return receipt;
    }

    private PreparedSqliteDestination PrepareSqliteDestination(
        MappingProject project,
        JobResult result,
        string workspaceRoot)
    {
        var plan = project.Destination ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            "The project has no configured destination.", DiagnosticStage.Publication);
        var planDiagnostics = DestinationPlanValidator.Validate(plan, result.Manifest.OperationId);
        if (planDiagnostics.HasBlockingErrors)
        {
            var first = planDiagnostics.Items.First(diagnostic => diagnostic.IsBlocking);
            throw new AdapterException(first.Code,
                string.Join(Environment.NewLine, planDiagnostics.Items.Select(diagnostic => diagnostic.Message)),
                DiagnosticStage.Publication);
        }
        if (!StringComparer.Ordinal.Equals(plan.Kind, DestinationPlanValidator.SupportedKind))
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                "This operation requires a SQLite fact-table destination.", DiagnosticStage.Publication);
        if (!result.IsPublishable)
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                "A destination change preview requires a successful full run; partial previews cannot be compared.",
                DiagnosticStage.Validation);

        var manifest = result.Manifest;
        var semanticDigest = project.ComputeSemanticDigest();
        if (manifest.ProjectId != project.ProjectId
            || manifest.SemanticRevision != project.SemanticRevision
            || !StringComparer.Ordinal.Equals(manifest.ProjectSemanticDigest, semanticDigest)
            || !StringComparer.Ordinal.Equals(manifest.ProfileDigest, _profile.Digest))
        {
            throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                "Run evidence no longer matches the project semantic revision and filing profile.",
                manifest.RunId, plan.Locator);
        }

        string expectedJson;
        try
        {
            expectedJson = OimSerializer.Serialize(_profile, result.Facts);
        }
        catch (XbrlExportException ex)
        {
            throw new PublicationException(ex.Code, ex.Message, manifest.RunId, plan.Locator);
        }

        var jsonBytes = Encoding.UTF8.GetBytes(result.XbrlJson);
        var jsonDigest = RunManifest.ComputeSha256(jsonBytes);
        if (!StringComparer.Ordinal.Equals(expectedJson, result.XbrlJson)
            || !manifest.Outputs.Any(output => output.Kind == "xbrl-json"
                && output.Digest == jsonDigest
                && output.Bytes == jsonBytes.Length))
        {
            throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                "The mapped facts do not match the report artifact recorded by the run.",
                manifest.RunId, plan.Locator);
        }

        var root = Path.GetFullPath(workspaceRoot);
        var destinationPath = PathGuard.EnsureWithin(root,
            Path.IsPathRooted(plan.Locator) ? plan.Locator : Path.Combine(root, plan.Locator));
        if (!File.Exists(destinationPath))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "SQLite destination database must already exist; create or select an existing database file.",
                DiagnosticStage.Validation);

        var records = XbrlFactRecordMapper.ToRecords(result.Facts, _profile.Digest);
        if (records.Count == 0)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "An empty fact set cannot be published to the SQLite destination.",
                DiagnosticStage.Validation);

        return new PreparedSqliteDestination(plan, destinationPath, records, jsonDigest);
    }

    private sealed record PreparedSqliteDestination(
        DestinationPlan Plan,
        string Path,
        IReadOnlyList<DataRecord> Records,
        string ReportDigest);

    /// <summary>
    /// Publish mapped XBRL fact rows through an explicitly selected staged file adapter.
    /// This path accepts only the versioned CSV/XLSX/JSON/XML contracts and rechecks
    /// approval, run identity and the xBRL-JSON evidence before replacing a file.
    /// </summary>
    public async Task<OutputIdentity> PublishFileDestinationAsync(
        MappingProject project,
        JobResult result,
        string workspaceRoot,
        bool allowSyntheticProfile,
        CancellationToken cancellationToken = default)
    {
        var plan = project.Destination ?? throw new AdapterException(
            DiagnosticCodes.IoUnsupportedOperation,
            "The project has no configured destination.", DiagnosticStage.Publication);
        var planDiagnostics = DestinationPlanValidator.Validate(plan, result.Manifest.OperationId);
        if (planDiagnostics.HasBlockingErrors)
        {
            var first = planDiagnostics.Items.First(diagnostic => diagnostic.IsBlocking);
            throw new AdapterException(first.Code,
                string.Join(Environment.NewLine, planDiagnostics.Items.Select(diagnostic => diagnostic.Message)),
                DiagnosticStage.Publication);
        }
        if (!DestinationPlanValidator.IsFileKind(plan.Kind))
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                $"Destination kind '{plan.Kind}' is not a file output contract.", DiagnosticStage.Publication);
        if (!result.IsPublishable)
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                "Only a successful full run can be written to a file destination; previews are never published.",
                DiagnosticStage.Publication);

        var manifest = result.Manifest;
        var semanticDigest = project.ComputeSemanticDigest();
        if (manifest.ProjectId != project.ProjectId
            || manifest.SemanticRevision != project.SemanticRevision
            || !StringComparer.Ordinal.Equals(manifest.ProjectSemanticDigest, semanticDigest)
            || !StringComparer.Ordinal.Equals(manifest.ProfileDigest, _profile.Digest))
        {
            throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                "Run evidence no longer matches the project semantic revision and filing profile.",
                manifest.RunId, plan.Locator);
        }

        var gate = PublicationGate.Evaluate(project, _profile, allowSyntheticProfile, manifest.OperationId);
        if (gate.HasBlockingErrors)
        {
            var first = gate.Items.First(diagnostic => diagnostic.IsBlocking);
            throw new PublicationException(first.Code,
                string.Join(Environment.NewLine, gate.Items.Select(diagnostic => diagnostic.Message)),
                manifest.RunId, plan.Locator);
        }

        string expectedJson;
        try
        {
            expectedJson = OimSerializer.Serialize(_profile, result.Facts);
        }
        catch (XbrlExportException ex)
        {
            throw new PublicationException(ex.Code, ex.Message, manifest.RunId, plan.Locator);
        }
        var jsonBytes = Encoding.UTF8.GetBytes(result.XbrlJson);
        var jsonDigest = RunManifest.ComputeSha256(jsonBytes);
        if (!StringComparer.Ordinal.Equals(expectedJson, result.XbrlJson)
            || !manifest.Outputs.Any(output => output.Kind == "xbrl-json"
                && output.Digest == jsonDigest
                && output.Bytes == jsonBytes.Length))
        {
            throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                "The mapped facts do not match the report artifact recorded by the run.",
                manifest.RunId, plan.Locator);
        }

        var root = Path.GetFullPath(workspaceRoot);
        var destinationPath = PathGuard.EnsureWithin(root,
            Path.IsPathRooted(plan.Locator) ? plan.Locator : Path.Combine(root, plan.Locator));
        var pathDiagnostics = DestinationPlanValidator.ValidateFileDestinationPath(destinationPath);
        if (pathDiagnostics.HasBlockingErrors)
        {
            var first = pathDiagnostics.Items.First(diagnostic => diagnostic.IsBlocking);
            throw new AdapterException(first.Code,
                string.Join(Environment.NewLine, pathDiagnostics.Items.Select(diagnostic => diagnostic.Message)),
                DiagnosticStage.Publication);
        }
        var directory = Path.GetDirectoryName(destinationPath)!;
        if (!Directory.Exists(directory))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "The file destination directory must already exist.", DiagnosticStage.Publication);

        var records = XbrlFactRecordMapper.ToRecords(result.Facts, _profile.Digest);
        var headers = XbrlFactRecordMapper.OutputColumns;
        var connection = new SourceConnection("destination", plan.Kind, destinationPath,
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = BuildTargetAdapter(connection);
        var payload = adapter switch
        {
            CsvAdapter csv => csv.WriteRecords(headers, records),
            ExcelOutputAdapter excel => excel.WriteWorkbook(headers,
                records.Select(record => headers.Select(header => ToExcelCell(record.Fields[header])))),
            JsonOutputAdapter json => json.Write(records),
            XmlOutputAdapter xml => xml.Write(records),
            _ => throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                $"No serializer is registered for file destination '{plan.Kind}'.", DiagnosticStage.Publication)
        };

        using var destinationLock = await DestinationLock.AcquireAsync(
            destinationPath, TimeSpan.FromSeconds(30), cancellationToken);
        var originalDigest = destinationLock.DestinationDigestAtAcquire;
        var backupPath = destinationPath + $".xms-rollback-{manifest.RunId}";
        if (File.Exists(backupPath))
            throw new PublicationException(DiagnosticCodes.PubPartialFailure,
                $"A recovery copy from an earlier publication attempt already exists: {backupPath}.",
                manifest.RunId, destinationPath);
        if (originalDigest is not null)
        {
            try
            {
                File.Copy(destinationPath, backupPath, overwrite: false);
                if (!StringComparer.Ordinal.Equals(originalDigest, CurrentFileDigest(backupPath)))
                    throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                        "The rollback copy does not match the destination bytes captured at lock acquisition.",
                        manifest.RunId, destinationPath);
            }
            catch
            {
                TryDeleteFile(backupPath);
                throw;
            }
        }
        string? stagedPath = null;
        string? stagedDigest = null;
        var preserveBackup = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(originalDigest, CurrentFileDigest(destinationPath)))
                throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                    "The destination changed while publication was being prepared.", manifest.RunId, destinationPath);

            stagedPath = await adapter.StageArtifactAsync(connection, payload,
                Path.GetExtension(destinationPath), cancellationToken);
            stagedDigest = DestinationLock.DigestFile(stagedPath);
            cancellationToken.ThrowIfCancellationRequested();

            if (!StringComparer.Ordinal.Equals(originalDigest, CurrentFileDigest(destinationPath)))
                throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                    "The destination changed while the staged artifact was being verified.", manifest.RunId, destinationPath);

            var output = await adapter.PublishAsync(stagedPath, connection, cancellationToken);
            var finalDigest = CurrentFileDigest(destinationPath);
            if (finalDigest is null || !StringComparer.Ordinal.Equals(output.Digest, finalDigest))
                throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                    "Published file bytes do not match the staged adapter receipt.", manifest.RunId, destinationPath);

            manifest.Outputs = manifest.Outputs
                .Where(existing => !StringComparer.OrdinalIgnoreCase.Equals(existing.Path, destinationPath))
                .Append(output)
                .ToArray();
            manifest.CompletedUtc = DateTimeOffset.UtcNow;
            return output;
        }
        catch (Exception publishError)
        {
            var currentDigest = CurrentFileDigest(destinationPath);
            if (!StringComparer.Ordinal.Equals(originalDigest, currentDigest))
            {
                var stagedArtifactMoved = stagedPath is not null && !File.Exists(stagedPath);
                if (!stagedArtifactMoved || stagedDigest is null
                    || !StringComparer.Ordinal.Equals(stagedDigest, currentDigest))
                {
                    // Another writer changed the file while we held the advisory lock.
                    // Keep the saved prior copy for explicit recovery; never erase a
                    // third party's bytes to make the failed operation look clean.
                    preserveBackup = File.Exists(backupPath);
                }
                else
                {
                    try
                    {
                        if (originalDigest is null)
                        {
                            if (File.Exists(destinationPath)) File.Delete(destinationPath);
                        }
                        else
                            File.Move(backupPath, destinationPath, overwrite: true);
                    }
                    catch (Exception rollbackError)
                    {
                        preserveBackup = File.Exists(backupPath);
                        var recovery = preserveBackup
                            ? $"Recovery copy: {backupPath}. "
                            : $"No previous file was present; reconcile the current destination at {destinationPath}. ";
                        throw new PublicationException(DiagnosticCodes.PubPartialFailure,
                            $"File publication failed and rollback could not restore the prior output. {recovery}Publication: {publishError.Message}; rollback: {rollbackError.Message}",
                            manifest.RunId, destinationPath);
                    }
                }
            }
            throw;
        }
        finally
        {
            if (stagedPath is not null) TryDeleteFile(stagedPath);
            if (!preserveBackup) TryDeleteFile(backupPath);
        }
    }

    private static string? CurrentFileDigest(string path)
        => File.Exists(path) ? DestinationLock.DigestFile(path) : null;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* keep the primary publication outcome */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* keep the primary publication outcome */ }
    }

    private static object? ToExcelCell(DataValue value)
        => value.Presence switch
        {
            ValuePresence.Missing or ValuePresence.ExplicitNil => null,
            ValuePresence.Blank => string.Empty,
            _ when value.Raw is decimal number => number.ToString(CultureInfo.InvariantCulture),
            _ when value.Raw is DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ when value.Raw is DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ => value.Raw
        };

    private static string ComputeDestinationOperationFingerprint(
        MappingProject project,
        JobResult result,
        DestinationPlan plan,
        string destinationPath,
        string reportDigest)
    {
        var builder = new StringBuilder();
        AppendIntentPart(builder, project.ProjectId);
        AppendIntentPart(builder, project.SemanticRevision.ToString(CultureInfo.InvariantCulture));
        AppendIntentPart(builder, project.ComputeSemanticDigest());
        AppendIntentPart(builder, result.Manifest.ProfileDigest);
        AppendIntentPart(builder, result.Manifest.EngineVersion);
        AppendIntentPart(builder, destinationPath);
        AppendIntentPart(builder, plan.Table);
        AppendIntentPart(builder, plan.Format);
        AppendIntentPart(builder, plan.WriteMode);
        AppendIntentPart(builder, plan.Compatibility);
        AppendIntentPart(builder, reportDigest);

        builder.Append(plan.BusinessKey.Count).Append(':');
        foreach (var key in plan.BusinessKey)
            AppendIntentPart(builder, key);

        foreach (var input in result.Manifest.Inputs.OrderBy(input => input.SourceId, StringComparer.Ordinal))
        {
            AppendIntentPart(builder, input.SourceId);
            AppendIntentPart(builder, input.Kind);
            AppendIntentPart(builder, input.Digest ?? "");
            AppendIntentPart(builder, input.IsReplayable ? "1" : "0");
            AppendIntentPart(builder, input.RowIdentityStrategy ?? "");
        }

        foreach (var parameter in result.Manifest.FrozenParameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal))
        {
            AppendIntentPart(builder, parameter.Key);
            AppendIntentPart(builder, parameter.Value);
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
        return "sha256:" + digest;
    }

    private static void AppendIntentPart(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);

    private static ISourceAdapter BuildAdapter(SourceConnection conn) => conn.Kind switch
    {
        "excel" => ExcelAdapter.FromConnection(conn),
        "csv" => new CsvAdapter(CsvAdapter.Options.FromConfiguration(conn.Configuration)),
        "json" => new JsonAdapter(JsonAdapter.Options.FromConfiguration(conn.Configuration)),
        "xml" => new XmlAdapter(),
        "xbrl-report" => new XbrlReportAdapter(),
        "pdf-digital" => new PdfSourceAdapter(),
        "sqlite" => new SqliteSourceAdapter(),
        _ => throw new NotSupportedException($"Adapter kind '{conn.Kind}' is not supported in this build.")
    };

    private ISourceAdapter BuildExecutionAdapter(SourceConnection conn)
        => StringComparer.Ordinal.Equals(conn.Kind, "pdf-digital")
            ? new PdfSourceAdapter(new PdfEvidenceStore(_applicationDatabaseFactory()))
            : BuildAdapter(conn);

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
            "json" => new JsonOutputAdapter(),
            "xml" => new XmlOutputAdapter(),
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

    /// <summary>
    /// IO01 capability surfacing. The designer asks this before mapping starts so a
    /// read-only connector is visibly disabled as a target and an uncertified operation
    /// is described with a stable reason. The gate never throws: the desktop pane uses
    /// <see cref="TargetSuitability.Reason"/> to decorate unsupported options. SQLite
    /// publication is handled by <see cref="SqliteTargetPublisher"/>, not by a file
    /// <see cref="ITargetAdapter"/>, so its supported target capability is described here.
    /// </summary>
    public static TargetSuitability DescribeTargetSuitability(SourceConnection conn)
    {
        if (StringComparer.Ordinal.Equals(conn.Kind, DestinationPlanValidator.SupportedKind))
            return new TargetSuitability(true, conn.Kind, Reason: null, AdapterCapabilities.WriteOnly);

        try
        {
            var adapter = BuildTargetAdapter(conn);
            var caps = adapter.Capabilities;
            if (!caps.AllowsWriteTarget)
                return new TargetSuitability(false, adapter.Kind,
                    $"Adapter '{adapter.Kind}' does not declare a staged write or publication path.", caps);
            return new TargetSuitability(true, adapter.Kind, Reason: null, caps);
        }
        catch (NotSupportedException ex)
        {
            return new TargetSuitability(false, conn.Kind, DescribeUnsupportedTarget(conn.Kind, ex.Message));
        }
        catch (AdapterException ex)
        {
            return new TargetSuitability(false, conn.Kind, ex.Message);
        }
    }

    /// <summary>IO01 read-side surfacing: whether the binding kind exposes schema
    /// inspection, sampling, reading and lineage in the current build.</summary>
    public static SourceSuitability DescribeSourceSuitability(SourceConnection conn)
    {
        ISourceAdapter adapter;
        try { adapter = BuildAdapter(conn); }
        catch (NotSupportedException ex)
        { return new SourceSuitability(false, conn.Kind, DescribeUnsupportedSource(conn.Kind, ex.Message)); }
        catch (AdapterException ex)
        { return new SourceSuitability(false, conn.Kind, ex.Message); }
        var caps = adapter.Capabilities;
        if (!caps.SupportsReading)
            return new SourceSuitability(false, adapter.Kind,
                $"Adapter '{adapter.Kind}' does not declare a read path.", caps);
        return new SourceSuitability(true, adapter.Kind, Reason: null, caps);
    }

    private static string DescribeUnsupportedTarget(string kind, string fallback) => kind switch
    {
        "xbrl-report" => "xBRL-JSON reports are input-only; no report target contract exists.",
        "pdf" or "pdf-digital" => "PDF output is unsupported; no qualified writer exists.",
        "oracle" or "postgresql" or "sqlserver" or "mysql" or "mariadb" =>
            $"The {kind} database target is not implemented or qualified in this build.",
        _ => fallback
    };

    private static string DescribeUnsupportedSource(string kind, string fallback) => kind switch
    {
        "pdf" or "pdf-digital" => "A current approved PDF review manifest and persisted evidence references are required.",
        "oracle" or "postgresql" or "sqlserver" or "mysql" or "mariadb" =>
            $"The {kind} database source is not implemented or qualified in this build.",
        _ => fallback
    };

    private static async Task<IReadOnlyList<InputIdentity>> RecordInputIdentities(
        IReadOnlyDictionary<string, SourceConnection> connections,
        CancellationToken ct,
        long maxInMemorySourceBytes)
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
            if (StringComparer.Ordinal.Equals(kv.Value.Kind, "pdf-digital"))
            {
                var pdfDigest = kv.Value.Path is null
                    ? null
                    : await ComputeBoundedFileDigestAsync(kv.Value.Path,
                        PdfDigitalExtractionOptions.AbsoluteMaxFileBytes, ct);
                list.Add(new InputIdentity(kv.Key, "file", pdfDigest, pdfDigest is not null, kv.Value.Path));
            }
            else if (kv.Value.Path is not null && File.Exists(kv.Value.Path))
            {
                var digest = await ComputeFileDigestWithinLimitAsync(
                    kv.Value.Path, maxInMemorySourceBytes, ct);
                list.Add(new InputIdentity(kv.Key, "file", digest, IsReplayable: digest is not null, kv.Value.Path));
            }
            else
            {
                list.Add(new InputIdentity(kv.Key, kv.Value.Kind, null, IsReplayable: false, kv.Value.Path));
            }

            if (StringComparer.Ordinal.Equals(kv.Value.Kind, "pdf-digital") && kv.Value.Path is not null)
            {
                var reviewPath = PdfSourceAdapter.ResolveReviewManifestPath(kv.Value);
                var reviewDigest = await ComputeBoundedFileDigestAsync(reviewPath,
                    PdfSourceAdapter.MaxReviewManifestBytes, ct);
                list.Add(new InputIdentity(kv.Key + ":review-manifest", "pdf-review-manifest",
                    reviewDigest, reviewDigest is not null, reviewPath));
            }
        }
        return list;
    }

    private static async Task<string?> ComputeFileDigestWithinLimitAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maximumBytes) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (read > maximumBytes - total) return null;
                total += read;
                hash.AppendData(buffer, 0, read);
            }
            if (total != stream.Length || total != info.Length) return null;
            return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<bool> PdfInputsMatchRecordedIdentityAsync(
        string bindingId,
        SourceConnection connection,
        IReadOnlyList<InputIdentity> recordedInputs,
        CancellationToken cancellationToken)
    {
        if (connection.Path is null) return false;
        var sourceInput = recordedInputs.FirstOrDefault(input =>
            StringComparer.Ordinal.Equals(input.SourceId, bindingId));
        var reviewInput = recordedInputs.FirstOrDefault(input =>
            StringComparer.Ordinal.Equals(input.SourceId, bindingId + ":review-manifest"));
        if (sourceInput is null || reviewInput is null
            || !sourceInput.IsReplayable || string.IsNullOrWhiteSpace(sourceInput.Digest)
            || !reviewInput.IsReplayable || string.IsNullOrWhiteSpace(reviewInput.Digest))
            return false;

        var sourceDigest = await ComputeBoundedFileDigestAsync(connection.Path,
            PdfDigitalExtractionOptions.AbsoluteMaxFileBytes, cancellationToken);
        var reviewDigest = await ComputeBoundedFileDigestAsync(
            PdfSourceAdapter.ResolveReviewManifestPath(connection),
            PdfSourceAdapter.MaxReviewManifestBytes, cancellationToken);
        return StringComparer.Ordinal.Equals(sourceDigest, sourceInput.Digest)
            && StringComparer.Ordinal.Equals(reviewDigest, reviewInput.Digest);
    }

    private static async Task<string?> ComputeBoundedFileDigestAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maximumBytes) return null;
                hash.AppendData(buffer, 0, read);
            }
            if (total != stream.Length || total != info.Length) return null;
            return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
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

/// <summary>IO01 result of <see cref="MappingJobService.DescribeTargetSuitability"/>.
/// A false <see cref="Allowed"/> carries a stable reason the desktop renders inline
/// so the analyst sees "this connector cannot be chosen as a target" before mapping.</summary>
public sealed record TargetSuitability(bool Allowed, string Kind, string? Reason,
    AdapterCapabilities? Capabilities = null);

/// <summary>IO01 source-side analogue.</summary>
public sealed record SourceSuitability(bool Allowed, string Kind, string? Reason,
    AdapterCapabilities? Capabilities = null);

public sealed record JobResult(
    RunManifest Manifest,
    IReadOnlyList<XbrlFact> Facts,
    string XbrlJson,
    DiagnosticBag Diagnostics,
    // MAP08: aggregates computed during a preview run, each flagged IsPartial so a
    // reviewer cannot mistake a sampled total for a full-run figure.
    IReadOnlyList<SampledTotal> SampledTotals) : IDisposable
{
    /// <summary>Disk-backed large-run report and facts. Owned by this result.</summary>
    public LargeRunArtifact? LargeArtifact { get; init; }

    public void Dispose() => LargeArtifact?.Dispose();

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
        IReadOnlyList<SampledTotal>? sampledTotals = null, LargeRunArtifact? largeArtifact = null)
    {
        var bag = new DiagnosticBag();
        foreach (var d in manifest.Diagnostics) bag.Add(d);
        return new JobResult(manifest, facts, json, bag, sampledTotals ?? Array.Empty<SampledTotal>())
        {
            LargeArtifact = largeArtifact
        };
    }
}
