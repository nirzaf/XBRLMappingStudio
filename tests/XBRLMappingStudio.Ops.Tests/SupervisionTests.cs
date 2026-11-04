using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Worker;

namespace XBRLMappingStudio.Ops.Tests;

/// <summary>
/// OPS03 — supervision of a real subprocess: stdout flood stays bounded, a crashing
/// worker yields a distinct terminal outcome from a timeout, watchdog kills the tree,
/// and the next job starts clean. Exercises the same supervision the Arelle Python
/// host will run under (the interpreter itself is owner-gated for real profiles).
/// </summary>
public class SupervisionTests
{
    // Two script variants per scenario: POSIX sh on Unix, PowerShell on Windows.
    // Handing bash syntax to cmd.exe (the previous Windows branch) fails to parse
    // loops / `; exit N` / `sleep`, producing wrong exit codes and outcomes.
    private static (string Exe, string[] Args) Shell(string posixScript, string windowsScript) =>
        OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", windowsScript })
            : ("/bin/sh", new[] { "-c", posixScript });

    [Fact]
    public async Task FloodedStdout_IsBoundedByBudget()
    {
        var supervisor = new WorkerSupervisor(maxLogBytes: 4096);
        var (exe, args) = Shell(
            "i=0; while [ $i -lt 2000 ]; do echo \"flood-line-$i\"; i=$((i+1)); done",
            "1..2000 | ForEach-Object { \"flood-line-$_\" }");

        var result = await supervisor.RunAsync(exe, args, TimeSpan.FromSeconds(30));

        var capturedBytes = result.CapturedLog.Sum(l => System.Text.Encoding.UTF8.GetByteCount(l) + 1);
        Assert.Equal(SupervisedOutcome.Completed, result.Outcome);
        Assert.True(result.LogTruncated);
        Assert.True(capturedBytes <= 4096 + 1024); // bounded, head kept
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task CrashingWorker_DistinctTerminalOutcome_AndExitCodePreserved()
    {
        var supervisor = new WorkerSupervisor();
        var (exe, args) = Shell("echo dying; exit 7", "Write-Output dying; exit 7");

        var result = await supervisor.RunAsync(exe, args, TimeSpan.FromSeconds(30));

        Assert.Equal(SupervisedOutcome.Crashed, result.Outcome);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(DiagnosticCodes.WkrCrash, result.Failure!.Code);
        Assert.Contains("dying", string.Join('\n', result.CapturedLog));
    }

    [Fact]
    public async Task WatchdogDeadline_KillsRunawayWorker()
    {
        var supervisor = new WorkerSupervisor();
        var (exe, args) = Shell("echo starting; sleep 30; echo never", "Write-Output starting; Start-Sleep 30; Write-Output never");

        var started = DateTimeOffset.UtcNow;
        var result = await supervisor.RunAsync(exe, args, TimeSpan.FromMilliseconds(400));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(SupervisedOutcome.TimedOut, result.Outcome);
        Assert.Equal(DiagnosticCodes.WkrTimeout, result.Failure!.Code);
        Assert.True(elapsed.TotalSeconds < 10, $"watchdog took {elapsed}");
        Assert.False(result.OrphanDetected); // tree kill completed
        Assert.DoesNotContain("never", string.Join('\n', result.CapturedLog));
    }

    [Fact]
    public async Task NextJobStartsClean_AfterSupervisedFailure()
    {
        var supervisor = new WorkerSupervisor();
        var (badExe, badArgs) = Shell("exit 3", "exit 3");
        await supervisor.RunAsync(badExe, badArgs, TimeSpan.FromSeconds(10));

        var (okExe, okArgs) = Shell("echo healthy", "Write-Output healthy");
        var result = await supervisor.RunAsync(okExe, okArgs, TimeSpan.FromSeconds(10));

        Assert.Equal(SupervisedOutcome.Completed, result.Outcome);
        Assert.Contains("healthy", string.Join('\n', result.CapturedLog));
    }

    [Fact]
    public async Task StdinPayload_IsDelimitedToWorker_AndBoundedResponseCaptured()
    {
        // 'cat' echoes stdin on stdout: proves framed hand-off works end to end
        // with the same pipe budgeting the protocol reader will use.
        var supervisor = new WorkerSupervisor(maxLogBytes: 1024);
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "more" })
            : ("/bin/cat", Array.Empty<string>());

        var result = await supervisor.RunAsync(exe, args, TimeSpan.FromSeconds(15),
            stdinPayload: "hello-supervisor\n");

        Assert.Equal(SupervisedOutcome.Completed, result.Outcome);
        Assert.Contains("hello-supervisor", string.Join('\n', result.CapturedLog));
    }
}
