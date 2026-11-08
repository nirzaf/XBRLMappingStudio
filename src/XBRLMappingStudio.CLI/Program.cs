using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.Worker;
using XBRLMappingStudio.XBRL;
using ExitCodesLocal = XBRLMappingStudio.CLI.ExitCodes;

// Headless CLI (OPS01). Exposes inspect, validate, preview, execute against the
// same engine and libraries used by the desktop designer. Standard output is
// reserved for machine-readable results when requested; standard error carries
// diagnostics (per section 9 "CLI contract").

if (PdfExtractionWorkerProtocol.IsInternalCommand(args))
    return await PdfExtractionWorkerProtocol.RunAsync();

if (args.Length == 0)
{
    PrintUsage();
    return ExitCodesLocal.IncompleteProfile;
}

var command = args[0];
var rest = args.Skip(1).ToArray();

return await XBRLMappingStudio.CLI.CliLifetime.RunAsync(async cancellationToken =>
{
    return command switch
    {
        "inspect" => Inspect(rest),
        "inspect-source" => await InspectSourceAsync(rest, cancellationToken),
        "snapshot-source" => await SnapshotSourceAsync(rest, cancellationToken),
        "validate" => await ValidateAsync(rest),
        "migrate-project-profile" => MigrateProjectProfile(rest),
        "configure-destination" => ConfigureDestination(rest),
        "taxonomy-search" => TaxonomySearch(rest),
        "install-profile" => await InstallProfileAsync(rest, cancellationToken),
        "validate-artifact" => await ValidateArtifactAsync(rest, cancellationToken),
        "inspect-run-result" => InspectRunResult(rest),
        "reconcile-publication" => await ReconcilePublicationAsync(rest, cancellationToken),
        "support-bundle" => SupportBundleCommand(rest),
        "execute" => await ExecuteAsync(rest, preview: false, cancellationToken),
        "preview" => await ExecuteAsync(rest, preview: true, cancellationToken),
        "--help" or "-h" or "help" => PrintUsage(),
        _ => Unknown(command)
    };
});

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    PrintUsage();
    return ExitCodesLocal.IncompleteProfile;
}

static int PrintUsage()
{
    Console.Error.WriteLine("xbrl-studio - XBRL Mapping Studio headless runner");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  xbrl-studio inspect  <project.xmap>");
    Console.Error.WriteLine("  xbrl-studio inspect-source <project.xmap> [--binding <id>] (SQLite source schema and capabilities)");
    Console.Error.WriteLine("  xbrl-studio snapshot-source <project.xmap> --binding <id> --out <new-bundle-directory> [--workspace-root <dir>] (retains the entire database)");
    Console.Error.WriteLine("  xbrl-studio validate <project.xmap> [--allow-synthetic-profile]");
    Console.Error.WriteLine("  xbrl-studio migrate-project-profile <legacy.xmap> --output <new.xmap> --profile-id <id> --profile-digest <digest> [--allow-synthetic-profile]");
    Console.Error.WriteLine("  xbrl-studio configure-destination <project.xmap> --kind sqlite --path <existing-db> --table <name> --key <column[,column...]> [--write-mode upsert]");
    Console.Error.WriteLine("  xbrl-studio configure-destination <project.xmap> --kind <csv|excel|json|xml> --path <file> --format <contract> [--write-mode replace]");
    Console.Error.WriteLine("  xbrl-studio execute  <project.xmap> --binding <id>=<path> --param <k>=<v> --output <path> [--allow-synthetic-profile]");
    Console.Error.WriteLine("                       [--output-format xbrl-json|xbrl-xml] (default: xbrl-json)");
    Console.Error.WriteLine("                       [--source-snapshot <id>=<snapshot.json>] (replay a retained SQLite source without reading the live database)");
    Console.Error.WriteLine("                       [--manifest <path>] (completed run metadata; early failures may have no manifest)");
    Console.Error.WriteLine("                       [--workspace-root <dir>] [--publish-destination] [--arelle [--python <exe>] [--taxonomy-root <dir>]]");
    Console.Error.WriteLine("                       Combined --output and --publish-destination requires a saved SQLite plan; reconcile partial results by mixed operation key.");
    Console.Error.WriteLine("  xbrl-studio preview  <project.xmap> --binding <id>=<path> --rows 10 [--allow-synthetic-profile] [--arelle [--python <exe>] [--taxonomy-root <dir>]]");
    Console.Error.WriteLine("  xbrl-studio taxonomy-search [--allow-synthetic-profile] [--query <text>] [--lang <tag>] [--page <n>] [--size <n>]");
    Console.Error.WriteLine("  xbrl-studio install-profile <profile-id/version-directory> [--catalog <directory>] (offline; installs disabled)");
    Console.Error.WriteLine("  xbrl-studio validate-artifact <file.xbrl.json> --allow-synthetic-profile");
    Console.Error.WriteLine("  xbrl-studio validate-artifact <file.xbrl.json> --allow-synthetic-profile --arelle [--python <exe>] [--taxonomy-root <dir>] [--package <zip>] [--plugin <name>] [--disclosure-system <name>]");
    Console.Error.WriteLine("  xbrl-studio inspect-run-result <runId>");
    Console.Error.WriteLine("  xbrl-studio reconcile-publication <mixed-operation-key>");
    Console.Error.WriteLine("  xbrl-studio support-bundle <project.xmap> <runId> [--out <path>] [--log <file>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Exit codes: 0=success 1=success-with-warnings 2=invalid-mapping 3=data-error");
    Console.Error.WriteLine("            4=incomplete-profile 5=authorization-failure 6=cancelled 7=operational-failure");
    return ExitCodesLocal.Success;
}

static int MigrateProjectProfile(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("migrate-project-profile requires <legacy-project.xmap>.");
        return ExitCodesLocal.IncompleteProfile;
    }

    string? outputPath = null;
    string? profileId = null;
    string? profileDigest = null;
    var allowSyntheticProfile = false;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--output" when i + 1 < args.Length && outputPath is null:
                outputPath = args[++i];
                break;
            case "--profile-id" when i + 1 < args.Length && profileId is null:
                profileId = args[++i];
                break;
            case "--profile-digest" when i + 1 < args.Length && profileDigest is null:
                profileDigest = args[++i];
                break;
            case "--allow-synthetic-profile":
                allowSyntheticProfile = true;
                break;
            default:
                Console.Error.WriteLine($"Unrecognized, duplicate, or incomplete migrate-project-profile option: {args[i]}");
                return ExitCodesLocal.IncompleteProfile;
        }
    }

    if (string.IsNullOrWhiteSpace(outputPath)
        || string.IsNullOrWhiteSpace(profileId)
        || string.IsNullOrWhiteSpace(profileDigest))
    {
        Console.Error.WriteLine("migrate-project-profile requires --output, --profile-id, and --profile-digest; no profile is inferred.");
        return ExitCodesLocal.IncompleteProfile;
    }

    try
    {
        var service = new LegacyProjectProfileAdoptionService(FilingProfileResolver.FromDefaultCatalog());
        var result = service.Adopt(args[0], outputPath, profileId, profileDigest, allowSyntheticProfile);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            decision = "explicit-profile-adoption-save-as",
            sourcePath = result.SourcePath,
            sourceSha256 = result.SourceSha256,
            destinationPath = result.DestinationPath,
            addedIdentityFields = result.AddedIdentityFields,
            profileId = result.ProfileId,
            profileDigest = result.ProfileDigest,
            projectId = result.NewProjectId,
            semanticDigest = result.NewSemanticDigest,
            approvalsReset = true,
            sourceModified = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return ExitCodesLocal.Success;
    }
    catch (LegacyProjectProfileAdoptionBlockedException ex)
    {
        PrintDiagnostics(ex.Diagnostics);
        return ExitCodesLocal.IncompleteProfile;
    }
    catch (ProjectSchemaException ex)
    {
        Console.Error.WriteLine($"PRJ: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.DataError;
    }
    catch (ProjectSaveException ex)
    {
        Console.Error.WriteLine($"PRJ: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.OperationalFailure;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"PRJ: profile adoption refused: {ex.Message}");
        return ExitCodesLocal.DataError;
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"PRJ: invalid profile adoption request: {ex.Message}");
        return ExitCodesLocal.IncompleteProfile;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"PRJ: profile adoption failed: {ex.Message}");
        return ExitCodesLocal.OperationalFailure;
    }
}

static async Task<int> InstallProfileAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("install-profile requires a local <profile-id>/<version> directory.");
        return ExitCodesLocal.IncompleteProfile;
    }
    if (args[0].StartsWith("-", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("install-profile requires the source directory before any options.");
        return ExitCodesLocal.IncompleteProfile;
    }

    string? catalogDirectory = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--catalog" && i + 1 < args.Length && catalogDirectory is null)
        {
            catalogDirectory = args[++i];
            if (string.IsNullOrWhiteSpace(catalogDirectory))
            {
                Console.Error.WriteLine("--catalog requires a directory.");
                return ExitCodesLocal.IncompleteProfile;
            }
        }
        else
        {
            Console.Error.WriteLine($"Unrecognized or incomplete install-profile option: {args[i]}");
            return ExitCodesLocal.IncompleteProfile;
        }
    }

    try
    {
        var installed = await InstalledFilingProfileCatalog.InstallAsync(
            args[0], catalogDirectory ?? InstalledFilingProfileCatalog.DefaultDirectory, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            installed.Profile.ProfileId,
            installed.Version,
            installed.SchemaVersion,
            installed.Profile.Digest,
            installed.IsEnabled,
            installed.InstallationDirectory
        }, new JsonSerializerOptions { WriteIndented = true }));
        return ExitCodesLocal.Success;
    }
    catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or InvalidOperationException
        or DirectoryNotFoundException or FileNotFoundException)
    {
        Console.Error.WriteLine($"Profile install rejected: {ex.Message}");
        return ExitCodesLocal.DataError;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("CLI_CANCELLED: Operation cancelled; inspect publication receipts before retrying.");
        return ExitCodesLocal.Cancelled;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"Profile install failed: {ex.Message}");
        return ExitCodesLocal.OperationalFailure;
    }
}

static int TaxonomySearch(string[] args)
{
    string query = "";
    string? lang = null;
    int page = 0;
    int size = XBRLMappingStudio.XBRL.TaxonomyIndex.DefaultPageSize;
    bool allowSyntheticProfile = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--allow-synthetic-profile": allowSyntheticProfile = true; break;
            case "--query": if (i + 1 < args.Length) query = args[++i]; break;
            case "--lang": if (i + 1 < args.Length) lang = args[++i]; break;
            case "--page": if (i + 1 < args.Length) page = int.Parse(args[++i]); break;
            case "--size": if (i + 1 < args.Length) size = int.Parse(args[++i]); break;
            default: Console.Error.WriteLine($"Unrecognized option: {args[i]}"); return ExitCodesLocal.IncompleteProfile;
        }
    }

    if (!allowSyntheticProfile)
    {
        Console.Error.WriteLine("Taxonomy search uses the synthetic development profile. Pass --allow-synthetic-profile to continue.");
        return ExitCodesLocal.IncompleteProfile;
    }

    var profile = SyntheticProfile.Build();
    var index = new XBRLMappingStudio.XBRL.TaxonomyIndex(profile);
    var result = index.Search(query, lang, page, size);

    var json = JsonSerializer.Serialize(new
    {
        query,
        lang,
        page = result.Page,
        pageSize = result.PageSize,
        totalCount = result.TotalCount,
        hasNext = result.HasNext,
        items = result.Items.Select(c => new
        {
            qualifiedName = c.QualifiedName,
            localName = c.LocalName,
            label = XBRLMappingStudio.XBRL.TaxonomyIndex.ResolveLabel(c, lang),
            dataType = c.DataType,
            periodType = c.PeriodType,
            isAbstract = c.IsAbstract,
            isFactTargetable = XBRLMappingStudio.XBRL.TaxonomyIndex.IsFactTargetable(c)
        })
    }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    return ExitCodesLocal.Success;
}

// OPS01 completeness: validate the exact bytes of an exported artifact against the
// baseline filing rules, independent of any project state.
static async Task<int> ValidateArtifactAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 1) { Console.Error.WriteLine("validate-artifact requires <file>"); return ExitCodesLocal.IncompleteProfile; }

    bool useArelle = false;
    bool allowSyntheticProfile = false;
    string? python = null;
    string? taxonomyRoot = null;
    string? disclosureSystem = null;
    var packages = new List<string>();
    var plugins = new List<string>();
    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--allow-synthetic-profile": allowSyntheticProfile = true; break;
            case "--arelle": useArelle = true; break;
            case "--python" when i + 1 < args.Length: python = args[++i]; break;
            case "--taxonomy-root" when i + 1 < args.Length: taxonomyRoot = args[++i]; break;
            case "--disclosure-system" when i + 1 < args.Length: disclosureSystem = args[++i]; break;
            case "--package" when i + 1 < args.Length: packages.Add(args[++i]); break;
            case "--plugin" when i + 1 < args.Length: plugins.Add(args[++i]); break;
            default: Console.Error.WriteLine($"Unrecognized or incomplete option: {args[i]}"); return ExitCodesLocal.IncompleteProfile;
        }
    }

    if (!allowSyntheticProfile)
    {
        Console.Error.WriteLine("Artifact validation currently uses the synthetic development profile in local and Arelle modes. Pass --allow-synthetic-profile to continue.");
        return ExitCodesLocal.IncompleteProfile;
    }

    if (!File.Exists(args[0])) { Console.Error.WriteLine($"Artifact not found: {args[0]}"); return ExitCodesLocal.DataError; }

    if (useArelle)
    {
        var script = FindArelleWorkerScript();
        if (script is null)
        {
            Console.Error.WriteLine("Arelle worker script was not found beside the CLI or in the source checkout.");
            return ExitCodesLocal.IncompleteProfile;
        }
        var workerArgs = new List<string> { script };
        taxonomyRoot ??= FindArelleTaxonomyRoot();
        if (taxonomyRoot is not null) { workerArgs.Add("--taxonomy-root"); workerArgs.Add(Path.GetFullPath(taxonomyRoot)); }
        foreach (var package in packages) { workerArgs.Add("--package"); workerArgs.Add(Path.GetFullPath(package)); }
        foreach (var plugin in plugins) { workerArgs.Add("--plugin"); workerArgs.Add(plugin); }
        if (disclosureSystem is not null) { workerArgs.Add("--disclosure-system"); workerArgs.Add(disclosureSystem); }

        try
        {
            await using var worker = new BoundedWorkerClient(() =>
                new ArelleWorker(python ?? (OperatingSystem.IsWindows() ? "python" : "python3"), workerArgs.ToArray()));
            var handshake = await worker.HandshakeAsync(cancellationToken);
            if (!handshake.Capabilities.Contains(WorkerOperations.ValidateReport, StringComparer.Ordinal))
            {
                Console.Error.WriteLine("Arelle worker does not support report validation.");
                return ExitCodesLocal.IncompleteProfile;
            }
            var syntheticProfile = SyntheticProfile.Build();
            var reportText = File.ReadAllText(args[0]);
            var request = new WorkerRequest(
                WorkerOperations.ValidateReport,
                Guid.NewGuid().ToString("N"),
                "cli-validate-artifact",
                syntheticProfile.Digest,
                DateTimeOffset.UtcNow.AddMinutes(2),
                JsonSerializer.SerializeToElement(reportText))
            {
                ValidationRequirements = new ValidationRequirements(
                    syntheticProfile.ProfileId,
                    syntheticProfile.Digest,
                    syntheticProfile.RequiredChecks.Order(StringComparer.Ordinal).ToArray(),
                    Array.Empty<string>())
            };
            var response = await worker.RequestAsync(request, cancellationToken);
            if (response.Status == WorkerStatus.Cancelled)
                throw new OperationCanceledException("Worker validation cancelled.", cancellationToken);
            var localFacts = OimSerializer.ReadFacts(reportText, syntheticProfile);
            var completion = ValidationCompletionReportValidator.Evaluate(
                response.Result, syntheticProfile, localFacts);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                artifact = Path.GetFileName(args[0]),
                engine = handshake.EngineVersion,
                status = response.Status.ToString(),
                result = response.Result,
                completion = completion.State.ToString(),
                diagnostics = response.Diagnostics,
                error = response.Error
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            if (completion.State == ValidationCompletionState.Failed) return ExitCodesLocal.InvalidMapping;
            if (completion.State == ValidationCompletionState.Incomplete) return ExitCodesLocal.IncompleteProfile;
            return response.Status switch
            {
                WorkerStatus.Success when !(response.Diagnostics ?? Array.Empty<Diagnostic>()).Any(d => d.IsBlocking) => ExitCodesLocal.Success,
                WorkerStatus.Success or WorkerStatus.DomainFailure => ExitCodesLocal.InvalidMapping,
                WorkerStatus.Cancelled => ExitCodesLocal.Cancelled,
                WorkerStatus.IncompleteValidation => ExitCodesLocal.IncompleteProfile,
                _ => ExitCodesLocal.OperationalFailure
            };
        }
        catch (WorkerStartupException ex) when (ex.Status == WorkerStatus.Cancelled)
        {
            throw new OperationCanceledException("Worker handshake cancelled.", ex, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Arelle validation could not start or complete: {ex.Message}");
            return ExitCodesLocal.IncompleteProfile;
        }
    }

    var text = File.ReadAllText(args[0]);
    var profile = SyntheticProfile.Build();
    var facts = OimSerializer.ReadFacts(text, profile);
    var result = new FilingRuleValidator(profile).Validate(facts);

    var json = JsonSerializer.Serialize(new
    {
        artifact = Path.GetFileName(args[0]),
        factCount = facts.Count,
        isClean = result.IsClean,
        runOutcome = result.MapToRunOutcome().ToString(),
        rules = result.Outcomes.Select(o => new { rule = o.RuleId, status = o.Status.ToString(), detail = o.Detail }),
    }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);

    if (result.HasFailures) return ExitCodesLocal.InvalidMapping;
    if (result.HasIncomplete) return ExitCodesLocal.IncompleteProfile;
    return ExitCodesLocal.Success;
}

static string? FindArelleWorkerScript()
{
    var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "arelle_worker.py");
    if (File.Exists(bundled)) return bundled;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "tools", "worker", "arelle_worker.py");
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}

static string? FindArelleTaxonomyRoot()
{
    var bundled = Path.Combine(AppContext.BaseDirectory, "worker", "taxonomy");
    if (File.Exists(Path.Combine(bundled, SyntheticProfile.TaxonomyFileName))) return bundled;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "tools", "worker", "taxonomy");
        if (File.Exists(Path.Combine(candidate, SyntheticProfile.TaxonomyFileName))) return candidate;
    }
    return null;
}

// OPS01 completeness: read back the persisted run journal receipt for one run id.
static int InspectRunResult(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("inspect-run-result requires <runId>"); return ExitCodesLocal.IncompleteProfile; }
    var journal = new RunJournal();
    var entry = journal.Load(args[0]);
    if (entry is null)
    {
        Console.Error.WriteLine($"No journal entry for run '{args[0]}'.");
        return ExitCodesLocal.DataError;
    }
    Console.WriteLine(JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
    return ExitCodesLocal.Success;
}

static async Task<int> ReconcilePublicationAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
    {
        Console.Error.WriteLine("Usage: xbrl-studio reconcile-publication <mixed-operation-key>");
        return ExitCodesLocal.IncompleteProfile;
    }

    try
    {
        var coordinator = new MixedPublicationCoordinator(
            new MixedPublicationStore(new LocalApplicationDatabase()));
        var record = await coordinator.ReconcileAsync(args[0], cancellationToken);
        if (record is null)
        {
            Console.Error.WriteLine($"No mixed publication record for '{args[0]}'.");
            return ExitCodesLocal.DataError;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            operationKey = record.OperationKey,
            intentFingerprint = record.IntentFingerprint,
            updatedUtc = record.UpdatedUtc,
            outcomes = record.ToManifestOutcomes()
        }, new JsonSerializerOptions { WriteIndented = true }));

        var states = new[] { record.FileState, record.SqliteState };
        if (states.All(state => state == DestinationPublicationState.Confirmed))
            return ExitCodesLocal.Success;
        if (states.Any(state => state is DestinationPublicationState.Unknown or DestinationPublicationState.InProgress
                or DestinationPublicationState.Pending))
            return ExitCodesLocal.OperationalFailure;
        return ExitCodesLocal.DataError;
    }
    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException)
    {
        Console.Error.WriteLine($"Publication reconciliation could not complete: {ex.Message}");
        return ExitCodesLocal.OperationalFailure;
    }
}

// OPS08 (partial): redacted diagnostic bundle for one run; scheduler integration stays owner-gated.
static int SupportBundleCommand(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("support-bundle requires <project> <runId> [--out <path>] [--log <file>]"); return ExitCodesLocal.IncompleteProfile; }
    var projectPath = args[0];
    var runId = args[1];
    string? outPath = null;
    string? logPath = null;
    for (int i = 2; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out": if (i + 1 < args.Length) outPath = args[++i]; break;
            case "--log": if (i + 1 < args.Length) logPath = args[++i]; break;
            default: Console.Error.WriteLine($"Unrecognized option: {args[i]}"); return ExitCodesLocal.IncompleteProfile;
        }
    }

    var logLines = logPath is not null && File.Exists(logPath)
        ? File.ReadAllLines(logPath)
        : Enumerable.Empty<string>();
    var bundle = SupportBundle.Build(projectPath, runId, new RunJournal(), logLines);

    if (outPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, bundle);
        Console.Error.WriteLine($"Support bundle written to {outPath}");
    }
    else
    {
        Console.WriteLine(bundle);
    }
    return ExitCodesLocal.Success;
}

static int Inspect(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("inspect requires <project>"); return ExitCodesLocal.IncompleteProfile; }
    var pm = new ProjectManager(args[0]);
    var p = pm.Load();
    Console.WriteLine($"Project:        {p.Name}");
    Console.WriteLine($"Id:             {p.ProjectId}");
    Console.WriteLine($"Profile:        {p.ProfileId}");
    Console.WriteLine($"Profile digest: {p.ProfileDigest}");
    Console.WriteLine($"Semantic rev:   {p.SemanticRevision}");
    Console.WriteLine($"Layout gen:     {p.LayoutGeneration}");
    Console.WriteLine($"Nodes:          {p.Nodes.Count}");
    Console.WriteLine($"Edges:          {p.Edges.Count}");
    Console.WriteLine($"Bindings:       {p.Bindings.Count}");
    if (p.Destination is { } destination)
    {
        Console.WriteLine($"Destination:    {destination.Kind} {destination.Locator}#{destination.Table}");
        Console.WriteLine($"  format/write: {destination.Format} / {destination.WriteMode}");
        Console.WriteLine($"  business key: {string.Join(",", destination.BusinessKey)}");
    }
    else
    {
        Console.WriteLine("Destination:    none");
    }
    Console.WriteLine($"Digest:         {p.ComputeSemanticDigest()}");
    foreach (var n in p.Nodes)
        Console.WriteLine($"  node  {n.NodeId} [{n.NodeType}] {n.DisplayName}");
    foreach (var e in p.Edges)
        Console.WriteLine($"  edge  {e.EdgeId}: {e.SourceNodeId}.{e.SourcePortName} -> {e.TargetNodeId}.{e.TargetPortName}");
    return ExitCodesLocal.Success;
}

static async Task<int> SnapshotSourceAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 1) return ExitCodesLocal.IncompleteProfile;
    string? bindingId = null, output = null, workspaceRoot = null;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--binding" when i + 1 < args.Length && bindingId is null: bindingId = args[++i]; break;
            case "--out" when i + 1 < args.Length && output is null: output = args[++i]; break;
            case "--workspace-root" when i + 1 < args.Length && workspaceRoot is null: workspaceRoot = args[++i]; break;
            default:
                Console.Error.WriteLine($"Unrecognized, duplicate or incomplete snapshot-source option: {args[i]}");
                return ExitCodesLocal.IncompleteProfile;
        }
    }
    if (string.IsNullOrWhiteSpace(bindingId) || string.IsNullOrWhiteSpace(output))
    {
        Console.Error.WriteLine("snapshot-source requires --binding <id> and --out <new-bundle-directory>.");
        return ExitCodesLocal.IncompleteProfile;
    }
    try
    {
        var projectPath = Path.GetFullPath(args[0]);
        var project = new ProjectManager(projectPath).Load();
        var binding = project.Bindings.SingleOrDefault(binding => binding.BindingId == bindingId);
        if (binding is null || binding.Kind != "sqlite" || string.IsNullOrWhiteSpace(binding.Locator))
        {
            Console.Error.WriteLine("Select one existing SQLite binding for snapshot capture.");
            return ExitCodesLocal.IncompleteProfile;
        }
        var root = Path.GetFullPath(workspaceRoot ?? Path.GetDirectoryName(projectPath)!);
        string Guard(string path) => PathGuard.EnsureWithin(root, Path.GetFullPath(path, root));
        var source = new SourceConnection(binding.BindingId, binding.Kind, Guard(binding.Locator),
            binding.Configuration.Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!.ToString()!, StringComparer.Ordinal));
        var destination = Guard(output);
        Console.Error.WriteLine("Retaining a confidential copy of the entire SQLite database, including tables outside this binding.");
        var metadataPath = await SqliteSourceSnapshotStore.CaptureAsync(source, destination, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(new { snapshot = metadataPath, sourceId = bindingId,
            strategy = SqliteSourceSnapshotStore.Strategy, entireDatabase = true }));
        return ExitCodesLocal.Success;
    }
    catch (AdapterException ex)
    {
        Console.Error.WriteLine($"IO: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.DataError;
    }
}

static async Task<int> InspectSourceAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("inspect-source requires <project> [--binding <id>]");
        return ExitCodesLocal.IncompleteProfile;
    }

    string? bindingId = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--binding" && i + 1 < args.Length) bindingId = args[++i];
        else
        {
            Console.Error.WriteLine($"Unrecognized or incomplete option: {args[i]}");
            return ExitCodesLocal.IncompleteProfile;
        }
    }

    try
    {
        var projectPath = Path.GetFullPath(args[0]);
        var project = new ProjectManager(projectPath).Load();
        var sourceBindings = project.Bindings.Where(binding => binding.Kind != "param").ToArray();
        var selected = bindingId is null
            ? sourceBindings.Length == 1 ? sourceBindings[0] : null
            : sourceBindings.FirstOrDefault(binding => StringComparer.Ordinal.Equals(binding.BindingId, bindingId));
        if (selected is null)
        {
            Console.Error.WriteLine(bindingId is null
                ? "Select a source with --binding when the project has zero or multiple source bindings."
                : $"Source binding '{bindingId}' was not found.");
            return ExitCodesLocal.IncompleteProfile;
        }
        if (!StringComparer.OrdinalIgnoreCase.Equals(selected.Kind, "sqlite"))
        {
            Console.Error.WriteLine($"inspect-source currently supports SQLite sources; binding '{selected.BindingId}' is '{selected.Kind}'.");
            return ExitCodesLocal.IncompleteProfile;
        }
        if (string.IsNullOrWhiteSpace(selected.Locator))
        {
            Console.Error.WriteLine($"SQLite binding '{selected.BindingId}' has no source path.");
            return ExitCodesLocal.DataError;
        }

        var workspace = Path.GetDirectoryName(projectPath)!;
        var sourcePath = PathGuard.EnsureWithin(workspace,
            Path.IsPathRooted(selected.Locator) ? selected.Locator : Path.Combine(workspace, selected.Locator));
        var configuration = selected.Configuration.ToDictionary(
            pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);
        var connection = new SourceConnection(selected.BindingId, selected.Kind, sourcePath, configuration);
        var adapter = new SqliteSourceAdapter();
        var schema = await adapter.InspectSchemaAsync(connection, cancellationToken);
        var descriptor = SqliteSourceAdapter.Describe(connection, schema);
        var output = new
        {
            sourceId = selected.BindingId,
            kind = selected.Kind,
            table = descriptor.Table,
            schemaFingerprint = descriptor.SchemaFingerprint,
            primaryKeyColumns = descriptor.PrimaryKeyColumns,
            rowIdentityStrategy = descriptor.RowIdentityStrategy,
            hasStableRowIdentity = descriptor.HasStableRowIdentity,
            configuredExactDecimalColumns = descriptor.DecimalOverrides,
            unknownDecimalOverrides = descriptor.UnknownDecimalOverrides,
            decimalCulture = descriptor.DecimalCulture,
            capabilities = new
            {
                schemaInspection = descriptor.Capabilities.SupportsSchemaInspection,
                sampling = descriptor.Capabilities.SupportsSampling,
                reading = descriptor.Capabilities.SupportsReading,
                stagedWrite = descriptor.Capabilities.SupportsStagedWrite,
                publication = descriptor.Capabilities.SupportsPublication,
                lineage = descriptor.Capabilities.SupportsLineage
            },
            columns = descriptor.Columns.Select(column => new
            {
                name = column.Name,
                inferredShape = column.InferredShape.ToString(),
                ordinal = column.Ordinal,
                primaryKeyOrdinal = column.PrimaryKeyOrdinal,
                exactDecimalOverride = descriptor.DecimalOverrides.Contains(column.Name, StringComparer.Ordinal)
            })
        };
        Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        return ExitCodesLocal.Success;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AdapterException
        or ArgumentException or InvalidOperationException or SqliteException)
    {
        Console.Error.WriteLine($"SQLite source inspection failed: {ex.Message}");
        return ExitCodesLocal.DataError;
    }
}

static int ConfigureDestination(string[] args)
{
    if (args.Length < 1)
    {
        Console.Error.WriteLine("configure-destination requires <project>");
        return ExitCodesLocal.IncompleteProfile;
    }

    var projectPath = args[0];
    string? kind = null;
    string? locator = null;
    string? table = null;
    string? keyList = null;
    string? format = null;
    string? requestedWriteMode = null;
    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--kind" when i + 1 < args.Length: kind = args[++i]; break;
            case "--path" when i + 1 < args.Length: locator = args[++i]; break;
            case "--table" when i + 1 < args.Length: table = args[++i]; break;
            case "--key" when i + 1 < args.Length: keyList = args[++i]; break;
            case "--format" when i + 1 < args.Length: format = args[++i]; break;
            case "--write-mode" when i + 1 < args.Length: requestedWriteMode = args[++i]; break;
            default:
                Console.Error.WriteLine($"Unrecognized or incomplete option: {args[i]}");
                return ExitCodesLocal.IncompleteProfile;
        }
    }

    if (kind is null || locator is null)
    {
        Console.Error.WriteLine("configure-destination requires --kind and --path.");
        return ExitCodesLocal.IncompleteProfile;
    }

    var isSqlite = StringComparer.Ordinal.Equals(kind, DestinationPlanValidator.SupportedKind);
    var isFileKind = DestinationPlanValidator.IsFileKind(kind);
    if (isSqlite && (table is null || keyList is null))
    {
        Console.Error.WriteLine("SQLite destinations require --table and --key.");
        return ExitCodesLocal.IncompleteProfile;
    }
    if (isFileKind && (string.IsNullOrWhiteSpace(format) || table is not null || keyList is not null))
    {
        Console.Error.WriteLine("File destinations require --format and do not accept --table or --key.");
        return ExitCodesLocal.IncompleteProfile;
    }

    try
    {
        var manager = new ProjectManager(projectPath);
        var project = manager.Load();
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var fullDestination = PathGuard.EnsureWithin(projectDirectory,
            Path.IsPathRooted(locator) ? locator : Path.Combine(projectDirectory, locator));
        if (isSqlite && !File.Exists(fullDestination))
        {
            var missing = new DiagnosticBag();
            missing.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Publication,
                DiagnosticStage.Validation,
                "SQLite destination database must already exist; configure-destination will not create it.");
            PrintDiagnostics(missing.Items);
            return ExitCodesLocal.DataError;
        }
        if (isFileKind)
        {
            var pathDiagnostics = DestinationPlanValidator.ValidateFileDestinationPath(fullDestination);
            if (pathDiagnostics.HasBlockingErrors)
            {
                PrintDiagnostics(pathDiagnostics.Items);
                return ExitCodesLocal.DataError;
            }
            if (Directory.Exists(fullDestination))
            {
                var invalid = new DiagnosticBag();
                invalid.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Publication,
                    DiagnosticStage.Validation, "A file destination path cannot name a directory.");
                PrintDiagnostics(invalid.Items);
                return ExitCodesLocal.DataError;
            }
            var destinationDirectory = Path.GetDirectoryName(fullDestination);
            if (destinationDirectory is null || !Directory.Exists(destinationDirectory))
            {
                var missing = new DiagnosticBag();
                missing.Error(DiagnosticCodes.IoSchemaMismatch, DiagnosticSubsystem.Publication,
                    DiagnosticStage.Validation, "The file destination directory must already exist.");
                PrintDiagnostics(missing.Items);
                return ExitCodesLocal.DataError;
            }
        }

        var storedLocator = Path.IsPathRooted(locator)
            ? fullDestination
            : Path.GetRelativePath(projectDirectory, fullDestination);
        var plan = new DestinationPlan
        {
            Kind = kind,
            Locator = storedLocator,
            Table = table ?? string.Empty,
            BusinessKey = keyList?.Split(',', StringSplitOptions.None)
                .Select(key => key.Trim())
                .ToList() ?? new List<string>(),
            Format = format ?? (isSqlite
                ? DestinationPlanValidator.SupportedFormat
                : DestinationPlanValidator.FileFormatFor(kind) ?? string.Empty),
            WriteMode = requestedWriteMode ?? (isSqlite ? "upsert" : "replace")
        };
        var diagnostics = DestinationPlanValidator.Validate(plan);
        if (diagnostics.HasBlockingErrors)
        {
            PrintDiagnostics(diagnostics.Items);
            return ExitCodesLocal.InvalidMapping;
        }

        project.Destination = plan;
        var saved = manager.Save(project, expectedLayoutGeneration: project.LayoutGeneration);
        Console.Error.WriteLine(
            $"Saved {(isSqlite ? "SQLite" : kind)} destination at semantic revision {saved.NewSemanticRevision}. "
            + "A reviewer must approve this destination-bound revision before a full publish.");
        return ExitCodesLocal.Success;
    }
    catch (ProjectSchemaException ex)
    {
        Console.Error.WriteLine($"PRJ: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.IncompleteProfile;
    }
    catch (ProjectSaveException ex)
    {
        Console.Error.WriteLine($"PRJ: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.OperationalFailure;
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine($"IO: {DiagnosticCodes.IoPathEscapesWorkspace}: {ex.Message}");
        return ExitCodesLocal.DataError;
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"IO: {DiagnosticCodes.IoSchemaMismatch}: {ex.Message}");
        return ExitCodesLocal.DataError;
    }
}

static async Task<int> ValidateAsync(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("validate requires <project>"); return ExitCodesLocal.IncompleteProfile; }
    bool allowSyntheticProfile = false;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--allow-synthetic-profile") allowSyntheticProfile = true;
        else { Console.Error.WriteLine($"Unrecognized option: {args[i]}"); return ExitCodesLocal.IncompleteProfile; }
    }

    try
    {
        var p = new ProjectManager(args[0]).Load();
        var resolution = FilingProfileResolver.FromDefaultCatalog().Resolve(p, allowSyntheticProfile);
        if (!resolution.Success)
        {
            PrintDiagnostics(resolution.Diagnostics.Items);
            return ExitCodesLocal.IncompleteProfile;
        }

        var profile = resolution.Profile!;
        var outcome = MappingJobService.ValidateProject(p, profile);
        PrintDiagnostics(outcome.Diagnostics.Items);
        if (outcome.Success) return ExitCodesLocal.Success;
        return outcome.Diagnostics.Items.Any(d => d.Code == DiagnosticCodes.XbrProfileMismatch)
            ? ExitCodesLocal.IncompleteProfile
            : ExitCodesLocal.InvalidMapping;
    }
    catch (ProjectSchemaException ex)
    {
        Console.Error.WriteLine($"PRJ: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.IncompleteProfile;
    }
}

static async Task<int> ExecuteAsync(string[] args, bool preview, CancellationToken cancellationToken)
{
    if (args.Length < 1) { Console.Error.WriteLine("requires <project>"); return ExitCodesLocal.IncompleteProfile; }
    var projectPath = args[0];
    var bindings = new List<(string id, string path)>();
    var sourceSnapshots = new Dictionary<string, string>(StringComparer.Ordinal);
    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    string? output = null;
    string outputFormat = "xbrl-json";
    string? manifestPath = null;
    string? workspaceRootArg = null;
    int rows = 10;
    bool allowSyntheticProfile = false;
    bool useArelle = false;
    bool publishDestination = false;
    string? python = null;
    string? taxonomyRoot = null;

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--binding":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--binding requires id=path"); return ExitCodesLocal.IncompleteProfile; }
                var (bid, bpath) = ParsePair(args[++i]);
                bindings.Add((bid, bpath));
                break;
            case "--source-snapshot":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                var (snapshotId, snapshotPath) = ParsePair(args[++i]);
                if (!sourceSnapshots.TryAdd(snapshotId, snapshotPath))
                {
                    Console.Error.WriteLine("A source binding can select only one retained snapshot.");
                    return ExitCodesLocal.IncompleteProfile;
                }
                break;
            case "--param":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--param requires name=value"); return ExitCodesLocal.IncompleteProfile; }
                var (k, v) = ParsePair(args[++i]);
                parameters[k] = v;
                break;
            case "--output":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                output = args[++i];
                break;
            case "--output-format":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--output-format requires xbrl-json or xbrl-xml."); return ExitCodesLocal.IncompleteProfile; }
                outputFormat = args[++i];
                break;
            case "--manifest":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                manifestPath = args[++i];
                break;
            case "--workspace-root":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                workspaceRootArg = args[++i];
                break;
            case "--rows":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                rows = int.Parse(args[++i]);
                break;
            case "--allow-synthetic-profile":
                allowSyntheticProfile = true;
                break;
            case "--publish-destination":
                publishDestination = true;
                break;
            case "--arelle":
                useArelle = true;
                break;
            case "--python":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                python = args[++i];
                break;
            case "--taxonomy-root":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                taxonomyRoot = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unrecognized option: {args[i]}");
                return ExitCodesLocal.IncompleteProfile;
        }
    }

    if (!useArelle && (python is not null || taxonomyRoot is not null))
    {
        Console.Error.WriteLine("--python and --taxonomy-root require --arelle.");
        return ExitCodesLocal.IncompleteProfile;
    }

    if (outputFormat is not ("xbrl-json" or "xbrl-xml"))
    {
        Console.Error.WriteLine("--output-format must be xbrl-json or xbrl-xml.");
        return ExitCodesLocal.IncompleteProfile;
    }
    if (outputFormat == "xbrl-xml" && (preview || output is null))
    {
        Console.Error.WriteLine("xBRL-XML output requires execute --output and cannot be combined with preview.");
        return ExitCodesLocal.InvalidMapping;
    }

    if (preview && publishDestination)
    {
        Console.Error.WriteLine("Preview runs cannot publish to a durable destination.");
        return ExitCodesLocal.InvalidMapping;
    }
    try
    {
        var pm = new ProjectManager(projectPath);
        var p = pm.Load();
        var profileResolver = FilingProfileResolver.FromDefaultCatalog();
        var profileResolution = profileResolver.Resolve(p, allowSyntheticProfile, DiagnosticStage.Validation);
        if (!profileResolution.Success)
        {
            PrintDiagnostics(profileResolution.Diagnostics.Items);
            return ExitCodesLocal.IncompleteProfile;
        }
        var profile = profileResolution.Profile!;
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var workspaceRoot = Path.GetFullPath(workspaceRootArg ?? projectDirectory);
        string GuardPath(string path) => PathGuard.EnsureWithin(workspaceRoot,
            Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));

        if (publishDestination && p.Destination is null)
        {
            Console.Error.WriteLine($"IO: {DiagnosticCodes.IoUnsupportedOperation}: The project has no saved destination plan.");
            return ExitCodesLocal.InvalidMapping;
        }
        if (publishDestination)
        {
            var destinationDiagnostics = DestinationPlanValidator.Validate(p.Destination);
            if (destinationDiagnostics.HasBlockingErrors)
            {
                PrintDiagnostics(destinationDiagnostics.Items);
                return ExitCodesLocal.InvalidMapping;
            }
            if (output is not null
                && !StringComparer.Ordinal.Equals(p.Destination!.Kind, DestinationPlanValidator.SupportedKind))
            {
                Console.Error.WriteLine("Combined --output and --publish-destination requires a saved SQLite destination plan.");
                return ExitCodesLocal.InvalidMapping;
            }
            if (DestinationPlanValidator.IsFileKind(p.Destination!.Kind))
            {
                var destinationPath = Path.GetFullPath(Path.IsPathRooted(p.Destination.Locator)
                    ? p.Destination.Locator
                    : Path.Combine(projectDirectory, p.Destination.Locator));
                try { _ = GuardPath(destinationPath); }
                catch (UnauthorizedAccessException ex)
                {
                    Console.Error.WriteLine($"IO: {DiagnosticCodes.IoPathEscapesWorkspace}: {ex.Message}");
                    return ExitCodesLocal.DataError;
                }
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (Directory.Exists(destinationPath) || destinationDirectory is null || !Directory.Exists(destinationDirectory))
                {
                    Console.Error.WriteLine($"IO: {DiagnosticCodes.IoSchemaMismatch}: File destination must name a file in an existing directory.");
                    return ExitCodesLocal.DataError;
                }
            }
        }

        var conns = new Dictionary<string, SourceConnection>(StringComparer.Ordinal);
        foreach (var b in p.Bindings)
        {
            var cfg = b.Configuration is null
                ? new Dictionary<string, string>()
                : b.Configuration
                    .Where(kv => kv.Value is not null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value!.ToString()!, StringComparer.Ordinal);
            conns[b.BindingId] = new SourceConnection(b.BindingId, b.Kind, b.Locator, cfg);
        }
        foreach (var (id, path) in bindings)
        {
            // A command-line override replaces only the locator. When the id matches a
            // project binding, preserve its declared adapter kind and configuration so
            // that overriding a JSON/CSV/XML/XBRL source path does not silently force it
            // to the Excel reader (or drop its adapter settings).
            if (conns.TryGetValue(id, out var existing))
                conns[id] = existing with { Path = path };
            else
                conns[id] = new SourceConnection(id, "excel", path); // ad-hoc locator for an unknown binding
        }
        foreach (var (id, conn) in conns.ToArray())
            if (conn.Path is not null) conns[id] = conn with { Path = GuardPath(conn.Path) };
        foreach (var (id, path) in sourceSnapshots.ToArray())
        {
            if (!conns.TryGetValue(id, out var source) || source.Kind != "sqlite"
                || bindings.Any(binding => binding.id == id))
            {
                Console.Error.WriteLine("--source-snapshot requires an existing SQLite binding without a competing --binding path override.");
                return ExitCodesLocal.IncompleteProfile;
            }
            sourceSnapshots[id] = GuardPath(path);
            _ = GuardPath(Path.Combine(Path.GetDirectoryName(sourceSnapshots[id])!, "source.sqlite"));
        }
        if (output is not null) output = GuardPath(output);
        if (manifestPath is not null) manifestPath = GuardPath(manifestPath);

        if (!preview && output is not null && manifestPath is not null
            && DestinationPathGuard.AreSamePath(output, manifestPath, workspaceRoot))
        {
            Console.Error.WriteLine("The report and run manifest must be different paths.");
            return ExitCodesLocal.InvalidMapping;
        }

        if (publishDestination)
        {
            var destinationPath = Path.GetFullPath(Path.IsPathRooted(p.Destination!.Locator)
                ? p.Destination.Locator : Path.Combine(workspaceRoot, p.Destination.Locator));
            if (output is not null && DestinationPathGuard.AreSamePath(output, destinationPath, workspaceRoot))
            {
                Console.Error.WriteLine("The report and saved destination must be different paths.");
                return ExitCodesLocal.InvalidMapping;
            }
            if (manifestPath is not null
                && DestinationPathGuard.AreSamePath(manifestPath, destinationPath, workspaceRoot))
            {
                Console.Error.WriteLine("The run manifest and saved destination must be different paths.");
                return ExitCodesLocal.InvalidMapping;
            }
        }

        var writeTargets = new List<(string Name, string Path)>();
        if (!preview && output is not null) writeTargets.Add(("Report", output));
        if (manifestPath is not null) writeTargets.Add(("Run manifest", manifestPath));
        if (publishDestination && DestinationPlanValidator.IsFileKind(p.Destination!.Kind))
        {
            var fileDestinationPath = Path.GetFullPath(Path.IsPathRooted(p.Destination.Locator)
                ? p.Destination.Locator : Path.Combine(workspaceRoot, p.Destination.Locator));
            writeTargets.Add(("Saved destination", fileDestinationPath));
        }

        var fullProjectPath = Path.GetFullPath(projectPath);
        IEnumerable<(string SourceId, string? Path)> sourcePaths =
            conns.Values.Select(connection => (connection.SourceId, connection.Path))
            .Concat(p.Bindings.Select(binding => (SourceId: binding.BindingId, Path: (string?)binding.Locator)))
            .Concat(sourceSnapshots.SelectMany(snapshot => new (string SourceId, string? Path)[]
            {
                (snapshot.Key, snapshot.Value),
                (snapshot.Key, Path.Combine(Path.GetDirectoryName(snapshot.Value)!, "source.sqlite"))
            }))
            .Concat(new (string SourceId, string? Path)[]
            {
                ("project-file", fullProjectPath),
                ("project-lock", fullProjectPath + ProjectManager.LockSuffix),
                ("project-backup", fullProjectPath + ProjectManager.BackupSuffix)
            });
        foreach (var (targetName, targetPath) in writeTargets)
        {
            var protectedPathDiagnostics = DestinationPlanValidator.ValidateFileDestinationPath(targetPath);
            if (protectedPathDiagnostics.HasBlockingErrors)
            {
                PrintDiagnostics(protectedPathDiagnostics.Items);
                return ExitCodesLocal.DataError;
            }

            var sourceId = DestinationPathGuard.FindSourceIdAtPath(targetPath, sourcePaths, workspaceRoot);
            if (sourceId is null) continue;
            Console.Error.WriteLine(
                $"IO: {DiagnosticCodes.IoUnsupportedOperation}: {targetName} path cannot replace source input '{sourceId}'. Choose a different path.");
            return ExitCodesLocal.DataError;
        }

        // EG03/XG01: an unattended (non-preview) execute is a production publication and
        // must clear the profile-identity, explicit-synthetic-opt-in, and approval gates
        // before the graph runs at all — a missing, substituted, or unapproved profile
        // fails here, leaving any previously accepted destination untouched.
        if (!preview)
        {
            var gate = PublicationGate.Evaluate(p, profile, allowSyntheticProfile);
            if (gate.HasBlockingErrors)
            {
                PrintDiagnostics(gate.Items);
                // Profile identity/synthetic-mode problems are a configuration/authz
                // refusal to run; a missing approval specifically is an authorization
                // failure per the section 9 exit-code contract.
                bool onlyApproval = gate.Items.All(d => d.Code == DiagnosticCodes.XbrApprovalRequired);
                return onlyApproval ? ExitCodesLocal.AuthorizationFailure : ExitCodesLocal.IncompleteProfile;
            }
        }

        foreach (var (id, path) in sourceSnapshots)
            conns[id] = await SqliteSourceSnapshotStore.OpenAsync(path, conns[id], cancellationToken);

        IWorkerClient worker;
        try
        {
            worker = FilingProfileWorkerFactory.ForInstalledProfiles(
                profileResolver, python, FindArelleWorkerScript(), taxonomyRoot).Create(profile, useArelle);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodesLocal.IncompleteProfile;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Worker: {DiagnosticCodes.XbrWorkerUnavailable}: Could not start the selected profile worker: {ex.Message}");
            return ExitCodesLocal.IncompleteProfile;
        }
        await using var workerLifetime = worker;
        var service = new MappingJobService(profile, worker);

        using var result = await service.RunAsync(p, conns, parameters, preview, cancellationToken,
            outputKind: outputFormat);
        PrintDiagnostics(result.Diagnostics.Items);
        var selectedOutputKind = outputFormat;
        byte[]? selectedOutputPayload = null;

        void RecordDestinationFailure(Diagnostic diagnostic, RunOutcome outcome)
        {
            result.Diagnostics.Add(diagnostic);
            result.Manifest.Diagnostics = result.Manifest.Diagnostics.Append(diagnostic).ToArray();
            result.Manifest.Outcome = outcome;
            result.Manifest.CompletedUtc = DateTimeOffset.UtcNow;
            PrintDiagnostics(new[] { diagnostic });
        }

        if (!preview && output is not null && outputFormat == "xbrl-xml" && result.IsPublishable)
        {
            if (result.LargeArtifact is { } largeXmlArtifact
                && StringComparer.Ordinal.Equals(largeXmlArtifact.OutputKind, "xbrl-xml"))
            {
                // The application streamed, validated and recorded this XML artifact
                // directly from its disk-backed fact spool; do not materialize facts.
            }
            else if (result.LargeArtifact is not null)
                RecordDestinationFailure(new Diagnostic
                {
                    Code = DiagnosticCodes.PubDigestMismatch,
                    Subsystem = DiagnosticSubsystem.XBRL,
                    Severity = DiagnosticSeverity.Error,
                    Stage = DiagnosticStage.Serialization,
                    IsBlocking = true,
                    Message = "The large-run artifact kind does not match the requested xBRL-XML output.",
                    OperationId = result.Manifest.OperationId
                }, RunOutcome.IncompleteConfiguration);
            else
            {
                try
                {
                    var xml = XmlSerializer.Serialize(profile, result.Facts);
                    selectedOutputPayload = new System.Text.UTF8Encoding(false, true).GetBytes(xml);
                    var digest = RunManifest.ComputeSha256(selectedOutputPayload);
                    result.Manifest.Outputs = result.Manifest.Outputs
                        .Append(new OutputIdentity("<memory>", digest, selectedOutputPayload.LongLength, "xbrl-xml"))
                        .ToArray();
                }
                catch (XbrlExportException ex)
                {
                    RecordDestinationFailure(new Diagnostic
                    {
                        Code = ex.Code,
                        Subsystem = DiagnosticSubsystem.XBRL,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Serialization,
                        IsBlocking = true,
                        Message = ex.Message,
                        OperationId = result.Manifest.OperationId
                    }, RunOutcome.InvalidMapping);
                }
            }
        }

        static RunOutcome PublicationFailureOutcome(PublicationException ex) => ex.Code switch
        {
            DiagnosticCodes.XbrApprovalRequired => RunOutcome.AuthorizationFailure,
            DiagnosticCodes.XbrSyntheticProfileNotAuthorized or DiagnosticCodes.XbrWorkerUnavailable
                or DiagnosticCodes.XbrValidationIncomplete => RunOutcome.IncompleteConfiguration,
            DiagnosticCodes.WkrTimeout or DiagnosticCodes.WkrCrash or DiagnosticCodes.PubStagingFailed
                or DiagnosticCodes.PubDestinationLocked => RunOutcome.OperationalFailure,
            DiagnosticCodes.PubPartialFailure or DiagnosticCodes.PubDigestMismatch => RunOutcome.Unknown,
            _ => RunOutcome.InvalidMapping
        };

        if (publishDestination)
        {
            if (!result.IsPublishable)
            {
                Console.Error.WriteLine(
                    $"Run {result.Manifest.RunId} finished as {result.Manifest.Outcome}; "
                    + "saved destination left unchanged and no destination receipt issued.");
            }
            else
            {
                try
                {
                    if (output is not null)
                    {
                        var destinationPath = Path.GetFullPath(Path.IsPathRooted(p.Destination!.Locator)
                            ? p.Destination.Locator : Path.Combine(projectDirectory, p.Destination.Locator));
                        var mixedFilePayload = selectedOutputPayload ?? System.Text.Encoding.UTF8.GetBytes(result.XbrlJson);
                        var reportBytes = result.LargeArtifact?.Bytes ?? mixedFilePayload.LongLength;
                        var intent = MixedPublicationIntent.Create(
                            p, result, output, reportBytes, destinationPath, p.Destination.Table, selectedOutputKind);
                        var coordinator = new MixedPublicationCoordinator(
                            new MixedPublicationStore(new LocalApplicationDatabase()));
                        var publisher = new PublicationService(new RunJournal());
                        var attempt = await coordinator.PublishAsync(
                            intent,
                            async token => result.LargeArtifact is { } largeArtifact
                                ? await publisher.PublishAuthorizedLargeArtifactAsync(
                                    output, largeArtifact, workspaceRoot, p, profile, allowSyntheticProfile, result,
                                    (stagedPath, validationToken) => service.ValidateStagedArtifactAsync(
                                        stagedPath, output, result, validationToken, selectedOutputKind), token,
                                    refuseDifferentExisting: true, outputKind: selectedOutputKind)
                                : await publisher.PublishAuthorizedAsync(
                                    output, mixedFilePayload, workspaceRoot,
                                    p, profile, allowSyntheticProfile, result,
                                    (stagedPath, validationToken) => service.ValidateStagedArtifactAsync(
                                        stagedPath, output, result, validationToken, selectedOutputKind), token,
                                    refuseDifferentExisting: true, outputKind: selectedOutputKind),
                            (onIntentPrepared, token) => service.PublishDestinationAsync(
                                p, result, projectDirectory, allowSyntheticProfile, token, onIntentPrepared),
                            cancellationToken);
                        result.Manifest.PublicationReconciliationId = intent.OperationKey;
                        result.Manifest.PublicationOutcomes = attempt.Record.ToManifestOutcomes();
                        if (attempt.Record.FileState == DestinationPublicationState.Confirmed)
                        {
                            result.Manifest.PublishedArtifactDigest = attempt.Record.FileDigest;
                            result.Manifest.Outputs = result.Manifest.Outputs
                                .Select(existing => existing.Kind == intent.FileKind
                                    ? existing with { Path = attempt.Record.FilePath }
                                    : existing)
                                .ToArray();
                        }
                        if (attempt.Record.SqliteState == DestinationPublicationState.Confirmed
                            && !result.Manifest.Outputs.Any(existing => existing.Kind == "db-receipt"
                                && Path.GetFullPath(existing.Path) == attempt.Record.SqlitePath))
                        {
                            result.Manifest.Outputs = result.Manifest.Outputs
                                .Append(new OutputIdentity(attempt.Record.SqlitePath,
                                    attempt.Record.SqliteIntentFingerprint ?? string.Empty, 0, "db-receipt")
                                {
                                    RowsWritten = attempt.Record.SqliteRowsWritten,
                                    OperationKey = attempt.Record.SqliteOperationKey,
                                    Table = attempt.Record.SqliteTable
                                })
                                .ToArray();
                        }
                        if (attempt.Failure is null)
                        {
                            Console.Error.WriteLine(
                                $"Published mixed operation {intent.OperationKey}: file={attempt.Record.FileState}, "
                                + $"sqlite={attempt.Record.SqliteState} ({attempt.Record.SqliteRowsWritten ?? 0} row(s)).");
                        }
                        else if (attempt.Failure is PublicationException publicationFailure)
                        {
                            RecordDestinationFailure(publicationFailure.ToDiagnostic(),
                                attempt.Record.FileState == DestinationPublicationState.Unknown
                                    || attempt.Record.SqliteState == DestinationPublicationState.Unknown
                                    ? RunOutcome.Unknown : PublicationFailureOutcome(publicationFailure));
                        }
                        else if (attempt.Failure is AdapterException adapterFailure)
                        {
                            RecordDestinationFailure(adapterFailure.ToDiagnostic(operationId: result.Manifest.OperationId),
                                attempt.Record.FileState == DestinationPublicationState.Unknown
                                    || attempt.Record.SqliteState == DestinationPublicationState.Unknown
                                    ? RunOutcome.Unknown : RunOutcome.DataError);
                        }
                        else
                        {
                            RecordDestinationFailure(new Diagnostic
                            {
                                Code = DiagnosticCodes.PubPartialFailure,
                                Subsystem = DiagnosticSubsystem.Publication,
                                Severity = DiagnosticSeverity.Error,
                                Stage = DiagnosticStage.Publication,
                                IsBlocking = true,
                                Message = $"Mixed publication outcome requires reconciliation: {attempt.Failure.Message}",
                                OperationId = result.Manifest.OperationId
                            }, attempt.Record.FileState == DestinationPublicationState.Unknown
                                || attempt.Record.SqliteState == DestinationPublicationState.Unknown
                                ? RunOutcome.Unknown : RunOutcome.OperationalFailure);
                        }
                        Console.Error.WriteLine($"Reconcile this publication with: xbrl-studio reconcile-publication {intent.OperationKey}");
                    }
                    else if (DestinationPlanValidator.IsFileKind(p.Destination!.Kind))
                    {
                        var outputIdentity = await service.PublishFileDestinationAsync(
                            p, result, projectDirectory, allowSyntheticProfile, cancellationToken);
                        Console.Error.WriteLine(
                            $"Published {outputIdentity.Kind} {outputIdentity.Path} "
                            + $"({outputIdentity.Bytes} bytes, sha256 {outputIdentity.Digest}).");
                    }
                    else
                    {
                        var receipt = await service.PublishDestinationAsync(
                            p, result, projectDirectory, allowSyntheticProfile, cancellationToken);
                        Console.Error.WriteLine(
                            $"Published SQLite {receipt.OperationKey} ({receipt.WrittenRows} row(s)) "
                            + $"table={p.Destination!.Table} intent={receipt.IntentFingerprint}.");
                    }
                }
                catch (AdapterException ex)
                {
                    var outcome = ex.Code == DiagnosticCodes.IoUnsupportedOperation
                        ? RunOutcome.InvalidMapping
                        : RunOutcome.DataError;
                    RecordDestinationFailure(ex.ToDiagnostic(operationId: result.Manifest.OperationId), outcome);
                }
                catch (PublicationException ex)
                {
                    RecordDestinationFailure(ex.ToDiagnostic(), PublicationFailureOutcome(ex));
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                {
                    RecordDestinationFailure(new Diagnostic
                    {
                        Code = DiagnosticCodes.PubPartialFailure,
                        Subsystem = DiagnosticSubsystem.Publication,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Publication,
                        IsBlocking = true,
                        Message = "Destination publication did not return a durable receipt; reconcile the destination before retrying. " + ex.Message,
                        OperationId = result.Manifest.OperationId
                    }, RunOutcome.Unknown);
                }
            }
        }
        else if (preview)
        {
            if (!string.IsNullOrEmpty(result.XbrlJson)) Console.WriteLine(result.XbrlJson);
        }
        else if (output is not null)
        {
            // OG01/OG02 fail-closed publishing: only a publishable full-run result may
            // touch the destination, and it goes through PublicationService (stage ->
            // verify -> atomic replace -> receipt under a run journal), never a direct
            // file write. Anything else — cancelled, invalid mapping, data error, an
            // incomplete or unauthorized run, or a publication failure mid-replace —
            // leaves a prior accepted file byte-identical and issues no receipt.
            if (!result.IsPublishable)
            {
                Console.Error.WriteLine(
                    $"Run {result.Manifest.RunId} finished as {result.Manifest.Outcome}; "
                    + "destination left unchanged and no publication receipt issued.");
            }
            else
            {
                try
                {
                    var publisher = new PublicationService(new RunJournal());
                    PublicationReceipt receipt;
                    long payloadBytes;
                    if (result.LargeArtifact is { } largeArtifact)
                    {
                        receipt = await publisher.PublishAuthorizedLargeArtifactAsync(
                            output, largeArtifact, workspaceRoot, p, profile, allowSyntheticProfile, result,
                            (stagedPath, token) => service.ValidateStagedArtifactAsync(
                                stagedPath, output, result, token, selectedOutputKind), cancellationToken,
                            outputKind: selectedOutputKind);
                        payloadBytes = largeArtifact.Bytes;
                    }
                    else
                    {
                        var payload = selectedOutputPayload
                            ?? System.Text.Encoding.UTF8.GetBytes(result.XbrlJson);
                        receipt = await publisher.PublishAuthorizedAsync(
                            output, payload, workspaceRoot, p, profile, allowSyntheticProfile, result,
                            (stagedPath, token) => service.ValidateStagedArtifactAsync(
                                stagedPath, output, result, token, selectedOutputKind), cancellationToken,
                            outputKind: selectedOutputKind);
                        payloadBytes = payload.Length;
                    }
                    Console.Error.WriteLine(
                        $"Published {receipt.DestinationPath} ({payloadBytes} bytes) "
                        + $"run={receipt.RunId} digest={receipt.FinalDigest}.");
                }
                catch (PublicationException ex)
                {
                    RecordDestinationFailure(ex.ToDiagnostic(), PublicationFailureOutcome(ex));
                }
                catch (OperationCanceledException)
                {
                    RecordDestinationFailure(new Diagnostic
                    {
                        Code = DiagnosticCodes.MapExecutionCancelled,
                        Subsystem = DiagnosticSubsystem.Publication,
                        Severity = DiagnosticSeverity.Error,
                        Stage = DiagnosticStage.Publication,
                        IsBlocking = true,
                        Message = "Staged-artifact validation or publication was cancelled; the previous destination was preserved.",
                        OperationId = result.Manifest.OperationId
                    }, RunOutcome.Cancelled);
                }
            }
        }

        if (manifestPath is not null)
        {
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(result.Manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        return ExitCodesLocal.FromOutcome(result.Manifest.Outcome);
    }
    catch (ProjectSchemaException ex)
    {
        Console.Error.WriteLine($"PRJ: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.IncompleteProfile;
    }
    catch (ProjectSaveException ex)
    {
        Console.Error.WriteLine($"PRJ: {ex.Code}: {ex.Message}");
        return ExitCodesLocal.OperationalFailure;
    }
    catch (AdapterException ex)
    {
        Console.Error.WriteLine($"IO: {ex.Code}: {ex.Message}");
        return ex.Code == DiagnosticCodes.IoInputIdentityChanged
            ? ExitCodesLocal.DataError : ExitCodesLocal.AuthorizationFailure;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("CLI_CANCELLED: Operation cancelled; inspect publication receipts before retrying.");
        return ExitCodesLocal.Cancelled;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"CLI_OPERATIONAL_FAILURE: {ex.GetType().Name}: {ex.Message}");
        return ExitCodesLocal.OperationalFailure;
    }
}

static (string, string) ParsePair(string s)
{
    var idx = s.IndexOf('=');
    if (idx <= 0) throw new ArgumentException($"Expected 'key=value' but got '{s}'.");
    return (s.Substring(0, idx), s.Substring(idx + 1));
}

static void PrintDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
{
    foreach (var d in diagnostics)
    {
        var tag = d.Severity.ToString().ToUpperInvariant();
        var loc = "";
        if (d.Source is not null) loc += $" [{d.Source}";
        if (d.NodeId is not null) loc += (loc.Length == 0 ? " [node=" : $" node={d.NodeId}");
        if (d.PortName is not null) loc += $" port={d.PortName}";
        if (loc.Length > 0 && !loc.EndsWith(']')) loc += "]";
        Console.Error.WriteLine($"{tag} {d.Code} {d.Subsystem}/{d.Stage}: {d.Message}{loc}");
    }
}
