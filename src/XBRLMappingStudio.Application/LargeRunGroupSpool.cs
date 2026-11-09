using System.Globalization;
using System.Text;
using System.Text.Json;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Application;

internal sealed partial class LargeRunFactStore
{
    private long _groupBytes;
    private long _groupRows;
    private int _groupCount;
    private bool _groupsInitialized;

    public int GroupCount => _groupCount;

    /// <summary>
    /// Partitions relevant source fields on disk without retaining a group-key
    /// dictionary. First-seen group order and member input order are explicit.
    /// Facts, source identity rows, group rows and indexes share one disk cap.
    /// A single group must still fit the bounded engine's input/provenance limits.
    /// </summary>
    public void AddGroupRecord(DataRecord record, PartitionedGroupPlan plan,
        ExecutionBudget budget, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The fact spool is already finalized.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!_groupsInitialized)
        {
            using var schema = _connection.CreateCommand();
            schema.Transaction = _transaction;
            schema.CommandText = """
                CREATE TABLE groups (id INTEGER PRIMARY KEY, group_key BLOB NOT NULL UNIQUE,
                    row_count INTEGER NOT NULL, input_bytes INTEGER NOT NULL);
                CREATE TABLE group_rows (seq INTEGER PRIMARY KEY, group_id INTEGER NOT NULL,
                    row_json TEXT NOT NULL);
                CREATE INDEX group_rows_order ON group_rows(group_id, seq);
                """;
            ExecuteSpoolWrite(schema);
            _groupsInitialized = true;
        }
        var fields = new Dictionary<string, SpilledValue>(StringComparer.Ordinal);
        var sources = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
        foreach (var (name, value) in record.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep every field CollectRows retains, including fields not used by
            // GroupBy: their decimal validation and sampled evidence still matter.
            if (plan.RetainedFields is not null && !plan.RetainedFields.Contains(name)) continue;
            fields.Add(name, SpilledValue.From(value));
            var source = value.Source;
            if (source is null && record.CellSources is not null)
                record.CellSources.TryGetValue(name, out source);
            if (source is not null) sources.Add(name, source);
        }
        var json = JsonSerializer.Serialize(new SpilledRecord(record.RowId, fields, sources, record.HasStableRowIdentity));
        var jsonBytes = Encoding.UTF8.GetByteCount(json);
        if (jsonBytes > budget.MaxRowBytes)
            throw new LargeRunBudgetExceededException($"A grouped source row exceeded the {budget.MaxRowBytes} byte row budget.");
        // Use the full input record estimate too, not only its projected payload.
        var inputBytes = Math.Max(jsonBytes, BufferedSourceReader.EstimateRecordBytes(record));
        var values = plan.KeyFields.Select(field => plan.RequiredFields.Contains(field)
            ? record[field].AsString() : "").ToArray();
        var key = Utf16OrdinalKey(GroupIdentity.Encode(values, cancellationToken));
        long groupId;
        long rowCount = 0;
        long retainedBytes = 0;
        using (var lookup = _connection.CreateCommand())
        {
            lookup.Transaction = _transaction;
            lookup.CommandText = "SELECT id, row_count, input_bytes FROM groups WHERE group_key=$key;";
            lookup.Parameters.AddWithValue("$key", key);
            using var result = lookup.ExecuteReader();
            if (result.Read())
            {
                groupId = result.GetInt64(0);
                rowCount = result.GetInt64(1);
                retainedBytes = result.GetInt64(2);
            }
            else groupId = _groupCount + 1L;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (jsonBytes + (rowCount == 0 ? (long)key.Length : 0) > _maxBytes - _bytes - _sourceBytes - _groupBytes - _joinBytes)
            throw new LargeRunBudgetExceededException($"The combined grouping, fact and source spool exceeded its {_maxBytes} byte budget.");
        if (rowCount == 0 && (_groupCount >= budget.MaxGroupCount || _groupCount >= budget.MaxCollectionItems))
            throw new GroupPartitionBudgetExceededException(plan.GroupNodeId,
                $"GroupBy exceeded MaxGroupCount={budget.MaxGroupCount} or output MaxCollectionItems={budget.MaxCollectionItems}.");
        if (rowCount >= budget.MaxCollectionItems || inputBytes > budget.MaxBatchBytes - retainedBytes)
            throw new GroupPartitionBudgetExceededException(plan.GroupNodeId,
                $"One disk-partitioned group exceeded MaxCollectionItems={budget.MaxCollectionItems} or MaxBatchBytes={budget.MaxBatchBytes}; within-group spill is unsupported.");
        using (var group = _connection.CreateCommand())
        {
            group.Transaction = _transaction;
            group.CommandText = rowCount == 0
                ? "INSERT INTO groups(id, group_key, row_count, input_bytes) VALUES($id, $key, 1, $bytes);"
                : "UPDATE groups SET row_count=row_count+1, input_bytes=input_bytes+$bytes WHERE id=$id;";
            group.Parameters.AddWithValue("$id", groupId);
            group.Parameters.AddWithValue("$key", key);
            group.Parameters.AddWithValue("$bytes", inputBytes);
            ExecuteSpoolWrite(group);
        }
        using (var row = _connection.CreateCommand())
        {
            row.Transaction = _transaction;
            row.CommandText = "INSERT INTO group_rows(group_id, row_json) VALUES($id, $json);";
            row.Parameters.AddWithValue("$id", groupId);
            row.Parameters.AddWithValue("$json", json);
            ExecuteSpoolWrite(row);
        }
        if (rowCount == 0)
        {
            _groupCount++;
            _groupBytes += key.Length;
        }
        _groupBytes += jsonBytes;
        if (++_groupRows % 512 == 0) CommitAndBeginInsertTransaction();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public IReadOnlyList<DataRecord> ReadGroup(int id, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<DataRecord>();
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = "SELECT row_json FROM group_rows WHERE group_id=$id ORDER BY seq;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = JsonSerializer.Deserialize<SpilledRecord>(reader.GetString(0))
                ?? throw new InvalidDataException("The group spool contains an invalid row.");
            rows.Add(new DataRecord
            {
                RowId = row.RowId,
                Fields = row.Fields.ToDictionary(pair => pair.Key, pair => pair.Value.Restore(), StringComparer.Ordinal),
                CellSources = row.CellSources,
                HasStableRowIdentity = row.HasStableRowIdentity
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }

    private sealed record SpilledRecord(string RowId, Dictionary<string, SpilledValue> Fields,
        Dictionary<string, SourceReference> CellSources, bool HasStableRowIdentity);

    private sealed record SpilledValue(PortShape Shape, ValuePresence Presence, string? Lexical,
        SourceReference? Source, JsonElement Raw)
    {
        public static SpilledValue From(DataValue value)
        {
            if (value.Shape is not (PortShape.Decimal or PortShape.Integer or PortShape.String
                or PortShape.Boolean or PortShape.Date or PortShape.Instant))
                throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                    $"Disk-partitioned grouping does not support source values shaped '{value.Shape}'.");
            return new(value.Shape, value.Presence, value.LexicalSource, value.Source,
                JsonSerializer.SerializeToElement(value.Raw));
        }

        public DataValue Restore()
        {
            if (Presence == ValuePresence.Missing) return DataValue.Missing(Shape);
            if (Presence == ValuePresence.ExplicitNil) return DataValue.ExplicitNil(Shape);
            return Shape switch
            {
                PortShape.Decimal => DataValue.DecimalValue(Raw.GetDecimal(), Source, Lexical),
                PortShape.Integer => DataValue.IntegerValue(Raw.GetInt64(), Source),
                PortShape.String => DataValue.StringValue(Raw.GetString()!, Source),
                PortShape.Boolean => DataValue.BoolValue(Raw.GetBoolean()),
                PortShape.Date => DataValue.DateValue(DateOnly.ParseExact(Raw.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                PortShape.Instant => DataValue.InstantValue(Raw.GetDateTimeOffset()),
                _ => throw new InvalidDataException("The group spool contains an unsupported source value.")
            };
        }
    }
}

internal sealed class GroupPartitionBudgetExceededException(string nodeId, string message) : Exception(message)
{
    public string NodeId { get; } = nodeId;
}
