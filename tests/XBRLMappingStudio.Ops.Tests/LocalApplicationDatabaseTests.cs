using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class LocalApplicationDatabaseTests
{
    [Fact]
    public void SelectedAiProviderStoresOnlyAUuidConfigurationReference()
    {
        using var workspace = new TemporaryDirectory();
        var database = new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db"));
        var configId = Guid.NewGuid().ToString("D");

        database.SetSelectedAiProviderConfigId(configId);

        Assert.Equal(configId, database.GetSelectedAiProviderConfigId());
        Assert.Throws<ArgumentException>(() => database.SetSelectedAiProviderConfigId("sk-test-secret-value"));
        database.SetSelectedAiProviderConfigId(null);
        Assert.Null(database.GetSelectedAiProviderConfigId());
    }

    [Fact]
    public void InitializesVersionedSchemaAndPersistsAtomicPublicationReceipt()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "state.db");
        var database = new LocalApplicationDatabase(path);
        Assert.Equal("ok", database.IntegrityCheck());
        using (var schema = database.OpenConnection())
        using (var version = schema.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(8L, version.ExecuteScalar());
            version.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('pdf_source_documents','pdf_source_evidence','pdf_review_audit','taxonomy_profile_index','mixed_publication_reconciliation')";
            Assert.Equal(5L, version.ExecuteScalar());
        }

        var journal = new RunJournal(databasePath: path);
        var staged = new JournalEntry("run-1", "/workspace/report.json", JournalState.Validated,
            "sha256:abc", null, "lock", DateTimeOffset.UtcNow)
        { ValidatedDigest = "sha256:abc" };
        journal.Save(staged);
        var finalized = staged with { State = JournalState.Finalized, FinalDigest = staged.StagedDigest };
        journal.Save(finalized);

        Assert.Equal(finalized, journal.Load("run-1"));
        Assert.Single(journal.AllEntries());
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT final_digest,validated_digest FROM publication_receipts WHERE run_id=$id";
        command.Parameters.AddWithValue("$id", "run-1");
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("sha256:abc", reader.GetString(0));
        Assert.Equal("sha256:abc", reader.GetString(1));
    }

    [Fact]
    public void MigratesVersionTwoDatabaseWithDurableValidatedDigestColumns()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "v2-state.db");
        using (var legacy = new SqliteConnection($"Data Source={path}"))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                PRAGMA foreign_keys=ON;
                CREATE TABLE app_settings (
                    key TEXT PRIMARY KEY NOT NULL,
                    value TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE project_registry (
                    project_path TEXT PRIMARY KEY NOT NULL,
                    project_id TEXT NOT NULL,
                    content_sha256 TEXT,
                    last_opened_utc TEXT NOT NULL
                );
                CREATE TABLE run_journal (
                    run_id TEXT PRIMARY KEY NOT NULL, destination_path TEXT NOT NULL,
                    state INTEGER NOT NULL, staged_digest TEXT, final_digest TEXT,
                    lock_token TEXT, updated_utc TEXT NOT NULL
                );
                CREATE TABLE publication_receipts (
                    run_id TEXT PRIMARY KEY NOT NULL REFERENCES run_journal(run_id) ON DELETE CASCADE,
                    final_digest TEXT NOT NULL, completed_utc TEXT NOT NULL
                );
                CREATE TABLE suggestion_audit (
                    event_id TEXT PRIMARY KEY NOT NULL,
                    project_id TEXT NOT NULL,
                    semantic_revision INTEGER NOT NULL,
                    source_field_ref TEXT NOT NULL,
                    candidate_ids_json TEXT NOT NULL,
                    provider_config_id TEXT NOT NULL,
                    decision TEXT NOT NULL,
                    accepted_revision INTEGER,
                    occurred_utc TEXT NOT NULL,
                    method TEXT NOT NULL DEFAULT 'retrieval',
                    actor TEXT NOT NULL DEFAULT '',
                    prompt_template_revision TEXT NOT NULL DEFAULT '',
                    original_proposal_json TEXT NOT NULL DEFAULT '',
                    approved_edit_json TEXT NOT NULL DEFAULT '',
                    provider_snapshot_limitation TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO app_settings VALUES('density','compact','2026-01-01T00:00:00Z');
                INSERT INTO project_registry VALUES('/workspace/legacy.xmap','legacy-project','sha256:project','2026-01-01T00:00:00Z');
                INSERT INTO run_journal VALUES('legacy-v2','/workspace/report.json',3,'sha256:staged','sha256:final','lock','2026-01-01T00:00:00Z');
                INSERT INTO publication_receipts VALUES('legacy-v2','sha256:final','2026-01-01T00:00:00Z');
                INSERT INTO suggestion_audit (
                    event_id,project_id,semantic_revision,source_field_ref,candidate_ids_json,
                    provider_config_id,decision,accepted_revision,occurred_utc,actor)
                VALUES ('legacy-audit-v2','legacy-project',2,'Revenue','[]','cfg','accept',2,'2026-01-01T00:00:00Z','analyst');
                PRAGMA user_version=2;
                """;
            create.ExecuteNonQuery();
        }

        var database = new LocalApplicationDatabase(path);
        var journal = new RunJournal(databasePath: path);
        var receiptEntry = new JournalEntry("run-v2", "/workspace/report.json", JournalState.Finalized,
            "sha256:final", "sha256:final", "lock", DateTimeOffset.UtcNow)
        { ValidatedDigest = "sha256:validated" };
        journal.Save(receiptEntry);

        Assert.Equal(8, LocalApplicationDatabase.CurrentSchemaVersion);
        var legacyEntry = journal.Load("legacy-v2");
        Assert.Equal("/workspace/report.json", legacyEntry?.DestinationPath);
        Assert.Equal(JournalState.Finalized, legacyEntry?.State);
        Assert.Equal("sha256:staged", legacyEntry?.StagedDigest);
        Assert.Equal("sha256:final", legacyEntry?.FinalDigest);
        Assert.Null(legacyEntry?.ValidatedDigest);
        Assert.Equal(receiptEntry, journal.Load("run-v2"));
        using var connection = database.OpenConnection();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(8L, check.ExecuteScalar());
        check.CommandText = "SELECT value FROM app_settings WHERE key='density'";
        Assert.Equal("compact", check.ExecuteScalar());
        check.CommandText = "SELECT project_id FROM project_registry WHERE project_path='/workspace/legacy.xmap'";
        Assert.Equal("legacy-project", check.ExecuteScalar());
        check.CommandText = "SELECT final_digest FROM publication_receipts WHERE run_id='legacy-v2'";
        Assert.Equal("sha256:final", check.ExecuteScalar());
        check.CommandText = "SELECT actor FROM suggestion_audit WHERE event_id='legacy-audit-v2'";
        Assert.Equal("analyst", check.ExecuteScalar());
        check.CommandText = "SELECT validated_digest FROM publication_receipts WHERE run_id='run-v2'";
        Assert.Equal("sha256:validated", check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='taxonomy_profile_index'";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    [Fact]
    public void MigratesVersionThreeDatabaseWithoutChangingExistingSettings()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "v3-state.db");
        using (var legacy = new SqliteConnection($"Data Source={path}"))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE app_settings(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO app_settings VALUES('density','compact','2026-11-01T00:00:00Z');
                PRAGMA user_version=3;
                """;
            create.ExecuteNonQuery();
        }

        var database = new LocalApplicationDatabase(path);
        using var connection = database.OpenConnection();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT value FROM app_settings WHERE key='density'";
        Assert.Equal("compact", check.ExecuteScalar());
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(8L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='pdf_source_evidence'";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='pdf_review_audit'";
        Assert.Equal(1L, check.ExecuteScalar());
    }

    [Fact]
    public void MigratesVersionSevenMixedPublicationRowsAsXbrlJson()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "v7-mixed-state.db");
        using (var legacy = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE mixed_publication_reconciliation (
                    operation_key TEXT PRIMARY KEY NOT NULL,
                    intent_fingerprint TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    file_digest TEXT NOT NULL,
                    file_state TEXT NOT NULL,
                    file_failure_code TEXT,
                    sqlite_path TEXT NOT NULL,
                    sqlite_table TEXT NOT NULL,
                    sqlite_operation_key TEXT,
                    sqlite_intent_fingerprint TEXT,
                    sqlite_state TEXT NOT NULL,
                    sqlite_rows_written INTEGER,
                    sqlite_failure_code TEXT,
                    updated_utc TEXT NOT NULL
                );
                INSERT INTO mixed_publication_reconciliation(
                    operation_key,intent_fingerprint,file_path,file_digest,file_state,
                    sqlite_path,sqlite_table,sqlite_state,updated_utc)
                VALUES('legacy-mix','sha256:intent','/workspace/report.json','sha256:file','Confirmed',
                    '/workspace/facts.db','facts','Pending','2026-11-01T00:00:00Z');
                PRAGMA user_version=7;
                """;
            create.ExecuteNonQuery();
        }

        var database = new LocalApplicationDatabase(path);
        using var connection = database.OpenConnection();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(8L, check.ExecuteScalar());
        check.CommandText = "SELECT file_kind,file_state,file_digest FROM mixed_publication_reconciliation WHERE operation_key='legacy-mix'";
        using var reader = check.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("xbrl-json", reader.GetString(0));
        Assert.Equal("Confirmed", reader.GetString(1));
        Assert.Equal("sha256:file", reader.GetString(2));
    }

    [Fact]
    public void RefusesFutureSchemaWithoutChangingDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "future.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE preserve_me(value TEXT); INSERT INTO preserve_me VALUES('keep'); PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => { _ = new LocalApplicationDatabase(path); });
        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var check = verify.CreateCommand();
        check.CommandText = "SELECT value FROM preserve_me";
        Assert.Equal("keep", check.ExecuteScalar());
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(99L, check.ExecuteScalar());
        check.CommandText = "PRAGMA journal_mode";
        Assert.Equal("delete", check.ExecuteScalar());
    }

    [Fact]
    public void FailedV1ToV6MigrationRollsBackSchemaVersionAndHistoricalState()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "rollback-v1.db");
        using (var legacy = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            legacy.Open();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                PRAGMA foreign_keys=ON;
                CREATE TABLE app_settings (
                    key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL, updated_utc TEXT NOT NULL
                );
                CREATE TABLE project_registry (
                    project_path TEXT PRIMARY KEY NOT NULL, project_id TEXT NOT NULL,
                    content_sha256 TEXT, last_opened_utc TEXT NOT NULL
                );
                CREATE TABLE run_journal (
                    run_id TEXT PRIMARY KEY NOT NULL, destination_path TEXT NOT NULL,
                    state INTEGER NOT NULL, staged_digest TEXT, final_digest TEXT,
                    lock_token TEXT, updated_utc TEXT NOT NULL
                );
                CREATE TABLE publication_receipts (
                    run_id TEXT PRIMARY KEY NOT NULL REFERENCES run_journal(run_id) ON DELETE CASCADE,
                    final_digest TEXT NOT NULL, completed_utc TEXT NOT NULL
                );
                CREATE TABLE suggestion_audit (
                    event_id TEXT PRIMARY KEY NOT NULL, project_id TEXT NOT NULL,
                    semantic_revision INTEGER NOT NULL, source_field_ref TEXT NOT NULL,
                    candidate_ids_json TEXT NOT NULL, provider_config_id TEXT NOT NULL,
                    decision TEXT NOT NULL, accepted_revision INTEGER, occurred_utc TEXT NOT NULL
                );
                CREATE TABLE taxonomy_profile_index (marker TEXT NOT NULL);
                INSERT INTO app_settings VALUES('theme','dark','2026-01-01T00:00:00Z');
                INSERT INTO run_journal VALUES('keep-run','/report.json',3,'sha256:a','sha256:b','lock','2026-01-01T00:00:00Z');
                INSERT INTO suggestion_audit VALUES('keep-audit','p',1,'Revenue','[]','cfg','accept',1,'2026-01-01T00:00:00Z');
                INSERT INTO taxonomy_profile_index VALUES('collision-sentinel');
                PRAGMA user_version=1;
                """;
            create.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => _ = new LocalApplicationDatabase(path));

        using var verify = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        verify.Open();
        using var check = verify.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, check.ExecuteScalar());
        check.CommandText = "SELECT value FROM app_settings WHERE key='theme'";
        Assert.Equal("dark", check.ExecuteScalar());
        check.CommandText = "SELECT run_id FROM run_journal WHERE run_id='keep-run'";
        Assert.Equal("keep-run", check.ExecuteScalar());
        check.CommandText = "SELECT event_id FROM suggestion_audit WHERE event_id='keep-audit'";
        Assert.Equal("keep-audit", check.ExecuteScalar());
        check.CommandText = "SELECT marker FROM taxonomy_profile_index";
        Assert.Equal("collision-sentinel", check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('run_journal') WHERE name='validated_digest'";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('suggestion_audit') WHERE name='actor'";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('pdf_source_documents','pdf_source_evidence','pdf_review_audit')";
        Assert.Equal(0L, check.ExecuteScalar());
    }

    [Fact]
    public async Task ConcurrentFirstLaunchSerializesSchemaMigration()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "concurrent.db");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new LocalApplicationDatabase(path))));
        Assert.Equal("ok", new LocalApplicationDatabase(path).IntegrityCheck());
    }

    [Fact]
    public void BackupCreatesConsistentIndependentDatabaseAndNeverOverwrites()
    {
        using var workspace = new TemporaryDirectory();
        var sourcePath = Path.Combine(workspace.Path, "state.db");
        var backupPath = Path.Combine(workspace.Path, "backup.db");
        var database = new LocalApplicationDatabase(sourcePath);
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO app_settings(key,value,updated_utc) VALUES('theme','light','2026-01-01T00:00:00Z')";
            command.ExecuteNonQuery();
        }

        database.BackupTo(backupPath);
        Assert.Empty(Directory.GetDirectories(workspace.Path, ".xms-backup-*"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backupPath));
        using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly"))
        {
            backup.Open();
            using var command = backup.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key='theme'";
            Assert.Equal("light", command.ExecuteScalar());
        }
        Assert.Throws<IOException>(() => database.BackupTo(backupPath));
    }

    [Fact]
    public async Task ConcurrentBackupsPublishOnlyOneSnapshotWithoutOverwriting()
    {
        using var workspace = new TemporaryDirectory();
        var database = new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db"));
        var backup = Path.Combine(workspace.Path, "shared-backup.db");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { database.BackupTo(backup); return "saved"; }
            catch (IOException) { return "exists"; }
        })));
        Assert.Equal(1, results.Count(x => x == "saved"));
        Assert.Equal(1, results.Count(x => x == "exists"));
        Assert.Empty(Directory.GetDirectories(workspace.Path, ".xms-backup-*"));
        using var verify = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
        verify.Open();
        using var command = verify.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
    }

    [Fact]
    public void CallerOwnedParentDirectoryPermissionsAreUnchanged()
    {
        if (OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryDirectory();
        var before = File.GetUnixFileMode(workspace.Path);
        _ = new LocalApplicationDatabase(Path.Combine(workspace.Path, "state.db"));
        Assert.Equal(before, File.GetUnixFileMode(workspace.Path));
    }

    [Fact]
    public void PrivateBackupDirectoryRestrictsAccessToCurrentUser()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, $".xms-backup-{Guid.NewGuid():N}");
        LocalApplicationDatabase.CreatePrivateDirectory(path);
        try
        {
            if (OperatingSystem.IsWindows())
                Assert.True(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);
            else
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(path); }
    }

    [Fact]
    public void FailedReceiptInsertRollsBackTheJournalRow()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "state.db");
        var database = new LocalApplicationDatabase(path);
        using (var connection = database.OpenConnection())
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_receipt BEFORE INSERT ON publication_receipts BEGIN SELECT RAISE(ABORT,'injected'); END";
            trigger.ExecuteNonQuery();
        }
        var journal = new RunJournal(databasePath: path);
        var entry = new JournalEntry("rollback", "/workspace/report.json", JournalState.Finalized,
            "sha256:a", "sha256:a", "lock", DateTimeOffset.UtcNow);
        Assert.Throws<SqliteException>(() => journal.Save(entry));
        using var verify = database.OpenConnection();
        using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM run_journal WHERE run_id='rollback'";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM publication_receipts WHERE run_id='rollback'";
        Assert.Equal(0L, check.ExecuteScalar());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-state-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
