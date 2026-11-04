using Microsoft.Data.Sqlite;

namespace XBRLMappingStudio.Application;

/// <summary>Versioned local application state. Mapping graphs remain in portable .xmap files.</summary>
public sealed class LocalApplicationDatabase
{
    public const int CurrentSchemaVersion = 1;
    public string Path { get; }

    public LocalApplicationDatabase(string? path = null)
    {
        Path = System.IO.Path.GetFullPath(path ?? DefaultPath());
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        RestrictDirectoryPermissions(System.IO.Path.GetDirectoryName(Path)!);
        Migrate();
        if (!OperatingSystem.IsWindows())
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

    public void BackupTo(string backupPath)
    {
        var fullBackupPath = System.IO.Path.GetFullPath(backupPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(Path, fullBackupPath))
            throw new ArgumentException("Backup path must differ from the application database.", nameof(backupPath));
        if (File.Exists(fullBackupPath)) throw new IOException("Backup destination already exists; refusing to overwrite it.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullBackupPath)!);
        try
        {
            using var source = OpenConnection();
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fullBackupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 5,
                ForeignKeys = true
            }.ToString());
            target.Open();
            source.BackupDatabase(target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fullBackupPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            foreach (var file in new[] { fullBackupPath, fullBackupPath + "-wal", fullBackupPath + "-shm" })
                try { File.Delete(file); } catch (IOException) { }
            throw;
        }
    }

    private void Migrate()
    {
        using var connection = OpenConnection();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL";
            _ = wal.ExecuteScalar();
        }
        using var transaction = connection.BeginTransaction();
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
        transaction.Commit();
    }

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
