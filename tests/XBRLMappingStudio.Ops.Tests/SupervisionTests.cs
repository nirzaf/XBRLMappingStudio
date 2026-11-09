using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Worker;

namespace XBRLMappingStudio.Ops.Tests;

[CollectionDefinition("Worker process supervision", DisableParallelization = true)]
public sealed class WorkerProcessSupervisionCollection { }

/// <summary>
/// OPS03 — supervision of a real subprocess: stdout flood stays bounded, a crashing
/// worker yields a distinct terminal outcome from a timeout, watchdog kills the tree,
/// and the next job starts clean. Exercises the same supervision the Arelle Python
/// host will run under (the interpreter itself is owner-gated for real profiles).
/// </summary>
[Collection("Worker process supervision")]
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
        await supervisor.RunAsync(badExe, badArgs, TimeSpan.FromSeconds(30));

        var (okExe, okArgs) = Shell("echo healthy", "Write-Output healthy");
        var result = await supervisor.RunAsync(okExe, okArgs, TimeSpan.FromSeconds(30));

        Assert.Equal(SupervisedOutcome.Completed, result.Outcome);
        Assert.Contains("healthy", string.Join('\n', result.CapturedLog));
    }

    [Fact]
    public async Task ConcurrentWorkers_AreBoundedAcrossSupervisorInstances()
    {
        const int workerCount = 8;
        const int maximumWorkers = 2;
        var workspace = Path.Combine(Path.GetTempPath(), "xms-worker-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var quotedRelease = OperatingSystem.IsWindows()
            ? "'" + Path.Combine(workspace, "release").Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + Path.Combine(workspace, "release").Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var quotedWorkspace = OperatingSystem.IsWindows()
            ? "'" + workspace.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + workspace.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                $"$marker = Join-Path {quotedWorkspace} ('worker-' + $PID); " +
                "[System.IO.File]::WriteAllText($marker, [string]$PID); " +
                $"while (-not (Test-Path -LiteralPath {quotedRelease})) {{ Start-Sleep -Milliseconds 10 }}; " +
                "Remove-Item -LiteralPath $marker -Force"
            })
            : ("/bin/sh", new[]
            {
                "-c",
                $"marker={quotedWorkspace}/worker-$$; : > \"$marker\"; " +
                $"while [ ! -f {quotedRelease} ]; do sleep 0.01; done; rm -f \"$marker\""
            });

        try
        {
            var runs = Enumerable.Range(0, workerCount)
                .Select(_ => new WorkerSupervisor().RunAsync(exe, args, TimeSpan.FromSeconds(15)))
                .ToArray();
            var allRuns = Task.WhenAll(runs);
            var maximumObserved = 0;
            var observationDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!allRuns.IsCompleted && maximumObserved < maximumWorkers && DateTime.UtcNow < observationDeadline)
            {
                maximumObserved = Math.Max(maximumObserved,
                    Directory.EnumerateFiles(workspace, "worker-*").Count());
                await Task.Delay(10);
            }

            // Keep the first active workers in place long enough for any process that
            // incorrectly bypasses the shared capacity gate to become observable.
            if (!allRuns.IsCompleted && maximumObserved == maximumWorkers)
            {
                var overCapacityObservationDeadline = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < overCapacityObservationDeadline)
                {
                    maximumObserved = Math.Max(maximumObserved,
                        Directory.EnumerateFiles(workspace, "worker-*").Count());
                    await Task.Delay(10);
                }
            }

            File.WriteAllText(Path.Combine(workspace, "release"), "release workers");

            var results = await allRuns.WaitAsync(TimeSpan.FromSeconds(15));
            maximumObserved = Math.Max(maximumObserved,
                Directory.EnumerateFiles(workspace, "worker-*").Count());

            Assert.All(results, result => Assert.Equal(SupervisedOutcome.Completed, result.Outcome));
            Assert.InRange(maximumObserved, 1, maximumWorkers);
        }
        finally
        {
            try { File.WriteAllText(Path.Combine(workspace, "release"), "release workers"); } catch (IOException) { }
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task DeadlineWhileWaitingForWorkerSlot_DoesNotStartAnotherProcess()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "xms-worker-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var quotedWorkspace = OperatingSystem.IsWindows()
            ? "'" + workspace.Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + workspace.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var activeScript = OperatingSystem.IsWindows()
            ? $"$marker = Join-Path {quotedWorkspace} ('active-' + $PID); " +
              "[System.IO.File]::WriteAllText($marker, [string]$PID); " +
              "Start-Sleep -Milliseconds 1400; Remove-Item -LiteralPath $marker -Force"
            : $"marker={quotedWorkspace}/active-$$; : > \"$marker\"; sleep 1.4; rm -f \"$marker\"";
        var queuedScript = OperatingSystem.IsWindows()
            ? $"$marker = Join-Path {quotedWorkspace} 'queued'; " +
              "[System.IO.File]::WriteAllText($marker, 'started')"
            : $"marker={quotedWorkspace}/queued; : > \"$marker\"";
        var (exe, activeArgs) = Shell(activeScript, activeScript);
        var (_, queuedArgs) = Shell(queuedScript, queuedScript);

        try
        {
            var activeRuns = Enumerable.Range(0, 2)
                .Select(_ => new WorkerSupervisor().RunAsync(exe, activeArgs, TimeSpan.FromSeconds(10)))
                .ToArray();
            var waitUntil = DateTimeOffset.UtcNow.AddSeconds(10);
            while (Directory.EnumerateFiles(workspace, "active-*").Count() < 2
                   && DateTimeOffset.UtcNow < waitUntil)
                await Task.Delay(10);

            Assert.Equal(2, Directory.EnumerateFiles(workspace, "active-*").Count());

            var queued = await new WorkerSupervisor().RunAsync(
                exe, queuedArgs, TimeSpan.FromMilliseconds(150));

            Assert.Equal(SupervisedOutcome.TimedOut, queued.Outcome);
            Assert.Equal(DiagnosticCodes.WkrTimeout, queued.Failure!.Code);
            Assert.False(File.Exists(Path.Combine(workspace, "queued")));
            var activeResults = await Task.WhenAll(activeRuns).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(activeResults, result => Assert.Equal(SupervisedOutcome.Completed, result.Outcome));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ArelleWorkerLeases_ShareCapacityWithSupervisedProcesses()
    {
        var clients = Enumerable.Range(0, WorkerProcessCapacity.MaximumConcurrentProcesses)
            .Select(_ => new BoundedWorkerClient(() => new StubWorkerClient()))
            .ToArray();
        var (exe, args) = Shell("echo supervised", "Write-Output supervised");

        try
        {
            await Task.WhenAll(clients.Select(client =>
                client.HandshakeAsync(CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(15));

            var queued = await new WorkerSupervisor().RunAsync(
                exe, args, TimeSpan.FromMilliseconds(150));
            Assert.Equal(SupervisedOutcome.TimedOut, queued.Outcome);
            Assert.Contains("waiting for a worker slot", queued.Failure!.Message,
                StringComparison.OrdinalIgnoreCase);

            await clients[0].DisposeAsync();
            var admitted = await new WorkerSupervisor().RunAsync(
                exe, args, TimeSpan.FromSeconds(5));
            Assert.Equal(SupervisedOutcome.Completed, admitted.Outcome);
            Assert.Contains("supervised", string.Join('\n', admitted.CapturedLog));
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    [Fact]
    public void ArelleWorker_RejectsNulArgumentBeforeStartingProcess()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new ArelleWorker("unavailable-runtime", new[] { "worker.py", "invalid\0argument" }));

        Assert.Contains("NUL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Supervisor_RejectsUnboundedArgumentEnumerationBeforeStartingProcess()
    {
        static IEnumerable<string> EndlessArguments()
        {
            while (true) yield return "argument";
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new WorkerSupervisor().RunAsync(
            "unavailable-runtime", EndlessArguments(), TimeSpan.FromSeconds(1)));

        Assert.Contains("argument limit", error.Message, StringComparison.OrdinalIgnoreCase);
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
            $"IFS= read -r launch; sleep 60 & child=$!; printf '%s\\n%s\\n' \"$$\" \"$child\" > {quotedPidPath}; wait",
            $"$null = [Console]::In.ReadLine(); $child = Start-Process -FilePath 'ping.exe' -ArgumentList @('-t','127.0.0.1') -PassThru -WindowStyle Hidden; [System.IO.File]::WriteAllLines({quotedPidPath}, @([string]$PID, [string]$child.Id)); while ($true) {{ Start-Sleep -Seconds 1 }}");

        using var cancellation = new CancellationTokenSource();
        Task<SupervisionResult>? running = null;
        try
        {
            var supervisor = new WorkerSupervisor();
            // Windows hosted runners can take longer to start PowerShell when other
            // process-heavy suites are active; cancellation is asserted separately
            // below once the child PIDs are observed.
            running = supervisor.RunAsync(exe, args, TimeSpan.FromSeconds(60),
                stdinPayload: "launch\n", cancellationToken: cancellation.Token);
            var pidFileWait = WaitForFileAsync(pidPath, TimeSpan.FromSeconds(45));
            if (await Task.WhenAny(pidFileWait, running) == running)
            {
                var completed = await running;
                Assert.Fail(
                    $"The supervised process exited before publishing its test PIDs: outcome={completed.Outcome}, exit={completed.ExitCode}, failure={completed.Failure?.Message}, log={string.Join(" | ", completed.CapturedLog)}.");
            }
            Assert.True(await pidFileWait,
                "The supervised process tree did not publish its test PIDs within 15 seconds.");
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
            if (running is not null)
            {
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
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
            // Allow the Windows shell and its child to start on a loaded CI runner;
            // the inherited-pipe timeout is still asserted as the terminal outcome.
            var running = new WorkerSupervisor().RunAsync(exe, args,
                TimeSpan.FromSeconds(30), stdinPayload: "launch\n");
            Assert.True(await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(25)),
                "The parent did not start its pipe-inheriting child.");
            childId = int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
            var result = await running.WaitAsync(TimeSpan.FromSeconds(35));

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

    private sealed class StubWorkerClient : IWorkerClient
    {
        public Task<WorkerHandshake> HandshakeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkerHandshake(
                WorkerProtocol.CurrentVersion,
                "stub/1.0",
                new[] { WorkerOperations.ValidateReport }));
        }

        public Task<WorkerResponse> RequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WorkerResponse(request.OperationId,
                WorkerStatus.Success, null, Array.Empty<Diagnostic>(), null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
