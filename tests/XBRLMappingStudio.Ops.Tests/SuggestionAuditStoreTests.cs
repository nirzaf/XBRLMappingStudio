using XBRLMappingStudio.AI;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Project;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Ops.Tests;

/// <summary>
/// AI06 end-to-end audit persistence: SuggestionSession accepts, edits, rejects
/// and undoes; every decision lands in <c>suggestion_audit</c> with the governed
/// actor and prompt-template revision, and can be reconstructed from disk by a
/// reviewer without re-running the model.
/// </summary>
public sealed class SuggestionAuditStoreTests : IDisposable
{
    private readonly string _dir;

    public SuggestionAuditStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xms-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string NewDbPath() => Path.Combine(_dir, $"state-{Guid.NewGuid():N}.db");

    [Fact]
    public void Append_LandsRowInDatabase_WithReconstructableContext()
    {
        var database = new LocalApplicationDatabase(NewDbPath());
        var store = new SuggestionAuditStore(database);

        var context = new AuditContext(
            "project-1",
            7,
            new[] { "s1", "s2" },
            "local-retrieval",
            "retrieval",
            "analyst@example",
            "pt-v1",
            """{"concept":"synthetic:Revenue"}""",
            """{"concept":"synthetic:NetIncome"}""",
            "alias 'default' has no pinned deployment");
        var record = new AuditRecord(1, "edit", "Sales", "synthetic:NetIncome", 0, Array.Empty<string>());
        store.Append(record, context);

        var rows = store.Read("project-1", 7);
        var row = Assert.Single(rows);
        Assert.Equal("edit", row.Decision);
        Assert.Equal("analyst@example", row.Actor);
        Assert.Equal("retrieval", row.Method);
        Assert.Equal("pt-v1", row.PromptTemplateRevision);
        Assert.Equal(new[] { "s1", "s2" }, row.CandidateIds);
        Assert.Equal("alias 'default' has no pinned deployment", row.ProviderSnapshotLimitation);
        Assert.Null(row.AcceptedRevision); // only accept/batch-accept stamp an accepted revision
        Assert.Contains("synthetic:Revenue", row.OriginalProposalJson);
        Assert.Contains("synthetic:NetIncome", row.ApprovedEditJson);
    }

    [Fact]
    public void Accept_StampedWithAcceptedRevision()
    {
        var database = new LocalApplicationDatabase(NewDbPath());
        var store = new SuggestionAuditStore(database);
        var context = new AuditContext(
            "p", 3, Array.Empty<string>(), "cfg", "retrieval", "u", "pt", "{}", "{}");
        store.Append(new AuditRecord(1, "accept", "Sales", "synthetic:Revenue", 0, Array.Empty<string>()), context);

        var row = Assert.Single(store.Read("p", 3));
        Assert.Equal(3, row.AcceptedRevision);
    }

    [Fact]
    public void AcceptAllValid_PersistsBatchAndUndoAcrossStoreReopen()
    {
        var databasePath = NewDbPath();
        var database = new LocalApplicationDatabase(databasePath);
        var store = new SuggestionAuditStore(database);
        var profile = SyntheticProfile.Build();
        var project = new MappingProject
        {
            ProjectId = "batch-audit-project",
            Name = "Batch audit fixture",
            ProfileId = SyntheticProfile.ProfileId,
            ProfileDigest = profile.Digest
        };
        var session = new SuggestionSession(
            project,
            new DraftValidator(new TaxonomyIndex(profile), profile),
            store,
            new SuggestionSessionContext(
                Actor: "test-analyst",
                Method: "offline-retrieval",
                ProviderConfigId: "local-retrieval"));
        session.LoadSuggestions(new[]
        {
            new Suggestion("sales", "source:Sales",
                new DraftMapping("sales", "source:Sales", "synthetic:Revenue", "USD", "duration"), "t", 9),
            new Suggestion("costs", "source:Costs",
                new DraftMapping("costs", "source:Costs", "synthetic:Expenses", "USD", "duration"), "t", 8),
        });

        var accepted = session.AcceptAllValid();

        Assert.True(accepted.Applied, accepted.Reason);
        Assert.Equal(SuggestionState.Accepted, session.StateOf("sales"));
        Assert.Equal(SuggestionState.Accepted, session.StateOf("costs"));
        var reopenedStore = new SuggestionAuditStore(new LocalApplicationDatabase(databasePath));
        var batch = Assert.Single(reopenedStore.Read(project.ProjectId, project.SemanticRevision));
        Assert.Equal("batch-accept", batch.Decision);
        Assert.Equal("test-analyst", batch.Actor);
        Assert.Equal("offline-retrieval", batch.Method);
        Assert.Equal(new[] { "sales", "costs" }, batch.CandidateIds);
        Assert.Equal(project.SemanticRevision, batch.AcceptedRevision);
        Assert.Contains("synthetic:Revenue", batch.ApprovedEditJson);
        Assert.Contains("synthetic:Expenses", batch.ApprovedEditJson);

        Assert.True(session.Undo().Applied);
        Assert.Equal(SuggestionState.Pending, session.StateOf("sales"));
        Assert.Equal(SuggestionState.Pending, session.StateOf("costs"));
        Assert.Empty(session.Draft.Nodes);
        Assert.Contains(reopenedStore.Read(project.ProjectId, project.SemanticRevision),
            row => row.Decision == "undo");
    }

    [Fact]
    public void RenderForScan_SurfacesSeededCanaryForAuditLogScanner()
    {
        var database = new LocalApplicationDatabase(NewDbPath());
        var store = new SuggestionAuditStore(database);
        var context = new AuditContext(
            "p", 1, Array.Empty<string>(), "cfg", "retrieval", "u", "pt",
            """{"canary":"12345.67"}""", "");
        store.Append(new AuditRecord(1, "edit", "Sales", "synthetic:Revenue", 0, Array.Empty<string>()), context);

        var log = new AuditLog();
        log.RegisterCanary("12345.67");
        var scan = log.Scan(new[] { store.RenderForScan() });
        Assert.False(scan.Clean);
        Assert.Contains(scan.Findings, f => f.Contains("canary"));
    }

    [Fact]
    public void Append_RejectsMissingProjectId()
    {
        var database = new LocalApplicationDatabase(NewDbPath());
        var store = new SuggestionAuditStore(database);
        var context = new AuditContext(
            "", 1, Array.Empty<string>(), "cfg", "retrieval", "u", "pt", "{}", "{}");
        Assert.Throws<ArgumentException>(() => store.Append(
            new AuditRecord(1, "accept", "Sales", "synthetic:Revenue", 0, Array.Empty<string>()), context));
    }

    [Fact]
    public void MigrationFromV1_PreservesHistoricalRows()
    {
        var path = NewDbPath();

        // 1. Open once to create the current schema, then remove the newer columns
        //    and tables and roll the file back to a strict v1 shape.
        var seed = new LocalApplicationDatabase(path);
        using (var connection = seed.OpenConnection())
        {
            using var rollbackColumns = connection.CreateCommand();
            rollbackColumns.CommandText = """
                ALTER TABLE publication_receipts DROP COLUMN validated_digest;
                ALTER TABLE run_journal DROP COLUMN validated_digest;
                DROP TABLE pdf_source_evidence;
                DROP TABLE pdf_source_documents;
                DROP TABLE pdf_review_audit;
                DROP TABLE taxonomy_profile_index;
                DROP TABLE mixed_publication_reconciliation;
                DROP TABLE retention_audit;
                DROP TABLE temporary_artifact_retention_audit;
                DROP TABLE snapshot_staging_retention_audit;
                """;
            rollbackColumns.ExecuteNonQuery();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE suggestion_audit;";
            drop.ExecuteNonQuery();
            using var recreate = connection.CreateCommand();
            recreate.CommandText = """
                CREATE TABLE suggestion_audit (
                    event_id TEXT PRIMARY KEY NOT NULL,
                    project_id TEXT NOT NULL,
                    semantic_revision INTEGER NOT NULL,
                    source_field_ref TEXT NOT NULL,
                    candidate_ids_json TEXT NOT NULL,
                    provider_config_id TEXT NOT NULL,
                    decision TEXT NOT NULL,
                    accepted_revision INTEGER,
                    occurred_utc TEXT NOT NULL
                );
                CREATE INDEX ix_suggestion_audit_project_revision
                    ON suggestion_audit(project_id, semantic_revision, occurred_utc);
                PRAGMA user_version=1;
                """;
            recreate.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO suggestion_audit (
                    event_id, project_id, semantic_revision, source_field_ref,
                    candidate_ids_json, provider_config_id, decision, accepted_revision, occurred_utc)
                VALUES ('legacy-1','p',1,'Sales','["s1"]','cfg','accept',1,'2026-01-01T00:00:00Z');
                """;
            insert.ExecuteNonQuery();
        }

        // 2. Reopen; the migration to v2 must widen the table without dropping the
        //    historical row.
        var upgraded = new LocalApplicationDatabase(path);
        var store = new SuggestionAuditStore(upgraded);
        var legacy = store.Read("p", 1);
        Assert.Single(legacy);
        Assert.Equal("legacy-1", legacy[0].EventId);
        Assert.Equal("", legacy[0].Actor);
        Assert.Equal("retrieval", legacy[0].Method);
        using (var verify = upgraded.OpenConnection())
        using (var check = verify.CreateCommand())
        {
            check.CommandText = "PRAGMA user_version";
            Assert.Equal(12L, check.ExecuteScalar());
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='taxonomy_profile_index'";
            Assert.Equal(1L, check.ExecuteScalar());
        }

        // 3. A new row on top of the migrated schema still lands cleanly.
        store.Append(
            new AuditRecord(2, "accept", "Sales", "synthetic:Revenue", 0, Array.Empty<string>()),
            new AuditContext("p", 1, Array.Empty<string>(), "cfg", "retrieval", "u", "pt", "{}", "{}"));
        Assert.Equal(2, store.Read("p", 1).Count);
    }
}
