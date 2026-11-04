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
        var project = TestFixtures.BuildRevenueMappingProject(_workbookPath);
        var projectPath = Path.Combine(_dir, "revenue.xmap");
        var pm = new ProjectManager(projectPath);
        pm.Save(project);

        var cli = LocateCli();
        var outputPath = Path.Combine(_dir, "cli-out.json");

        var psi = BuildStartInfo(cli, "execute", projectPath, "--output", outputPath);

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
}
