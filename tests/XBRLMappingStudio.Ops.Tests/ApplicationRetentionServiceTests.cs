using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.AI;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class ApplicationRetentionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xms-retention-" + Guid.NewGuid().ToString("N"));

    public ApplicationRetentionServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Preview_IsReadOnlyBoundedAndIncludesOnlyExpiredTaxonomyCache()
    {
        var database = NewDatabase();
        InsertCache(database, "old-profile", "a", "2026-10-01T00:00:00.0000000+00:00", "old-cache");
        InsertCache(database, "current-profile", "b", "2026-11-02T00:00:00.0000000+00:00", "current-cache");
        var service = new ApplicationRetentionService(database);

        var plan = service.PreviewTaxonomyCachePrune(DateTimeOffset.Parse("2026-11-01T00:00:00Z"));

        var candidate = Assert.Single(plan.TaxonomyCacheCandidates);
        Assert.Equal("old-profile", candidate.ProfileId);
        Assert.Equal(Encoding.UTF8.GetByteCount("old-cache"), candidate.SnapshotBytes);
        Assert.Equal(Digest("old-cache"), candidate.SnapshotSha256);
        Assert.Equal(Encoding.UTF8.GetByteCount("old-cache"), plan.CandidateBytes);
        Assert.Equal(64, plan.PlanSha256.Length);
        Assert.Equal(2L, Scalar(database, "SELECT COUNT(*) FROM taxonomy_profile_index"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM retention_audit"));
    }

    [Fact]
    public void Apply_DeletesOnlyPreviewedDisposableCacheAndPreservesAcceptedDataAndAudit()
    {
        var database = NewDatabase();
        InsertCache(database, "expired-profile", "a", "2026-10-01T00:00:00.0000000+00:00", "expired-cache");
        InsertCache(database, "current-profile", "b", "2026-11-02T00:00:00.0000000+00:00", "current-cache");
        var suggestionStore = new SuggestionAuditStore(database);
        suggestionStore.Append(
            new AuditRecord(1, "accept", "Revenue", "synthetic:Revenue", 0, Array.Empty<string>()),
            new AuditContext("project-1", 1, Array.Empty<string>(), "offline", "retrieval", "reviewer-id", "pt-v1", "{}", "{}"));
        var destination = Path.Combine(_directory, "accepted-report.json");
        var journal = new RunJournal(databasePath: database.Path);
        var receipt = new PublicationService(journal).Publish(destination, Encoding.UTF8.GetBytes("accepted-report"), runId: "accepted-run");
        var auditRowsBefore = suggestionStore.Read("project-1", 1).Count;
        var receiptRowsBefore = Scalar(database, "SELECT COUNT(*) FROM publication_receipts");
        var runRowsBefore = Scalar(database, "SELECT COUNT(*) FROM run_journal");
        var cutoff = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var plan = new ApplicationRetentionService(database).PreviewTaxonomyCachePrune(cutoff);
        var approval = new RetentionApproval("local-cache-policy-v1", Guid.NewGuid().ToString("D"), "reviewer-id", plan.PlanSha256);
        var service = new ApplicationRetentionService(database, new ExactPlanAuthorizer(plan.PlanSha256));

        var result = service.ApplyTaxonomyCachePrune(plan, approval);

        Assert.Equal(1, result.DeletedCacheEntries);
        Assert.Equal(plan.CandidateBytes, result.DeletedCacheBytes);
        Assert.Equal(plan.PlanSha256, result.PlanSha256);
        Assert.True(result.AuditSequence > 0);
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM taxonomy_profile_index"));
        Assert.Equal("current-profile", Text(database, "SELECT profile_id FROM taxonomy_profile_index"));
        Assert.Equal(auditRowsBefore, suggestionStore.Read("project-1", 1).Count);
        Assert.Equal(receiptRowsBefore, Scalar(database, "SELECT COUNT(*) FROM publication_receipts"));
        Assert.Equal(runRowsBefore, Scalar(database, "SELECT COUNT(*) FROM run_journal"));
        Assert.Equal(receipt.FinalDigest, journal.Load("accepted-run")?.FinalDigest);
        Assert.Equal("accepted-report", Encoding.UTF8.GetString(File.ReadAllBytes(destination)));
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM retention_audit WHERE approval_id=$id", ("$id", approval.ApprovalId)));
        Assert.Throws<SqliteException>(() => Execute(database, "DELETE FROM retention_audit"));
        Assert.Throws<SqliteException>(() => Execute(database, "UPDATE retention_audit SET policy_id='changed'"));
        Assert.Equal("ok", database.IntegrityCheck());
    }

    [Fact]
    public void Apply_RequiresConfiguredAuthorizerAndExactPlanDigest()
    {
        var database = NewDatabase();
        InsertCache(database, "expired-profile", "a", "2026-10-01T00:00:00.0000000+00:00", "expired-cache");
        var cutoff = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var plan = new ApplicationRetentionService(database).PreviewTaxonomyCachePrune(cutoff);
        var approval = new RetentionApproval("local-cache-policy-v1", Guid.NewGuid().ToString("D"), "reviewer-id", plan.PlanSha256);

        Assert.Throws<UnauthorizedAccessException>(() => new ApplicationRetentionService(database).ApplyTaxonomyCachePrune(plan, approval));
        Assert.Throws<UnauthorizedAccessException>(() => new ApplicationRetentionService(database, new ExactPlanAuthorizer(plan.PlanSha256, authorized: false))
            .ApplyTaxonomyCachePrune(plan, approval));
        Assert.Throws<ArgumentException>(() => new ApplicationRetentionService(database, new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyTaxonomyCachePrune(plan, approval with { PlanSha256 = new string('0', 64) }));
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM taxonomy_profile_index"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM retention_audit"));
    }

    [Fact]
    public void Apply_RejectsStalePlanWithoutDeletingNewOrChangedCacheRows()
    {
        var database = NewDatabase();
        InsertCache(database, "expired-profile", "a", "2026-10-01T00:00:00.0000000+00:00", "expired-cache");
        var cutoff = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var plan = new ApplicationRetentionService(database).PreviewTaxonomyCachePrune(cutoff);
        var approval = new RetentionApproval("local-cache-policy-v1", Guid.NewGuid().ToString("D"), "reviewer-id", plan.PlanSha256);
        InsertCache(database, "second-expired-profile", "c", "2026-10-10T00:00:00.0000000+00:00", "new-expired-cache");

        Assert.Throws<InvalidOperationException>(() => new ApplicationRetentionService(database, new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyTaxonomyCachePrune(plan, approval));

        Assert.Equal(2L, Scalar(database, "SELECT COUNT(*) FROM taxonomy_profile_index"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM retention_audit"));
    }

    [Fact]
    public void Preview_RefusesCorruptCacheAndDoesNotDeleteIt()
    {
        var database = NewDatabase();
        InsertCache(database, "corrupt-profile", "a", "2026-10-01T00:00:00.0000000+00:00", "altered-cache", digestOverride: new string('0', 64));

        Assert.Throws<InvalidDataException>(() => new ApplicationRetentionService(database)
            .PreviewTaxonomyCachePrune(DateTimeOffset.Parse("2026-11-01T00:00:00Z")));

        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM taxonomy_profile_index"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM retention_audit"));
    }

    private LocalApplicationDatabase NewDatabase()
        => new(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));

    private static void InsertCache(
        LocalApplicationDatabase database,
        string profileId,
        string digestSuffix,
        string indexedUtc,
        string snapshot,
        string? digestOverride = null)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO taxonomy_profile_index(
                profile_id, profile_digest, cache_schema, taxonomy_version, concept_count,
                snapshot_json, snapshot_sha256, indexed_utc)
            VALUES($profile_id, $profile_digest, 3, '2025', 1, $snapshot, $snapshot_sha256, $indexed_utc);
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        command.Parameters.AddWithValue("$profile_digest", new string(digestSuffix[0], 64));
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$snapshot_sha256", digestOverride ?? Digest(snapshot));
        command.Parameters.AddWithValue("$indexed_utc", indexedUtc);
        command.ExecuteNonQuery();
    }

    private static string Digest(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static long Scalar(LocalApplicationDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? Text(LocalApplicationDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static void Execute(LocalApplicationDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ExactPlanAuthorizer(string planSha256, bool authorized = true) : IRetentionPolicyAuthorizer
    {
        public bool IsAuthorized(RetentionApproval approval, ApplicationRetentionPlan plan)
            => authorized
               && StringComparer.Ordinal.Equals(approval.PolicyId, "local-cache-policy-v1")
               && StringComparer.Ordinal.Equals(approval.PlanSha256, planSha256)
               && StringComparer.Ordinal.Equals(plan.PlanSha256, planSha256);
    }
}
