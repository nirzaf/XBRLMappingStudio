using System.Diagnostics;
using XBRLMappingStudio.CLI;

namespace XBRLMappingStudio.Integration.Tests;

public sealed partial class CliParityTests
{
    [Fact]
    public async Task Cli_RecoveryCommandRejectsArgumentsWithoutOpeningLocalState()
    {
        using var process = Process.Start(BuildStartInfo(LocateCli(),
            "recover-file-publications", "unexpected"))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ExitCodes.IncompleteProfile, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Contains("Usage: xbrl-studio recover-file-publications", await stderr);
    }

    [Theory]
    [InlineData("taxonomy-search", "--page", "not-a-number")]
    [InlineData("taxonomy-search", "--size", "999999999999999999999")]
    [InlineData("preview", "missing.xmap", "--rows", "NaN")]
    [InlineData("execute", "missing.xmap", "--binding", "missing-equals")]
    [InlineData("execute", "missing.xmap", "--param", "=missing-key")]
    public async Task Cli_MalformedArgumentsHaveStableExitAndStderr(params string[] arguments)
    {
        using var process = Process.Start(BuildStartInfo(LocateCli(), arguments[0], arguments[1..]))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ExitCodes.IncompleteProfile, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Contains("CLI_INVALID_ARGUMENT:", await stderr);
        Assert.DoesNotContain("Unhandled exception", await stderr);
    }

    [Fact]
    public async Task Cli_UnhandledReadFailureHasOperationalExitRatherThanRuntimeAbort()
    {
        using var process = Process.Start(BuildStartInfo(LocateCli(), "inspect",
            Path.Combine(_dir, "does-not-exist.xmap")))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ExitCodes.OperationalFailure, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Contains("CLI_OPERATIONAL_FAILURE:", await stderr);
    }

    [Theory]
    [InlineData("TERM")]
    [InlineData("INT")]
    public async Task Cli_UnixStopCancelsPendingWorkerAndReturnsStableExit(string signal)
    {
        if (!OperatingSystem.IsLinux()) return;
        // An actual subprocess blocked on the worker handshake exercises signal wiring,
        // token propagation and worker disposal without needing an Arelle installation.
        var worker = Path.Combine(_dir, "pending-worker.sh");
        await File.WriteAllTextAsync(worker, "#!/bin/sh\necho ready > \"$0.ready\"\nexec sleep 60\n");
        File.SetUnixFileMode(worker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var artifact = Path.Combine(_dir, "report.json");
        await File.WriteAllTextAsync(artifact, "{}");
        using var process = Process.Start(BuildStartInfo(LocateCli(), "validate-artifact", artifact,
            "--allow-synthetic-profile", "--arelle", "--python", worker))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(worker + ".ready"))
            {
                Assert.False(process.HasExited, "CLI exited before worker handshake was pending.");
                await Task.Delay(25, timeout.Token);
            }
            using var sender = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                ArgumentList = { "-" + signal, process.Id.ToString() },
                UseShellExecute = false
            })!;
            await sender.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, sender.ExitCode);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(ExitCodes.Cancelled, process.ExitCode);
            Assert.Empty(await stdout);
            Assert.Contains("CLI_STOP_REQUESTED:", await stderr);
            Assert.Contains("CLI_CANCELLED:", await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Cli_LifetimePreservesSuccessfulCommitOutcomeAndMapsCancellation()
    {
        Assert.Equal(ExitCodes.Success, await CliLifetime.RunAsync(_ => Task.FromResult(ExitCodes.Success)));
        Assert.Equal(ExitCodes.SuccessWithWarnings,
            await CliLifetime.RunAsync(_ => Task.FromResult(ExitCodes.SuccessWithWarnings)));
        Assert.Equal(ExitCodes.Cancelled,
            await CliLifetime.RunAsync(_ => Task.FromException<int>(new OperationCanceledException())));
        Assert.Equal(ExitCodes.AuthorizationFailure,
            await CliLifetime.RunAsync(_ => Task.FromException<int>(new UnauthorizedAccessException("denied"))));
    }
}
