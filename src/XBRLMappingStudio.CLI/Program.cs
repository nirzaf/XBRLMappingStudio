using System.Text.Json;
using XBRLMappingStudio.Adapters;
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

if (args.Length == 0)
{
    PrintUsage();
    return ExitCodesLocal.IncompleteProfile;
}

var command = args[0];
var rest = args.Skip(1).ToArray();

return command switch
{
    "inspect" => Inspect(rest),
    "validate" => await ValidateAsync(rest),
    "taxonomy-search" => TaxonomySearch(rest),
    "validate-artifact" => await ValidateArtifactAsync(rest),
    "inspect-run-result" => InspectRunResult(rest),
    "support-bundle" => SupportBundleCommand(rest),
    "execute" => await ExecuteAsync(rest, preview: false),
    "preview" => await ExecuteAsync(rest, preview: true),
    "--help" or "-h" or "help" => PrintUsage(),
    _ => Unknown(command)
};

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
    Console.Error.WriteLine("  xbrl-studio validate <project.xmap>");
    Console.Error.WriteLine("  xbrl-studio execute  <project.xmap> --binding <id>=<path> --param <k>=<v> --output <path> [--allow-synthetic-profile]");
    Console.Error.WriteLine("                       [--workspace-root <dir>] [--arelle [--python <exe>] [--taxonomy-root <dir>]]");
    Console.Error.WriteLine("  xbrl-studio preview  <project.xmap> --binding <id>=<path> --rows 10 [--arelle [--python <exe>] [--taxonomy-root <dir>]]");
    Console.Error.WriteLine("  xbrl-studio taxonomy-search [--query <text>] [--lang <tag>] [--page <n>] [--size <n>]");
    Console.Error.WriteLine("  xbrl-studio validate-artifact <file.xbrl.json>");
    Console.Error.WriteLine("  xbrl-studio validate-artifact <file.xbrl.json> --arelle [--python <exe>] [--taxonomy-root <dir>] [--package <zip>] [--plugin <name>] [--disclosure-system <name>]");
    Console.Error.WriteLine("  xbrl-studio inspect-run-result <runId>");
    Console.Error.WriteLine("  xbrl-studio support-bundle <project.xmap> <runId> [--out <path>] [--log <file>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Exit codes: 0=success 1=success-with-warnings 2=invalid-mapping 3=data-error");
    Console.Error.WriteLine("            4=incomplete-profile 5=authorization-failure 6=cancelled 7=operational-failure");
    return ExitCodesLocal.Success;
}

static int TaxonomySearch(string[] args)
{
    string query = "";
    string? lang = null;
    int page = 0;
    int size = XBRLMappingStudio.XBRL.TaxonomyIndex.DefaultPageSize;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--query": if (i + 1 < args.Length) query = args[++i]; break;
            case "--lang": if (i + 1 < args.Length) lang = args[++i]; break;
            case "--page": if (i + 1 < args.Length) page = int.Parse(args[++i]); break;
            case "--size": if (i + 1 < args.Length) size = int.Parse(args[++i]); break;
            default: Console.Error.WriteLine($"Unrecognized option: {args[i]}"); return ExitCodesLocal.IncompleteProfile;
        }
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
static async Task<int> ValidateArtifactAsync(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("validate-artifact requires <file>"); return ExitCodesLocal.IncompleteProfile; }
    if (!File.Exists(args[0])) { Console.Error.WriteLine($"Artifact not found: {args[0]}"); return ExitCodesLocal.DataError; }

    bool useArelle = false;
    string? python = null;
    string? taxonomyRoot = null;
    string? disclosureSystem = null;
    var packages = new List<string>();
    var plugins = new List<string>();
    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--arelle": useArelle = true; break;
            case "--python" when i + 1 < args.Length: python = args[++i]; break;
            case "--taxonomy-root" when i + 1 < args.Length: taxonomyRoot = args[++i]; break;
            case "--disclosure-system" when i + 1 < args.Length: disclosureSystem = args[++i]; break;
            case "--package" when i + 1 < args.Length: packages.Add(args[++i]); break;
            case "--plugin" when i + 1 < args.Length: plugins.Add(args[++i]); break;
            default: Console.Error.WriteLine($"Unrecognized or incomplete option: {args[i]}"); return ExitCodesLocal.IncompleteProfile;
        }
    }

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
            await using var worker = new ArelleWorker(python ?? (OperatingSystem.IsWindows() ? "python" : "python3"), workerArgs.ToArray());
            var handshake = await worker.HandshakeAsync(CancellationToken.None);
            if (!handshake.Capabilities.Contains(WorkerOperations.ValidateReport, StringComparer.Ordinal))
            {
                Console.Error.WriteLine("Arelle worker does not support report validation.");
                return ExitCodesLocal.IncompleteProfile;
            }
            var request = new WorkerRequest(
                WorkerOperations.ValidateReport,
                Guid.NewGuid().ToString("N"),
                "cli-validate-artifact",
                "-",
                DateTimeOffset.UtcNow.AddMinutes(2),
                JsonSerializer.SerializeToElement(File.ReadAllText(args[0])));
            var response = await worker.RequestAsync(request, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                artifact = Path.GetFileName(args[0]),
                engine = handshake.EngineVersion,
                status = response.Status.ToString(),
                result = response.Result,
                diagnostics = response.Diagnostics,
                error = response.Error
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return response.Status switch
            {
                WorkerStatus.Success when !(response.Diagnostics ?? Array.Empty<Diagnostic>()).Any(d => d.IsBlocking) => ExitCodesLocal.Success,
                WorkerStatus.Success or WorkerStatus.DomainFailure => ExitCodesLocal.InvalidMapping,
                WorkerStatus.Cancelled => ExitCodesLocal.Cancelled,
                WorkerStatus.IncompleteValidation => ExitCodesLocal.IncompleteProfile,
                _ => ExitCodesLocal.OperationalFailure
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Arelle validation could not start or complete: {ex.Message}");
            return ExitCodesLocal.IncompleteProfile;
        }
    }

    var text = File.ReadAllText(args[0]);
    var facts = OimSerializer.ReadFacts(text);
    var result = new FilingRuleValidator(SyntheticProfile.Build()).Validate(facts);

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
    Console.WriteLine($"Digest:         {p.ComputeSemanticDigest()}");
    foreach (var n in p.Nodes)
        Console.WriteLine($"  node  {n.NodeId} [{n.NodeType}] {n.DisplayName}");
    foreach (var e in p.Edges)
        Console.WriteLine($"  edge  {e.EdgeId}: {e.SourceNodeId}.{e.SourcePortName} -> {e.TargetNodeId}.{e.TargetPortName}");
    return ExitCodesLocal.Success;
}

static async Task<int> ValidateAsync(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("validate requires <project>"); return ExitCodesLocal.IncompleteProfile; }
    var pm = new ProjectManager(args[0]);
    var p = pm.Load();
    var profile = SyntheticProfile.Build();
    await using var worker = new LocalSyntheticWorker(profile);
    var service = new MappingJobService(profile, worker);
    var outcome = service.Validate(p);
    PrintDiagnostics(outcome.Diagnostics.Items);
    if (outcome.Success) return ExitCodesLocal.Success;
    return outcome.Diagnostics.Items.Any(d => d.Code == DiagnosticCodes.XbrProfileMismatch)
        ? ExitCodesLocal.IncompleteProfile
        : ExitCodesLocal.InvalidMapping;
}

static async Task<int> ExecuteAsync(string[] args, bool preview)
{
    if (args.Length < 1) { Console.Error.WriteLine("requires <project>"); return ExitCodesLocal.IncompleteProfile; }
    var projectPath = args[0];
    var bindings = new List<(string id, string path)>();
    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    string? output = null;
    string? manifestPath = null;
    string? workspaceRootArg = null;
    int rows = 10;
    bool allowSyntheticProfile = false;
    bool useArelle = false;
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
            case "--param":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--param requires name=value"); return ExitCodesLocal.IncompleteProfile; }
                var (k, v) = ParsePair(args[++i]);
                parameters[k] = v;
                break;
            case "--output":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                output = args[++i];
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

    try
    {
        var pm = new ProjectManager(projectPath);
        var p = pm.Load();
        var workspaceRoot = Path.GetFullPath(workspaceRootArg ?? Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        string GuardPath(string path) => PathGuard.EnsureWithin(workspaceRoot,
            Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));

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
        if (output is not null) output = GuardPath(output);
        if (manifestPath is not null) manifestPath = GuardPath(manifestPath);

        var profile = SyntheticProfile.Build();

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

        IWorkerClient worker;
        if (useArelle)
        {
            var script = FindArelleWorkerScript();
            var root = taxonomyRoot ?? FindArelleTaxonomyRoot();
            if (script is null || root is null)
            {
                Console.Error.WriteLine("Arelle worker or taxonomy package was not found; provide --taxonomy-root and run from a checkout or packaged CLI.");
                return ExitCodesLocal.IncompleteProfile;
            }
            worker = new ArelleWorker(python ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
                new[] { script, "--taxonomy-root", Path.GetFullPath(root) });
        }
        else
        {
            worker = new LocalSyntheticWorker(profile);
        }
        await using var workerLifetime = worker;
        var service = new MappingJobService(profile, worker);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var result = await service.RunAsync(p, conns, parameters, preview, cts.Token);
        PrintDiagnostics(result.Diagnostics.Items);

        if (preview)
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
                    var receipt = publisher.PublishAuthorized(
                        output,
                        System.Text.Encoding.UTF8.GetBytes(result.XbrlJson),
                        workspaceRoot,
                        p,
                        profile,
                        allowSyntheticProfile,
                        result);
                    Console.Error.WriteLine(
                        $"Published {receipt.DestinationPath} ({result.XbrlJson.Length} bytes) "
                        + $"run={receipt.RunId} digest={receipt.FinalDigest}.");
                }
                catch (PublicationException ex)
                {
                    Console.Error.WriteLine($"PUB: {ex.Code}: {ex.Message}");
                    return ExitCodesLocal.OperationalFailure;
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
        return ExitCodesLocal.AuthorizationFailure;
    }
    catch (OperationCanceledException)
    {
        return ExitCodesLocal.Cancelled;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Uncaught: " + ex);
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
