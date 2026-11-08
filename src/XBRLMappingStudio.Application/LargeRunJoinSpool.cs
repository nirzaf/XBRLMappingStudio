using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Application;

internal sealed partial class LargeRunFactStore
{
    private long _joinBytes;
    private long _joinRows;
    private int _joinPartitions;
    private bool _joinsInitialized;
    public int JoinPartitionCount => _joinPartitions;

    public void AddJoinRecord(DataRecord record, bool left, PartitionedJoinPlan plan,
        ExecutionBudget budget, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The fact spool is already finalized.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!_joinsInitialized)
        {
            using var schema = _connection.CreateCommand();
            schema.Transaction = _transaction;
            schema.CommandText = """
                CREATE TABLE join_partitions (id INTEGER PRIMARY KEY, partition_key BLOB NOT NULL UNIQUE,
                    matchable INTEGER NOT NULL, left_count INTEGER NOT NULL DEFAULT 0, right_count INTEGER NOT NULL DEFAULT 0,
                    left_bytes INTEGER NOT NULL DEFAULT 0, right_bytes INTEGER NOT NULL DEFAULT 0,
                    first_left INTEGER, first_right INTEGER, phase INTEGER, first_output INTEGER);
                CREATE TABLE join_rows (seq INTEGER PRIMARY KEY, partition_id INTEGER NOT NULL, side INTEGER NOT NULL, row_json TEXT NOT NULL);
                CREATE INDEX join_rows_order ON join_rows(partition_id, side, seq);
                """;
            schema.ExecuteNonQuery();
            _joinsInitialized = true;
        }
        var retained = left ? plan.LeftFields : plan.RightFields;
        var fields = new Dictionary<string, SpilledValue>(StringComparer.Ordinal);
        var sources = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
        foreach (var (name, value) in record.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retained is not null && !retained.Contains(name)) continue;
            fields.Add(name, SpilledValue.From(value));
            var source = value.Source;
            if (source is null && record.CellSources is not null) record.CellSources.TryGetValue(name, out source);
            if (source is not null) sources.Add(name, source);
        }
        var json = JsonSerializer.Serialize(new SpilledRecord(record.RowId, fields, sources, record.HasStableRowIdentity));
        var jsonBytes = Encoding.UTF8.GetByteCount(json);
        if (jsonBytes > budget.MaxRowBytes)
            throw new LargeRunBudgetExceededException($"A joined source row exceeded the {budget.MaxRowBytes} byte row budget.");
        var inputBytes = Math.Max(jsonBytes, BufferedSourceReader.EstimateRecordBytes(record));
        var keyValue = record[plan.KeyField];
        var text = keyValue.AsString();
        var key = Utf16OrdinalKey(text);
        var matchable = keyValue.Presence == ValuePresence.Present && !string.IsNullOrWhiteSpace(text);
        long id = _joinPartitions + 1L, count = 0, bytes = 0;
        var exists = false;
        using (var lookup = _connection.CreateCommand())
        {
            lookup.Transaction = _transaction;
            lookup.CommandText = $"SELECT id, {(left ? "left_count" : "right_count")}, left_bytes+right_bytes FROM join_partitions WHERE partition_key=$key;";
            lookup.Parameters.AddWithValue("$key", key);
            using var result = lookup.ExecuteReader();
            if (result.Read())
            {
                exists = true;
                id = result.GetInt64(0);
                count = result.GetInt64(1);
                bytes = result.GetInt64(2);
            }
        }
        if (jsonBytes + (!exists ? (long)key.Length : 0) > _maxBytes - _bytes - _sourceBytes - _groupBytes - _joinBytes)
            throw new LargeRunBudgetExceededException($"The combined join, fact and source spool exceeded its {_maxBytes} byte budget.");
        if (count >= budget.MaxCollectionItems || inputBytes > budget.MaxBatchBytes - bytes)
            throw new JoinPartitionBudgetExceededException(plan.JoinNodeId, DiagnosticCodes.MapCollectionBudgetExceeded,
                $"One join-key partition exceeded per-side MaxCollectionItems={budget.MaxCollectionItems} or combined MaxBatchBytes={budget.MaxBatchBytes}; within-key spill is unsupported.");
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = _transaction;
            insert.CommandText = $"""
                INSERT INTO join_partitions(id, partition_key, matchable) VALUES($id, $key, $matchable) ON CONFLICT(id) DO NOTHING;
                UPDATE join_partitions SET {(left ? "left_count=left_count+1, left_bytes=left_bytes+$bytes, first_left=COALESCE(first_left,$seq)" : "right_count=right_count+1, right_bytes=right_bytes+$bytes, first_right=COALESCE(first_right,$seq)")}
                    WHERE id=$id;
                INSERT INTO join_rows(seq, partition_id, side, row_json) VALUES($seq,$id,$side,$json);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$matchable", matchable ? 1 : 0);
            insert.Parameters.AddWithValue("$bytes", inputBytes);
            insert.Parameters.AddWithValue("$seq", _joinRows + 1);
            insert.Parameters.AddWithValue("$side", left ? 0 : 1);
            insert.Parameters.AddWithValue("$json", json);
            insert.ExecuteNonQuery();
        }
        if (!exists) { _joinPartitions++; _joinBytes += key.Length; }
        _joinBytes += jsonBytes;
        if (++_joinRows % 512 == 0) CommitAndBeginInsertTransaction();
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Preflight global expansion/policy budgets and disk-index the exact
    /// first-seen GroupBy order of the ordinary Join, including unmatched rights.</summary>
    public void PrepareJoinPartitions(PartitionedJoinPlan plan, ExecutionBudget budget, CancellationToken cancellationToken)
    {
        long expansion = 0;
        var groups = 0;
        for (var id = 1; id <= _joinPartitions; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long lc, rc, lb, rb, lf, rf;
            bool matchable;
            using (var lookup = _connection.CreateCommand())
            {
                lookup.Transaction = _transaction;
                lookup.CommandText = "SELECT matchable,left_count,right_count,left_bytes,right_bytes,first_left,first_right FROM join_partitions WHERE id=$id;";
                lookup.Parameters.AddWithValue("$id", id);
                using var row = lookup.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("Missing join partition.");
                matchable = row.GetBoolean(0);
                lc = row.GetInt64(1); rc = row.GetInt64(2); lb = row.GetInt64(3); rb = row.GetInt64(4);
                lf = row.IsDBNull(5) ? long.MaxValue : row.GetInt64(5);
                rf = row.IsDBNull(6) ? long.MaxValue : row.GetInt64(6);
            }
            if (matchable && rc > 1 && plan.DuplicatePolicy == "Fail")
                throw new JoinPartitionBudgetExceededException(plan.JoinNodeId, DiagnosticCodes.MapDuplicateJoinKey,
                    "Duplicate right key with duplicateKey=Fail.");
            var rightCount = matchable && plan.DuplicatePolicy == "First" ? Math.Min(rc, 1) : rc;
            if (matchable && lc > 1 && rightCount > 1 && plan.ManyToManyPolicy == "Fail")
                throw new JoinPartitionBudgetExceededException(plan.JoinNodeId, DiagnosticCodes.MapManyToManyJoinBlocked,
                    "Join inputs contain a duplicate key on both sides; manyToMany=Fail blocks the Cartesian match.");
            var matches = matchable && lc > 0 && rightCount > 0;
            var leftOutput = matches || (plan.Cardinality is "Left" or "Full" && lc > 0);
            var rightOutput = !matches && plan.Cardinality is "Right" or "Full" && rightCount > 0;
            var rows = matches ? lc * rightCount : (leftOutput ? lc : 0) + (rightOutput ? rightCount : 0);
            if (rows > budget.MaxJoinExpansionRows - expansion)
                throw new JoinPartitionBudgetExceededException(plan.JoinNodeId, DiagnosticCodes.MapJoinExpansionExceeded,
                    $"Join expansion exceeded global MaxJoinExpansionRows={budget.MaxJoinExpansionRows}.");
            expansion += rows;
            if (rows > 0 && (++groups > budget.MaxGroupCount || groups > budget.MaxCollectionItems))
                throw new JoinPartitionBudgetExceededException(plan.GroupNodeId, DiagnosticCodes.MapCollectionBudgetExceeded,
                    $"Joined grouping exceeded MaxGroupCount={budget.MaxGroupCount} or output MaxCollectionItems={budget.MaxCollectionItems}.");
            // Estimate duplicated input payload before the engine allocates merged
            // records/provenance. Division prevents a byte-multiplication overflow.
            var max = budget.MaxGroupWorkingBytes;
            if (matches && (lb > max / rightCount || rb > max / lc
                || lb * rightCount > max - rb * lc))
                throw new JoinPartitionBudgetExceededException(plan.JoinNodeId, DiagnosticCodes.MapCollectionBudgetExceeded,
                    $"Expanded join input estimate exceeded MaxGroupWorkingBytes={max}.");
            using var update = _connection.CreateCommand();
            update.Transaction = _transaction;
            update.CommandText = "UPDATE join_partitions SET phase=$phase, first_output=$seq WHERE id=$id;";
            update.Parameters.AddWithValue("$phase", leftOutput ? 0 : rightOutput ? (matchable ? 1 : 2) : 3);
            update.Parameters.AddWithValue("$seq", leftOutput ? lf : rf);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var order = _connection.CreateCommand();
        order.Transaction = _transaction;
        order.CommandText = "CREATE INDEX join_partition_order ON join_partitions(phase, first_output, id);";
        order.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        CommitAndBeginInsertTransaction();
    }

    public IEnumerable<(IReadOnlyList<DataRecord> Left, IReadOnlyList<DataRecord> Right)> ReadJoinPartitions(CancellationToken cancellationToken)
    {
        // Keyset pagination closes the cursor before fact writes may commit.
        var phase = -1;
        long sequence = -1, priorId = -1;
        for (var i = 0; i < _joinPartitions; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long id;
            using (var lookup = _connection.CreateCommand())
            {
                lookup.Transaction = _transaction;
                lookup.CommandText = "SELECT id,phase,first_output FROM join_partitions WHERE (phase,first_output,id)>($phase,$seq,$id) ORDER BY phase,first_output,id LIMIT 1;";
                lookup.Parameters.AddWithValue("$phase", phase);
                lookup.Parameters.AddWithValue("$seq", sequence);
                lookup.Parameters.AddWithValue("$id", priorId);
                using var row = lookup.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("Missing ordered join partition.");
                id = row.GetInt64(0); phase = row.GetInt32(1); sequence = row.GetInt64(2); priorId = id;
            }
            var left = ReadJoinSide(id, 0, cancellationToken);
            var right = ReadJoinSide(id, 1, cancellationToken);
            yield return (left, right);
        }
    }

    private IReadOnlyList<DataRecord> ReadJoinSide(long id, int side, CancellationToken cancellationToken)
    {
        var rows = new List<DataRecord>();
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = "SELECT row_json FROM join_rows WHERE partition_id=$id AND side=$side ORDER BY seq;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$side", side);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = JsonSerializer.Deserialize<SpilledRecord>(reader.GetString(0))
                ?? throw new InvalidDataException("The join spool contains an invalid row.");
            rows.Add(new DataRecord { RowId = row.RowId,
                Fields = row.Fields.ToDictionary(p => p.Key, p => p.Value.Restore(), StringComparer.Ordinal),
                CellSources = row.CellSources, HasStableRowIdentity = row.HasStableRowIdentity });
        }
        return rows;
    }
}

internal sealed class JoinPartitionBudgetExceededException(string nodeId, string code, string message) : Exception(message)
{
    public string NodeId { get; } = nodeId;
    public string Code { get; } = code;
}
