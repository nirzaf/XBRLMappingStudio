using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Explicit, local, bounded SQLite backup capture. A bundle contains the entire database,
/// including other tables; it must be retained as confidential source evidence. No live
/// database is modified, and no existing snapshot bundle is overwritten.
/// </summary>
public static class SqliteSourceSnapshotStore
{
    public const string Strategy = "sqlite-retained-backup-v1";
    public const long MaximumDatabaseBytes = 1024L * 1024 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const string DatabaseFile = "source.sqlite";
    private const string MetadataFile = "snapshot.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record SnapshotMetadata(int Version, string SourceId, string OriginalSourcePath,
        string ConfigurationDigest, string SchemaFingerprint, string DatabaseDigest,
        long DatabaseBytes, DateTimeOffset CreatedUtc);

    public static async Task<string> CaptureAsync(SourceConnection source, string bundleDirectory,
        CancellationToken cancellationToken, IProgress<long>? copiedPages = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind != "sqlite" || source.RetainedSnapshot is not null || string.IsNullOrWhiteSpace(source.Path))
            throw Invalid("Capture requires a live SQLite source binding.");
        var destination = Path.GetFullPath(bundleDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Snapshot destination already exists; refusing to overwrite it.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Snapshot parent directory must already exist.");
        var staging = Path.Combine(parent, ".xms-snapshot-" + Guid.NewGuid().ToString("N"));
        LocalApplicationDatabase.CreatePrivateDirectory(staging);
        try
        {
            var databasePath = Path.Combine(staging, DatabaseFile);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(databasePath, options)) { }
            await using (var original = Open(source.Path, SqliteOpenMode.ReadOnly))
            await using (var target = Open(databasePath, SqliteOpenMode.ReadWrite))
            {
                await original.OpenAsync(cancellationToken);
                await target.OpenAsync(cancellationToken);
                // Establish and retain a read transaction before checking the page budget.
                // Every backup step observes that same snapshot, including committed WAL pages.
                await using var transaction = original.BeginTransaction(deferred: true);
                long Scalar(string sql)
                {
                    using var command = original.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                }
                var pageSize = Scalar("PRAGMA page_size");
                var pageCount = Scalar("PRAGMA page_count");
                if (pageSize <= 0 || pageCount <= 0 || pageCount > MaximumDatabaseBytes / pageSize)
                    throw Invalid($"SQLite snapshot exceeds the {MaximumDatabaseBytes} byte retained-database limit.");
                using (var backup = raw.sqlite3_backup_init(target.Handle, "main", original.Handle, "main"))
                {
                    if (backup.IsInvalid) throw Invalid("SQLite could not start a consistent source backup.");
                    var busy = Stopwatch.StartNew();
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var status = raw.sqlite3_backup_step(backup, 128);
                        copiedPages?.Report(raw.sqlite3_backup_pagecount(backup) - raw.sqlite3_backup_remaining(backup));
                        if (status == raw.SQLITE_DONE) break;
                        if (status == raw.SQLITE_OK) { busy.Restart(); continue; }
                        if ((status == raw.SQLITE_BUSY || status == raw.SQLITE_LOCKED) && busy.Elapsed < TimeSpan.FromSeconds(5))
                        {
                            await Task.Delay(25, cancellationToken);
                            continue;
                        }
                        throw Invalid($"SQLite source backup failed with status {status}.");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                using var checkpoint = target.CreateCommand();
                checkpoint.CommandText = "PRAGMA journal_mode=DELETE";
                if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToString(checkpoint.ExecuteScalar()), "delete"))
                    throw Invalid("Retained source could not be made independent of journal sidecars.");
            }
            var captured = source with { Path = databasePath };
            var adapter = new SqliteSourceAdapter();
            var schema = await adapter.InspectSchemaAsync(captured, cancellationToken);
            // Qualify the selected table with the ordinary exact-numeric and bounded-row
            // contract before exposing the bundle as replayable source evidence.
            await foreach (var _ in adapter.ReadFullAsync(captured, schema, cancellationToken)) { }
            RejectSidecars(databasePath);
            using (var durable = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                durable.Flush(flushToDisk: true);
            var digest = await DigestAsync(databasePath, cancellationToken);
            var metadata = new SnapshotMetadata(1, source.SourceId, Path.GetFullPath(source.Path),
                ConfigurationDigest(source), schema.Fingerprint, digest,
                new FileInfo(databasePath).Length, DateTimeOffset.UtcNow);
            var payload = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
            if (payload.Length > MaximumMetadataBytes) throw Invalid("Snapshot metadata exceeds its size limit.");
            var metadataPath = Path.Combine(staging, MetadataFile);
            using (var stream = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(payload, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(databasePath, UnixFileMode.UserRead);
                File.SetUnixFileMode(metadataPath, UnixFileMode.UserRead);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return Path.Combine(destination, MetadataFile);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static async Task<SourceConnection> OpenAsync(string metadataPath, SourceConnection expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expected.Kind != "sqlite") throw Invalid("Only SQLite bindings can use a retained SQLite snapshot.");
        var fullMetadataPath = Path.GetFullPath(metadataPath);
        var info = new FileInfo(fullMetadataPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumMetadataBytes)
            throw Invalid("Snapshot metadata is missing or exceeds its size limit.");
        SnapshotMetadata? metadata;
        try
        {
            await using var stream = new FileStream(fullMetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[checked((int)info.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.Position != stream.Length) throw Invalid("Snapshot metadata changed while being read.");
            metadata = JsonSerializer.Deserialize<SnapshotMetadata>(bytes, JsonOptions);
        }
        catch (JsonException ex) { throw Invalid("Snapshot metadata is invalid: " + ex.Message); }
        if (metadata is null || metadata.Version != 1 || metadata.SourceId != expected.SourceId
            || metadata.ConfigurationDigest != ConfigurationDigest(expected)
            || string.IsNullOrWhiteSpace(metadata.OriginalSourcePath)
            || string.IsNullOrWhiteSpace(metadata.SchemaFingerprint)
            || metadata.DatabaseBytes is <= 0 or > MaximumDatabaseBytes)
            throw Invalid("Snapshot version, binding identity, configuration or size does not match this source.");
        var databasePath = Path.Combine(Path.GetDirectoryName(fullMetadataPath)!, DatabaseFile);
        var connection = expected with
        {
            Path = databasePath,
            RetainedSnapshot = new(metadata.DatabaseDigest, metadata.OriginalSourcePath, metadata.ConfigurationDigest, fullMetadataPath)
        };
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length != metadata.DatabaseBytes)
            throw Invalid("Retained source database length differs from its capture metadata.");
        await VerifyConnectionAsync(connection, cancellationToken);
        var schema = await new SqliteSourceAdapter().InspectSchemaAsync(connection, cancellationToken);
        if (schema.Fingerprint != metadata.SchemaFingerprint)
            throw Invalid("Retained source schema differs from its capture metadata.");
        return connection;
    }

    public static async Task VerifyConnectionAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Kind != "sqlite" || connection.RetainedSnapshot is not { } snapshot
            || string.IsNullOrWhiteSpace(connection.Path)
            || snapshot.ConfigurationDigest != ConfigurationDigest(connection))
            throw Invalid("The retained source connection does not match its captured configuration.");
        if (!await MatchesAsync(connection.Path, snapshot.DatabaseDigest, cancellationToken))
            throw Invalid("Retained source bytes are missing, changed, journal-dependent or unverifiable.");
    }

    internal static async Task<bool> MatchesAsync(string? path, string? digest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || digest is null || digest.Length != 71
            || !digest.StartsWith("sha256:", StringComparison.Ordinal)) return false;
        try
        {
            RejectSidecars(path);
            var actual = await DigestAsync(path, cancellationToken);
            RejectSidecars(path);
            return StringComparer.Ordinal.Equals(actual, digest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AdapterException
            or ArgumentException or NotSupportedException) { return false; }
    }

    private static async Task<string> DigestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length is <= 0 or > MaximumDatabaseBytes) throw Invalid("Retained database is outside the size budget.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long readBytes = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            readBytes += count;
            if (readBytes > MaximumDatabaseBytes) throw Invalid("Retained database exceeded its size budget.");
            hash.AppendData(buffer, 0, count);
        }
        if (readBytes != length || stream.Length != length) throw Invalid("Retained database changed while hashing.");
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void RejectSidecars(string path)
    {
        // SQLite resolves file links before finding its journals. Checking only the
        // lexical alias would miss a target WAL while hashing unchanged main bytes.
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw Invalid("Retained database file aliases are unsupported; use the actual captured file.");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix)) throw Invalid("Retained database must not have SQLite journal sidecars.");
    }

    private static string ConfigurationDigest(SourceConnection source)
    {
        var text = new StringBuilder();
        foreach (var pair in (source.Configuration ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(pair.Key.Length).Append(':').Append(pair.Key);
            text.Append(pair.Value.Length).Append(':').Append(pair.Value);
        }
        return RunManifest.ComputeSha256(text.ToString());
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(path), Mode = mode, Pooling = false, DefaultTimeout = 5
    }.ToString());

    private static AdapterException Invalid(string message) => new(DiagnosticCodes.IoInputIdentityChanged, message);
}
