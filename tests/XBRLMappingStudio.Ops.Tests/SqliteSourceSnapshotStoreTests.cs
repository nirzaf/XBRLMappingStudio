using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Application;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class SqliteSourceSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xms-snapshot-test-" + Guid.NewGuid().ToString("N"));
    public SqliteSourceSnapshotStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public async Task CaptureIncludesWalCommitAndKeepsOneSnapshotWhileWriterCommits()
    {
        var source = await Source(5000);
        using var writer = new SqliteConnection($"Data Source={source.Path};Pooling=False");
        writer.Open();
        Execute(writer, "PRAGMA journal_mode=WAL; UPDATE financials SET amount='1234.50' WHERE id=1;");
        var wrote = false;
        var progress = new InlineProgress<long>(_ =>
        {
            if (wrote) return;
            Execute(writer, "UPDATE financials SET amount='9999' WHERE id=1; INSERT INTO financials VALUES (99999,'7777','late');");
            wrote = true;
        });
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default, progress);
        Assert.True(wrote);
        var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
        var rows = await Rows(replay);
        Assert.Equal(5000, rows.Count);
        Assert.Equal(1234.50m, rows.Single(row => Equals(row.Fields["id"].Raw, 1L)).Fields["amount"].AsDecimal());
        Assert.Equal(source.Path, replay.RetainedSnapshot!.OriginalSourcePath);
        Assert.StartsWith("sha256:", replay.RetainedSnapshot.DatabaseDigest);
        Assert.Equal(SqliteSourceSnapshotStore.Strategy, replay.RetainedSnapshot.SnapshotStrategy);
        Assert.False(File.Exists(replay.Path + "-wal"));
        Assert.False(File.Exists(replay.Path + "-shm"));
        using var retained = new SqliteConnection($"Data Source={replay.Path};Mode=ReadOnly;Pooling=False");
        retained.Open();
        using var query = retained.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='other_table'";
        Assert.Equal(0L, Convert.ToInt64(query.ExecuteScalar()));
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND substr(name,1,7) <> 'sqlite_'";
        Assert.Equal(1L, Convert.ToInt64(query.ExecuteScalar()));
        query.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND substr(name,1,7) <> 'sqlite_'";
        Assert.Equal("financials", query.ExecuteScalar());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(replay.Path!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(metadata)!));
        }
    }

    [Fact]
    public async Task OpensLegacyVersionOneFullDatabaseSnapshotWithoutChangingItsMeaning()
    {
        var source = await Source(3);
        var schema = await new SqliteSourceAdapter().InspectSchemaAsync(source, default);
        var bundle = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(bundle);
        var databasePath = Path.Combine(bundle, "source.sqlite");
        File.Copy(source.Path!, databasePath);
        var configText = new StringBuilder();
        foreach (var pair in source.Configuration!.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            configText.Append(pair.Key.Length).Append(':').Append(pair.Key);
            configText.Append(pair.Value.Length).Append(':').Append(pair.Value);
        }
        var databaseBytes = await File.ReadAllBytesAsync(databasePath);
        var metadata = new JsonObject
        {
            ["version"] = 1,
            ["sourceId"] = source.SourceId,
            ["originalSourcePath"] = Path.GetFullPath(source.Path!),
            ["configurationDigest"] = RunManifest.ComputeSha256(configText.ToString()),
            ["schemaFingerprint"] = schema.Fingerprint,
            ["databaseDigest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(databaseBytes)).ToLowerInvariant(),
            ["databaseBytes"] = databaseBytes.LongLength,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O")
        };
        var metadataPath = Path.Combine(bundle, "snapshot.json");
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());

        var replay = await SqliteSourceSnapshotStore.OpenAsync(metadataPath, source, default);

        Assert.Equal(SqliteSourceSnapshotStore.LegacyStrategy, replay.RetainedSnapshot!.SnapshotStrategy);
        using var retained = new SqliteConnection($"Data Source={replay.Path};Mode=ReadOnly;Pooling=False");
        retained.Open();
        using var query = retained.CreateCommand();
        query.CommandText = "SELECT secret FROM other_table";
        Assert.Equal("whole-database-backup", query.ExecuteScalar());
    }

    [Fact]
    public async Task CaptureResolvesConfiguredTableNameUsingSqliteIdentifierCaseRules()
    {
        var source = await Source(3);
        source = source with
        {
            Configuration = new Dictionary<string, string>(source.Configuration!, StringComparer.Ordinal)
            {
                ["table"] = "Financials"
            }
        };

        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "case-insensitive"), default);
        var replay = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
        var rows = await Rows(replay);

        Assert.Equal(3, rows.Count);
        Assert.Equal(12.50m, rows.Single(row => Equals(row.Fields["id"].Raw, 1L)).Fields["amount"].AsDecimal());
    }

    [Fact]
    public async Task FreshOpenReplaysAfterLiveDatabaseIsDeletedAndBundleIsMoved()
    {
        var source = await Source(3);
        var expected = await Rows(source);
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default);
        File.Delete(source.Path!);
        var moved = Path.Combine(_root, "moved % # space");
        Directory.Move(Path.GetDirectoryName(metadata)!, moved);
        var replay = await SqliteSourceSnapshotStore.OpenAsync(Path.Combine(moved, "snapshot.json"), source, default);
        var actual = await Rows(replay);
        Assert.Equal(expected.Select(row => row.RowId), actual.Select(row => row.RowId));
        Assert.Equal(expected.Select(row => row.Fields["amount"]), actual.Select(row => row.Fields["amount"]));
        Assert.Equal(expected[0].CellSources, actual[0].CellSources);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("wal")]
    [InlineData("journal")]
    [InlineData("configuration")]
    [InlineData("binding")]
    [InlineData("version")]
    public async Task OpenFailsClosedForChangedOrUnverifiableCapture(string change)
    {
        var source = await Source(3);
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default);
        var database = Path.Combine(Path.GetDirectoryName(metadata)!, "source.sqlite");
        switch (change)
        {
            case "bytes":
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(database, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var bytes = await File.ReadAllBytesAsync(database);
                bytes[^1] ^= 1;
                await File.WriteAllBytesAsync(database, bytes);
                break;
            case "missing": File.Delete(database); break;
            case "wal": await File.WriteAllTextAsync(database + "-wal", "unexpected"); break;
            case "journal": await File.WriteAllTextAsync(database + "-journal", "unexpected"); break;
            case "configuration": source = source with { Configuration = new Dictionary<string, string> { ["table"] = "other_table" } }; break;
            case "binding": source = source with { SourceId = "different" }; break;
            case "version":
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(metadata, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var node = JsonNode.Parse(await File.ReadAllTextAsync(metadata))!;
                node["version"] = 3;
                await File.WriteAllTextAsync(metadata, node.ToJsonString());
                break;
        }
        var exception = await Record.ExceptionAsync(() => SqliteSourceSnapshotStore.OpenAsync(metadata, source, default));
        Assert.NotNull(exception);
        Assert.True(exception is AdapterException or IOException, exception.ToString());
    }

    [Fact]
    public async Task SymbolicLinkToDatabaseWithHiddenWalIsNotAcceptedAsRetainedBytes()
    {
        var source = await Source(3);
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, Path.Combine(_root, "retained"), default);
        var trusted = await SqliteSourceSnapshotStore.OpenAsync(metadata, source, default);
        var snapshot = Path.Combine(Path.GetDirectoryName(metadata)!, "source.sqlite");
        var bytes = await File.ReadAllBytesAsync(snapshot);
        var target = Path.Combine(_root, "alias-target.sqlite");
        await File.WriteAllBytesAsync(target, bytes);
        File.Delete(snapshot);
        try { File.CreateSymbolicLink(snapshot, target); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is (UnauthorizedAccessException or IOException))
        {
            return; // Symlink privilege is not available to every Windows CI account.
        }
        using var writer = new SqliteConnection($"Data Source={target};Pooling=False");
        writer.Open();
        Execute(writer, "PRAGMA journal_mode=WAL; UPDATE financials SET amount='9999' WHERE id=1;");
        // Preserve the captured main-file bytes while leaving a changed WAL at the
        // resolved target. A lexical sidecar-only check misses this dependency.
        if (!OperatingSystem.IsWindows())
        {
            await File.WriteAllBytesAsync(target, bytes);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot));
        }
        Assert.False(File.Exists(snapshot + "-wal"));
        Assert.True(File.Exists(target + "-wal"));
        var immutableRows = await Rows(trusted);
        Assert.Equal(12.50m, immutableRows.Single(row => Equals(row.Fields["id"].Raw, 1L)).Fields["amount"].AsDecimal());
        var error = await Assert.ThrowsAsync<AdapterException>(() =>
            SqliteSourceSnapshotStore.OpenAsync(metadata, source, default));
        Assert.Equal(DiagnosticCodes.IoInputIdentityChanged, error.Code);
    }

    [Fact]
    public async Task CancellationDuringCopyLeavesNoPublishedBundleOrPrivateTemporaryArtifacts()
    {
        var source = await Source(5000);
        using var cancelled = new CancellationTokenSource();
        var destination = Path.Combine(_root, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteSourceSnapshotStore.CaptureAsync(
            source, destination, cancelled.Token, new InlineProgress<long>(_ => cancelled.Cancel())));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(_root, ".xms-snapshot-*"));
        Assert.Equal(5000, (await Rows(source)).Count);
    }

    [Fact]
    public async Task CaptureNeverOverwritesExistingBundle()
    {
        var source = await Source(3);
        var destination = Path.Combine(_root, "retained");
        var metadata = await SqliteSourceSnapshotStore.CaptureAsync(source, destination, default);
        var before = await File.ReadAllBytesAsync(metadata);
        await Assert.ThrowsAsync<IOException>(() => SqliteSourceSnapshotStore.CaptureAsync(source, destination, default));
        Assert.Equal(before, await File.ReadAllBytesAsync(metadata));
        Assert.Empty(Directory.GetDirectories(_root, ".xms-snapshot-*"));
    }

    [Fact]
    public async Task UnsupportedBinaryNumericSourceDoesNotPublishBundle()
    {
        var source = await Source(1);
        using var db = new SqliteConnection($"Data Source={source.Path};Pooling=False");
        db.Open();
        Execute(db, "ALTER TABLE financials ADD COLUMN unsupported REAL; UPDATE financials SET unsupported=1.1;");
        await Assert.ThrowsAsync<AdapterException>(() => SqliteSourceSnapshotStore.CaptureAsync(
            source, Path.Combine(_root, "invalid"), default));
        Assert.False(Directory.Exists(Path.Combine(_root, "invalid")));
        Assert.Empty(Directory.GetDirectories(_root, ".xms-snapshot-*"));
    }

    private async Task<SourceConnection> Source(int count)
    {
        var path = Path.Combine(_root, "source.sqlite");
        await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        await db.OpenAsync();
        Execute(db, "CREATE TABLE financials(id INTEGER PRIMARY KEY, amount TEXT, note TEXT); CREATE TABLE other_table(secret TEXT); INSERT INTO other_table VALUES ('whole-database-backup');");
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO financials VALUES ($id,'0012.50',$note)";
        command.Parameters.AddWithValue("$id", 0);
        command.Parameters.AddWithValue("$note", new string('x', 300));
        for (int i = 1; i <= count; i++) { command.Parameters[0].Value = i; command.ExecuteNonQuery(); }
        transaction.Commit();
        return new SourceConnection("financial-source", "sqlite", path,
            new Dictionary<string, string> { ["table"] = "financials", ["decimalColumns"] = "amount" });
    }
    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static async Task<List<XBRLMappingStudio.Engine.DataRecord>> Rows(SourceConnection source)
    {
        var adapter = new SqliteSourceAdapter();
        var schema = await adapter.InspectSchemaAsync(source, default);
        var result = new List<XBRLMappingStudio.Engine.DataRecord>();
        await foreach (var row in adapter.ReadFullAsync(source, schema, default)) result.Add(row);
        return result;
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
