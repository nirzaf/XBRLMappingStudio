using System.Diagnostics;
using System.Text;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Worker;

public enum SupervisedOutcome
{
    /// <summary>Process exited 0 within the deadline.</summary>
    Completed,
    /// <summary>Process exited with a non-zero code before any response (distinct terminal outcome).</summary>
    Crashed,
    /// <summary>Watchdog deadline elapsed; the whole process tree was killed.</summary>
    TimedOut,
}

/// <summary>Result of one supervised subprocess round (OPS03).</summary>
public sealed record SupervisionResult(
    SupervisedOutcome Outcome,
    int? ExitCode,
    IReadOnlyList<string> CapturedLog,
    bool LogTruncated,
    bool OrphanDetected,
    Diagnostic? Failure);

/// <summary>
/// Supervises the bundled worker subprocess (the host ArelleWorker runs in) with the
/// three unattended-operations guarantees (OPS03):
///   - bounded stdout/stderr pipes: capture stops at a byte budget but keeps draining,
///     so a flooding worker cannot stall the pipe or balloon memory;
///   - watchdog kill on deadline miss, entire process tree;
///   - orphan check on shutdown: any direct child still alive after supervision is
///     reported so the next job can start clean (and is killed).
/// </summary>
public sealed class WorkerSupervisor
{
    private readonly long _maxLogBytes;

    public WorkerSupervisor(long maxLogBytes = 64 * 1024)
        => _maxLogBytes = maxLogBytes;

    public async Task<SupervisionResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan deadline,
        string? stdinPayload = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{executable}'.");

        var log = new BoundedLog(_maxLogBytes);
        var stdoutTask = PumpAsync(process.StandardOutput);
        var stderrTask = PumpAsync(process.StandardError);

        if (stdinPayload is not null)
        {
            await process.StandardInput.WriteAsync(stdinPayload);
            process.StandardInput.Flush();
        }
        try { process.StandardInput.Close(); } catch (IOException) { /* already gone */ }

        using var watchdog = new CancellationTokenSource(deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            watchdog.Token, cancellationToken);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
        {
            timedOut = true;
            KillTree(process);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation: same terminal treatment as the watchdog.
            KillTree(process);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        // Orphan check on shutdown: after tree-kill the supervised process must be gone.
        bool orphan = false;
        if (!process.HasExited)
        {
            KillTree(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* timeout below counts as orphan */ }
            orphan = !process.HasExited;
        }

        var exitCode = process.HasExited ? (int?)process.ExitCode : null;

        if (timedOut)
            return new SupervisionResult(SupervisedOutcome.TimedOut, exitCode, log.Lines,
                log.Truncated, orphan, Diag(DiagnosticCodes.WkrTimeout,
                    $"Worker '{executable}' exceeded its {deadline} deadline and was killed."));

        if (exitCode is not 0)
            return new SupervisionResult(SupervisedOutcome.Crashed, exitCode, log.Lines,
                log.Truncated, orphan, Diag(DiagnosticCodes.WkrCrash,
                    $"Worker '{executable}' terminated with exit code {exitCode}."));

        return new SupervisionResult(SupervisedOutcome.Completed, exitCode, log.Lines,
            log.Truncated, orphan, Failure: null);

        async Task PumpAsync(StreamReader reader)
        {
            // Drain continuously; append only until the budget is spent.
            while (true)
            {
                var line = await reader.ReadLineAsync(CancellationToken.None);
                if (line is null) break;
                log.Append(line);
            }
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* raced with exit */ }
        catch { /* best effort, mirroring ArelleWorker disposal */ }
    }

    private static Diagnostic Diag(string code, string message) => new()
    {
        Code = code,
        Subsystem = DiagnosticSubsystem.Worker,
        Severity = DiagnosticSeverity.Error,
        Stage = DiagnosticStage.Worker,
        IsBlocking = true,
        Message = message,
    };

    /// <summary>Head-trimmed, byte-budgeted line collector.</summary>
    private sealed class BoundedLog
    {
        private readonly List<string> _lines = new();
        private long _bytes;
        public bool Truncated { get; private set; }

        public BoundedLog(long maxBytes) => _max = maxBytes;
        private readonly long _max;

        public IReadOnlyList<string> Lines => _lines;

        public void Append(string line)
        {
            _bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (_bytes > _max) { Truncated = true; return; }
            _lines.Add(line);
        }
    }
}
