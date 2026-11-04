using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Sqlite;

/// <summary>Read-only SQLite table adapter (IO07 pilot). Numeric REAL values are refused
/// because SQLite stores them as binary floating point; store exact financial decimals as
/// TEXT and parse them explicitly in the mapping.</summary>
public sealed class SqliteSourceAdapter : ISourceAdapter
{
    public string Kind => "sqlite";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;

    public async Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var table = GetTable(connection);
        await using var db = await OpenConnectionAsync(connection, cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        var decimalOverrides = DecimalOverrides(connection);
        var columns = new List<ColumnSchema>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            var declaredType = reader.IsDBNull(2) ? "" : reader.GetString(2);
            columns.Add(new ColumnSchema(name, decimalOverrides.Contains(name) ? PortShape.Decimal : Shape(declaredType), columns.Count, name));
        }
        if (columns.Count == 0)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, $"SQLite table '{table}' does not exist or has no columns.");
        return TabularSchema.FromSource(connection.SourceId, columns);
    }

    public async Task<DataSample> ReadSampleAsync(SourceConnection connection, ISourceSchema schema,
        int maxRows, CancellationToken cancellationToken)
    {
        if (maxRows < 0) throw new ArgumentOutOfRangeException(nameof(maxRows));
        var rows = new List<DataRecord>(Math.Min(maxRows, 256));
        var truncated = false;
        await foreach (var row in ReadFullAsync(connection, schema, cancellationToken))
        {
            if (rows.Count == maxRows) { truncated = true; break; }
            rows.Add(row);
        }
        return new DataSample(schema, rows, truncated);
    }

    public async IAsyncEnumerable<DataRecord> ReadFullAsync(SourceConnection connection, ISourceSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var table = GetTable(connection);
        await using var db = await OpenConnectionAsync(connection, cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(table)}";
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var columns = schema.Columns.OrderBy(c => c.Ordinal).ToArray();
        if (reader.FieldCount != columns.Length
            || Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i) != columns[i].Name))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite table '{table}' changed after schema inspection; inspect and remap before reading.");
        var actual = TabularSchema.FromSource(connection.SourceId, columns);
        if (schema.SourceId != actual.SourceId || schema.Fingerprint != actual.Fingerprint)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite table '{table}' changed after schema inspection; inspect and remap before reading.");

        long rowNumber = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var sources = new Dictionary<string, SourceReference>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                var value = reader.GetValue(column.Ordinal);
                fields[column.Name] = ConvertValue(value, column.InferredShape, column.Name);
                sources[column.Name] = new SourceReference(connection.SourceId, $"{table}.{column.Name}", rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            yield return new DataRecord { RowId = rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), Fields = fields, CellSources = sources };
            rowNumber++;
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.Path))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, "SQLite source requires a database file path.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(connection.Path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await db.OpenAsync(cancellationToken);
        return db;
    }

    private static string GetTable(SourceConnection connection)
    {
        if (connection.Configuration is null
            || !connection.Configuration.TryGetValue("table", out var table)
            || string.IsNullOrWhiteSpace(table))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, "SQLite source requires a configured table name.");
        return table;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static HashSet<string> DecimalOverrides(SourceConnection connection)
        => connection.Configuration is not null && connection.Configuration.TryGetValue("decimalColumns", out var value)
            ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static PortShape Shape(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("BOOL", StringComparison.Ordinal)) return PortShape.Boolean;
        if (type.Contains("INT", StringComparison.Ordinal)) return PortShape.Integer;
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal)) return PortShape.Decimal;
        if (type.Contains("NUM", StringComparison.Ordinal) || type.Contains("DEC", StringComparison.Ordinal)) return PortShape.Decimal;
        return PortShape.String;
    }

    private static DataValue ConvertValue(object value, PortShape shape, string column)
    {
        if (value is DBNull) return DataValue.ExplicitNil(shape);
        if (value is double or float)
            throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                $"SQLite column '{column}' returned binary floating-point data; exact numeric ingestion requires a text decimal.");
        return value switch
        {
            long n when shape == PortShape.Boolean => DataValue.BoolValue(n != 0),
            long n when shape == PortShape.Decimal => DataValue.DecimalValue(n),
            long n => DataValue.IntegerValue(n),
            int n => DataValue.IntegerValue(n),
            string s when shape == PortShape.Decimal && decimal.TryParse(s, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var number) => DataValue.DecimalValue(number, lexical: s),
            string s when shape == PortShape.Decimal => throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                $"SQLite decimal column '{column}' contains non-decimal value '{s}'."),
            string s => DataValue.StringValue(s),
            byte[] => throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation, $"SQLite blob column '{column}' is unsupported."),
            _ => DataValue.StringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "")
        };
    }
}
