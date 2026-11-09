using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class TemporaryArtifactRetentionServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xms-temp-retention-test-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> _spools = new(StringComparer.OrdinalIgnoreCase);

    public TemporaryArtifactRetentionServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        foreach (var path in _spools)
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Preview_IsReadOnlySkipsActiveSpoolsAndOnlyIncludesExpiredOwnerMarkedDirectories()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedSpool("abandoned-facts", "report-json");
        using var active = TemporaryArtifactLease.CreateLargeRun(_directory);
        _spools.Add(active.DirectoryPath);
        File.WriteAllText(Path.Combine(active.DirectoryPath, "report.json"), "active-report");
        var unrelated = Path.Combine(_directory, "xms-large-not-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "unowned");
        var reportPath = Path.Combine(_directory, "accepted-report.json");
        File.WriteAllText(reportPath, "accepted-report");
        var service = NewService(database);

        var plan = service.PreviewAbandonedLargeRunSpools(DateTimeOffset.UtcNow.AddSeconds(2));

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(abandoned.DirectoryId, candidate.DirectoryId);
        Assert.Equal(2, candidate.FileCount);
        Assert.True(candidate.Bytes > Encoding.UTF8.GetByteCount("abandoned-facts"));
        Assert.Equal(3, Directory.GetFiles(abandoned.DirectoryPath, "*", SearchOption.AllDirectories).Length);
        Assert.True(Directory.Exists(active.DirectoryPath));
        Assert.True(Directory.Exists(unrelated));
        Assert.Equal("accepted-report", File.ReadAllText(reportPath));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM temporary_artifact_retention_audit"));
    }

    [Fact]
    public void Apply_RemovesOnlyApprovedOrphanSpoolAndPreservesAcceptedReportAndReceipt()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedSpool("facts-snapshot", "streamed-report");
        var acceptedPath = Path.Combine(_directory, "accepted-report.json");
        var journal = new RunJournal(databasePath: database.Path);
        var receipt = new PublicationService(journal).Publish(acceptedPath,
            Encoding.UTF8.GetBytes("accepted-report"), runId: "accepted-report-run");
        var serviceWithoutAuthorizer = NewService(database);
        var plan = serviceWithoutAuthorizer.PreviewAbandonedLargeRunSpools(DateTimeOffset.UtcNow.AddSeconds(2));
        var approval = new TemporaryArtifactRetentionApproval("local-temp-policy-v1", Guid.NewGuid().ToString("D"),
            "reviewer-id", plan.PlanSha256);
        var service = NewService(database, new ExactPlanAuthorizer(plan.PlanSha256));

        var result = service.ApplyAbandonedLargeRunSpoolPrune(plan, approval);

        Assert.Equal("complete", result.State);
        Assert.Equal(1, result.RemovedDirectories);
        Assert.Equal(plan.CandidateBytes, result.RemovedBytes);
        Assert.False(Directory.Exists(abandoned.DirectoryPath));
        Assert.Equal("accepted-report", File.ReadAllText(acceptedPath));
        Assert.Equal(receipt.FinalDigest, journal.Load("accepted-report-run")?.FinalDigest);
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM publication_receipts"));
        Assert.Equal(1L, Scalar(database,
            "SELECT COUNT(*) FROM temporary_artifact_retention_audit WHERE approval_id=$id AND state='complete' AND removed_directories=1",
            ("$id", approval.ApprovalId)));
        Assert.Throws<SqliteException>(() => Execute(database, "DELETE FROM temporary_artifact_retention_audit"));
        Assert.Throws<SqliteException>(() => Execute(database,
            "UPDATE temporary_artifact_retention_audit SET policy_id='changed' WHERE approval_id=$id",
            ("$id", approval.ApprovalId)));
        Assert.Equal("ok", database.IntegrityCheck());
    }

    [Fact]
    public void Apply_RequiresConfiguredExactAuthorizerAndRejectsStaleOrTamperedTree()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedSpool("facts", "report");
        var plan = NewService(database)
            .PreviewAbandonedLargeRunSpools(DateTimeOffset.UtcNow.AddSeconds(2));
        var approval = new TemporaryArtifactRetentionApproval("local-temp-policy-v1", Guid.NewGuid().ToString("D"),
            "reviewer-id", plan.PlanSha256);

        Assert.Throws<UnauthorizedAccessException>(() => NewService(database)
            .ApplyAbandonedLargeRunSpoolPrune(plan, approval));
        Assert.Throws<ArgumentException>(() => NewService(database,
                new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyAbandonedLargeRunSpoolPrune(plan with { PlanSha256 = new string('0', 64) }, approval));
        File.AppendAllText(Path.Combine(abandoned.DirectoryPath, "report.json"), "changed-after-preview");
        Assert.Throws<InvalidOperationException>(() => NewService(database,
                new ExactPlanAuthorizer(plan.PlanSha256))
            .ApplyAbandonedLargeRunSpoolPrune(plan, approval));

        Assert.True(Directory.Exists(abandoned.DirectoryPath));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM temporary_artifact_retention_audit"));
    }

    [Fact]
    public void Apply_AuditCanarySurvivesBackup()
    {
        var database = NewDatabase();
        var abandoned = CreateAbandonedSpool("facts", "report");
        var plan = NewService(database)
            .PreviewAbandonedLargeRunSpools(DateTimeOffset.UtcNow.AddSeconds(2));
        var approval = new TemporaryArtifactRetentionApproval("local-temp-policy-v1", Guid.NewGuid().ToString("D"),
            "reviewer-id", plan.PlanSha256);
        var service = NewService(database, new ExactPlanAuthorizer(plan.PlanSha256));
        var result = service.ApplyAbandonedLargeRunSpoolPrune(plan, approval);
        Assert.Equal("complete", result.State);
        Assert.False(Directory.Exists(abandoned.DirectoryPath));

        var backupPath = Path.Combine(_directory, "temporary-retention-backup.db");
        database.BackupTo(backupPath);
        var restored = new LocalApplicationDatabase(backupPath);

        Assert.Equal("ok", restored.IntegrityCheck());
        Assert.Equal(1L, Scalar(restored,
            "SELECT COUNT(*) FROM temporary_artifact_retention_audit WHERE approval_id=$id AND plan_sha256=$digest AND state='complete'",
            ("$id", approval.ApprovalId), ("$digest", plan.PlanSha256)));
        Assert.Throws<SqliteException>(() => Execute(restored, "DELETE FROM temporary_artifact_retention_audit"));
    }

    private LocalApplicationDatabase NewDatabase()
        => new(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"));

    private TemporaryArtifactRetentionService NewService(
        LocalApplicationDatabase database,
        ITemporaryArtifactRetentionPolicyAuthorizer? authorizer = null)
        => new(database, authorizer, _directory);

    private (Guid DirectoryId, string DirectoryPath) CreateAbandonedSpool(params string[] contents)
    {
        var lease = TemporaryArtifactLease.CreateLargeRun(_directory);
        var path = lease.DirectoryPath;
        _spools.Add(path);
        foreach (var (content, index) in contents.Select((value, index) => (value, index)))
            File.WriteAllText(Path.Combine(path, index == 0 ? "facts.sqlite" : index == 1 ? "report.json" : $"artifact-{index}.dat"), content);
        var id = Guid.ParseExact(Path.GetFileName(path)["xms-large-".Length..], "N");
        lease.Dispose();
        return (id, path);
    }

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

    private sealed class ExactPlanAuthorizer(string planSha256) : ITemporaryArtifactRetentionPolicyAuthorizer
    {
        public bool IsAuthorized(TemporaryArtifactRetentionApproval approval, TemporaryArtifactRetentionPlan plan)
            => StringComparer.Ordinal.Equals(approval.PolicyId, "local-temp-policy-v1")
               && StringComparer.Ordinal.Equals(approval.PlanSha256, planSha256)
               && StringComparer.Ordinal.Equals(plan.PlanSha256, planSha256);
    }
}
