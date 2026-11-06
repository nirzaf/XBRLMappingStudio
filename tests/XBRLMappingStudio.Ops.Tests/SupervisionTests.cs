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
    public async Task SingleOversizedNewlineFreeRecord_IsDrainedWithBoundedCapture()
    {
        var supervisor = new WorkerSupervisor(maxLogBytes: 4096);
        var (exe, args) = Shell(
            "head -c 1048576 /dev/zero | tr '\\000' x",
            "[Console]::Out.Write(('x' * 1048576)); [Console]::Out.Flush()");

        var result = await supervisor.RunAsync(exe, args, TimeSpan.FromSeconds(30));

        var capturedBytes = result.CapturedLog.Sum(line => System.Text.Encoding.UTF8.GetByteCount(line) + 1);
        Assert.Equal(SupervisedOutcome.Completed, result.Outcome);
        Assert.True(result.LogTruncated);
        Assert.True(capturedBytes <= 4096);
        Assert.Single(result.CapturedLog);
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

    [Fact]
    public async Task WatchdogDeadline_CoversBlockedStdinWrite()
    {
        var supervisor = new WorkerSupervisor();
        var (exe, args) = Shell("sleep 30", "Start-Sleep -Seconds 30");
        var started = DateTimeOffset.UtcNow;

        var result = await supervisor.RunAsync(exe, args, TimeSpan.FromMilliseconds(400),
            stdinPayload: new string('x', 32 * 1024 * 1024));

        Assert.Equal(SupervisedOutcome.TimedOut, result.Outcome);
        Assert.Equal(DiagnosticCodes.WkrTimeout, result.Failure!.Code);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.False(result.OrphanDetected);
    }

    [Fact]
    public async Task CallerCancellation_KillsWindowsOrUnixChildTree()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "xms-supervisor-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var pidPath = Path.Combine(workspace, "processes.txt");
        var quotedPidPath = OperatingSystem.IsWindows()
            ? "'" + pidPath.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + pidPath.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var (exe, args) = Shell(
            $"sleep 60 & child=$!; printf '%s\\n%s\\n' \"$$\" \"$child\" > {quotedPidPath}; wait",
            $"$child = Start-Process -FilePath 'ping.exe' -ArgumentList @('-t','127.0.0.1') -PassThru -WindowStyle Hidden; [System.IO.File]::WriteAllLines({quotedPidPath}, @([string]$PID, [string]$child.Id)); while ($true) {{ Start-Sleep -Seconds 1 }}");

        using var cancellation = new CancellationTokenSource();
        try
        {
            var supervisor = new WorkerSupervisor();
            var running = supervisor.RunAsync(exe, args, TimeSpan.FromSeconds(30),
                cancellationToken: cancellation.Token);
            Assert.True(await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(5)),
                "The supervised process tree did not publish its test PIDs.");
            var processIds = (await File.ReadAllLinesAsync(pidPath))
                .Select(line => int.Parse(line, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(2, processIds.Length);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            foreach (var processId in processIds)
                Assert.True(await WaitForProcessExitAsync(processId, TimeSpan.FromSeconds(5)),
                    $"Supervised process {processId} remained alive after caller cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PipeDrainDeadlineAfterParentExit_IsTimedOutAndReportsUnreachableDescendant()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "xms-supervisor-inherited-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var pidPath = Path.Combine(workspace, "child.pid");
        var quotedPidPath = OperatingSystem.IsWindows()
            ? "'" + pidPath.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + pidPath.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var (exe, args) = Shell(
            $"IFS= read -r launch; sleep 60 & child=$!; echo \"$child\" > {quotedPidPath}; exit 0",
            $"$null = [Console]::In.ReadLine(); $start = New-Object System.Diagnostics.ProcessStartInfo; $start.FileName = 'ping.exe'; $start.Arguments = '-t 127.0.0.1'; $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $child = [System.Diagnostics.Process]::Start($start); [System.IO.File]::WriteAllText({quotedPidPath}, [string]$child.Id); exit 0");
        int? childId = null;
        try
        {
            var running = new WorkerSupervisor().RunAsync(exe, args,
                TimeSpan.FromSeconds(5), stdinPayload: "launch\n");
            Assert.True(await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(5)),
                "The parent did not start its pipe-inheriting child.");
            childId = int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
            var result = await running.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(SupervisedOutcome.TimedOut, result.Outcome);
            Assert.Equal(DiagnosticCodes.WkrTimeout, result.Failure!.Code);
            Assert.False(result.OrphanDetected,
                $"The process group/job cleanup reported an orphan; outcome={result.Outcome}, exit={result.ExitCode}, logs={string.Join(" | ", result.CapturedLog)}");
            Assert.True(await WaitForProcessExitAsync(childId.Value, TimeSpan.FromSeconds(3)),
                "The supervised process group/job must kill descendants after the direct parent exits.");
        }
        finally
        {
            if (childId is not null) TryKillProcess(childId.Value);
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(25);
        }
        return File.Exists(path);
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive(processId)) return true;
            await Task.Delay(50);
        }
        return !IsProcessAlive(processId);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}
