using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using SQLitePCL;
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

    public static Task<string> CaptureAsync(SourceConnection source, string bundleDirectory,
        CancellationToken cancellationToken, IProgress<long>? copiedRows = null)
        => CaptureAsync(source, bundleDirectory, cancellationToken, copiedRows, captureBoundaryForTest: null);

    // Synchronous, per-capture observation keeps process-loss qualification on the
    // production capture path without exposing test controls through the public API.
    internal static async Task<string> CaptureAsync(SourceConnection source, string bundleDirectory,
        CancellationToken cancellationToken, IProgress<long>? copiedRows,
        Action<string, string>? captureBoundaryForTest)
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
        using var stagingLease = SnapshotStagingLease.Create(staging);
        LocalApplicationDatabase.CreatePrivateDirectory(staging);
        try
        {
            captureBoundaryForTest?.Invoke("staging-created", staging);
            var databasePath = Path.Combine(staging, DatabaseFile);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(databasePath, options)) { }

            var adapter = new SqliteSourceAdapter();
            var sourceSchema = await CopySelectedTableAsync(source, databasePath, cancellationToken, copiedRows);
            captureBoundaryForTest?.Invoke("after-copy", staging);

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
                captureBoundaryForTest?.Invoke("metadata-created", staging);
                await stream.WriteAsync(payload, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            captureBoundaryForTest?.Invoke("metadata-flushed", staging);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(databasePath, UnixFileMode.UserRead);
                File.SetUnixFileMode(metadataPath, UnixFileMode.UserRead);
            }
            cancellationToken.ThrowIfCancellationRequested();
            captureBoundaryForTest?.Invoke("before-publish", staging);
            Directory.Move(staging, destination);
            stagingLease.DeleteLeaseFile();
            captureBoundaryForTest?.Invoke("after-publish", destination);
            return Path.Combine(destination, MetadataFile);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_TOOBIG)
        {
            throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                "SQLite snapshot schema, row, or index expression exceeded its byte budget; no bundle was published.",
                isInputBudgetExceeded: true);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// Owns the cross-process lease that makes an unpublished snapshot staging
    /// directory eligible for retention only after capture has stopped.
    /// </summary>
    internal sealed class SnapshotStagingLease : IDisposable
    {
        internal const string Suffix = ".lease";
        private const int MarkerSchema = 1;
        private static readonly ConcurrentDictionary<string, byte> Active = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly string _leasePath;
        private readonly FileStream _stream;
        private bool _disposed;

        private SnapshotStagingLease(string leasePath, FileStream stream)
            => (_leasePath, _stream) = (leasePath, stream);

        public static SnapshotStagingLease Create(string stagingPath)
        {
            var fullStagingPath = Path.GetFullPath(stagingPath);
            var leasePath = fullStagingPath + Suffix;
            var key = Path.GetFullPath(leasePath);
            if (!Active.TryAdd(key, 0)) throw new IOException("A snapshot staging lease already exists.");
            FileStream? stream = null;
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                stream = new FileStream(leasePath, options);
                if (!TryLock(stream)) throw new IOException("Could not acquire the snapshot staging lease.");
                JsonSerializer.Serialize(stream, new LeaseMarker(MarkerSchema, "sqlite-snapshot-staging", DateTimeOffset.UtcNow));
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                return new SnapshotStagingLease(leasePath, stream);
            }
            catch
            {
                stream?.Dispose();
                Active.TryRemove(key, out _);
                try { File.Delete(leasePath); } catch { }
                throw;
            }
        }

        internal static FileStream? TryAcquire(string stagingPath)
        {
            var fullStagingPath = Path.GetFullPath(stagingPath);
            var leasePath = fullStagingPath + Suffix;
            if (!File.Exists(leasePath) || (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
                return null;
            var key = Path.GetFullPath(leasePath);
            if (!Active.TryAdd(key, 0)) return null;
            FileStream? stream = null;
            try
            {
                stream = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
                if (!TryLock(stream))
                {
                    stream.Dispose();
                    Active.TryRemove(key, out _);
                    return null;
                }
                return stream;
            }
            catch (IOException)
            {
                stream?.Dispose();
                Active.TryRemove(key, out _);
                return null;
            }
            catch
            {
                stream?.Dispose();
                Active.TryRemove(key, out _);
                throw;
            }
        }

        internal static DateTimeOffset ReadCreatedUtc(FileStream stream)
        {
            if (stream.Length is <= 0 or > 4096)
                throw new InvalidDataException("Snapshot staging lease marker is outside its supported size.");
            stream.Position = 0;
            var marker = JsonSerializer.Deserialize<LeaseMarker>(stream)
                ?? throw new InvalidDataException("Snapshot staging lease marker is malformed.");
            stream.Position = 0;
            if (marker.SchemaVersion != MarkerSchema
                || !StringComparer.Ordinal.Equals(marker.Purpose, "sqlite-snapshot-staging")
                || marker.CreatedUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Snapshot staging lease marker has an unsupported identity.");
            return marker.CreatedUtc;
        }

        public void DeleteLeaseFile()
        {
            try { File.Delete(_leasePath); }
            catch (FileNotFoundException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var key = Path.GetFullPath(_leasePath);
            try { Unlock(_stream); } catch (IOException) { }
            finally
            {
                _stream.Dispose();
                Active.TryRemove(key, out _);
                try { File.Delete(_leasePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        internal static void ReleaseAcquired(string stagingPath, FileStream stream)
        {
            var key = Path.GetFullPath(Path.GetFullPath(stagingPath) + Suffix);
            try { Unlock(stream); } catch (IOException) { }
            finally
            {
                stream.Dispose();
                Active.TryRemove(key, out _);
            }
        }

        internal static void DeleteLeaseFileUnderLock(string stagingPath)
        {
            try { File.Delete(Path.GetFullPath(stagingPath) + Suffix); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool TryLock(FileStream stream)
        {
            if (OperatingSystem.IsMacOS())
            {
                if (MacFileLock.Flock(stream.SafeFileHandle, MacFileLock.LockExclusive | MacFileLock.LockNonBlocking) == 0)
                    return true;
                var error = Marshal.GetLastPInvokeError();
                if (error is 11 or 35) return false;
                throw new IOException("Could not acquire the macOS snapshot staging lease.", new Win32Exception(error));
            }
            stream.Lock(0, 1);
            return true;
        }

        private static void Unlock(FileStream stream)
        {
            if (OperatingSystem.IsMacOS())
            {
                if (MacFileLock.Flock(stream.SafeFileHandle, MacFileLock.LockUnlock) != 0)
                    throw new IOException("Could not release the macOS snapshot staging lease.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                return;
            }
            stream.Unlock(0, 1);
        }

        private sealed record LeaseMarker(int SchemaVersion, string Purpose, DateTimeOffset CreatedUtc);

        private static class MacFileLock
        {
            internal const int LockExclusive = 2;
            internal const int LockNonBlocking = 4;
            internal const int LockUnlock = 8;

            [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
            internal static extern int Flock(SafeFileHandle file, int operation);
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
        if (StringComparer.Ordinal.Equals(snapshot.SnapshotStrategy, Strategy))
            await VerifySelectedTableOnlyAsync(connection.Path, SqliteSourceAdapter.GetConfiguredTable(connection), cancellationToken);
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

    private static async Task<ISourceSchema> CopySelectedTableAsync(SourceConnection source,
        string databasePath, CancellationToken cancellationToken, IProgress<long>? copiedRows)
    {
        var table = SqliteSourceAdapter.GetConfiguredTable(source);
        await using var original = Open(source.Path!, SqliteOpenMode.ReadOnly);
        await using var target = Open(databasePath, SqliteOpenMode.ReadWrite);
        await original.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        await ConfigureCaptureConnectionAsync(original, cancellationToken);
        await ConfigureCaptureConnectionAsync(target, cancellationToken);
        // Schema admission, copying, and both verification scans all observe this
        // same source snapshot, including committed WAL pages visible at its start.
        await using var sourceTransaction = original.BeginTransaction(deferred: true);
        var createTableSql = await ReadSelectedTableSqlAsync(original, sourceTransaction, table, cancellationToken);
        AdmitSchemaStatement(createTableSql, table, null, cancellationToken);
        var indexSql = await ReadSelectedIndexSqlAsync(original, sourceTransaction, table, cancellationToken);
        string encodingName;
        await using (var encoding = original.CreateCommand())
        {
            encoding.Transaction = sourceTransaction;
            encoding.CommandText = "PRAGMA encoding";
            encodingName = Convert.ToString(await encoding.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        var textEncoding = encodingName switch
        {
            "UTF-8" => new UTF8Encoding(false, true),
            "UTF-16le" => (Encoding)new UnicodeEncoding(false, false, true),
            "UTF-16be" => new UnicodeEncoding(true, false, true),
            _ => throw Invalid("The SQLite source uses an unsupported text encoding.")
        };
        await using (var pragma = target.CreateCommand())
        {
            // Encoding affects BINARY text-key ordering, not just text decoding.
            // These values are chosen above, never interpolated from arbitrary SQL.
            pragma.CommandText = $"PRAGMA encoding='{encodingName}'; PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON";
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
        await using (var create = target.CreateCommand())
        {
            create.CommandText = createTableSql;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        // Inspect only the admitted, size-bounded schema in our private staging
        // image. Inspecting the live source before admission would materialize
        // arbitrary schema names before their schema-byte budget was checked.
        var schema = await new SqliteSourceAdapter().InspectSchemaAsync(source with { Path = databasePath }, cancellationToken);
        var columns = schema.Columns.OrderBy(column => column.Ordinal).ToArray();
        if (columns.Length == 0) throw Invalid("The configured SQLite source table has no readable columns.");
        // DDL needs a separate schema budget; value/expression work is restricted
        // to the much smaller row budget, including during index construction.
        raw.sqlite3_limit(original.Handle, raw.SQLITE_LIMIT_LENGTH, SqliteSourceAdapter.DefaultMaximumRowBytes);
        raw.sqlite3_limit(target.Handle, raw.SQLITE_LIMIT_LENGTH, SqliteSourceAdapter.DefaultMaximumRowBytes);
        await using var targetTransaction = target.BeginTransaction();
        var quotedTable = Quote(table);
        var cellBytes = CombineSqlExpressions(columns.Select(column =>
            $"COALESCE(length(CAST({Quote(column.Name)} AS BLOB)), 0)"), "+");
        var containsBlob = CombineSqlExpressions(columns.Select(column =>
            $"typeof({Quote(column.Name)}) = 'blob'"), "OR");
        var budgetSql = $"CASE WHEN {containsBlob} THEN -1 ELSE {cellBytes} END";
        // CASE maps only TEXT to raw database-encoding bytes. The final budget
        // sentinel rejects source BLOBs before any cells are materialized, so a
        // byte[] payload unambiguously represents original TEXT storage.
        var rawColumns = string.Join(", ", columns.Select(column =>
            $"CASE WHEN typeof({Quote(column.Name)})='text' THEN CAST({Quote(column.Name)} AS BLOB) ELSE {Quote(column.Name)} END AS {Quote(column.Name)}"));
        var rawSelectSql = $"SELECT {rawColumns}, {budgetSql} FROM {quotedTable}";
        var adapterSelectSql = $"SELECT *, {budgetSql} FROM {quotedTable}";
        var rowBudgetAdapter = new SqliteSourceAdapter();
        void CheckRowBudget(SqliteDataReader row, long rowNumber)
        {
            var totalCellBytes = row.GetInt64(columns.Length);
            if (totalCellBytes < 0)
                throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                    $"SQLite table '{table}' contains a BLOB value; BLOB source values are unsupported.");
            var rowId = columns.Any(column => column.PrimaryKeyOrdinal > 0)
                ? "pk:sha256:" + new string('0', 64)
                : $"pos:{rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (rowBudgetAdapter.EstimateRowBytesUpperBound(
                    columns, source.SourceId, table, rowId, totalCellBytes) > SqliteSourceAdapter.DefaultMaximumRowBytes)
                throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                    $"A SQLite source row exceeded the {SqliteSourceAdapter.DefaultMaximumRowBytes} byte row budget before its cells were materialized.",
                    isInputBudgetExceeded: true);
        }
        void CheckColumns(SqliteDataReader row)
        {
            if (row.FieldCount != columns.Length + 1
                || Enumerable.Range(0, columns.Length).Any(index => !StringComparer.Ordinal.Equals(row.GetName(index), columns[index].Name)))
                throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                    $"SQLite table '{table}' has columns that the bounded source adapter cannot replay.");
        }
        // SELECT * additionally rejects generated/hidden column layouts that the
        // ordinary source adapter cannot replay, without silently dropping them.
        await using (var check = original.CreateCommand())
        {
            check.Transaction = sourceTransaction;
            check.CommandText = adapterSelectSql;
            await using var shape = await check.ExecuteReaderAsync(cancellationToken);
            CheckColumns(shape);
        }
        await using (var select = original.CreateCommand())
        {
            select.Transaction = sourceTransaction;
            select.CommandText = rawSelectSql;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            CheckColumns(reader);
            var insertColumns = string.Join(", ", columns.Select(column => Quote(column.Name)));
            var parameters = string.Join(", ", Enumerable.Range(0, columns.Length).Select(index => $"$p{index}"));
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
                CheckRowBudget(reader, rowNumber);
                for (var index = 0; index < columns.Length; index++)
                {
                    var value = reader.GetValue(index);
                    if (value is byte[] bytes)
                    {
                        try { value = textEncoding.GetString(bytes); }
                        catch (DecoderFallbackException)
                        {
                            throw Invalid("SQLite source TEXT contains invalid encoding bytes; no snapshot was published.");
                        }
                    }
                    if (value is double or float)
                        throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                            "SQLite binary floating-point data cannot be retained for exact numeric ingestion.");
                    insert.Parameters[index].Value = value;
                }
                await insert.ExecuteNonQueryAsync(cancellationToken);
                rowNumber++;
                copiedRows?.Report(rowNumber);
                if ((rowNumber & 127) == 0)
                    await EnsurePageBudgetAsync(target, targetTransaction, cancellationToken);
            }
        }
        // Triggers and views are deliberately not copied. Indexes preserve supported
        // scan layouts, but are admitted as single statements before any execution.
        foreach (var sql in indexSql)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var index = target.CreateCommand();
            index.Transaction = targetTransaction;
            index.CommandText = sql;
            await index.ExecuteNonQueryAsync(cancellationToken);
        }
        await EnsurePageBudgetAsync(target, targetTransaction, cancellationToken);
        // Never canonicalize with ORDER BY rowid: that would hide a changed adapter
        // scan order. Check raw cells and also the exact ordinary-adapter projection,
        // whose query plan can differ when the source has planner statistics.
        await VerifyRowsAsync(rawSelectSql);
        await VerifyRowsAsync(adapterSelectSql);
        cancellationToken.ThrowIfCancellationRequested();
        await targetTransaction.CommitAsync(cancellationToken);
        await using var journalMode = target.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=DELETE";
        if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToString(
                await journalMode.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture), "delete"))
            throw Invalid("Retained source could not be made independent of journal sidecars.");
        return schema;

        async Task VerifyRowsAsync(string sql)
        {
            await using var sourceRead = original.CreateCommand();
            sourceRead.Transaction = sourceTransaction;
            sourceRead.CommandText = sql;
            await using var targetRead = target.CreateCommand();
            targetRead.Transaction = targetTransaction;
            targetRead.CommandText = sql;
            await using var sourceRows = await sourceRead.ExecuteReaderAsync(cancellationToken);
            await using var targetRows = await targetRead.ExecuteReaderAsync(cancellationToken);
            CheckColumns(sourceRows);
            CheckColumns(targetRows);
            long rowNumber = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceHasRow = await sourceRows.ReadAsync(cancellationToken);
                var targetHasRow = await targetRows.ReadAsync(cancellationToken);
                if (sourceHasRow != targetHasRow)
                    throw Invalid("Retained SQLite row count differs from the captured source transaction.");
                if (!sourceHasRow) return;
                CheckRowBudget(sourceRows, rowNumber);
                CheckRowBudget(targetRows, rowNumber);
                for (var index = 0; index < columns.Length; index++)
                {
                    var left = sourceRows.GetValue(index);
                    var right = targetRows.GetValue(index);
                    var equal = left.GetType() == right.GetType() && (left is byte[] leftBytes
                        ? leftBytes.AsSpan().SequenceEqual((byte[])right)
                        : left.Equals(right));
                    if (!equal)
                        throw Invalid("Retained SQLite storage types, cell bytes, or scan order differ from the captured source transaction.");
                }
                rowNumber++;
            }
        }
    }

    private static async Task ConfigureCaptureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_LENGTH, MaximumTableSchemaSqlBytes);
        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_SQL_LENGTH, MaximumTableSchemaSqlBytes);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size=-2048; PRAGMA temp_store=FILE; PRAGMA mmap_size=0; PRAGMA trusted_schema=OFF";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReadSelectedTableSqlAsync(SqliteConnection source,
        SqliteTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT length(CAST(sql AS BLOB)), sql FROM sqlite_master WHERE type='table' AND name=$name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, $"SQLite table '{table}' no longer exists.");
        if (reader.GetInt64(0) is <= 0 or > MaximumTableSchemaSqlBytes)
            throw Invalid("Selected SQLite table definition exceeds the snapshot schema budget.");
        var sql = reader.GetString(1);
        if (string.IsNullOrWhiteSpace(sql) || await reader.ReadAsync(cancellationToken))
            throw Invalid("Selected SQLite table definition is missing or ambiguous.");
        return sql;
    }

    private static async Task<IReadOnlyList<string>> ReadSelectedIndexSqlAsync(SqliteConnection source,
        SqliteTransaction transaction, string table, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = source.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT length(CAST(sql AS BLOB)), length(CAST(name AS BLOB)), name, sql FROM sqlite_master "
            + "WHERE type='index' AND tbl_name=$name COLLATE NOCASE AND sql IS NOT NULL ORDER BY name";
        command.Parameters.AddWithValue("$name", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var schemaBytes = 0L;
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= MaximumIndexCount)
                throw Invalid("Selected SQLite table exceeds the retained index-count limit.");
            var sqlBytes = reader.GetInt64(0);
            var nameBytes = reader.GetInt64(1);
            if (sqlBytes <= 0 || nameBytes <= 0 || nameBytes > MaximumTableSchemaSqlBytes
                || sqlBytes > MaximumTableSchemaSqlBytes - schemaBytes)
                throw Invalid("Selected SQLite index definitions exceed the snapshot schema budget.");
            schemaBytes += sqlBytes;
            var name = reader.GetString(2);
            var sql = reader.GetString(3);
            AdmitSchemaStatement(sql, table, name, cancellationToken);
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
        // Scope validation must not consult or create journals for retained images.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(databasePath)).AbsoluteUri + "?immutable=1&mode=ro",
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ConfigureCaptureConnectionAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE COALESCE("
            + "(type='table' AND name=$name COLLATE NOCASE AND tbl_name=$name COLLATE NOCASE) "
            + "OR (type='index' AND tbl_name=$name COLLATE NOCASE) "
            + "OR (type='table' AND name='sqlite_sequence' AND tbl_name='sqlite_sequence'), 0)=0";
        command.Parameters.AddWithValue("$name", expectedTable);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw Invalid("Retained table-only snapshot contains unexpected schema objects.");
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name COLLATE NOCASE";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw Invalid("Retained table-only snapshot is missing its selected table.");
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='sqlite_sequence'";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            command.CommandText = "SELECT count(*) FROM sqlite_sequence WHERE name IS NULL OR name<>$name COLLATE NOCASE";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw Invalid("Retained table-only snapshot contains an unrelated SQLite sequence.");
        }
    }

    private static void AdmitSchemaStatement(string sql, string table, string? indexName,
        CancellationToken cancellationToken)
    {
        // sqlite_master.sql is untrusted, including on otherwise readable databases.
        // Microsoft.Data.Sqlite executes every statement in CommandText, so admit
        // exactly one CREATE of the catalog object before ever sending it there.
        var position = 0;
        void SkipTrivia()
        {
            while (position < sql.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sql[position] is ' ' or '\t' or '\r' or '\n' or '\f' or '\v') { position++; continue; }
                if (position + 1 < sql.Length && sql[position] == '-' && sql[position + 1] == '-')
                {
                    position += 2;
                    while (position < sql.Length && sql[position] != '\n') position++;
                    continue;
                }
                if (position + 1 < sql.Length && sql[position] == '/' && sql[position + 1] == '*')
                {
                    var end = sql.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0) throw Invalid("SQLite schema has an unterminated comment.");
                    position = end + 2;
                    continue;
                }
                break;
            }
        }
        (string Text, bool Quoted) Token()
        {
            SkipTrivia();
            if (position >= sql.Length) throw Invalid("SQLite schema definition is incomplete.");
            var first = sql[position++];
            if (first is '\'' or '"' or '`' or '[')
            {
                var close = first == '[' ? ']' : first;
                var value = new StringBuilder();
                while (position < sql.Length)
                {
                    var current = sql[position++];
                    if (current == close)
                    {
                        if (close != ']' && position < sql.Length && sql[position] == close)
                        { value.Append(close); position++; continue; }
                        return (value.ToString(), true);
                    }
                    value.Append(current);
                }
                throw Invalid("SQLite schema has an unterminated quoted token.");
            }
            var start = position - 1;
            if (char.IsLetterOrDigit(first) || first is '_' or '$' || first >= 128)
                while (position < sql.Length && (char.IsLetterOrDigit(sql[position]) || sql[position] is '_' or '$' || sql[position] >= 128)) position++;
            return (sql[start..position], false);
        }
        void Keyword(string expected)
        {
            var token = Token();
            if (token.Quoted || !StringComparer.OrdinalIgnoreCase.Equals(token.Text, expected))
                throw Invalid("SQLite schema contains an unsupported CREATE definition.");
        }
        void Name(string expected)
        {
            var name = Token().Text;
            SkipTrivia();
            if (position < sql.Length && sql[position] == '.')
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(name, "main"))
                    throw Invalid("SQLite snapshot schema cannot target another database.");
                position++;
                name = Token().Text;
            }
            if (!StringComparer.OrdinalIgnoreCase.Equals(name, expected))
                throw Invalid("SQLite schema definition does not target its selected catalog object.");
        }
        if (sql.IndexOf('\0') >= 0) throw Invalid("SQLite schema contains a NUL byte.");
        Keyword("CREATE");
        if (indexName is null) Keyword("TABLE");
        else
        {
            var saved = position;
            var token = Token();
            if (!token.Quoted && StringComparer.OrdinalIgnoreCase.Equals(token.Text, "UNIQUE")) Keyword("INDEX");
            else { position = saved; Keyword("INDEX"); }
        }
        var optional = position;
        var next = Token();
        if (!next.Quoted && StringComparer.OrdinalIgnoreCase.Equals(next.Text, "IF"))
        { Keyword("NOT"); Keyword("EXISTS"); }
        else position = optional;
        Name(indexName ?? table);
        if (indexName is not null) { Keyword("ON"); Name(table); }
        SkipTrivia();
        if (position >= sql.Length || sql[position] != '(')
            throw Invalid("SQLite table snapshots require a column or index definition, not CREATE AS SELECT.");
        while (position < sql.Length)
        {
            if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            SkipTrivia();
            if (position >= sql.Length) break;
            var current = sql[position];
            if (current == ';')
            {
                position++;
                SkipTrivia();
                if (position != sql.Length) throw Invalid("SQLite schema contains more than one SQL statement.");
                break;
            }
            if (current is '\'' or '"' or '`' or '[') { Token(); continue; }
            position++;
        }
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
