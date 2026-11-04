using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class LocalApplicationDatabaseTests
{
    [Fact]
    public void InitializesVersionedSchemaAndPersistsAtomicPublicationReceipt()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "state.db");
        var database = new LocalApplicationDatabase(path);
        Assert.Equal("ok", database.IntegrityCheck());

        var journal = new RunJournal(databasePath: path);
        var staged = new JournalEntry("run-1", "/workspace/report.json", JournalState.Staged,
            "sha256:abc", null, "lock", DateTimeOffset.UtcNow);
        journal.Save(staged);
        var finalized = staged with { State = JournalState.Finalized, FinalDigest = staged.StagedDigest };
        journal.Save(finalized);

        Assert.Equal(finalized, journal.Load("run-1"));
        Assert.Single(journal.AllEntries());
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT final_digest FROM publication_receipts WHERE run_id=$id";
        command.Parameters.AddWithValue("$id", "run-1");
        Assert.Equal("sha256:abc", command.ExecuteScalar());
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
            command.CommandText = "CREATE TABLE preserve_me(value TEXT); PRAGMA user_version=99;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => { _ = new LocalApplicationDatabase(path); });
        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='preserve_me'";
        Assert.Equal(1L, check.ExecuteScalar());
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
        using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly"))
        {
            backup.Open();
            using var command = backup.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key='theme'";
            Assert.Equal("light", command.ExecuteScalar());
        }
        Assert.Throws<IOException>(() => database.BackupTo(backupPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xms-state-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
