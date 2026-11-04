using System.Diagnostics;
using System.Text.Json;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Fixtures;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Integration.Tests;

/// <summary>
/// OPS01: run the compiled CLI (xbrl-studio) as a subprocess and confirm the
/// emitted xBRL-JSON matches the in-process execution byte-for-byte (modulo
/// per-run fact identifiers). Also verifies exit-code contract.
/// </summary>
public sealed class CliParityTests : IAsyncLifetime
{
    private string _dir = "";
    private string _workbookPath = "";

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xbrl-cli-" + Guid.NewGuid().ToString("N"));
        _workbookPath = TestFixtures.MaterializeWorkbook(_dir);
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // The CLI is launched as a real subprocess. We run the framework-dependent
    // apphost the SDK emits next to xbrl-studio.dll (xbrl-studio.exe on Windows,
    // xbrl-studio on Unix) so we never depend on locating the `dotnet` muxer —
    // on the Windows vstest host Process.MainModule is testhost.exe, not dotnet,
    // so invoking `dotnet <dll>` via MainModule breaks there. Falls back to
    // `dotnet <dll>` (resolved on PATH) when no apphost is present.
    private static (string FileName, bool IsAppHost) LocateCli()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var binRoot = Path.Combine(repoRoot, "src", "XBRLMappingStudio.CLI", "bin");

        string? PickDll() =>
            Directory.EnumerateFiles(binRoot, "xbrl-studio.dll", SearchOption.AllDirectories)
                .OrderBy(p => p.Contains("Release") ? 0 : 1)
                .FirstOrDefault();

        var dll = PickDll();
        if (dll is null)
            throw new FileNotFoundException("xbrl-studio.dll not built.");

        var appHost = Path.Combine(Path.GetDirectoryName(dll)!,
            OperatingSystem.IsWindows() ? "xbrl-studio.exe" : "xbrl-studio");
        return File.Exists(appHost) ? (appHost, true) : (dll, false);
    }

    private static ProcessStartInfo BuildStartInfo(
        (string FileName, bool IsAppHost) cli, string verb, params string[] extra)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (cli.IsAppHost)
        {
            psi.FileName = cli.FileName;
        }
        else
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(cli.FileName);
        }
        psi.ArgumentList.Add(verb);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        return psi;
    }

    [Fact]
    public async Task Cli_ExecuteMatchesInProcess_WhenGivenSavedProject()
    {
        // A production (non-preview) execute must clear the EG03/XG01 publication gate:
        // an approved project plus an explicit synthetic-profile acknowledgment.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "revenue.xmap");
        var pm = new ProjectManager(projectPath);
        pm.Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "cli-out.json");
        var manifestPath = Path.Combine(_dir, "cli-manifest.json");

        var psi = BuildStartInfo(cli, "execute", projectPath,
            "--output", outputPath, "--manifest", manifestPath, "--allow-synthetic-profile");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("CLI process failed to start.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.True(proc.ExitCode == 0,
            $"Expected exit 0 but got {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.True(File.Exists(outputPath),
            $"Expected output file at {outputPath}.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        // Reopen and compare with the in-process service result.
        var cliJson = await File.ReadAllTextAsync(outputPath);
        using var doc = JsonDocument.Parse(cliJson);
        Assert.True(doc.RootElement.TryGetProperty("facts", out var facts));
        var first = facts.EnumerateObject().First();
        var actual = first.Value;
        Assert.Equal("synthetic:Revenue", actual.GetProperty("concept").GetString());
        Assert.Equal(1_250_000, actual.GetProperty("value").GetInt64());
        Assert.Equal("QAR", actual.GetProperty("unit").GetString());
        Assert.Equal(-3, actual.GetProperty("decimals").GetInt32());
    }

    [Fact]
    public async Task Cli_ValidateReturnsExitCode_Two_ForInvalidMapping()
    {
        // Introduce an unknown node type so validation blocks with a stable diagnostic.
        var seed = Guid.Parse("44444444-1111-2222-3333-444444444444");
        var project = TestFixtures.BuildRevenueMappingProject(idSeed: seed, workbookPath: _workbookPath);
        project.Nodes.Add(new MappingNode
        {
            NodeId = Id.New(),
            NodeType = "quantumFlanger",
            DisplayName = "Unknown"
        });

        var projectPath = Path.Combine(_dir, "bad.xmap");
        var pm = new ProjectManager(projectPath);
        pm.Save(project);

        var cli = LocateCli();
        var psi = BuildStartInfo(cli, "validate", projectPath);

        using var proc = Process.Start(psi)!;
        await proc.StandardOutput.ReadToEndAsync();
        await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        // Invalid mapping exit code = 2 per ExitCodes contract.
        Assert.Equal(2, proc.ExitCode);
    }

    // ---- OG01/OG02: fail-closed publishing through the real CLI process. --------

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(
        (string FileName, bool IsAppHost) cli, string verb, params string[] extra)
    {
        var psi = BuildStartInfo(cli, verb, extra);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("CLI process failed to start.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadRunIdFromManifestAsync(string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("RunId").GetString()
            ?? throw new InvalidOperationException("Manifest missing RunId.");
    }

    [Fact]
    public async Task Cli_ExecutePublishes_ThroughPublicationService_AndReceiptIsInspectable()
    {
        // OG02: a valid, approved production run writes through the four-phase publisher,
        // and its receipt must be retrievable by run id via the inspect-run-result verb.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "publish-ok.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "publish-ok-out.json");
        var manifestPath = Path.Combine(_dir, "publish-ok-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--manifest", manifestPath, "--allow-synthetic-profile");
        Assert.True(exit == 0, $"Expected exit 0, got {exit}.\nstderr:\n{stderr}");
        Assert.True(File.Exists(outputPath));

        var runId = await ReadRunIdFromManifestAsync(manifestPath);
        var (inspectExit, inspectStdout, inspectStderr) = await RunCliAsync(cli, "inspect-run-result", runId);
        Assert.True(inspectExit == 0,
            $"inspect-run-result should find the receipt, got exit {inspectExit}.\nstderr:\n{inspectStderr}");
        using var doc = JsonDocument.Parse(inspectStdout);
        // JournalState.Finalized == 3: the run completed every publication phase.
        Assert.Equal(3, doc.RootElement.GetProperty("State").GetInt32());
        Assert.Equal(Path.GetFullPath(outputPath).Replace('\\', '/'),
            doc.RootElement.GetProperty("DestinationPath").GetString()?.Replace('\\', '/'));
    }

    [Fact]
    public async Task Cli_ExecuteInvalidMapping_PreservesPriorOutput_AndIssuesNoReceipt()
    {
        // OG01 core sentinel: seed a prior accepted destination. An invalid graph must
        // leave it byte-identical and must not create a run-journal entry.
        var priorBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
        var outputPath = Path.Combine(_dir, "fail-closed-out.json");
        await File.WriteAllBytesAsync(outputPath, priorBytes);

        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        project.Nodes.Add(new MappingNode
        {
            NodeId = Id.New(),
            NodeType = "quantumFlanger",
            DisplayName = "Unknown"
        });
        var projectPath = Path.Combine(_dir, "fail-closed.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var manifestPath = Path.Combine(_dir, "fail-closed-manifest.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--manifest", manifestPath, "--allow-synthetic-profile");

        Assert.Equal(2, exit); // InvalidMapping
        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(outputPath));
        Assert.Contains("no publication receipt issued", stderr, StringComparison.Ordinal);

        var runId = await ReadRunIdFromManifestAsync(manifestPath);
        var (inspectExit, _, _) = await RunCliAsync(cli, "inspect-run-result", runId);
        Assert.Equal(3, inspectExit); // DataError: "No journal entry for run" — never published.
    }

    // ---- EG03/XG01: profile identity + approval gate before production publish. ----

    [Fact]
    public async Task Cli_ExecuteWithoutApproval_IsBlockedAsAuthorizationFailure_AndWritesNothing()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: false);
        var projectPath = Path.Combine(_dir, "no-approval.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "no-approval-out.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--allow-synthetic-profile");

        Assert.Equal(5, exit); // AuthorizationFailure
        Assert.False(File.Exists(outputPath));
        Assert.Contains("XBR016", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ExecuteWithoutSyntheticOptIn_IsBlocked_AndWritesNothing()
    {
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        var projectPath = Path.Combine(_dir, "no-synthetic-flag.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "no-synthetic-flag-out.json");

        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath, "--output", outputPath);

        Assert.Equal(4, exit); // IncompleteProfile
        Assert.False(File.Exists(outputPath));
        Assert.Contains("XBR017", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_ExecuteWithSubstitutedProfile_IsBlocked_AndPreservesPriorOutput()
    {
        const string priorContent = "PRIOR-ACCEPTED";
        var outputPath = Path.Combine(_dir, "substituted-out.json");
        File.WriteAllText(outputPath, priorContent);

        var approved = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: true);
        // A project created against a different (e.g. a real, non-synthetic) taxonomy
        // profile must be refused outright rather than executed under the synthetic one.
        var substituted = new MappingProject
        {
            ProjectId = approved.ProjectId,
            Name = approved.Name,
            ProfileId = "qatar-real-taxonomy-v1",
            ProfileDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            FunctionCatalogVersion = approved.FunctionCatalogVersion,
            SemanticRevision = approved.SemanticRevision,
            Metadata = approved.Metadata,
            Nodes = approved.Nodes,
            Edges = approved.Edges,
            Bindings = approved.Bindings,
            Functions = approved.Functions,
            Approvals = approved.Approvals,
        };
        var projectPath = Path.Combine(_dir, "substituted.xmap");
        new ProjectManager(projectPath).Save(substituted);

        var cli = LocateCli();
        var (exit, _, stderr) = await RunCliAsync(cli, "execute", projectPath,
            "--output", outputPath, "--allow-synthetic-profile");

        Assert.Equal(4, exit); // IncompleteProfile (profile identity mismatch)
        Assert.Equal(priorContent, File.ReadAllText(outputPath)); // unchanged
        Assert.Contains("XBR015", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_PreviewDoesNotRequireApproval_OrSyntheticOptIn()
    {
        // EG03 boundary: a draft preview stays available on an unapproved, unacknowledged
        // revision so a reviewer can inspect before accepting — only publish gates close.
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath, approved: false);
        var projectPath = Path.Combine(_dir, "preview-no-approval.xmap");
        new ProjectManager(projectPath).Save(project);

        var cli = LocateCli();
        var (exit, stdout, stderr) = await RunCliAsync(cli, "preview", projectPath);

        Assert.True(exit is 0 or 1, $"Expected preview to run, got exit {exit}.\nstderr:\n{stderr}");
        Assert.Contains("synthetic:Revenue", stdout, StringComparison.Ordinal);
    }
}
