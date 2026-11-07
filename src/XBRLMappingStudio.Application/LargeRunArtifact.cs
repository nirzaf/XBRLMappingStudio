using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.XBRL;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Limits used by mapping execution. The probe limit includes all bindings, not a
/// per-binding multiplier; the SQLite cache and batch size bound managed working
/// memory after large mode is selected. Collection, group-count, estimated
/// group-state-byte, and join limits also reach the engine's set-phase work.
/// </summary>
public sealed record ExecutionBudget(
    int InMemorySourceRecordLimit = 50_000,
    int RowBatchSize = 512,
    int SqliteCacheKiB = 4_096,
    int MaxNamespaces = 4_096,
    int MaxRowBytes = 4 * 1024 * 1024,
    int MaxFactBytes = 1 * 1024 * 1024,
    long MaxProbeBytes = 64L * 1024 * 1024,
    long MaxBatchBytes = 64L * 1024 * 1024,
    long MaxInMemorySourceBytes = 8L * 1024 * 1024,
    long MaxExpandedInMemorySourceBytes = 32L * 1024 * 1024,
    long MaxSpoolBytes = 2L * 1024 * 1024 * 1024,
    long MaxReportBytes = 2L * 1024 * 1024 * 1024,
    int MaxCollectionItems = 100_000,
    int MaxGroupCount = 100_000,
    int MaxJoinExpansionRows = 200_000,
    long MaxGroupWorkingBytes = 64L * 1024 * 1024,
    int MaxSignificantDigits = 29)
{
    public static ExecutionBudget Default { get; } = new();

    public void Validate()
    {
        if (InMemorySourceRecordLimit < 1) throw new ArgumentOutOfRangeException(nameof(InMemorySourceRecordLimit));
        if (RowBatchSize < 1) throw new ArgumentOutOfRangeException(nameof(RowBatchSize));
        if (SqliteCacheKiB < 256) throw new ArgumentOutOfRangeException(nameof(SqliteCacheKiB));
        if (MaxNamespaces < 16) throw new ArgumentOutOfRangeException(nameof(MaxNamespaces));
        if (MaxRowBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxRowBytes));
        if (MaxFactBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxFactBytes));
        if (MaxProbeBytes < MaxRowBytes) throw new ArgumentOutOfRangeException(nameof(MaxProbeBytes));
        if (MaxBatchBytes < MaxRowBytes) throw new ArgumentOutOfRangeException(nameof(MaxBatchBytes));
        if (MaxInMemorySourceBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxInMemorySourceBytes));
        if (MaxExpandedInMemorySourceBytes < MaxInMemorySourceBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxExpandedInMemorySourceBytes));
        if (MaxSpoolBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxSpoolBytes));
        if (MaxReportBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxReportBytes));
        if (MaxCollectionItems < 1) throw new ArgumentOutOfRangeException(nameof(MaxCollectionItems));
        if (MaxGroupCount < 1) throw new ArgumentOutOfRangeException(nameof(MaxGroupCount));
        if (MaxJoinExpansionRows < 1) throw new ArgumentOutOfRangeException(nameof(MaxJoinExpansionRows));
        if (MaxGroupWorkingBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxGroupWorkingBytes));
        if (MaxSignificantDigits is < 1 or > 29)
            throw new ArgumentOutOfRangeException(nameof(MaxSignificantDigits));
    }
}

/// <summary>
/// A private, disk-backed full-run result. It owns both the ordered fact spool and
/// exact streamed XBRL artifact. Callers must dispose it when the run result is no
/// longer needed; incomplete and cancelled runs never transfer ownership to a result.
/// </summary>
public sealed class LargeRunArtifact : IDisposable
{
    private readonly string _directory;
    private readonly LargeRunFactStore _facts;
    private bool _disposed;

    public string ReportPath { get; }
    public string OutputKind { get; }
    /// <summary>Compatibility alias for callers compiled against the original JSON-only large-run API.</summary>
    public string XbrlJsonPath => ReportPath;
    public string Sha256 { get; }
    public long Bytes { get; }
    public IReadOnlyList<XbrlFact> Facts => _facts;
    public int FactCount => _facts.Count;

    internal LargeRunArtifact(string directory, LargeRunFactStore facts, string reportPath,
        string sha256, long bytes, string outputKind = "xbrl-json")
        => (_directory, _facts, ReportPath, Sha256, Bytes, OutputKind)
            = (directory, facts, reportPath, sha256, bytes, outputKind);

    public Stream OpenReportRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new FileStream(ReportPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
    }

    /// <summary>Streams the original fact order from the private disk-backed spool.</summary>
    public IEnumerable<XbrlFact> InInputOrder(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _facts.InInputOrder(cancellationToken);
    }

    /// <summary>Streams facts with the same deterministic occurrence IDs written to xBRL-JSON.</summary>
    public IEnumerable<(XbrlFact Fact, int Occurrence)> InSemanticOrderWithOccurrences(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _facts.InSemanticOrderWithOccurrences(cancellationToken);
    }

    /// <summary>Gets the first repeated semantic key from the disk-backed fact index.</summary>
    public bool TryGetFirstDuplicate(out string semanticKey, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _facts.TryGetFirstDuplicate(out semanticKey, cancellationToken);
    }

    public void CopyReportTo(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var source = OpenReportRead();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
        }
    }

    public async Task CopyReportToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var source = new FileStream(ReportPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    internal IReadOnlyDictionary<string, XbrlFact> FindFactsByLocators(
        IEnumerable<string> locators,
        CancellationToken cancellationToken = default)
        => _facts.FindFactsByLocators(locators, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _facts.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// SQLite-backed fact spool. `cache_size` and `temp_store=FILE` keep sorting and
/// result enumeration within a fixed page-cache budget while preserving stable input
/// order among identical semantic keys.
/// </summary>
internal sealed class LargeRunFactStore : IReadOnlyList<XbrlFact>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;
    private readonly long _maxBytes;
    private SqliteTransaction? _transaction;
    private SqliteCommand? _insert;
    private long _bytes;
    private int _count;
    private long _sourceRows;
    private long _sourceBytes;
    private bool _finished;
    private bool _disposed;

    public int Count => _count;

    public XbrlFact this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT fact_json FROM facts ORDER BY seq LIMIT 1 OFFSET $offset;";
            command.Parameters.AddWithValue("$offset", index);
            var json = command.ExecuteScalar() as string
                ?? throw new InvalidDataException("The disk-backed fact spool is missing a row.");
            return JsonSerializer.Deserialize<XbrlFact>(json, JsonOptions)
                ?? throw new InvalidDataException("The disk-backed fact spool contains an invalid fact row.");
        }
    }

    public LargeRunFactStore(string databasePath, int cacheKiB, long maxBytes)
    {
        _databasePath = databasePath;
        _maxBytes = maxBytes;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        _connection.Open();
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=FILE; PRAGMA cache_size=-{cacheKiB};";
            pragma.ExecuteNonQuery();
        }
        using (var schema = _connection.CreateCommand())
        {
            schema.CommandText = "CREATE TABLE facts (seq INTEGER PRIMARY KEY AUTOINCREMENT, semantic_key TEXT NOT NULL, semantic_sort_key BLOB NOT NULL, fact_json TEXT NOT NULL); CREATE TABLE source_rows (seq INTEGER PRIMARY KEY AUTOINCREMENT, sort_key BLOB NOT NULL, canonical TEXT NOT NULL);";
            schema.ExecuteNonQuery();
        }
        BeginInsertTransaction();
    }

    public void Add(XbrlFact fact, int maxFactBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The fact spool is already finalized.");
        ArgumentNullException.ThrowIfNull(fact);
        var json = JsonSerializer.Serialize(fact, JsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        if (bytes > maxFactBytes)
            throw new LargeRunBudgetExceededException($"A serialized fact exceeded the {maxFactBytes} byte fact budget.");
        var semanticKey = fact.SemanticKey();
        if (System.Text.Encoding.UTF8.GetByteCount(semanticKey) > maxFactBytes)
            throw new LargeRunBudgetExceededException($"A fact semantic key exceeded the {maxFactBytes} byte fact budget.");
        if (bytes > _maxBytes - _bytes - _sourceBytes)
            throw new LargeRunBudgetExceededException($"The fact spool exceeded its {_maxBytes} byte budget.");

        _insert!.Parameters["$key"].Value = semanticKey;
        _insert.Parameters["$sortKey"].Value = Utf16OrdinalKey(semanticKey);
        _insert.Parameters["$json"].Value = json;
        _insert.ExecuteNonQuery();
        _count++;
        _bytes += bytes;
        if (_count % 512 == 0) CommitAndBeginInsertTransaction();
    }

    public void AddSourceDigestRecord(string canonicalRecord, int maxRowBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The fact spool is already finalized.");
        var rowBytes = System.Text.Encoding.UTF8.GetByteCount(canonicalRecord);
        if (rowBytes > maxRowBytes)
            throw new LargeRunBudgetExceededException($"A source row exceeded the {maxRowBytes} byte row budget.");
        if (rowBytes > _maxBytes - _bytes - _sourceBytes)
            throw new LargeRunBudgetExceededException($"The combined fact and source spool exceeded its {_maxBytes} byte budget.");
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = "INSERT INTO source_rows(sort_key, canonical) VALUES($sortKey, $canonical);";
        command.Parameters.AddWithValue("$sortKey", Utf16OrdinalKey(canonicalRecord));
        command.Parameters.AddWithValue("$canonical", canonicalRecord);
        command.ExecuteNonQuery();
        _sourceRows++;
        _sourceBytes += rowBytes;
        if ((_count + _sourceRows) % 512 == 0) CommitAndBeginInsertTransaction();
    }

    public string ComputeSourceDigest(string table, CancellationToken cancellationToken = default)
    {
        Finish(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPart(hash, table);
        AppendText(hash, _sourceRows.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT canonical FROM source_rows ORDER BY sort_key, seq;";
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendPart(hash, reader.GetString(0));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public IEnumerable<(XbrlFact Fact, int Occurrence)> InSemanticOrderWithOccurrences(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Finish(cancellationToken);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT fact_json, occurrence FROM (SELECT fact_json, semantic_sort_key, seq, ROW_NUMBER() OVER (PARTITION BY semantic_key ORDER BY seq) AS occurrence FROM facts) ORDER BY semantic_sort_key, seq;";
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = reader.GetString(0);
            yield return (JsonSerializer.Deserialize<XbrlFact>(json, JsonOptions)
                ?? throw new InvalidDataException("The disk-backed fact spool contains an invalid fact row."), reader.GetInt32(1));
        }
    }

    public IReadOnlyDictionary<string, XbrlFact> FindFactsByLocators(
        IEnumerable<string> locators,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(locators);
        var remaining = locators.Where(locator => !string.IsNullOrWhiteSpace(locator))
            .ToHashSet(StringComparer.Ordinal);
        var matches = new Dictionary<string, XbrlFact>(StringComparer.Ordinal);
        if (remaining.Count == 0) return matches;

        foreach (var (fact, occurrence) in InSemanticOrderWithOccurrences(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = OimSerializer.GetFactLocator(fact, occurrence);
            if (!remaining.Remove(locator)) continue;
            matches.Add(locator, fact);
            if (remaining.Count == 0) break;
        }
        return matches;
    }

    public void Finish(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_finished) return;
        cancellationToken.ThrowIfCancellationRequested();
        CommitInsertTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        using (var index = _connection.CreateCommand())
        {
            cancellationToken.ThrowIfCancellationRequested();
            index.CommandText = "CREATE INDEX facts_semantic_order ON facts(semantic_sort_key, seq);";
            index.ExecuteNonQuery();
            // Microsoft.Data.Sqlite executes this index build synchronously. Observe
            // cancellation as soon as the native SQLite call returns, then skip the
            // remaining index and let the owner discard the private spool.
            cancellationToken.ThrowIfCancellationRequested();
            index.CommandText = "CREATE INDEX source_rows_order ON source_rows(sort_key, seq);";
            index.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
        }
        _finished = true;
        EnsureDatabaseDiskBudget();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public bool TryGetFirstDuplicate(out string semanticKey, CancellationToken cancellationToken = default)
    {
        Finish(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var command = _connection.CreateCommand();
        // Match the in-memory validator's first repeated key: the duplicate group
        // whose first occurrence appears earliest in input order.
        command.CommandText = "SELECT semantic_key FROM facts GROUP BY semantic_key HAVING COUNT(*) > 1 ORDER BY MIN(seq) LIMIT 1;";
        semanticKey = command.ExecuteScalar() as string ?? "";
        cancellationToken.ThrowIfCancellationRequested();
        return semanticKey.Length > 0;
    }

    public IEnumerable<XbrlFact> InInputOrder() => ReadFacts("seq");
    public IEnumerable<XbrlFact> InSemanticOrder() => ReadFacts("semantic_sort_key, seq");

    public IEnumerable<XbrlFact> InInputOrder(CancellationToken cancellationToken)
        => ReadFacts("seq", cancellationToken);

    public IEnumerable<XbrlFact> InSemanticOrder(CancellationToken cancellationToken)
        => ReadFacts("semantic_sort_key, seq", cancellationToken);

    public IEnumerator<XbrlFact> GetEnumerator() => InInputOrder().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _insert?.Dispose();
            _insert = null;
            try { _transaction?.Rollback(); } catch (SqliteException) { }
            _transaction?.Dispose();
            _transaction = null;
        }
        finally { _connection.Dispose(); }
    }

    private IEnumerable<XbrlFact> ReadFacts(string orderBy, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Finish(cancellationToken);
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT fact_json FROM facts ORDER BY {orderBy};";
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = reader.GetString(0);
            yield return JsonSerializer.Deserialize<XbrlFact>(json, JsonOptions)
                ?? throw new InvalidDataException("The disk-backed fact spool contains an invalid fact row.");
        }
    }

    private void BeginInsertTransaction()
    {
        _transaction = _connection.BeginTransaction();
        _insert = _connection.CreateCommand();
        _insert.Transaction = _transaction;
        _insert.CommandText = "INSERT INTO facts(semantic_key, semantic_sort_key, fact_json) VALUES($key, $sortKey, $json);";
        _insert.Parameters.Add("$key", SqliteType.Text);
        _insert.Parameters.Add("$sortKey", SqliteType.Blob);
        _insert.Parameters.Add("$json", SqliteType.Text);
    }

    private void CommitAndBeginInsertTransaction()
    {
        CommitInsertTransaction();
        BeginInsertTransaction();
    }

    private void CommitInsertTransaction()
    {
        _insert?.Dispose();
        _insert = null;
        if (_transaction is null) return;
        _transaction.Commit();
        _transaction.Dispose();
        _transaction = null;
        EnsureDatabaseDiskBudget();
    }

    private void EnsureDatabaseDiskBudget()
    {
        if (File.Exists(_databasePath) && new FileInfo(_databasePath).Length > _maxBytes)
            throw new LargeRunBudgetExceededException($"The fact/source spool exceeded its {_maxBytes} byte on-disk budget.");
    }

    private static byte[] Utf16OrdinalKey(string value)
    {
        var key = new byte[checked(value.Length * 2)];
        for (var i = 0; i < value.Length; i++)
        {
            key[i * 2] = (byte)(value[i] >> 8);
            key[(i * 2) + 1] = (byte)value[i];
        }
        return key;
    }

    private static void AppendPart(IncrementalHash hash, string value)
        => AppendText(hash, value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value);

    private static void AppendText(IncrementalHash hash, string value)
        => hash.AppendData(System.Text.Encoding.UTF8.GetBytes(value));
}

internal sealed class LargeRunBudgetExceededException : IOException
{
    public LargeRunBudgetExceededException(string message) : base(message) { }
}
