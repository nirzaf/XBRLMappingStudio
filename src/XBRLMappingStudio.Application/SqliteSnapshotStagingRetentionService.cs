using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace XBRLMappingStudio.Application;

public sealed record SnapshotStagingRetentionApproval(
    string PolicyId,
    string ApprovalId,
    string ApprovedById,
    string PlanSha256);

public sealed record SnapshotStagingRetentionCandidate(
    Guid DirectoryId,
    DateTimeOffset CreatedUtc,
    string TreeSha256,
    int FileCount,
    long Bytes);

public sealed record SnapshotStagingRetentionPlan(
    string DatabaseIdentity,
    string StagingParentSha256,
    DateTimeOffset ExpireBeforeUtc,
    string PlanSha256,
    IReadOnlyList<SnapshotStagingRetentionCandidate> Candidates,
    long CandidateBytes);

public sealed record SnapshotStagingRetentionResult(
    string ApprovalId,
    string PlanSha256,
    string State,
    int PlannedDirectories,
    long PlannedBytes,
    int RemovedDirectories,
    long RemovedBytes,
    long AuditSequence);

public interface ISnapshotStagingRetentionPolicyAuthorizer
{
    bool IsAuthorized(SnapshotStagingRetentionApproval approval, SnapshotStagingRetentionPlan plan);
}

/// <summary>
/// Plans and prunes only expired, unpublished SQLite snapshot staging directories.
/// Final snapshot bundles and destination/report directories are never scanned.
/// There is no default age or permissive authorizer.
/// </summary>
public sealed class SqliteSnapshotStagingRetentionService
{
    public const int MaximumCandidatesPerPlan = 64;
    public const int MaximumFilesPerDirectory = 4_096;
    public const long MaximumBytesPerDirectory = 2L * 1024 * 1024 * 1024;
    public const long MaximumBytesPerPlan = 2L * 1024 * 1024 * 1024;
    private const int MaximumDirectoriesToInspect = 10_000;
    private const int MaximumRelativePathBytes = 32 * 1024;
    private const string Prefix = ".xms-snapshot-";

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16
    };

    private readonly LocalApplicationDatabase _database;
    private readonly ISnapshotStagingRetentionPolicyAuthorizer? _authorizer;
    private readonly string _databaseIdentity;

    public SqliteSnapshotStagingRetentionService(
        LocalApplicationDatabase database,
        ISnapshotStagingRetentionPolicyAuthorizer? authorizer = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _authorizer = authorizer;
        _databaseIdentity = Sha256(Encoding.UTF8.GetBytes(database.Path));
    }

    public SnapshotStagingRetentionPlan PreviewAbandonedStagingDirectories(
        string stagingParentDirectory,
        DateTimeOffset expireBeforeUtc)
    {
        var parent = ValidateParent(stagingParentDirectory);
        var cutoff = NormalizeUtc(expireBeforeUtc);
        using var connection = _database.OpenConnection();
        EnsureDatabaseIntegrity(connection);
        var inspected = ReadCandidates(parent, cutoff);
        try { return CreatePlan(parent, cutoff, inspected.Select(item => item.Candidate).ToArray()); }
        finally { ReleaseLocks(inspected); }
    }

    public SnapshotStagingRetentionResult ApplyAbandonedStagingPrune(
        string stagingParentDirectory,
        SnapshotStagingRetentionPlan plan,
        SnapshotStagingRetentionApproval approval)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        var parent = ValidateParent(stagingParentDirectory);
        if (!StringComparer.Ordinal.Equals(plan.DatabaseIdentity, _databaseIdentity)
            || !StringComparer.Ordinal.Equals(plan.StagingParentSha256, HashPath(parent)))
            throw new InvalidOperationException("The snapshot-staging plan belongs to another database or staging parent.");
        if (plan.Candidates is null || plan.Candidates.Count > MaximumCandidatesPerPlan
            || plan.Candidates.Any(candidate => candidate is null)
            || plan.CandidateBytes != SumBytes(plan.Candidates))
            throw new InvalidOperationException("The snapshot-staging plan is malformed or exceeds its bounded limits.");
        ValidateApproval(approval, plan);
        if (!StringComparer.Ordinal.Equals(plan.PlanSha256, ComputePlanDigest(plan.StagingParentSha256,
                plan.ExpireBeforeUtc, plan.Candidates)))
            throw new InvalidOperationException("The snapshot-staging preview was changed after it was created.");
        if (_authorizer?.IsAuthorized(approval, plan) != true)
            throw new UnauthorizedAccessException("Snapshot-staging retention approval is missing, invalid, or not authorized.");

        List<LockedCandidate>? current = null;
        long auditSequence;
        try
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureDatabaseIntegrity(connection, transaction);
            current = ReadCandidates(parent, NormalizeUtc(plan.ExpireBeforeUtc));
            var currentPlan = CreatePlan(parent, NormalizeUtc(plan.ExpireBeforeUtc),
                current.Select(item => item.Candidate).ToArray());
            if (!StringComparer.Ordinal.Equals(currentPlan.PlanSha256, plan.PlanSha256))
                throw new InvalidOperationException("The snapshot-staging preview is stale; create a new preview before applying policy.");
            if (current.Count == 0)
            {
                transaction.Rollback();
                return new SnapshotStagingRetentionResult(approval.ApprovalId, plan.PlanSha256,
                    "complete", 0, 0, 0, 0, 0);
            }

            using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO snapshot_staging_retention_audit(
                    approval_id, policy_id, approved_by_id, plan_sha256, staging_parent_sha256,
                    expired_before_utc, planned_directories, planned_bytes, plan_items_json, state, occurred_utc)
                VALUES($approval_id, $policy_id, $approved_by_id, $plan_sha256, $parent_sha256,
                    $cutoff, $directories, $bytes, $items, 'pending', $occurred_utc);
                SELECT last_insert_rowid();
                """;
            audit.Parameters.AddWithValue("$approval_id", approval.ApprovalId);
            audit.Parameters.AddWithValue("$policy_id", approval.PolicyId);
            audit.Parameters.AddWithValue("$approved_by_id", approval.ApprovedById);
            audit.Parameters.AddWithValue("$plan_sha256", plan.PlanSha256);
            audit.Parameters.AddWithValue("$parent_sha256", plan.StagingParentSha256);
            audit.Parameters.AddWithValue("$cutoff", currentPlan.ExpireBeforeUtc.ToString("O"));
            audit.Parameters.AddWithValue("$directories", current.Count);
            audit.Parameters.AddWithValue("$bytes", currentPlan.CandidateBytes);
            audit.Parameters.AddWithValue("$items", JsonSerializer.Serialize(current.Select(item => item.Candidate), FingerprintJsonOptions));
            audit.Parameters.AddWithValue("$occurred_utc", DateTimeOffset.UtcNow.ToString("O"));
            auditSequence = Convert.ToInt64(audit.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            transaction.Commit();
        }
        catch
        {
            if (current is not null) ReleaseLocks(current);
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
                    var actual = ComputeTree(item.DirectoryPath);
                    if (!StringComparer.Ordinal.Equals(actual.Sha256, item.Candidate.TreeSha256)
                        || actual.FileCount != item.Candidate.FileCount || actual.Bytes != item.Candidate.Bytes
                        || SqliteSourceSnapshotStore.SnapshotStagingLease.ReadCreatedUtc(item.Lease) != item.Candidate.CreatedUtc)
                        throw new InvalidDataException("Snapshot staging changed after its approved preview was rechecked.");
                    Directory.Delete(item.DirectoryPath, recursive: true);
                    removedDirectories++;
                    removedBytes = checked(removedBytes + item.Candidate.Bytes);
                    SqliteSourceSnapshotStore.SnapshotStagingLease.DeleteLeaseFileUnderLock(item.DirectoryPath);
                }
                catch (DirectoryNotFoundException)
                {
                    SqliteSourceSnapshotStore.SnapshotStagingLease.DeleteLeaseFileUnderLock(item.DirectoryPath);
                    removedDirectories++;
                    removedBytes = checked(removedBytes + item.Candidate.Bytes);
                }
                catch (IOException) { failures++; }
                catch (UnauthorizedAccessException) { failures++; }
                catch (InvalidDataException) { failures++; }
            }
        }
        finally
        {
            ReleaseLocks(current!);
        }

        var state = failures == 0 ? "complete" : removedDirectories == 0 ? "failed" : "partial";
        using (var connection = _database.OpenConnection())
        using (var transaction = connection.BeginTransaction(deferred: false))
        using (var outcome = connection.CreateCommand())
        {
            outcome.Transaction = transaction;
            outcome.CommandText = """
                UPDATE snapshot_staging_retention_audit
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
                throw new InvalidOperationException("Could not finalize the snapshot-staging retention audit record.");
            transaction.Commit();
        }
        return new SnapshotStagingRetentionResult(approval.ApprovalId, plan.PlanSha256, state,
            current!.Count, plan.CandidateBytes, removedDirectories, removedBytes, auditSequence);
    }

    private List<LockedCandidate> ReadCandidates(string parent, DateTimeOffset cutoff)
    {
        var result = new List<LockedCandidate>();
        var seen = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(parent, Prefix + "*", SearchOption.TopDirectoryOnly))
            {
                if (++seen > MaximumDirectoriesToInspect)
                    throw new InvalidDataException("Snapshot staging scan exceeds its bounded directory limit.");
                var name = Path.GetFileName(directory);
                if (name.Length != Prefix.Length + 32
                    || !Guid.TryParseExact(name[Prefix.Length..], "N", out var directoryId))
                    continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Snapshot staging directory is a reparse point; retention is refused.");
                var lease = SqliteSourceSnapshotStore.SnapshotStagingLease.TryAcquire(directory);
                if (lease is null) continue;
                var keepLease = false;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Snapshot staging directory became a reparse point during inspection.");
                    var created = SqliteSourceSnapshotStore.SnapshotStagingLease.ReadCreatedUtc(lease);
                    if (created >= cutoff) continue;
                    var tree = ComputeTree(directory);
                    var candidate = new SnapshotStagingRetentionCandidate(directoryId, created, tree.Sha256,
                        tree.FileCount, tree.Bytes);
                    result.Add(new LockedCandidate(candidate, directory, lease));
                    keepLease = true;
                    if (result.Count > MaximumCandidatesPerPlan)
                        throw new InvalidDataException("Expired snapshot staging count exceeds the retention preview limit.");
                    _ = SumBytes(result.Select(item => item.Candidate).ToArray());
                }
                finally
                {
                    if (!keepLease) SqliteSourceSnapshotStore.SnapshotStagingLease.ReleaseAcquired(directory, lease);
                }
            }
            return result.OrderBy(item => item.Candidate.DirectoryId).ToList();
        }
        catch
        {
            ReleaseLocks(result);
            throw;
        }
    }

    private SnapshotStagingRetentionPlan CreatePlan(
        string parent,
        DateTimeOffset cutoff,
        IReadOnlyList<SnapshotStagingRetentionCandidate> candidates)
    {
        var bytes = SumBytes(candidates);
        var parentHash = HashPath(parent);
        return new SnapshotStagingRetentionPlan(_databaseIdentity, parentHash, cutoff,
            ComputePlanDigest(parentHash, cutoff, candidates), candidates, bytes);
    }

    private static string ComputePlanDigest(
        string parentHash,
        DateTimeOffset cutoff,
        IReadOnlyList<SnapshotStagingRetentionCandidate> candidates)
    {
        var payload = new PlanFingerprint(1, parentHash, NormalizeUtc(cutoff).ToString("O"),
            candidates.OrderBy(candidate => candidate.DirectoryId)
                .Select(candidate => new CandidateFingerprint(candidate.DirectoryId.ToString("N"),
                    candidate.CreatedUtc.ToString("O"), candidate.TreeSha256, candidate.FileCount, candidate.Bytes))
                .ToArray());
        return Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, FingerprintJsonOptions)));
    }

    private static (string Sha256, int FileCount, long Bytes) ComputeTree(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        long totalBytes = 0;
        var buffer = new byte[64 * 1024];
        var lengthBytes = new byte[4];
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Snapshot staging artifact contains a reparse point; retention is refused.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                if (++count > MaximumFilesPerDirectory)
                    throw new InvalidDataException("Snapshot staging artifact exceeds its file-count limit.");
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                var pathBytes = Encoding.UTF8.GetBytes(relative);
                if (pathBytes.Length > MaximumRelativePathBytes)
                    throw new InvalidDataException("Snapshot staging artifact contains an oversized relative path.");
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
                    if (totalBytes > MaximumBytesPerDirectory)
                        throw new InvalidDataException("Snapshot staging artifact exceeds its byte limit.");
                    hash.AppendData(buffer, 0, read);
                }
            }
        }
        if (count == 0) throw new InvalidDataException("Snapshot staging artifact contains no files.");
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), count, totalBytes);
    }

    private static long SumBytes(IReadOnlyList<SnapshotStagingRetentionCandidate> candidates)
    {
        long total = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Bytes < 0) throw new InvalidOperationException("Snapshot staging plan contains a negative size.");
            total = checked(total + candidate.Bytes);
            if (total > MaximumBytesPerPlan)
                throw new InvalidDataException("Expired snapshot staging bytes exceed the retention plan limit.");
        }
        return total;
    }

    private static string ValidateParent(string parent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        var fullPath = Path.GetFullPath(parent);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Snapshot staging parent does not exist.");
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Snapshot staging parent is a reparse point; retention is refused.");
        return fullPath;
    }

    private static void ValidateApproval(SnapshotStagingRetentionApproval approval, SnapshotStagingRetentionPlan plan)
    {
        static bool Bounded(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
            && StringComparer.Ordinal.Equals(value, value.Trim());
        if (!Bounded(approval.PolicyId) || !Guid.TryParseExact(approval.ApprovalId, "D", out _)
            || !Bounded(approval.ApprovedById) || !IsSha256(approval.PlanSha256)
            || !StringComparer.Ordinal.Equals(approval.PlanSha256, plan.PlanSha256))
            throw new ArgumentException("Snapshot-staging approval must identify a policy, approver, and exact plan digest.", nameof(approval));
    }

    private static void EnsureDatabaseIntegrity(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        if (!StringComparer.Ordinal.Equals(Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "ok"))
            throw new InvalidDataException("Local application database integrity check failed; snapshot retention was not applied.");
    }

    private static void ReleaseLocks(IEnumerable<LockedCandidate> candidates)
    {
        foreach (var item in candidates)
            SqliteSourceSnapshotStore.SnapshotStagingLease.ReleaseAcquired(item.DirectoryPath, item.Lease);
    }

    private static string HashPath(string path) => Sha256(Encoding.UTF8.GetBytes(path));
    private static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) => value.ToUniversalTime();

    private sealed record LockedCandidate(
        SnapshotStagingRetentionCandidate Candidate,
        string DirectoryPath,
        FileStream Lease);
    private sealed record PlanFingerprint(int SchemaVersion, string StagingParentSha256, string ExpireBeforeUtc,
        CandidateFingerprint[] Candidates);
    private sealed record CandidateFingerprint(string DirectoryId, string CreatedUtc, string TreeSha256,
        int FileCount, long Bytes);
}
