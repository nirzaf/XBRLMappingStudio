using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;

namespace XBRLMappingStudio.Application;

public sealed record TemporaryArtifactRetentionApproval(
    string PolicyId,
    string ApprovalId,
    string ApprovedById,
    string PlanSha256);

public sealed record TemporaryArtifactRetentionCandidate(
    Guid DirectoryId,
    DateTimeOffset CreatedUtc,
    string TreeSha256,
    int FileCount,
    long Bytes);

public sealed record TemporaryArtifactRetentionPlan(
    string DatabaseIdentity,
    DateTimeOffset ExpireBeforeUtc,
    string PlanSha256,
    IReadOnlyList<TemporaryArtifactRetentionCandidate> Candidates,
    long CandidateBytes);

public sealed record TemporaryArtifactRetentionResult(
    string ApprovalId,
    string PlanSha256,
    string State,
    int PlannedDirectories,
    long PlannedBytes,
    int RemovedDirectories,
    long RemovedBytes,
    long AuditSequence);

public interface ITemporaryArtifactRetentionPolicyAuthorizer
{
    bool IsAuthorized(
        TemporaryArtifactRetentionApproval approval,
        TemporaryArtifactRetentionPlan plan);
}

/// <summary>
/// Owner marker and cross-process lease for one application-created large-run spool.
/// The lock is held until the result is disposed. A retention scan can only lease a
/// directory after its owner process releases the lock.
/// </summary>
internal sealed class TemporaryArtifactLease : IDisposable
{
    internal const string OwnerFileName = ".xms-large-run-owner.json";
    private const int OwnerSchemaVersion = 1;
    private readonly FileStream _lock;
    private bool _disposed;

    public string DirectoryPath { get; }

    private TemporaryArtifactLease(string directoryPath, FileStream ownerLock)
        => (DirectoryPath, _lock) = (directoryPath, ownerLock);

    public static TemporaryArtifactLease CreateLargeRun(string? temporaryRoot = null)
    {
        var directory = Path.Combine(Path.GetFullPath(temporaryRoot ?? Path.GetTempPath()), "xms-large-" + Guid.NewGuid().ToString("N"));
        LocalApplicationDatabase.CreatePrivateDirectory(directory);
        FileStream? ownerLock = null;
        try
        {
            var markerPath = Path.Combine(directory, OwnerFileName);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 4096,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            ownerLock = new FileStream(markerPath, options);
            if (!TryLock(ownerLock))
                throw new IOException("Could not acquire the new large-run spool lease.");
            var marker = new OwnerMarker(OwnerSchemaVersion, "large-run-spool", DateTimeOffset.UtcNow);
            JsonSerializer.Serialize(ownerLock, marker);
            ownerLock.Flush(flushToDisk: true);
            ownerLock.Position = 0;
            return new TemporaryArtifactLease(directory, ownerLock);
        }
        catch
        {
            ownerLock?.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
            throw;
        }
    }

    internal static FileStream? TryAcquire(string directory)
    {
        var markerPath = Path.Combine(directory, OwnerFileName);
        if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            return null;
        FileStream? ownerLock = null;
        try
        {
            ownerLock = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
            if (!TryLock(ownerLock))
            {
                ownerLock.Dispose();
                return null;
            }
            ownerLock.Position = 0;
            return ownerLock;
        }
        catch (IOException)
        {
            ownerLock?.Dispose();
            return null;
        }
        catch
        {
            ownerLock?.Dispose();
            throw;
        }
    }

    internal static OwnerMarker ReadMarker(FileStream ownerLock)
    {
        if (ownerLock.Length is <= 0 or > 4096)
            throw new InvalidDataException("Temporary artifact owner marker is outside its supported size.");
        ownerLock.Position = 0;
        var marker = JsonSerializer.Deserialize<OwnerMarker>(ownerLock)
            ?? throw new InvalidDataException("Temporary artifact owner marker is malformed.");
        ownerLock.Position = 0;
        if (marker.SchemaVersion != OwnerSchemaVersion
            || !StringComparer.Ordinal.Equals(marker.Purpose, "large-run-spool")
            || marker.CreatedUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Temporary artifact owner marker has an unsupported identity.");
        return marker;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Unlock(_lock); } catch (IOException) { }
        _lock.Dispose();
    }

    internal static bool TryLock(FileStream stream)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (MacFileLock.Flock(stream.SafeFileHandle, MacFileLock.LockExclusive | MacFileLock.LockNonBlocking) == 0)
                return true;
            var error = Marshal.GetLastPInvokeError();
            if (error is 11 or 35) return false;
            throw new IOException("Could not acquire the macOS large-run spool lease.", new Win32Exception(error));
        }
        stream.Lock(0, 1);
        return true;
    }

    internal static void Unlock(FileStream stream)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (MacFileLock.Flock(stream.SafeFileHandle, MacFileLock.LockUnlock) != 0)
                throw new IOException("Could not release the macOS large-run spool lease.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            return;
        }
        stream.Unlock(0, 1);
    }

    private static class MacFileLock
    {
        internal const int LockExclusive = 2;
        internal const int LockNonBlocking = 4;
        internal const int LockUnlock = 8;

        [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
        internal static extern int Flock(SafeFileHandle file, int operation);
    }

    internal sealed record OwnerMarker(int SchemaVersion, string Purpose, DateTimeOffset CreatedUtc);
}

/// <summary>
/// Plans and removes only abandoned, owner-marked large-run spool directories. There
/// is no default cutoff or permissive authorizer; report destinations and database
/// receipts are not searched or considered for deletion.
/// </summary>
public sealed class TemporaryArtifactRetentionService
{
    public const int MaximumCandidatesPerPlan = 64;
    public const int MaximumFilesPerArtifact = 4_096;
    public const long MaximumBytesPerArtifact = 2L * 1024 * 1024 * 1024;
    public const long MaximumBytesPerPlan = 2L * 1024 * 1024 * 1024;
    private const int MaximumDirectoriesToInspect = 10_000;
    private const int MaximumRelativePathBytes = 32 * 1024;

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16
    };

    private readonly LocalApplicationDatabase _database;
    private readonly ITemporaryArtifactRetentionPolicyAuthorizer? _authorizer;
    private readonly string _databaseIdentity;
    private readonly string _temporaryRoot;

    public TemporaryArtifactRetentionService(
        LocalApplicationDatabase database,
        ITemporaryArtifactRetentionPolicyAuthorizer? authorizer = null)
        : this(database, authorizer, Path.GetTempPath())
    {
    }

    internal TemporaryArtifactRetentionService(
        LocalApplicationDatabase database,
        ITemporaryArtifactRetentionPolicyAuthorizer? authorizer,
        string temporaryRoot)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _authorizer = authorizer;
        _databaseIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database.Path)))
            .ToLowerInvariant();
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
    }

    public TemporaryArtifactRetentionPlan PreviewAbandonedLargeRunSpools(DateTimeOffset expireBeforeUtc)
    {
        var cutoff = NormalizeUtc(expireBeforeUtc);
        using var connection = _database.OpenConnection();
        EnsureDatabaseIntegrity(connection);
        var inspected = ReadCandidates(cutoff);
        try { return CreatePlan(cutoff, inspected.Select(item => item.Candidate).ToArray()); }
        finally { DisposeLocks(inspected); }
    }

    public TemporaryArtifactRetentionResult ApplyAbandonedLargeRunSpoolPrune(
        TemporaryArtifactRetentionPlan plan,
        TemporaryArtifactRetentionApproval approval)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (!StringComparer.Ordinal.Equals(plan.DatabaseIdentity, _databaseIdentity))
            throw new InvalidOperationException("The temporary-artifact plan belongs to a different local application database.");
        if (plan.Candidates is null || plan.Candidates.Count > MaximumCandidatesPerPlan
            || plan.CandidateBytes != SumBytes(plan.Candidates))
            throw new InvalidOperationException("The temporary-artifact plan is malformed or exceeds its bounded limits.");
        ValidateApproval(approval, plan);
        if (!StringComparer.Ordinal.Equals(plan.PlanSha256, ComputePlanDigest(plan.ExpireBeforeUtc, plan.Candidates)))
            throw new InvalidOperationException("The temporary-artifact preview was changed after it was created.");
        if (_authorizer?.IsAuthorized(approval, plan) != true)
            throw new UnauthorizedAccessException("The temporary-artifact retention approval is missing or not authorized.");

        List<LockedCandidate>? current = null;
        long auditSequence;
        try
        {
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                EnsureDatabaseIntegrity(connection, transaction);
                current = ReadCandidates(NormalizeUtc(plan.ExpireBeforeUtc));
                var currentPlan = CreatePlan(NormalizeUtc(plan.ExpireBeforeUtc), current.Select(item => item.Candidate).ToArray());
                if (!StringComparer.Ordinal.Equals(currentPlan.PlanSha256, plan.PlanSha256))
                    throw new InvalidOperationException("The temporary-artifact preview is stale; create a new preview before applying policy.");
                if (current.Count == 0)
                {
                    DisposeLocks(current);
                    current = null;
                    transaction.Rollback();
                    return new TemporaryArtifactRetentionResult(approval.ApprovalId, plan.PlanSha256,
                        "complete", 0, 0, 0, 0, 0);
                }

                using var audit = connection.CreateCommand();
                audit.Transaction = transaction;
                audit.CommandText = """
                INSERT INTO temporary_artifact_retention_audit(
                    approval_id, policy_id, approved_by_id, plan_sha256, expired_before_utc,
                    planned_directories, planned_bytes, plan_items_json, state, occurred_utc)
                VALUES($approval_id, $policy_id, $approved_by_id, $plan_sha256, $cutoff,
                    $directories, $bytes, $items, 'pending', $occurred_utc);
                SELECT last_insert_rowid();
                """;
                audit.Parameters.AddWithValue("$approval_id", approval.ApprovalId);
                audit.Parameters.AddWithValue("$policy_id", approval.PolicyId);
                audit.Parameters.AddWithValue("$approved_by_id", approval.ApprovedById);
                audit.Parameters.AddWithValue("$plan_sha256", plan.PlanSha256);
                audit.Parameters.AddWithValue("$cutoff", currentPlan.ExpireBeforeUtc.ToString("O"));
                audit.Parameters.AddWithValue("$directories", current.Count);
                audit.Parameters.AddWithValue("$bytes", currentPlan.CandidateBytes);
                audit.Parameters.AddWithValue("$items", JsonSerializer.Serialize(current.Select(item => item.Candidate), FingerprintJsonOptions));
                audit.Parameters.AddWithValue("$occurred_utc", DateTimeOffset.UtcNow.ToString("O"));
                auditSequence = Convert.ToInt64(audit.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                transaction.Commit();
            }
        }
        catch
        {
            if (current is not null) DisposeLocks(current);
            throw;
        }

        var removedDirectories = 0;
        long removedBytes = 0;
        var failures = 0;
        try
        {
            foreach (var item in current!)
            {
                try
                {
                    Directory.Delete(item.DirectoryPath, recursive: true);
                    removedDirectories++;
                    removedBytes = checked(removedBytes + item.Candidate.Bytes);
                }
                catch (DirectoryNotFoundException)
                {
                    removedDirectories++;
                    removedBytes = checked(removedBytes + item.Candidate.Bytes);
                }
                catch (IOException)
                {
                    failures++;
                }
                catch (UnauthorizedAccessException)
                {
                    failures++;
                }
            }
        }
        finally
        {
            DisposeLocks(current!);
        }

        var state = failures == 0 ? "complete" : removedDirectories == 0 ? "failed" : "partial";
        using (var connection = _database.OpenConnection())
        using (var transaction = connection.BeginTransaction(deferred: false))
        using (var outcome = connection.CreateCommand())
        {
            outcome.Transaction = transaction;
            outcome.CommandText = """
                UPDATE temporary_artifact_retention_audit
                SET state=$state, removed_directories=$directories, removed_bytes=$bytes,
                    completed_utc=$completed_utc
                WHERE sequence=$sequence AND state='pending';
                """;
            outcome.Parameters.AddWithValue("$state", state);
            outcome.Parameters.AddWithValue("$directories", removedDirectories);
            outcome.Parameters.AddWithValue("$bytes", removedBytes);
            outcome.Parameters.AddWithValue("$completed_utc", DateTimeOffset.UtcNow.ToString("O"));
            outcome.Parameters.AddWithValue("$sequence", auditSequence);
            if (outcome.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Could not finalize the temporary-artifact retention audit record.");
            transaction.Commit();
        }
        return new TemporaryArtifactRetentionResult(approval.ApprovalId, plan.PlanSha256,
            state, current!.Count, plan.CandidateBytes, removedDirectories, removedBytes, auditSequence);
    }

    private List<LockedCandidate> ReadCandidates(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(_temporaryRoot)) return [];
        var result = new List<LockedCandidate>();
        var seen = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_temporaryRoot, "xms-large-*", SearchOption.TopDirectoryOnly))
            {
                if (++seen > MaximumDirectoriesToInspect)
                    throw new InvalidDataException("Temporary-artifact scan exceeds its bounded directory limit.");
                var name = Path.GetFileName(directory);
                if (name.Length != "xms-large-".Length + 32
                    || !Guid.TryParseExact(name["xms-large-".Length..], "N", out var directoryId))
                    continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A large-run temporary directory is a reparse point; retention is refused.");
                var ownerLock = TemporaryArtifactLease.TryAcquire(directory);
                if (ownerLock is null) continue;
                var keepLock = false;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("A large-run temporary directory changed to a reparse point during retention inspection.");
                    var marker = TemporaryArtifactLease.ReadMarker(ownerLock);
                    if (marker.CreatedUtc >= cutoff) continue;
                    var inventory = ComputeTree(directory);
                    var candidate = new TemporaryArtifactRetentionCandidate(
                        directoryId, marker.CreatedUtc, inventory.Sha256, inventory.FileCount, inventory.Bytes);
                    result.Add(new LockedCandidate(candidate, directory, ownerLock));
                    keepLock = true;
                    if (result.Count > MaximumCandidatesPerPlan)
                        throw new InvalidDataException("Expired temporary-artifact count exceeds the retention preview limit.");
                    if (SumBytes(result.Select(item => item.Candidate).ToArray()) > MaximumBytesPerPlan)
                        throw new InvalidDataException("Expired temporary-artifact bytes exceed the retention preview limit.");
                }
                finally
                {
                    if (!keepLock)
                    {
                        try { TemporaryArtifactLease.Unlock(ownerLock); } catch (IOException) { }
                        ownerLock.Dispose();
                    }
                }
            }
            return result.OrderBy(item => item.Candidate.DirectoryId).ToList();
        }
        catch
        {
            DisposeLocks(result);
            throw;
        }
    }

    private static (string Sha256, int FileCount, long Bytes) ComputeTree(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        long totalBytes = 0;
        var buffer = new byte[64 * 1024];
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Large-run temporary artifact contains a reparse point; retention is refused.");
                if (StringComparer.OrdinalIgnoreCase.Equals(entry, Path.Combine(root, TemporaryArtifactLease.OwnerFileName)))
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                if (++count > MaximumFilesPerArtifact)
                    throw new InvalidDataException("Large-run temporary artifact exceeds its file-count limit.");
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                var pathBytes = Encoding.UTF8.GetBytes(relative);
                if (pathBytes.Length > MaximumRelativePathBytes)
                    throw new InvalidDataException("Large-run temporary artifact contains an oversized relative path.");
        var lengthBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, pathBytes.Length);
                hash.AppendData(lengthBytes);
                hash.AppendData(pathBytes);
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read,
                    buffer.Length, FileOptions.SequentialScan);
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    totalBytes = checked(totalBytes + read);
                    if (totalBytes > MaximumBytesPerArtifact)
                        throw new InvalidDataException("Large-run temporary artifact exceeds its 2 GiB retention limit.");
                    hash.AppendData(buffer, 0, read);
                }
            }
        }
        if (count == 0) throw new InvalidDataException("Large-run temporary artifact contains no files.");
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), count, totalBytes);
    }

    private TemporaryArtifactRetentionPlan CreatePlan(
        DateTimeOffset cutoff,
        IReadOnlyList<TemporaryArtifactRetentionCandidate> candidates)
        => new(_databaseIdentity, cutoff, ComputePlanDigest(cutoff, candidates), candidates, SumBytes(candidates));

    private string ComputePlanDigest(
        DateTimeOffset cutoff,
        IReadOnlyList<TemporaryArtifactRetentionCandidate> candidates)
    {
        var payload = new PlanFingerprint(1, _databaseIdentity, NormalizeUtc(cutoff).ToString("O"),
            candidates.OrderBy(candidate => candidate.DirectoryId)
                .Select(candidate => new CandidateFingerprint(candidate.DirectoryId.ToString("N"),
                    candidate.CreatedUtc.ToString("O"), candidate.TreeSha256, candidate.FileCount, candidate.Bytes))
                .ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(payload, FingerprintJsonOptions)))).ToLowerInvariant();
    }

    private static long SumBytes(IReadOnlyList<TemporaryArtifactRetentionCandidate> candidates)
    {
        long total = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Bytes < 0) throw new InvalidOperationException("Retention plan contains a negative artifact size.");
            total = checked(total + candidate.Bytes);
            if (total > MaximumBytesPerPlan)
                throw new InvalidOperationException("Temporary-artifact plan exceeds its bounded byte limit.");
        }
        return total;
    }

    private static void ValidateApproval(
        TemporaryArtifactRetentionApproval approval,
        TemporaryArtifactRetentionPlan plan)
    {
        static bool BoundedIdentifier(string value)
            => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
               && StringComparer.Ordinal.Equals(value, value.Trim());
        if (!BoundedIdentifier(approval.PolicyId)
            || !Guid.TryParseExact(approval.ApprovalId, "D", out _)
            || !BoundedIdentifier(approval.ApprovedById)
            || !StringComparer.Ordinal.Equals(approval.PlanSha256, plan.PlanSha256)
            || !IsSha256(approval.PlanSha256))
            throw new ArgumentException("Temporary-artifact approval must identify a policy, approver, and exact plan digest.", nameof(approval));
    }

    private static void EnsureDatabaseIntegrity(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        if (!StringComparer.Ordinal.Equals(Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "ok"))
            throw new InvalidDataException("Local application database integrity check failed; temporary retention was not applied.");
    }

    private static void DisposeLocks(IEnumerable<LockedCandidate> candidates)
    {
        foreach (var item in candidates)
        {
            try { TemporaryArtifactLease.Unlock(item.OwnerLock); } catch (IOException) { }
            item.OwnerLock.Dispose();
        }
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) => value.ToUniversalTime();
    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record LockedCandidate(
        TemporaryArtifactRetentionCandidate Candidate,
        string DirectoryPath,
        FileStream OwnerLock);

    private sealed record PlanFingerprint(int SchemaVersion, string DatabaseIdentity, string ExpireBeforeUtc,
        CandidateFingerprint[] Candidates);
    private sealed record CandidateFingerprint(string DirectoryId, string CreatedUtc, string TreeSha256,
        int FileCount, long Bytes);
}
