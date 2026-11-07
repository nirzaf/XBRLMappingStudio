using System.Text.Json;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Application;

/// <summary>
/// Adds a deterministic, machine-readable presence map for the v2 CSV/XLSX row
/// contracts. The regular output cells stay convenient for spreadsheet use, while this
/// column preserves Missing, ExplicitNil, Blank, and Present as distinct states.
/// </summary>
internal static class FileOutputPresenceMetadata
{
    public const string ColumnName = "__xms_field_presence";
    public const string SchemaId = "xms-field-presence-v1";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<string> AppendHeader(IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Contains(ColumnName, StringComparer.Ordinal))
            throw new InvalidOperationException($"Output headers already contain reserved column '{ColumnName}'.");
        return headers.Append(ColumnName).ToArray();
    }

    public static IReadOnlyList<DataRecord> Append(
        IReadOnlyList<DataRecord> records,
        IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Contains(ColumnName, StringComparer.Ordinal))
            throw new InvalidOperationException($"Output headers already contain reserved column '{ColumnName}'.");

        return records.Select(record =>
        {
            if (record.Fields.ContainsKey(ColumnName))
                throw new InvalidOperationException($"Source row contains reserved column '{ColumnName}'.");
            var fields = record.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            fields[ColumnName] = DataValue.StringValue(Serialize(record, headers));
            return new DataRecord
            {
                RowId = record.RowId,
                Fields = fields,
                CellSources = record.CellSources,
                HasStableRowIdentity = record.HasStableRowIdentity
            };
        }).ToArray();
    }

    public static string Serialize(DataRecord record, IReadOnlyList<string> headers)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(headers);

        var fields = headers.Select(name => new PresenceField(
            name,
            record.Fields.TryGetValue(name, out var value) && value is not null
                ? StateName(value.Presence)
                : "missing")).ToArray();
        return JsonSerializer.Serialize(new PresenceDocument(SchemaId, fields), SerializerOptions);
    }

    private static string StateName(ValuePresence presence) => presence switch
    {
        ValuePresence.Missing => "missing",
        ValuePresence.ExplicitNil => "nil",
        ValuePresence.Blank => "blank",
        ValuePresence.Present => "present",
        _ => throw new ArgumentOutOfRangeException(nameof(presence), presence, "Unknown value-presence state.")
    };

    private sealed record PresenceDocument(string Schema, IReadOnlyList<PresenceField> Fields);
    private sealed record PresenceField(string Name, string State);
}
