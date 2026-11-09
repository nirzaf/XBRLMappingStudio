using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class SqliteSnapshotStagingRetentionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xms-snapshot-retention-" + Guid.NewGuid().ToString("N"));

    public SqliteSnapshotStagingRetentionServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Preview_IsReadOnlySkipsActiveStagingAndIgnoresCompletedBundles()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedStaging("partial-database", "partial-metadata");
        var activePath = StagingPath("active");
        using var activeLease = SqliteSourceSnapshotStore.SnapshotStagingLease.Create(activePath);
        LocalApplicationDatabase.CreatePrivateDirectory(activePath);
        File.WriteAllText(Path.Combine(activePath, "source.sqlite"), "active");
        var completed = Path.Combine(_directory, "accepted-snapshot");
        Directory.CreateDirectory(completed);
        File.WriteAllText(Path.Combine(completed, "source.sqlite"), "accepted-database");
        File.WriteAllText(Path.Combine(completed, "snapshot.json"), "accepted-metadata");

        var plan = NewService(database).PreviewAbandonedStagingDirectories(_directory, DateTimeOffset.UtcNow.AddSeconds(2));

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(abandoned.Id, candidate.DirectoryId);
        Assert.Equal(2, candidate.FileCount);
        Assert.True(candidate.Bytes > Encoding.UTF8.GetByteCount("partial-database"));
        Assert.True(Directory.Exists(abandoned.Path));
        Assert.True(Directory.Exists(activePath));
        Assert.True(Directory.Exists(completed));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM snapshot_staging_retention_audit"));
    }

    [Fact]
    public void Apply_RemovesOnlyApprovedStagingAndPreservesCompletedBundleAndPublicationReceipt()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedStaging("captured-source", "snapshot-metadata");
        var completed = Path.Combine(_directory, "approved-snapshot");
        Directory.CreateDirectory(completed);
        File.WriteAllText(Path.Combine(completed, "source.sqlite"), "accepted-database");
        File.WriteAllText(Path.Combine(completed, "snapshot.json"), "accepted-metadata");
        var acceptedReport = Path.Combine(_directory, "accepted-report.json");
        var journal = new RunJournal(databasePath: database.Path);
        var receipt = new PublicationService(journal).Publish(acceptedReport,
            Encoding.UTF8.GetBytes("accepted-report"), runId: "retained-snapshot-report");
        var plan = NewService(database).PreviewAbandonedStagingDirectories(_directory, DateTimeOffset.UtcNow.AddSeconds(2));
        var approval = new SnapshotStagingRetentionApproval("snapshot-temp-policy-v1", Guid.NewGuid().ToString("D"),
            "reviewer-id", plan.PlanSha256);

        var result = NewService(database, new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyAbandonedStagingPrune(_directory, plan, approval);

        Assert.Equal("complete", result.State);
        Assert.Equal(1, result.RemovedDirectories);
        Assert.Equal(plan.CandidateBytes, result.RemovedBytes);
        Assert.False(Directory.Exists(abandoned.Path));
        Assert.False(File.Exists(abandoned.Path + SqliteSourceSnapshotStore.SnapshotStagingLease.Suffix));
        Assert.Equal("accepted-database", File.ReadAllText(Path.Combine(completed, "source.sqlite")));
        Assert.Equal("accepted-metadata", File.ReadAllText(Path.Combine(completed, "snapshot.json")));
        Assert.Equal("accepted-report", File.ReadAllText(acceptedReport));
        Assert.Equal(receipt.FinalDigest, journal.Load("retained-snapshot-report")?.FinalDigest);
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM publication_receipts"));
        Assert.Equal(1L, Scalar(database,
            "SELECT COUNT(*) FROM snapshot_staging_retention_audit WHERE approval_id=$id AND state='complete' AND removed_directories=1",
            ("$id", approval.ApprovalId)));
        Assert.Throws<SqliteException>(() => Execute(database, "DELETE FROM snapshot_staging_retention_audit"));
        Assert.Throws<SqliteException>(() => Execute(database,
            "UPDATE snapshot_staging_retention_audit SET policy_id='changed' WHERE approval_id=$id",
            ("$id", approval.ApprovalId)));
        Assert.Equal("ok", database.IntegrityCheck());
    }

    [Fact]
    public void Apply_RequiresExactAuthorizerAndRejectsStaleOrTamperedStaging()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedStaging("source", "metadata");
        var plan = NewService(database).PreviewAbandonedStagingDirectories(_directory, DateTimeOffset.UtcNow.AddSeconds(2));
        var approval = new SnapshotStagingRetentionApproval("snapshot-temp-policy-v1", Guid.NewGuid().ToString("D"),
            "reviewer-id", plan.PlanSha256);

        Assert.Throws<UnauthorizedAccessException>(() => NewService(database)
            .ApplyAbandonedStagingPrune(_directory, plan, approval));
        Assert.Throws<ArgumentException>(() => NewService(database, new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyAbandonedStagingPrune(_directory, plan with { PlanSha256 = new string('0', 64) }, approval));
        File.AppendAllText(Path.Combine(abandoned.Path, "snapshot.json"), "changed-after-preview");
        Assert.Throws<InvalidOperationException>(() => NewService(database, new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyAbandonedStagingPrune(_directory, plan, approval));

        Assert.True(Directory.Exists(abandoned.Path));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM snapshot_staging_retention_audit"));
    }

    [Fact]
    public void Preview_RefusesMalformedLeaseMarkersAndMissingParents()
    {
        var database = NewDatabase();
        var malformed = StagingPath(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(malformed);
        File.WriteAllText(Path.Combine(malformed, "source.sqlite"), "data");
        File.WriteAllText(malformed + SqliteSourceSnapshotStore.SnapshotStagingLease.Suffix, "{}");

        Assert.Throws<InvalidDataException>(() => NewService(database)
            .PreviewAbandonedStagingDirectories(_directory, DateTimeOffset.UtcNow.AddSeconds(2)));
        Assert.Throws<DirectoryNotFoundException>(() => NewService(database)
            .PreviewAbandonedStagingDirectories(Path.Combine(_directory, "missing"), DateTimeOffset.UtcNow.AddSeconds(2)));
        Assert.Equal("ok", database.IntegrityCheck());
    }

    [Fact]
    public void Apply_AuditCanarySurvivesBackup()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedStaging("source", "metadata");
        var plan = NewService(database).PreviewAbandonedStagingDirectories(_directory, DateTimeOffset.UtcNow.AddSeconds(2));
        var approval = new SnapshotStagingRetentionApproval("snapshot-temp-policy-v1", Guid.NewGuid().ToString("D"),
            "reviewer-id", plan.PlanSha256);
        var result = NewService(database, new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyAbandonedStagingPrune(_directory, plan, approval);
        Assert.Equal("complete", result.State);
        Assert.False(Directory.Exists(abandoned.Path));

        var backupPath = Path.Combine(_directory, "snapshot-retention-backup.db");
        database.BackupTo(backupPath);
        var restored = new LocalApplicationDatabase(backupPath);

        Assert.Equal("ok", restored.IntegrityCheck());
        Assert.Equal(1L, Scalar(restored,
            "SELECT COUNT(*) FROM snapshot_staging_retention_audit WHERE approval_id=$id AND plan_sha256=$digest AND state='complete'",
            ("$id", approval.ApprovalId), ("$digest", plan.PlanSha256)));
        Assert.Throws<SqliteException>(() => Execute(restored, "DELETE FROM snapshot_staging_retention_audit"));
    }

    private LocalApplicationDatabase NewDatabase()
        => new(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));

    private SqliteSnapshotStagingRetentionService NewService(
        LocalApplicationDatabase database,
        ISnapshotStagingRetentionPolicyAuthorizer? authorizer = null)
        => new(database, authorizer);

    private (Guid Id, string Path) CreateAbandonedStaging(params string[] contents)
    {
        var path = StagingPath(Guid.NewGuid().ToString("N"));
        LocalApplicationDatabase.CreatePrivateDirectory(path);
        foreach (var (content, index) in contents.Select((value, index) => (value, index)))
            File.WriteAllText(Path.Combine(path, index == 0 ? "source.sqlite" : index == 1 ? "snapshot.json" : $"artifact-{index}.dat"), content);
        File.WriteAllText(path + SqliteSourceSnapshotStore.SnapshotStagingLease.Suffix,
            "{\"SchemaVersion\":1,\"Purpose\":\"sqlite-snapshot-staging\",\"CreatedUtc\":\"2026-01-01T00:00:00+00:00\"}");
        var id = Guid.ParseExact(Path.GetFileName(path)[".xms-snapshot-".Length..], "N");
        return (id, path);
    }

    private string StagingPath(string id) => Path.Combine(_directory, ".xms-snapshot-" + id);

    private static long Scalar(LocalApplicationDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(LocalApplicationDatabase database, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private sealed class ExactPlanAuthorizer(string planSha256) : ISnapshotStagingRetentionPolicyAuthorizer
    {
        public bool IsAuthorized(SnapshotStagingRetentionApproval approval, SnapshotStagingRetentionPlan plan)
            => StringComparer.Ordinal.Equals(approval.PolicyId, "snapshot-temp-policy-v1")
               && StringComparer.Ordinal.Equals(approval.PlanSha256, planSha256)
               && StringComparer.Ordinal.Equals(plan.PlanSha256, planSha256);
    }
}
