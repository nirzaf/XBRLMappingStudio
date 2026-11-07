using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
        cancellationToken.ThrowIfCancellationRequested();
        var validatedArguments = WorkerProcessArgumentBudget.ValidateAndCopy(executable, arguments);
        using var watchdog = new CancellationTokenSource(deadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            watchdog.Token, cancellationToken);

        IDisposable processSlot;
        try
        {
            processSlot = await WorkerProcessCapacity.AcquireAsync(linked.Token);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
        {
            return new SupervisionResult(SupervisedOutcome.TimedOut, null,
                Array.Empty<string>(), LogTruncated: false, OrphanDetected: false,
                Diag(DiagnosticCodes.WkrTimeout,
                    $"Worker '{executable}' exceeded its {deadline} deadline while waiting for a worker slot."));
        }

        using (processSlot)
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
            foreach (var a in validatedArguments) psi.ArgumentList.Add(a);

            var started = WorkerProcessTree.Start(psi);
            using var process = started.Process;
            using var processTree = started.Tree;

            var log = new BoundedLog(_maxLogBytes);
            var stdoutTask = PumpAsync(process.StandardOutput, linked.Token);
            var stderrTask = PumpAsync(process.StandardError, linked.Token);

            bool timedOut = false;
            bool callerCancelled = false;
            bool awaitingPipeDrain = false;
            bool orphan = false;
            try
            {
                if (stdinPayload is not null)
                {
                    await process.StandardInput.WriteAsync(stdinPayload.AsMemory(), linked.Token);
                    await process.StandardInput.FlushAsync(linked.Token);
                }
                try { process.StandardInput.Close(); } catch (IOException) { /* already gone */ }

                await process.WaitForExitAsync(linked.Token);
                awaitingPipeDrain = true;
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
            {
                timedOut = true;
                orphan = awaitingPipeDrain && process.HasExited && !processTree.CanCleanDescendantsAfterParentExit;
                processTree.Kill(process);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation: same terminal treatment as the watchdog.
                callerCancelled = true;
                orphan = awaitingPipeDrain && process.HasExited && !processTree.CanCleanDescendantsAfterParentExit;
                processTree.Kill(process);
            }
            catch
            {
                processTree.Kill(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { /* the failure below remains authoritative */ }
                try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* the failure below remains authoritative */ }
                throw;
            }

            // A worker can leave a descendant holding an inherited pipe open. The
            // watchdog/caller cancellation also cancels these fixed-buffer drains so
            // cleanup never waits forever for such a pipe to close. If the direct
            // process exited before the drain deadline, the surviving pipe is evidence
            // of a descendant that could not be reached through the exited parent.
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch
            {
                orphan |= awaitingPipeDrain && process.HasExited && !processTree.CanCleanDescendantsAfterParentExit;
                processTree.Kill(process);
            }

            // Orphan check on shutdown: after tree-kill the supervised process must be gone.
            if (!process.HasExited)
            {
                processTree.Kill(process);
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* timeout below counts as orphan */ }
                orphan |= !process.HasExited;
            }

            var exitCode = process.HasExited ? (int?)process.ExitCode : null;

            if (callerCancelled)
                throw new OperationCanceledException(cancellationToken);

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

            async Task PumpAsync(StreamReader reader, CancellationToken token)
            {
                // Never use ReadLineAsync here: a worker can emit one newline-free
                // record of arbitrary size. Keep at most one bounded line and drain
                // the rest in fixed-size chunks.
                var buffer = new char[4096];
                var line = new StringBuilder();
                long lineBytes = 0;
                var discardingLine = false;
                try
                {
                    while (true)
                    {
                        var count = await reader.ReadAsync(buffer.AsMemory(), token);
                        if (count == 0) break;
                        for (var index = 0; index < count; index++)
                        {
                            var character = buffer[index];
                            if (character == '\n')
                            {
                                if (!discardingLine)
                                {
                                    if (line.Length > 0 && line[^1] == '\r')
                                        line.Length--;
                                    log.Append(line.ToString());
                                }
                                else
                                    log.MarkTruncated();
                                line.Clear();
                                lineBytes = 0;
                                discardingLine = false;
                                continue;
                            }

                            if (discardingLine) continue;
                            var characterBytes = ConservativeUtf8ByteCount(character);
                            // Reserve a byte for the line terminator in the public
                            // captured-log accounting.
                            if (lineBytes + characterBytes + 1 > _maxLogBytes)
                            {
                                if (line.Length > 0) log.Append(line.ToString());
                                log.MarkTruncated();
                                line.Clear();
                                lineBytes = 0;
                                discardingLine = true;
                                continue;
                            }

                            line.Append(character);
                            lineBytes += characterBytes;
                        }
                    }

                    if (line.Length > 0 && !discardingLine)
                        log.Append(line.ToString());
                }
                catch (IOException) { /* process-tree shutdown closes redirected pipes */ }
            }
        }
    }

    private static int ConservativeUtf8ByteCount(char character) => character switch
    {
        <= '\u007f' => 1,
        <= '\u07ff' => 2,
        >= '\ud800' and <= '\udfff' => 2,
        _ => 3,
    };

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
        private readonly object _gate = new();
        public bool Truncated { get; private set; }

        public BoundedLog(long maxBytes) => _max = maxBytes;
        private readonly long _max;

        public IReadOnlyList<string> Lines => _lines;

        public void Append(string line)
        {
            lock (_gate)
            {
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (_bytes + bytes > _max) { Truncated = true; return; }
                _bytes += bytes;
                _lines.Add(line);
            }
        }

        public void MarkTruncated()
        {
            lock (_gate) Truncated = true;
        }
    }
}

/// <summary>
/// Owns the process-tree boundary for the complete worker lifetime. Windows workers
/// are assigned to a kill-on-close Job Object immediately after startup; Linux
/// workers are started in a new session/process group so descendants remain
/// addressable after the direct process exits. .NET Process.Start does not expose
/// suspended launch: supported worker entry points must not create child processes
/// before their first request, or a child could start before Windows job assignment.
/// </summary>
internal sealed class WorkerProcessTree : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNoMoreFiles = 18;
    private const int SigKill = 9;
    private const string WindowsJobNamePrefix = "XBRLMappingStudio.Worker.";
    private readonly bool _linuxProcessGroup;
    private SafeFileHandle? _job;
    private int? _processId;
    private bool _windowsJobAssigned;

    private WorkerProcessTree(bool linuxProcessGroup) => _linuxProcessGroup = linuxProcessGroup;

    public bool CanCleanDescendantsAfterParentExit
        => _linuxProcessGroup || _windowsJobAssigned;

    public static (Process Process, WorkerProcessTree Tree) Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var linuxProcessGroup = false;
        if (OperatingSystem.IsLinux())
        {
            var setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }
                .FirstOrDefault(File.Exists);
            if (setsid is null)
                throw new PlatformNotSupportedException("Linux worker isolation requires the 'setsid' executable.");

            var executable = ResolveLinuxExecutable(startInfo);
            var arguments = startInfo.ArgumentList.ToArray();
            startInfo.FileName = setsid;
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add(executable);
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            linuxProcessGroup = true;
        }

        var tree = new WorkerProcessTree(linuxProcessGroup);
        Process? process = null;
        try
        {
            // The bundled Arelle and PDF workers do not create subprocesses during
            // startup. Keep that invariant if changing their bootstrap; a worker
            // that needs eager children requires a native suspended-process launch.
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
            tree._processId = process.Id;
            if (OperatingSystem.IsWindows()) tree.AttachWindowsJob(process);
            return (process, tree);
        }
        catch
        {
            if (process is not null)
            {
                tree.Kill(process);
                process.Dispose();
            }
            tree.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    private static string ResolveLinuxExecutable(ProcessStartInfo startInfo)
    {
        var requested = startInfo.FileName;
        var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startInfo.WorkingDirectory;
        IEnumerable<string> candidates;
        if (Path.IsPathRooted(requested)
            || requested.Contains(Path.DirectorySeparatorChar)
            || requested.Contains(Path.AltDirectorySeparatorChar))
        {
            candidates = new[] { Path.GetFullPath(requested, workingDirectory) };
        }
        else
        {
            var searchPath = startInfo.Environment.TryGetValue("PATH", out var configuredPath)
                ? configuredPath
                : Environment.GetEnvironmentVariable("PATH");
            candidates = (searchPath ?? string.Empty)
                .Split(Path.PathSeparator)
                .Select(directory => Path.GetFullPath(
                    Path.Combine(string.IsNullOrEmpty(directory) ? workingDirectory : directory, requested),
                    workingDirectory));
        }

        var executeBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                if ((File.GetUnixFileMode(candidate) & executeBits) != 0)
                    return candidate;
            }
            catch (IOException) { /* try the next PATH entry */ }
            catch (UnauthorizedAccessException) { /* try the next PATH entry */ }
        }

        throw new Win32Exception(2, $"The Linux worker executable '{requested}' was not found or is not executable.");
    }

    private void AttachWindowsJob(Process process)
    {
        // A stable, process-specific name also lets the failure-injection worker
        // prove its descendants belong to this exact job (not merely an outer CI job).
        var job = CreateJobObjectW(IntPtr.Zero, WindowsJobNamePrefix + process.Id);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            TryKill(process);
            throw new InvalidOperationException("Could not create the Windows worker Job Object.", new Win32Exception(error));
        }

        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            TryKill(process);
            throw new InvalidOperationException("Could not configure the Windows worker Job Object.", new Win32Exception(error));
        }

        var confirmedLimits = new JobObjectExtendedLimitInformation();
        if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref confirmedLimits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>(), out _)
            || (confirmedLimits.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            TryKill(process);
            throw new InvalidOperationException("Windows did not confirm kill-on-close worker Job Object semantics.", new Win32Exception(error));
        }

        if (!AssignProcessToJobObject(job, process.SafeHandle))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            TryKill(process);
            throw new InvalidOperationException("Could not assign the worker process to its Windows Job Object.", new Win32Exception(error));
        }

        try
        {
            AssignExistingWindowsDescendants(job, process.Id);
        }
        catch (Exception ex)
        {
            // A launcher can create the actual worker before Process.Start returns.
            // Closing the configured job kills every descendant already attached;
            // Kill(entireProcessTree: true) is the fallback for anything missed if
            // the snapshot raced with process startup.
            job.Dispose();
            TryKill(process);
            throw new InvalidOperationException(
                "Could not attach existing worker descendants to the Windows Job Object.", ex);
        }

        _job = job;
        _windowsJobAssigned = true;
    }

    private static void AssignExistingWindowsDescendants(SafeFileHandle job, int rootProcessId)
    {
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
            throw new InvalidOperationException("Could not enumerate worker descendants.",
                new Win32Exception(Marshal.GetLastWin32Error()));

        var childrenByParent = new Dictionary<int, List<int>>();
        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32FirstW(snapshot, ref entry))
            throw new InvalidOperationException("Could not read the Windows process snapshot.",
                new Win32Exception(Marshal.GetLastWin32Error()));

        do
        {
            var processId = checked((int)entry.ProcessId);
            var parentProcessId = checked((int)entry.ParentProcessId);
            if (processId <= 0 || parentProcessId <= 0) continue;
            if (!childrenByParent.TryGetValue(parentProcessId, out var children))
            {
                children = new List<int>();
                childrenByParent[parentProcessId] = children;
            }
            children.Add(processId);
        }
        while (Process32NextW(snapshot, ref entry));
        var enumerationError = Marshal.GetLastWin32Error();
        if (enumerationError != ErrorNoMoreFiles)
            throw new InvalidOperationException("Could not finish reading the Windows process snapshot.",
                new Win32Exception(enumerationError));

        var pending = new Queue<int>();
        var seen = new HashSet<int> { rootProcessId };
        pending.Enqueue(rootProcessId);
        while (pending.TryDequeue(out var parentProcessId))
        {
            if (!childrenByParent.TryGetValue(parentProcessId, out var children)) continue;
            foreach (var processId in children)
            {
                if (!seen.Add(processId)) continue;
                // Include descendants of a process that exited between the snapshot
                // and OpenProcess; its children can still be alive and escaped the job.
                pending.Enqueue(processId);
                using var process = OpenProcess(
                    ProcessTerminate | ProcessSetQuota | ProcessQueryLimitedInformation,
                    inheritHandle: false, (uint)processId);
                if (process.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorInvalidParameter) continue;
                    throw new InvalidOperationException(
                        $"Could not open worker descendant process {processId}.", new Win32Exception(error));
                }

                if (!IsProcessInJob(process, job, out var alreadyAssigned))
                    throw new InvalidOperationException(
                        $"Could not inspect worker descendant process {processId}.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                if (alreadyAssigned) continue;
                if (!AssignProcessToJobObject(job, process))
                    throw new InvalidOperationException(
                        $"Could not assign worker descendant process {processId} to its Job Object.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    public void Kill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_job is { IsInvalid: false, IsClosed: false })
        {
            _ = TerminateJobObject(_job, 1);
            // Closing the job is the authoritative kill-on-close path, including
            // descendants whose direct parent has already exited.
            _job.Dispose();
            return;
        }
        if (_linuxProcessGroup && _processId is { } processId)
            _ = KillProcessGroup(processId);
        TryKill(process);
    }

    public void Dispose()
    {
        // KILL_ON_JOB_CLOSE also cleans descendants if the direct process exited
        // while they still held an inherited stdout/stderr handle.
        _job?.Dispose();
        if (_linuxProcessGroup && _processId is { } processId)
            _ = KillProcessGroup(processId);
    }

    private static int KillProcessGroup(int processId)
    {
        try { return kill(-processId, SigKill); }
        catch (DllNotFoundException) { return -1; }
        catch (EntryPointNotFoundException) { return -1; }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* raced with exit */ }
        catch { /* best effort after the job object was terminated */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation jobObjectInformation,
        uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation jobObjectInformation,
        uint jobObjectInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
