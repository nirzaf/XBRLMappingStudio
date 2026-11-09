using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace XBRLMappingStudio.Application;

/// <summary>
/// An explicit, externally authorized local retention decision. This record is not
/// itself proof of authority; the configured policy authorizer must verify it.
/// </summary>
public sealed record RetentionApproval(
    string PolicyId,
    string ApprovalId,
    string ApprovedById,
    string PlanSha256);

public interface IRetentionPolicyAuthorizer
{
    bool IsAuthorized(RetentionApproval approval, ApplicationRetentionPlan plan);
}

public sealed record TaxonomyCacheRetentionCandidate(
    string ProfileId,
    string ProfileDigest,
    int CacheSchema,
    string TaxonomyVersion,
    int ConceptCount,
    string SnapshotSha256,
    string IndexedUtc,
    long SnapshotBytes);

/// <summary>
/// Exact, read-only preview of cache rows older than the caller-selected cutoff.
/// Only disposable taxonomy metadata is in scope; projects, reports, receipts,
/// source snapshots, PDF evidence and business audit rows are never candidates.
/// </summary>
public sealed record ApplicationRetentionPlan(
    string DatabaseIdentity,
    DateTimeOffset ExpireBeforeUtc,
    string PlanSha256,
    IReadOnlyList<TaxonomyCacheRetentionCandidate> TaxonomyCacheCandidates,
    long CandidateBytes);

public sealed record ApplicationRetentionResult(
    string ApprovalId,
    string PlanSha256,
    int DeletedCacheEntries,
    long DeletedCacheBytes,
    long AuditSequence);

/// <summary>
/// Plans and applies age-based pruning for the recomputable taxonomy metadata cache.
/// There is deliberately no default retention duration or permissive authorizer.
/// </summary>
public sealed class ApplicationRetentionService
{
    public const int MaximumCandidatesPerPlan = 1_024;
    public const long MaximumCandidateBytesPerPlan = 256L * 1024 * 1024;
    private const int MaximumSnapshotBytes = 64 * 1024 * 1024;
    private const int MaximumIdentifierCharacters = 256;

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16
    };

    private readonly LocalApplicationDatabase _database;
    private readonly IRetentionPolicyAuthorizer? _authorizer;
    private readonly string _databaseIdentity;

    public ApplicationRetentionService(
        LocalApplicationDatabase database,
        IRetentionPolicyAuthorizer? authorizer = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _authorizer = authorizer;
        _databaseIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database.Path)))
            .ToLowerInvariant();
    }

    /// <summary>Build a bounded preview without changing application state.</summary>
    public ApplicationRetentionPlan PreviewTaxonomyCachePrune(DateTimeOffset expireBeforeUtc)
    {
        var cutoff = NormalizeUtc(expireBeforeUtc);
        using var connection = _database.OpenConnection();
        EnsureDatabaseIntegrity(connection);
        var candidates = ReadCandidates(connection, cutoff);
        return CreatePlan(cutoff, candidates);
    }

    /// <summary>
    /// Delete exactly the cache rows in a still-current preview. A policy authorizer
    /// must approve the exact plan digest; deletion and immutable audit append share
    /// one SQLite transaction.
    /// </summary>
    public ApplicationRetentionResult ApplyTaxonomyCachePrune(
        ApplicationRetentionPlan plan,
        RetentionApproval approval)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (!StringComparer.Ordinal.Equals(plan.DatabaseIdentity, _databaseIdentity))
            throw new InvalidOperationException("The retention plan belongs to a different local application database.");
        if (plan.TaxonomyCacheCandidates is null
            || plan.TaxonomyCacheCandidates.Count > MaximumCandidatesPerPlan
            || plan.TaxonomyCacheCandidates.Any(candidate => candidate is null)
            || plan.CandidateBytes != SumCandidateBytes(plan.TaxonomyCacheCandidates))
            throw new InvalidOperationException("The retention preview is malformed or exceeds its bounded limits.");

        ValidateApproval(approval, plan);
        if (_authorizer?.IsAuthorized(approval, plan) != true)
            throw new UnauthorizedAccessException("The retention policy approval is missing, invalid, or not authorized.");

        var suppliedPlanDigest = ComputePlanDigest(plan.ExpireBeforeUtc, plan.TaxonomyCacheCandidates);
        if (!StringComparer.Ordinal.Equals(plan.PlanSha256, suppliedPlanDigest))
            throw new InvalidOperationException("The retention preview was changed after it was created.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureDatabaseIntegrity(connection, transaction);
        var currentPlan = CreatePlan(
            NormalizeUtc(plan.ExpireBeforeUtc),
            ReadCandidates(connection, NormalizeUtc(plan.ExpireBeforeUtc), transaction));
        if (!StringComparer.Ordinal.Equals(currentPlan.PlanSha256, plan.PlanSha256))
            throw new InvalidOperationException("The retention preview is stale; create a new preview before applying policy.");

        if (currentPlan.TaxonomyCacheCandidates.Count == 0)
        {
            transaction.Rollback();
            return new ApplicationRetentionResult(approval.ApprovalId, plan.PlanSha256, 0, 0, 0);
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM taxonomy_profile_index
                WHERE profile_id=$profile_id AND profile_digest=$profile_digest
                  AND indexed_utc < $cutoff;
                """;
            delete.Parameters.Add("$profile_id", SqliteType.Text);
            delete.Parameters.Add("$profile_digest", SqliteType.Text);
            delete.Parameters.AddWithValue("$cutoff", currentPlan.ExpireBeforeUtc.ToString("O"));
            foreach (var candidate in currentPlan.TaxonomyCacheCandidates)
            {
                delete.Parameters["$profile_id"].Value = candidate.ProfileId;
                delete.Parameters["$profile_digest"].Value = candidate.ProfileDigest;
                if (delete.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("A taxonomy cache row changed during retention; the transaction was rolled back.");
            }
        }

        long auditSequence;
        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO retention_audit(
                    approval_id, policy_id, approved_by_id, plan_sha256,
                    expired_before_utc, deleted_cache_entries, deleted_cache_bytes, occurred_utc)
                VALUES($approval_id, $policy_id, $approved_by_id, $plan_sha256,
                    $cutoff, $entries, $bytes, $occurred_utc);
                SELECT last_insert_rowid();
                """;
            audit.Parameters.AddWithValue("$approval_id", approval.ApprovalId);
            audit.Parameters.AddWithValue("$policy_id", approval.PolicyId);
            audit.Parameters.AddWithValue("$approved_by_id", approval.ApprovedById);
            audit.Parameters.AddWithValue("$plan_sha256", plan.PlanSha256);
            audit.Parameters.AddWithValue("$cutoff", currentPlan.ExpireBeforeUtc.ToString("O"));
            audit.Parameters.AddWithValue("$entries", currentPlan.TaxonomyCacheCandidates.Count);
            audit.Parameters.AddWithValue("$bytes", currentPlan.CandidateBytes);
            audit.Parameters.AddWithValue("$occurred_utc", DateTimeOffset.UtcNow.ToString("O"));
            auditSequence = Convert.ToInt64(audit.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        transaction.Commit();
        return new ApplicationRetentionResult(
            approval.ApprovalId,
            plan.PlanSha256,
            currentPlan.TaxonomyCacheCandidates.Count,
            currentPlan.CandidateBytes,
            auditSequence);
    }

    private ApplicationRetentionPlan CreatePlan(
        DateTimeOffset cutoff,
        IReadOnlyList<TaxonomyCacheRetentionCandidate> candidates)
    {
        var bytes = SumCandidateBytes(candidates);
        return new ApplicationRetentionPlan(
            _databaseIdentity,
            cutoff,
            ComputePlanDigest(cutoff, candidates),
            candidates,
            bytes);
    }

    private string ComputePlanDigest(
        DateTimeOffset cutoff,
        IReadOnlyList<TaxonomyCacheRetentionCandidate> candidates)
    {
        var payload = new PlanFingerprint(
            SchemaVersion: 1,
            DatabaseIdentity: _databaseIdentity,
            ExpireBeforeUtc: NormalizeUtc(cutoff).ToString("O"),
            Candidates: candidates.OrderBy(candidate => candidate.ProfileId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ProfileDigest, StringComparer.Ordinal)
                .Select(candidate => new CandidateFingerprint(
                    candidate.ProfileId,
                    candidate.ProfileDigest,
                    candidate.CacheSchema,
                    candidate.TaxonomyVersion,
                    candidate.ConceptCount,
                    candidate.SnapshotSha256,
                    candidate.IndexedUtc,
                    candidate.SnapshotBytes))
                .ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(payload, FingerprintJsonOptions)))).ToLowerInvariant();
    }

    private static long SumCandidateBytes(IReadOnlyList<TaxonomyCacheRetentionCandidate> candidates)
    {
        long total = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.SnapshotBytes < 0)
                throw new InvalidOperationException("A retention preview contains a negative cache size.");
            total = checked(total + candidate.SnapshotBytes);
            if (total > MaximumCandidateBytesPerPlan)
                throw new InvalidOperationException("The retention preview exceeds its bounded byte limit.");
        }
        return total;
    }

    private static IReadOnlyList<TaxonomyCacheRetentionCandidate> ReadCandidates(
        SqliteConnection connection,
        DateTimeOffset cutoff,
        SqliteTransaction? transaction = null)
    {
        var cutoffText = cutoff.ToString("O");
        using (var oversized = connection.CreateCommand())
        {
            oversized.Transaction = transaction;
            oversized.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM taxonomy_profile_index
                    WHERE indexed_utc < $cutoff
                      AND length(CAST(snapshot_json AS BLOB)) > $max_bytes);
                """;
            oversized.Parameters.AddWithValue("$cutoff", cutoffText);
            oversized.Parameters.AddWithValue("$max_bytes", MaximumSnapshotBytes);
            if (Convert.ToInt32(oversized.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("An expired taxonomy cache entry exceeds the supported integrity-check size.");
        }

        var candidates = new List<TaxonomyCacheRetentionCandidate>();
        long candidateBytes = 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT profile_id, profile_digest, cache_schema, taxonomy_version,
                   concept_count, snapshot_json, snapshot_sha256, indexed_utc
            FROM taxonomy_profile_index
            WHERE indexed_utc < $cutoff
            ORDER BY indexed_utc, profile_id, profile_digest
            LIMIT $max_candidates_plus_one;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffText);
        command.Parameters.AddWithValue("$max_candidates_plus_one", MaximumCandidatesPerPlan + 1);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (candidates.Count == MaximumCandidatesPerPlan)
                throw new InvalidDataException("The expired taxonomy cache set exceeds the bounded retention preview limit.");
            var profileId = reader.GetString(0);
            var profileDigest = reader.GetString(1);
            var cacheSchema = reader.GetInt32(2);
            var taxonomyVersion = reader.GetString(3);
            var conceptCount = reader.GetInt32(4);
            var snapshotJson = reader.GetString(5);
            var snapshotSha256 = reader.GetString(6);
            var indexedUtc = reader.GetString(7);
            var snapshotBytes = Encoding.UTF8.GetByteCount(snapshotJson);
            if (string.IsNullOrWhiteSpace(profileId)
                || string.IsNullOrWhiteSpace(profileDigest)
                || string.IsNullOrWhiteSpace(taxonomyVersion)
                || cacheSchema <= 0
                || conceptCount < 0
                || snapshotBytes > MaximumSnapshotBytes
                || !IsSha256(snapshotSha256)
                || !StringComparer.Ordinal.Equals(snapshotSha256, ComputeSha256(snapshotJson))
                || !DateTimeOffset.TryParse(indexedUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsedIndexedUtc)
                || parsedIndexedUtc.Offset != TimeSpan.Zero
                || parsedIndexedUtc >= cutoff)
                throw new InvalidDataException("An expired taxonomy cache entry is malformed or fails its content digest check.");

            candidateBytes = checked(candidateBytes + snapshotBytes);
            if (candidateBytes > MaximumCandidateBytesPerPlan)
                throw new InvalidDataException("The expired taxonomy cache set exceeds the bounded retention preview byte limit.");
            candidates.Add(new TaxonomyCacheRetentionCandidate(
                profileId, profileDigest, cacheSchema, taxonomyVersion, conceptCount,
                snapshotSha256, indexedUtc, snapshotBytes));
        }
        return candidates;
    }

    private static void EnsureDatabaseIntegrity(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        if (!StringComparer.Ordinal.Equals(Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), "ok"))
            throw new InvalidDataException("Local application database integrity check failed; retention was not applied.");
    }

    private static void ValidateApproval(RetentionApproval approval, ApplicationRetentionPlan plan)
    {
        static bool BoundedIdentifier(string value)
            => !string.IsNullOrWhiteSpace(value)
               && value.Length <= MaximumIdentifierCharacters
               && StringComparer.Ordinal.Equals(value, value.Trim());

        if (!BoundedIdentifier(approval.PolicyId)
            || !Guid.TryParseExact(approval.ApprovalId, "D", out _)
            || !BoundedIdentifier(approval.ApprovedById)
            || !IsSha256(approval.PlanSha256)
            || !StringComparer.Ordinal.Equals(approval.PlanSha256, plan.PlanSha256))
            throw new ArgumentException("Retention approval must identify an authorized policy, approver, and exact preview digest.", nameof(approval));
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsSha256(string value)
        => !string.IsNullOrEmpty(value)
           && value.Length == 64
           && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record PlanFingerprint(
        int SchemaVersion,
        string DatabaseIdentity,
        string ExpireBeforeUtc,
        CandidateFingerprint[] Candidates);

    private sealed record CandidateFingerprint(
        string ProfileId,
        string ProfileDigest,
        int CacheSchema,
        string TaxonomyVersion,
        int ConceptCount,
        string SnapshotSha256,
        string IndexedUtc,
        long SnapshotBytes);
}
