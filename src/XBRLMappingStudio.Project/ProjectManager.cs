using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Project;

/// <summary>
/// Raised when a save cannot proceed. Carries a diagnostic code from PRJ namespace.
/// </summary>
public sealed class ProjectSaveException : Exception
{
    public string Code { get; }
    public ProjectSaveException(string code, string message, Exception? inner = null)
        : base(message, inner) { Code = code; }
}

/// <summary>
/// Result of an attempted save: reports whether the semantic revision changed and
/// the resulting generation counters.
/// </summary>
public sealed record SaveResult(
    string Path,
    int NewSemanticRevision,
    int NewLayoutGeneration,
    bool SemanticChanged,
    string SemanticDigest);

/// <summary>
/// Manages load/save of a <see cref="MappingProject"/> at a file path.
/// Implements BASE03:
///   - Stable IDs preserved across saves.
///   - Layout-only edits do not bump <see cref="MappingProject.SemanticRevision"/>.
///   - Atomic sibling-file staging with round-trip verification.
///   - Exclusive per-project writer lock via <c>.lock</c> file.
///   - Optimistic concurrency: caller provides expected generation; a stale save is rejected.
///   - Last readable project preserved when a save fails.
/// </summary>
public sealed class ProjectManager
{
    public const string ProjectExtension = ".xmap";
    public const string LockSuffix = ".lock";
    public const string BackupSuffix = ".bak";

    private readonly string _directory;
    private readonly string _fileName;

    public string FullPath => Path.Combine(_directory, _fileName);
    public string LockPath => FullPath + LockSuffix;
    public string BackupPath => FullPath + BackupSuffix;

    public ProjectManager(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir))
            throw new ArgumentException("Project path must include a directory.", nameof(fullPath));
        _directory = dir;
        _fileName = Path.GetFileName(fullPath);
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Load a project from disk. Throws <see cref="ProjectSchemaException"/> on schema mismatch.</summary>
    public MappingProject Load()
    {
        if (!File.Exists(FullPath))
            throw new FileNotFoundException($"Project file not found: {FullPath}", FullPath);

        using var _ = AcquireSharedLock();
        var json = File.ReadAllText(FullPath);
        return ProjectSerializer.Deserialize(json);
    }

    /// <summary>
    /// Save the project. If <paramref name="expectedLayoutGeneration"/> is supplied and
    /// does not match the current on-disk generation, the save is rejected with a revision
    /// conflict (BASE03 acceptance).
    /// </summary>
    public SaveResult Save(MappingProject project, int? expectedLayoutGeneration = null)
    {
        // Determine whether semantic content changed vs. the last loaded state.
        var newDigest = project.ComputeSemanticDigest();

        // Under an exclusive per-project writer lock (BASE03).
        using var writerLock = AcquireExclusiveLock();

        int currentSemanticRevision = project.SemanticRevision;
        int currentLayoutGeneration = project.LayoutGeneration;
        bool semanticChanged;

        // Re-read the current generation from disk to detect concurrent edits.
        if (File.Exists(FullPath))
        {
            var diskJson = File.ReadAllText(FullPath);
            var diskProject = ProjectSerializer.Deserialize(diskJson);

            if (expectedLayoutGeneration.HasValue
                && diskProject.LayoutGeneration != expectedLayoutGeneration.Value)
            {
                throw new ProjectSaveException(
                    DiagnosticCodes.PrjRevisionConflict,
                    $"Expected layout generation {expectedLayoutGeneration.Value}, "
                    + $"but on-disk is {diskProject.LayoutGeneration}.");
            }

            // Bump semantic revision only when the executable content differs from what was on disk.
            var diskDigest = diskProject.ComputeSemanticDigest();
            if (!string.Equals(diskDigest, newDigest, StringComparison.Ordinal))
            {
                currentSemanticRevision = diskProject.SemanticRevision + 1;
                semanticChanged = true;
            }
            else
            {
                currentSemanticRevision = diskProject.SemanticRevision;
                semanticChanged = false; // layout-only save (BASE03)
            }
            currentLayoutGeneration = diskProject.LayoutGeneration + 1;
        }
        else
        {
            currentLayoutGeneration = project.LayoutGeneration;
            currentSemanticRevision = project.SemanticRevision;
            semanticChanged = true; // first materialization of the artifact
        }

        project.SemanticRevision = currentSemanticRevision;
        project.LayoutGeneration = currentLayoutGeneration;
        project.Metadata = project.Metadata with
        {
            CreatedUtc = project.Metadata.CreatedUtc,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            Author = project.Metadata.Author,
            Description = project.Metadata.Description,
            Organization = project.Metadata.Organization
        };

        var json = ProjectSerializer.Serialize(project);

        // Round-trip verification (BASE03: staged temp file is verified before replacement).
        if (!ProjectSerializer.VerifyRoundTrip(json))
        {
            throw new ProjectSaveException(
                DiagnosticCodes.PrjAtomicSaveFailed,
                "Project failed round-trip verification before replacement.");
        }

        // Stage on the same filesystem as a sibling file.
        var tmpPath = Path.Combine(_directory,
            Path.GetFileNameWithoutExtension(_fileName)
            + $".tmp.{Guid.NewGuid():N}");

        try
        {
            File.WriteAllText(tmpPath, json);

            // Preserve prior version as backup (BASE03: retain readable original).
            if (File.Exists(FullPath))
            {
                File.Copy(FullPath, BackupPath, overwrite: true);
            }

            // Atomic same-filesystem replace. On Linux, File.Replace uses rename(2).
            if (File.Exists(FullPath))
            {
                File.Replace(tmpPath, FullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmpPath, FullPath);
            }
        }
        catch (Exception ex)
        {
            // Clean up staged file if it remains; the last readable project on disk is preserved.
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { /* best effort */ }
            }
            throw new ProjectSaveException(
                DiagnosticCodes.PrjAtomicSaveFailed,
                "Failed to atomically replace project file.", ex);
        }

        return new SaveResult(
            FullPath,
            currentSemanticRevision,
            currentLayoutGeneration,
            SemanticChanged: semanticChanged,
            SemanticDigest: newDigest);
    }

    // -------- Locking --------

    /// <summary>
    /// Acquire an exclusive writer lock. Throws if another writer already holds it.
    /// Implemented via <c>FileMode.CreateNew</c> on a lock file, which fails if it exists.
    /// </summary>
    public IDisposable AcquireExclusiveLock(int timeoutMs = 2000)
    {
        // Monotonic acquisition deadline (mirrors DestinationLock.AcquireAsync): a wall-clock
        // jump must not lengthen or shorten the bounded retry window.
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var writer = new StreamWriter(fs);
                writer.WriteLine($"pid={Environment.ProcessId} at={DateTimeOffset.UtcNow:o}");
                writer.Flush();
                return new ExclusiveLockHandle(LockPath, fs, writer);
            }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new ProjectSaveException(
                        DiagnosticCodes.PrjLockHeld,
                        $"Could not acquire project lock within {timeoutMs} ms: {LockPath}");
                }
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>
    /// Shared read lock: a no-op marker when the exclusive lock is not held.
    /// </summary>
    public IDisposable AcquireSharedLock(int timeoutMs = 2000)
    {
        // Simple approach: refuse to read while the exclusive lock is present.
        // Monotonic deadline (see AcquireExclusiveLock).
        var deadline = Environment.TickCount64 + timeoutMs;
        while (File.Exists(LockPath))
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new ProjectSaveException(
                    DiagnosticCodes.PrjLockHeld,
                    $"Project is locked for writing: {LockPath}");
            }
            Thread.Sleep(20);
        }
        return NoOpLock.Instance;
    }

    private sealed class ExclusiveLockHandle : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;
        private readonly StreamWriter _writer;
        private bool _disposed;

        public ExclusiveLockHandle(string path, FileStream stream, StreamWriter writer)
        {
            _path = path;
            _stream = stream;
            _writer = writer;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
            _stream.Dispose();
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
        }
    }

    private sealed class NoOpLock : IDisposable
    {
        public static readonly NoOpLock Instance = new();
        public void Dispose() { }
    }
}
