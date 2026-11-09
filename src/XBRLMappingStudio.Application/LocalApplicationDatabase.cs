using Microsoft.Data.Sqlite;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace XBRLMappingStudio.Application;

/// <summary>Versioned local application state. Mapping graphs remain in portable .xmap files.</summary>
public sealed class LocalApplicationDatabase
{
    /// <summary>
    /// AI06: v2 widens <c>suggestion_audit</c> with governed-actor and prompt-template
    /// columns. v3 adds the digest of the artifact accepted by the profile worker to
    /// the publication journal and durable receipt. v4 adds managed PDF evidence
    /// references, v5 adds append-only PDF review audit events, v6 adds an
    /// integrity-checked offline taxonomy metadata cache, v7 adds durable mixed
    /// file/SQLite publication reconciliation, v8 records its file artifact kind,
    /// v9 expands mixed reconciliation to versioned record file contracts, and v10
    /// adds append-only audit for explicitly authorized disposable-cache pruning,
    /// and v11 adds a pending-to-terminal audit for abandoned large-run spool cleanup.
    /// Migrations are non-destructive.
    /// </summary>
    public const int CurrentSchemaVersion = 11;
    public const string SelectedAiProviderConfigSetting = "selected_ai_provider_config_id";
    public string Path { get; }

    public LocalApplicationDatabase(string? path = null)
    {
        var applicationOwnedPath = path is null;
        Path = System.IO.Path.GetFullPath(path ?? DefaultPath());
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        if (applicationOwnedPath) RestrictDirectoryPermissions(directory);
        Migrate();
        if (applicationOwnedPath && !OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (PlatformNotSupportedException) { }
        }
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        busy.ExecuteNonQuery();
        return connection;
    }

    public string IntegrityCheck()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    /// <summary>Persist only a nonsecret UUID reference to an approved AI provider configuration.</summary>
    public void SetSelectedAiProviderConfigId(string? configId)
    {
        if (configId is not null && !Guid.TryParseExact(configId, "D", out _))
            throw new ArgumentException("Provider configuration ID must be a UUID; secrets and endpoints are not accepted here.", nameof(configId));

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        if (configId is null)
        {
            command.CommandText = "DELETE FROM app_settings WHERE key=$key";
            command.Parameters.AddWithValue("$key", SelectedAiProviderConfigSetting);
        }
        else
        {
            command.CommandText = """
                INSERT INTO app_settings(key,value,updated_utc) VALUES($key,$value,$time)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_utc=excluded.updated_utc
                """;
            command.Parameters.AddWithValue("$key", SelectedAiProviderConfigSetting);
            command.Parameters.AddWithValue("$value", configId);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        command.ExecuteNonQuery();
    }

    public string? GetSelectedAiProviderConfigId()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", SelectedAiProviderConfigSetting);
        return command.ExecuteScalar() as string;
    }

    public void BackupTo(string backupPath)
    {
        var fullBackupPath = System.IO.Path.GetFullPath(backupPath);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (comparer.Equals(Path, fullBackupPath))
            throw new ArgumentException("Backup path must differ from the application database.", nameof(backupPath));
        if (File.Exists(fullBackupPath)) throw new IOException("Backup destination already exists; refusing to overwrite it.");
        var directory = System.IO.Path.GetDirectoryName(fullBackupPath)!;
        Directory.CreateDirectory(directory);
        var temporaryDirectory = System.IO.Path.Combine(directory, $".xms-backup-{Guid.NewGuid():N}");
        var temporaryPath = System.IO.Path.Combine(temporaryDirectory, $"{System.IO.Path.GetFileName(fullBackupPath)}.tmp");
        var ownsTemporaryDirectory = false;
        try
        {
            CreatePrivateDirectory(temporaryDirectory);
            ownsTemporaryDirectory = true;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(temporaryPath, options)) { }
            using var source = OpenConnection();
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = 5,
                ForeignKeys = true
            }.ToString()))
            {
                target.Open();
                source.BackupDatabase(target);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            PublishBackupWithoutOverwrite(temporaryPath, fullBackupPath);
        }
        finally
        {
            if (ownsTemporaryDirectory && Directory.Exists(temporaryDirectory))
                try { Directory.Delete(temporaryDirectory, recursive: true); } catch (IOException) { }
        }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Could not determine the current Windows user for the private backup directory.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            FileSystemAclExtensions.CreateDirectory(security, path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Could not determine the current Windows user for the private evidence directory.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            if (!Directory.Exists(path))
            {
                try { FileSystemAclExtensions.CreateDirectory(security, path); }
                catch (IOException) when (Directory.Exists(path))
                {
                    FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
                }
            }
            else
            {
                FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
            }
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void Migrate()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
            throw new InvalidDataException($"Application database schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        if (version < 1)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
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
                    run_id TEXT PRIMARY KEY NOT NULL,
                    destination_path TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    staged_digest TEXT,
                    final_digest TEXT,
                    lock_token TEXT,
                    updated_utc TEXT NOT NULL
                );
                CREATE INDEX ix_run_journal_destination_updated
                    ON run_journal(destination_path, updated_utc DESC);
                CREATE TABLE publication_receipts (
                    run_id TEXT PRIMARY KEY NOT NULL REFERENCES run_journal(run_id) ON DELETE CASCADE,
                    final_digest TEXT NOT NULL,
                    completed_utc TEXT NOT NULL
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
                    occurred_utc TEXT NOT NULL
                );
                CREATE INDEX ix_suggestion_audit_project_revision
                    ON suggestion_audit(project_id, semantic_revision, occurred_utc);
                PRAGMA user_version=1;
                """;
            migration.ExecuteNonQuery();
        }
        if (version < 2)
        {
            // v2: AI06 governed-actor + prompt-template revision + optional payloads.
            // Each ADD COLUMN is a no-op default so historical v1 rows remain readable.
            using var migration2 = connection.CreateCommand();
            migration2.Transaction = transaction;
            migration2.CommandText = """
                ALTER TABLE suggestion_audit ADD COLUMN method TEXT NOT NULL DEFAULT 'retrieval';
                ALTER TABLE suggestion_audit ADD COLUMN actor TEXT NOT NULL DEFAULT '';
                ALTER TABLE suggestion_audit ADD COLUMN prompt_template_revision TEXT NOT NULL DEFAULT '';
                ALTER TABLE suggestion_audit ADD COLUMN original_proposal_json TEXT NOT NULL DEFAULT '';
                ALTER TABLE suggestion_audit ADD COLUMN approved_edit_json TEXT NOT NULL DEFAULT '';
                ALTER TABLE suggestion_audit ADD COLUMN provider_snapshot_limitation TEXT NOT NULL DEFAULT '';
                PRAGMA user_version=2;
                """;
            migration2.ExecuteNonQuery();
        }
        if (version < 3)
        {
            using var migration3 = connection.CreateCommand();
            migration3.Transaction = transaction;
            migration3.CommandText = """
                ALTER TABLE run_journal ADD COLUMN validated_digest TEXT;
                ALTER TABLE publication_receipts ADD COLUMN validated_digest TEXT;
                PRAGMA user_version=3;
                """;
            migration3.ExecuteNonQuery();
        }
        if (version < 4)
        {
            using var migration4 = connection.CreateCommand();
            migration4.Transaction = transaction;
            migration4.CommandText = """
                CREATE TABLE pdf_source_documents (
                    source_sha256 TEXT PRIMARY KEY NOT NULL,
                    source_byte_length INTEGER NOT NULL CHECK(source_byte_length > 0),
                    managed_file_name TEXT NOT NULL UNIQUE,
                    extraction_method TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE pdf_source_evidence (
                    evidence_id TEXT PRIMARY KEY NOT NULL,
                    source_sha256 TEXT NOT NULL REFERENCES pdf_source_documents(source_sha256) ON DELETE RESTRICT,
                    original_path TEXT NOT NULL,
                    page_number INTEGER NOT NULL CHECK(page_number > 0),
                    page_rotation_degrees INTEGER NOT NULL CHECK(page_rotation_degrees IN (0, 90, 180, 270)),
                    page_width REAL NOT NULL CHECK(page_width > 0),
                    page_height REAL NOT NULL CHECK(page_height > 0),
                    media_left REAL NOT NULL,
                    media_bottom REAL NOT NULL,
                    media_right REAL NOT NULL,
                    media_top REAL NOT NULL,
                    crop_left REAL NOT NULL,
                    crop_bottom REAL NOT NULL,
                    crop_right REAL NOT NULL,
                    crop_top REAL NOT NULL,
                    left_normalized REAL NOT NULL CHECK(left_normalized >= 0 AND left_normalized < right_normalized AND right_normalized <= 1),
                    bottom_normalized REAL NOT NULL CHECK(bottom_normalized >= 0 AND bottom_normalized < top_normalized AND top_normalized <= 1),
                    right_normalized REAL NOT NULL,
                    top_normalized REAL NOT NULL,
                    extracted_text TEXT NOT NULL CHECK(length(extracted_text) > 0),
                    template_revision TEXT,
                    extraction_method TEXT NOT NULL,
                    captured_utc TEXT NOT NULL
                );
                CREATE INDEX ix_pdf_source_evidence_digest_page
                    ON pdf_source_evidence(source_sha256, page_number, captured_utc);
                PRAGMA user_version=4;
                """;
            migration4.ExecuteNonQuery();
        }
        if (version < 5)
        {
            using var migration5 = connection.CreateCommand();
            migration5.Transaction = transaction;
            migration5.CommandText = """
                CREATE TABLE pdf_review_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL UNIQUE,
                    source_sha256 TEXT NOT NULL,
                    template_revision TEXT NOT NULL,
                    row_occurrence INTEGER NOT NULL CHECK(row_occurrence > 0),
                    reporting_year INTEGER NOT NULL CHECK(reporting_year BETWEEN 1900 AND 2200),
                    action TEXT NOT NULL CHECK(action IN ('approve', 'correct', 'reject', 'undo')),
                    original_raw_text TEXT NOT NULL,
                    parsed_value TEXT NOT NULL,
                    previous_reviewed_value TEXT NOT NULL,
                    previous_correction_reason TEXT,
                    previous_is_approved INTEGER NOT NULL CHECK(previous_is_approved IN (0, 1)),
                    reviewed_value TEXT NOT NULL,
                    correction_reason TEXT,
                    is_approved INTEGER NOT NULL CHECK(is_approved IN (0, 1)),
                    reviewer TEXT NOT NULL,
                    occurred_utc TEXT NOT NULL,
                    undoes_event_id TEXT REFERENCES pdf_review_audit(event_id) ON DELETE RESTRICT
                );
                CREATE INDEX ix_pdf_review_audit_source_template_row
                    ON pdf_review_audit(source_sha256, template_revision, row_occurrence, reporting_year, sequence);
                CREATE TRIGGER pdf_review_audit_no_update
                    BEFORE UPDATE ON pdf_review_audit
                    BEGIN SELECT RAISE(ABORT, 'PDF review audit entries are immutable'); END;
                CREATE TRIGGER pdf_review_audit_no_delete
                    BEFORE DELETE ON pdf_review_audit
                    BEGIN SELECT RAISE(ABORT, 'PDF review audit entries are immutable'); END;
                PRAGMA user_version=5;
                """;
            migration5.ExecuteNonQuery();
        }
        if (version < 6)
        {
            using var migration6 = connection.CreateCommand();
            migration6.Transaction = transaction;
            migration6.CommandText = """
                CREATE TABLE taxonomy_profile_index (
                    profile_id TEXT NOT NULL,
                    profile_digest TEXT NOT NULL,
                    cache_schema INTEGER NOT NULL,
                    taxonomy_version TEXT NOT NULL,
                    concept_count INTEGER NOT NULL CHECK(concept_count >= 0),
                    snapshot_json TEXT NOT NULL,
                    snapshot_sha256 TEXT NOT NULL CHECK(length(snapshot_sha256) = 64),
                    indexed_utc TEXT NOT NULL,
                    PRIMARY KEY(profile_id, profile_digest)
                );
                PRAGMA user_version=6;
                """;
            migration6.ExecuteNonQuery();
        }
        if (version < 7)
        {
            using var migration7 = connection.CreateCommand();
            migration7.Transaction = transaction;
            migration7.CommandText = """
                CREATE TABLE mixed_publication_reconciliation (
                    operation_key TEXT PRIMARY KEY NOT NULL,
                    intent_fingerprint TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    file_digest TEXT NOT NULL,
                    file_state TEXT NOT NULL CHECK(file_state IN ('Pending','InProgress','Confirmed','Rejected','Unknown')),
                    file_failure_code TEXT,
                    sqlite_path TEXT NOT NULL,
                    sqlite_table TEXT NOT NULL,
                    sqlite_operation_key TEXT,
                    sqlite_intent_fingerprint TEXT,
                    sqlite_state TEXT NOT NULL CHECK(sqlite_state IN ('Pending','InProgress','Confirmed','Rejected','Unknown')),
                    sqlite_rows_written INTEGER,
                    sqlite_failure_code TEXT,
                    updated_utc TEXT NOT NULL
                );
                PRAGMA user_version=7;
                """;
            migration7.ExecuteNonQuery();
        }
        if (version < 8)
        {
            using var migration8 = connection.CreateCommand();
            migration8.Transaction = transaction;
            migration8.CommandText = """
                ALTER TABLE mixed_publication_reconciliation
                    ADD COLUMN file_kind TEXT NOT NULL DEFAULT 'xbrl-json'
                    CHECK(file_kind IN ('xbrl-json','xbrl-xml'));
                PRAGMA user_version=8;
                """;
            migration8.ExecuteNonQuery();
        }
        if (version < 9)
        {
            using var migration9 = connection.CreateCommand();
            migration9.Transaction = transaction;
            migration9.CommandText = """
                CREATE TABLE mixed_publication_reconciliation_v9 (
                    operation_key TEXT PRIMARY KEY NOT NULL,
                    intent_fingerprint TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    file_digest TEXT NOT NULL,
                    file_state TEXT NOT NULL CHECK(file_state IN ('Pending','InProgress','Confirmed','Rejected','Unknown')),
                    file_failure_code TEXT,
                    sqlite_path TEXT NOT NULL,
                    sqlite_table TEXT NOT NULL,
                    sqlite_operation_key TEXT,
                    sqlite_intent_fingerprint TEXT,
                    sqlite_state TEXT NOT NULL CHECK(sqlite_state IN ('Pending','InProgress','Confirmed','Rejected','Unknown')),
                    sqlite_rows_written INTEGER,
                    sqlite_failure_code TEXT,
                    updated_utc TEXT NOT NULL,
                    file_kind TEXT NOT NULL DEFAULT 'xbrl-json'
                        CHECK(file_kind IN ('xbrl-json','xbrl-xml','csv-records-v1','csv-records-v2',
                            'xlsx-records-v1','xlsx-records-v2','json-records-v1','xml-records-v1'))
                );
                INSERT INTO mixed_publication_reconciliation_v9(
                    operation_key,intent_fingerprint,file_path,file_digest,file_state,file_failure_code,
                    sqlite_path,sqlite_table,sqlite_operation_key,sqlite_intent_fingerprint,
                    sqlite_state,sqlite_rows_written,sqlite_failure_code,updated_utc,file_kind)
                SELECT operation_key,intent_fingerprint,file_path,file_digest,file_state,file_failure_code,
                    sqlite_path,sqlite_table,sqlite_operation_key,sqlite_intent_fingerprint,
                    sqlite_state,sqlite_rows_written,sqlite_failure_code,updated_utc,file_kind
                FROM mixed_publication_reconciliation;
                DROP TABLE mixed_publication_reconciliation;
                ALTER TABLE mixed_publication_reconciliation_v9 RENAME TO mixed_publication_reconciliation;
                PRAGMA user_version=9;
                """;
            migration9.ExecuteNonQuery();
        }
        if (version < 10)
        {
            using var migration10 = connection.CreateCommand();
            migration10.Transaction = transaction;
            migration10.CommandText = """
                CREATE TABLE retention_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    approval_id TEXT NOT NULL UNIQUE,
                    policy_id TEXT NOT NULL,
                    approved_by_id TEXT NOT NULL,
                    plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256) = 64),
                    expired_before_utc TEXT NOT NULL,
                    deleted_cache_entries INTEGER NOT NULL CHECK(deleted_cache_entries > 0),
                    deleted_cache_bytes INTEGER NOT NULL CHECK(deleted_cache_bytes >= 0),
                    occurred_utc TEXT NOT NULL
                );
                CREATE TRIGGER retention_audit_no_update
                    BEFORE UPDATE ON retention_audit
                    BEGIN SELECT RAISE(ABORT, 'retention audit entries are immutable'); END;
                CREATE TRIGGER retention_audit_no_delete
                    BEFORE DELETE ON retention_audit
                    BEGIN SELECT RAISE(ABORT, 'retention audit entries are immutable'); END;
                PRAGMA user_version=10;
                """;
            migration10.ExecuteNonQuery();
        }
        if (version < 11)
        {
            using var migration11 = connection.CreateCommand();
            migration11.Transaction = transaction;
            migration11.CommandText = """
                CREATE TABLE temporary_artifact_retention_audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    approval_id TEXT NOT NULL UNIQUE,
                    policy_id TEXT NOT NULL,
                    approved_by_id TEXT NOT NULL,
                    plan_sha256 TEXT NOT NULL CHECK(length(plan_sha256) = 64),
                    expired_before_utc TEXT NOT NULL,
                    planned_directories INTEGER NOT NULL CHECK(planned_directories > 0),
                    planned_bytes INTEGER NOT NULL CHECK(planned_bytes >= 0),
                    plan_items_json TEXT NOT NULL CHECK(length(plan_items_json) <= 262144),
                    removed_directories INTEGER NOT NULL DEFAULT 0 CHECK(removed_directories >= 0),
                    removed_bytes INTEGER NOT NULL DEFAULT 0 CHECK(removed_bytes >= 0),
                    state TEXT NOT NULL CHECK(state IN ('pending','complete','partial','failed')),
                    occurred_utc TEXT NOT NULL,
                    completed_utc TEXT,
                    CHECK(removed_directories <= planned_directories),
                    CHECK(removed_bytes <= planned_bytes),
                    CHECK((state='pending' AND removed_directories=0 AND removed_bytes=0 AND completed_utc IS NULL)
                       OR (state<>'pending' AND completed_utc IS NOT NULL))
                );
                CREATE TRIGGER temporary_artifact_retention_audit_update
                    BEFORE UPDATE ON temporary_artifact_retention_audit
                    WHEN OLD.state <> 'pending'
                      OR NEW.state = 'pending'
                      OR NEW.sequence IS NOT OLD.sequence
                      OR NEW.approval_id IS NOT OLD.approval_id
                      OR NEW.policy_id IS NOT OLD.policy_id
                      OR NEW.approved_by_id IS NOT OLD.approved_by_id
                      OR NEW.plan_sha256 IS NOT OLD.plan_sha256
                      OR NEW.expired_before_utc IS NOT OLD.expired_before_utc
                      OR NEW.planned_directories IS NOT OLD.planned_directories
                      OR NEW.planned_bytes IS NOT OLD.planned_bytes
                      OR NEW.plan_items_json IS NOT OLD.plan_items_json
                      OR NEW.occurred_utc IS NOT OLD.occurred_utc
                      OR (NEW.state='complete' AND (NEW.removed_directories<>NEW.planned_directories OR NEW.removed_bytes<>NEW.planned_bytes))
                      OR (NEW.state='failed' AND (NEW.removed_directories<>0 OR NEW.removed_bytes<>0))
                      OR (NEW.state='partial' AND (NEW.removed_directories=0 OR NEW.removed_directories=NEW.planned_directories))
                    BEGIN SELECT RAISE(ABORT, 'temporary retention audit permits one terminal outcome only'); END;
                CREATE TRIGGER temporary_artifact_retention_audit_no_delete
                    BEFORE DELETE ON temporary_artifact_retention_audit
                    BEGIN SELECT RAISE(ABORT, 'temporary retention audit entries are immutable'); END;
                PRAGMA user_version=11;
                """;
            migration11.ExecuteNonQuery();
        }
        transaction.Commit();
        using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        _ = wal.ExecuteScalar();
    }

    private static void PublishBackupWithoutOverwrite(string temporaryPath, string destinationPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        // POSIX rename may replace a destination created after the earlier check.
        // A same-filesystem hard link publishes atomically and fails with EEXIST
        // when another backup wins the race.
        if (Link(temporaryPath, destinationPath) == 0) return;

        var error = Marshal.GetLastPInvokeError();
        if (error == 17) // EEXIST
            throw new IOException("Backup destination already exists; refusing to overwrite it.", new Win32Exception(error));
        throw new IOException("Could not atomically publish the backup without overwriting its destination.",
            new Win32Exception(error));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    private static string DefaultPath()
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XBRLMappingStudio", "state.db");

    private static void RestrictDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (PlatformNotSupportedException) { }
    }
}
