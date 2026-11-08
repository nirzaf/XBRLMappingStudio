using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Explicit, local, bounded SQLite source capture. New bundles contain only the configured
/// source table and its indexes. Legacy full-database bundles remain readable. No live
/// database is modified, and no existing snapshot bundle is overwritten.
/// </summary>
public static class SqliteSourceSnapshotStore
{
    public const string Strategy = "sqlite-retained-table-v2";
    public const string LegacyStrategy = "sqlite-retained-backup-v1";
    public const string SelectedTableScope = "selected-table";
    public const long MaximumDatabaseBytes = 1024L * 1024 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumIndexCount = 16_384;
    private const int MaximumTableSchemaSqlBytes = 16 * 1024 * 1024;
    private const string DatabaseFile = "source.sqlite";
    private const string MetadataFile = "snapshot.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record SnapshotMetadata(int Version, string SourceId, string OriginalSourcePath,
        string ConfigurationDigest, string SchemaFingerprint, string DatabaseDigest,
        long DatabaseBytes, DateTimeOffset CreatedUtc, string? Strategy = null, string? Scope = null);

    public static bool IsRetainedStrategy(string? strategy)
        => StringComparer.Ordinal.Equals(strategy, Strategy)
            || StringComparer.Ordinal.Equals(strategy, LegacyStrategy);

    public static async Task<string> CaptureAsync(SourceConnection source, string bundleDirectory,
        CancellationToken cancellationToken, IProgress<long>? copiedRows = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind != "sqlite" || source.RetainedSnapshot is not null || string.IsNullOrWhiteSpace(source.Path))
            throw Invalid("Capture requires a live SQLite source binding.");
        var table = SqliteSourceAdapter.GetConfiguredTable(source);
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

            var adapter = new SqliteSourceAdapter();
            var sourceSchema = await adapter.InspectSchemaAsync(source, cancellationToken);
            await CopySelectedTableAsync(source, sourceSchema, databasePath, cancellationToken, copiedRows);

            var captured = source with { Path = databasePath };
            var schema = await adapter.InspectSchemaAsync(captured, cancellationToken);
            if (!StringComparer.Ordinal.Equals(schema.Fingerprint, sourceSchema.Fingerprint))
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    "The selected SQLite table schema changed during snapshot capture; no bundle was published.");
            // Reuse the ordinary adapter's exact-numeric and bounded-row contract before
            // exposing the new image as replayable source evidence.
            await foreach (var _ in adapter.ReadFullAsync(captured, schema, cancellationToken)) { }
            await VerifySelectedTableOnlyAsync(databasePath, table, cancellationToken);
            RejectSidecars(databasePath);
            using (var durable = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                durable.Flush(flushToDisk: true);
            var digest = await DigestAsync(databasePath, cancellationToken);
            var metadata = new SnapshotMetadata(2, source.SourceId, Path.GetFullPath(source.Path),
                ConfigurationDigest(source), schema.Fingerprint, digest,
                new FileInfo(databasePath).Length, DateTimeOffset.UtcNow, Strategy, SelectedTableScope);
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
        var isLegacyFullDatabase = metadata is { Version: 1, Strategy: null, Scope: null };
        var isSelectedTable = metadata is { Version: 2, Strategy: Strategy, Scope: SelectedTableScope };
        if (metadata is null || (!isLegacyFullDatabase && !isSelectedTable) || metadata.SourceId != expected.SourceId
            || metadata.ConfigurationDigest != ConfigurationDigest(expected)
            || string.IsNullOrWhiteSpace(metadata.OriginalSourcePath)
            || string.IsNullOrWhiteSpace(metadata.SchemaFingerprint)
            || metadata.DatabaseBytes is <= 0 or > MaximumDatabaseBytes)
            throw Invalid("Snapshot version, binding identity, configuration or size does not match this source.");
        var databasePath = Path.Combine(Path.GetDirectoryName(fullMetadataPath)!, DatabaseFile);
        var connection = expected with
        {
            Path = databasePath,
            RetainedSnapshot = new(metadata.DatabaseDigest, metadata.OriginalSourcePath,
                metadata.ConfigurationDigest, fullMetadataPath,
                isLegacyFullDatabase ? LegacyStrategy : Strategy)
        };
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length != metadata.DatabaseBytes)
            throw Invalid("Retained source database length differs from its capture metadata.");
        await VerifyConnectionAsync(connection, cancellationToken);
        if (isSelectedTable)
            await VerifySelectedTableOnlyAsync(databasePath, SqliteSourceAdapter.GetConfiguredTable(expected), cancellationToken);
        var schema = await new SqliteSourceAdapter().InspectSchemaAsync(connection, cancellationToken);
        if (schema.Fingerprint != metadata.SchemaFingerprint)
            throw Invalid("Retained source schema differs from its capture metadata.");
        return connection;
    }

    public static async Task VerifyConnectionAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        if (connection.Kind != "sqlite" || connection.RetainedSnapshot is not { } snapshot
            || !IsRetainedStrategy(snapshot.SnapshotStrategy)
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

    private static async Task CopySelectedTableAsync(SourceConnection source, ISourceSchema schema,
        string databasePath, CancellationToken cancellationToken, IProgress<long>? copiedRows)
    {
        var table = SqliteSourceAdapter.GetConfiguredTable(source);
        var columns = schema.Columns.OrderBy(column => column.Ordinal).ToArray();
        if (columns.Length == 0) throw Invalid("The configured SQLite source table has no readable columns.");

        await using var original = Open(source.Path!, SqliteOpenMode.ReadOnly);
        await using var target = Open(databasePath, SqliteOpenMode.ReadWrite);
        await original.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        // The first schema read and every copied row use this same SQLite read
        // transaction, including committed WAL pages visible at its start.
        await using var sourceTransaction = original.BeginTransaction(deferred: true);
        var createTableSql = await ReadSelectedTableSqlAsync(original, sourceTransaction, table, cancellationToken);
        if (!createTableSql.TrimStart().StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
            || createTableSql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Virtual or unsupported SQLite table definitions cannot be retained as a table-only snapshot.");
        var indexSql = await ReadSelectedIndexSqlAsync(original, sourceTransaction, table, cancellationToken);

        await using (var pragma = target.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }
        long targetPageSize;
        await using (var pageSize = target.CreateCommand())
        {
            pageSize.CommandText = "PRAGMA page_size";
            targetPageSize = Convert.ToInt64(await pageSize.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        if (targetPageSize <= 0) throw Invalid("SQLite snapshot page size could not be read.");
        var maximumPages = MaximumDatabaseBytes / targetPageSize;
        await using (var maxPages = target.CreateCommand())
        {
            maxPages.CommandText = $"PRAGMA max_page_count={maximumPages.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var effective = Convert.ToInt64(await maxPages.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (effective <= 0 || effective > maximumPages)
                throw Invalid("SQLite could not enforce the retained snapshot page limit.");
        }
        await using var targetTransaction = target.BeginTransaction();
        await using (var create = target.CreateCommand())
        {
            create.Transaction = targetTransaction;
            create.CommandText = createTableSql;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var quotedTable = Quote(table);
        var cellBytes = CombineSqlExpressions(columns.Select(column =>
            $"COALESCE(length(CAST({Quote(column.Name)} AS BLOB)), 0)"), "+");
        var containsBlob = CombineSqlExpressions(columns.Select(column =>
            $"typeof({Quote(column.Name)}) = 'blob'"), "OR");
        await using var select = original.CreateCommand();
        select.Transaction = sourceTransaction;
        select.CommandText = $"SELECT *, CASE WHEN {containsBlob} THEN -1 ELSE {cellBytes} END FROM {quotedTable}";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (reader.FieldCount != columns.Length + 1
            || Enumerable.Range(0, columns.Length).Any(index => !StringComparer.Ordinal.Equals(reader.GetName(index), columns[index].Name)))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite table '{table}' has columns that the bounded source adapter cannot replay.");

        var insertColumns = string.Join(", ", columns.Select(column => Quote(column.Name)));
        var parameters = string.Join(", ", Enumerable.Range(0, columns.Length).Select(index => $"$p{index}"));
        var rowBudgetAdapter = new SqliteSourceAdapter();
        await using var insert = target.CreateCommand();
        insert.Transaction = targetTransaction;
        insert.CommandText = $"INSERT INTO {quotedTable} ({insertColumns}) VALUES ({parameters})";
        for (var index = 0; index < columns.Length; index++)
            insert.Parameters.Add(new SqliteParameter($"$p{index}", DBNull.Value));
        insert.Prepare();

        long rowNumber = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var totalCellBytes = reader.GetInt64(columns.Length);
            if (totalCellBytes < 0)
                throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                    $"SQLite table '{table}' contains a BLOB value; BLOB source values are unsupported.");
            var hasPrimaryKey = columns.Any(column => column.PrimaryKeyOrdinal > 0);
            var rowId = hasPrimaryKey
                ? "pk:sha256:" + new string('0', 64)
                : $"pos:{rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (rowBudgetAdapter.EstimateRowBytesUpperBound(
                    columns, source.SourceId, table, rowId, totalCellBytes) > SqliteSourceAdapter.DefaultMaximumRowBytes)
                throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                    $"A SQLite source row exceeded the {SqliteSourceAdapter.DefaultMaximumRowBytes} byte row budget before its cells were materialized.",
                    isInputBudgetExceeded: true);

            for (var index = 0; index < columns.Length; index++)
                insert.Parameters[index].Value = reader.GetValue(index);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            rowNumber++;
            copiedRows?.Report(rowNumber);
            if ((rowNumber & 127) == 0)
                await EnsurePageBudgetAsync(target, targetTransaction, cancellationToken);
        }
        await reader.DisposeAsync();
        await EnsurePageBudgetAsync(target, targetTransaction, cancellationToken);

        // Triggers and views can contain executable or unrelated database logic and
        // are not required by a read-only table binding. Preserve only its indexes.
        foreach (var sql in indexSql)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var index = target.CreateCommand();
            index.Transaction = targetTransaction;
            index.CommandText = sql;
            await index.ExecuteNonQueryAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await targetTransaction.CommitAsync(cancellationToken);
        await using var journalMode = target.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=DELETE";
        if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToString(
                await journalMode.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture), "delete"))
            throw Invalid("Retained source could not be made independent of journal sidecars.");
    }

    private static async Task<string> ReadSelectedTableSqlAsync(SqliteConnection source,
        SqliteTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", table);
        var sql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(sql))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, $"SQLite table '{table}' no longer exists.");
        if (Encoding.UTF8.GetByteCount(sql) > MaximumTableSchemaSqlBytes)
            throw Invalid("Selected SQLite table definition exceeds the snapshot schema budget.");
        return sql;
    }

    private static async Task<IReadOnlyList<string>> ReadSelectedIndexSqlAsync(SqliteConnection source,
        SqliteTransaction transaction, string table, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=$name COLLATE NOCASE AND sql IS NOT NULL ORDER BY name";
        command.Parameters.AddWithValue("$name", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var schemaBytes = 0L;
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= MaximumIndexCount)
                throw Invalid("Selected SQLite table exceeds the retained index-count limit.");
            var sql = reader.GetString(0);
            schemaBytes = checked(schemaBytes + Encoding.UTF8.GetByteCount(sql));
            if (schemaBytes > MaximumTableSchemaSqlBytes)
                throw Invalid("Selected SQLite index definitions exceed the snapshot schema budget.");
            result.Add(sql);
        }
        return result;
    }

    private static async Task EnsurePageBudgetAsync(SqliteConnection target, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        async Task<long> ReadPragmaAsync(string pragma)
        {
            await using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = pragma;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var pageSize = await ReadPragmaAsync("PRAGMA page_size");
        var pageCount = await ReadPragmaAsync("PRAGMA page_count");
        if (pageSize <= 0 || pageCount <= 0 || pageCount > MaximumDatabaseBytes / pageSize)
            throw Invalid($"Selected SQLite table snapshot exceeds the {MaximumDatabaseBytes} byte retained-database limit.");
    }

    private static async Task VerifySelectedTableOnlyAsync(string databasePath, string expectedTable,
        CancellationToken cancellationToken)
    {
        await using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name FROM sqlite_master "
            + "WHERE type IN ('table', 'view', 'trigger') AND substr(name,1,7) <> 'sqlite_' ORDER BY type, name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !StringComparer.Ordinal.Equals(reader.GetString(0), "table")
            || !StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(1), expectedTable)
            || await reader.ReadAsync(cancellationToken))
            throw Invalid("Retained table-only snapshot contains unexpected tables, views, or triggers.");
    }

    private static string CombineSqlExpressions(IEnumerable<string> expressions, string operation)
    {
        var level = expressions.ToList();
        while (level.Count > 1)
        {
            var next = new List<string>((level.Count + 1) / 2);
            for (var index = 0; index < level.Count; index += 2)
                next.Add(index + 1 < level.Count
                    ? $"({level[index]} {operation} {level[index + 1]})"
                    : level[index]);
            level = next;
        }
        return level[0];
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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
