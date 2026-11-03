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
    "validate-artifact" => ValidateArtifact(rest),
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
    Console.Error.WriteLine("  xbrl-studio execute  <project.xmap> --binding <id>=<path> --param <k>=<v> --output <path>");
    Console.Error.WriteLine("  xbrl-studio preview  <project.xmap> --binding <id>=<path> --rows 10");
    Console.Error.WriteLine("  xbrl-studio taxonomy-search [--query <text>] [--lang <tag>] [--page <n>] [--size <n>]");
    Console.Error.WriteLine("  xbrl-studio validate-artifact <file.xbrl.json>");
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
static int ValidateArtifact(string[] args)
{
    if (args.Length < 1) { Console.Error.WriteLine("validate-artifact requires <file>"); return ExitCodesLocal.IncompleteProfile; }
    if (!File.Exists(args[0])) { Console.Error.WriteLine($"Artifact not found: {args[0]}"); return ExitCodesLocal.DataError; }

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
    return outcome.Success ? ExitCodesLocal.Success : ExitCodesLocal.InvalidMapping;
}

static async Task<int> ExecuteAsync(string[] args, bool preview)
{
    if (args.Length < 1) { Console.Error.WriteLine("requires <project>"); return ExitCodesLocal.IncompleteProfile; }
    var projectPath = args[0];
    var bindings = new List<(string id, string path)>();
    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    string? output = null;
    string? manifestPath = null;
    int rows = 10;

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
            case "--rows":
                if (i + 1 >= args.Length) return ExitCodesLocal.IncompleteProfile;
                rows = int.Parse(args[++i]);
                break;
            default:
                Console.Error.WriteLine($"Unrecognized option: {args[i]}");
                return ExitCodesLocal.IncompleteProfile;
        }
    }

    try
    {
        var pm = new ProjectManager(projectPath);
        var p = pm.Load();

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
            conns[id] = new SourceConnection(id, "excel", path);

        var profile = SyntheticProfile.Build();
        await using var worker = new LocalSyntheticWorker(profile);
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
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = output + ".tmp";
            await File.WriteAllTextAsync(tmp, result.XbrlJson);
            if (File.Exists(output)) File.Replace(tmp, output, null);
            else File.Move(tmp, output);
            Console.Error.WriteLine($"Wrote {output} ({result.XbrlJson.Length} bytes)");
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
