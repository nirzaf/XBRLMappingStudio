using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
    public const int DefaultMaximumRowBytes = 4 * 1024 * 1024;
    public const string StablePrimaryKeyIdentityStrategy = "sqlite-primary-key-sha256";
    public const string PositionalIdentityStrategy = "positional-row-number";

    private readonly int _maximumRowBytes;

    public SqliteSourceAdapter(int maximumRowBytes = DefaultMaximumRowBytes)
    {
        if (maximumRowBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumRowBytes));
        _maximumRowBytes = maximumRowBytes;
    }

    public string Kind => "sqlite";
    public AdapterCapabilities Capabilities => AdapterCapabilities.ReadOnly;

    public static SqliteSourceDescriptor Describe(SourceConnection connection, ISourceSchema schema)
    {
        if (!StringComparer.Ordinal.Equals(connection.SourceId, schema.SourceId))
            throw new ArgumentException("The inspected schema belongs to a different source.", nameof(schema));

        var columns = schema.Columns.OrderBy(column => column.Ordinal).ToArray();
        var primaryKey = columns.Where(column => column.PrimaryKeyOrdinal > 0)
            .OrderBy(column => column.PrimaryKeyOrdinal)
            .Select(column => column.Name)
            .ToArray();
        var decimalOverrides = DecimalOverrides(connection).Order(StringComparer.Ordinal).ToArray();
        var unknownOverrides = decimalOverrides.Except(columns.Select(column => column.Name), StringComparer.Ordinal).ToArray();
        return new SqliteSourceDescriptor(
            GetConfiguredTable(connection),
            schema.Fingerprint,
            columns,
            primaryKey,
            decimalOverrides,
            unknownOverrides,
            primaryKey.Length > 0 ? StablePrimaryKeyIdentityStrategy : PositionalIdentityStrategy,
            "InvariantCulture",
            AdapterCapabilities.ReadOnly);
    }

    public async Task<IReadOnlyList<string>> DiscoverTablesAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        await using var db = await OpenConnectionAsync(connection, cancellationToken);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND substr(name,1,7) <> 'sqlite_' ORDER BY name";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        return tables;
    }

    public async Task<ISourceSchema> InspectSchemaAsync(SourceConnection connection, CancellationToken cancellationToken)
    {
        var table = GetTable(connection);
        await using var db = await OpenConnectionAsync(connection, cancellationToken);
        var columns = await ReadColumnsAsync(db, connection, table, cancellationToken);
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
        // Keep schema verification and the complete row scan in one SQLite read
        // transaction. In WAL mode, a writer may commit while this iterator is
        // yielding, but this reader continues to observe the same database snapshot.
        await using var transaction = db.BeginTransaction(deferred: true);
        var columns = (await ReadColumnsAsync(db, connection, table, cancellationToken, transaction))
            .OrderBy(c => c.Ordinal)
            .ToArray();
        var actual = TabularSchema.FromSource(connection.SourceId, columns);
        if (schema.SourceId != actual.SourceId || schema.Fingerprint != actual.Fingerprint)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite table '{table}' changed after schema inspection; inspect and remap before reading.");

        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        var columnBytes = CombineSqlExpressions(columns.Select(column =>
            $"COALESCE(length(CAST({Quote(column.Name)} AS BLOB)), 0)"), "+");
        var containsBlob = CombineSqlExpressions(columns.Select(column =>
            $"typeof({Quote(column.Name)}) = 'blob'"), "OR");
        command.CommandText = $"SELECT *, CASE WHEN {containsBlob} THEN -1 ELSE {columnBytes} END "
            + $"FROM {Quote(table)}";
        // The final expression reports all source cell byte lengths without asking
        // Microsoft.Data.Sqlite to materialize any cell. Read it before GetValue;
        // GetStream/GetBytes can allocate the whole value for tables without rowid.
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (reader.FieldCount != columns.Length + 1
            || Enumerable.Range(0, columns.Length).Any(i => reader.GetName(i) != columns[i].Name))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite table '{table}' changed after schema inspection; inspect and remap before reading.");

        var stableKeyColumns = columns.Where(c => c.PrimaryKeyOrdinal > 0)
            .OrderBy(c => c.PrimaryKeyOrdinal)
            .ToArray();

        long rowNumber = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var totalCellBytes = reader.GetInt64(columns.Length);
            if (totalCellBytes < 0)
                throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                    $"SQLite table '{table}' contains a BLOB value; BLOB source values are unsupported.");

            var rowId = stableKeyColumns.Length > 0
                ? "pk:sha256:" + new string('0', 64)
                : $"pos:{rowNumber.ToString(CultureInfo.InvariantCulture)}";
            var estimatedRowBytes = EstimateRowBytesUpperBound(
                columns, connection.SourceId, table, rowId, totalCellBytes);
            if (estimatedRowBytes > _maximumRowBytes)
                throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                    $"A SQLite source row exceeded the {_maximumRowBytes} byte row budget before its cells were materialized.");

            var fields = new Dictionary<string, DataValue>(StringComparer.Ordinal);
            var keyValues = new List<(ColumnSchema Column, object Value)>(stableKeyColumns.Length);
            foreach (var column in columns)
            {
                var value = reader.GetValue(column.Ordinal);
                fields[column.Name] = ConvertValue(value, column.InferredShape, column.Name);
                if (column.PrimaryKeyOrdinal > 0)
                    keyValues.Add((column, value));
            }

            var hasStableIdentity = stableKeyColumns.Length > 0;
            rowId = hasStableIdentity ? StablePrimaryKeyRowId(keyValues) : rowId;
            var sources = columns.ToDictionary(
                column => column.Name,
                column => new SourceReference(connection.SourceId, $"{table}.{column.Name}", rowId),
                StringComparer.Ordinal);
            yield return new DataRecord
            {
                RowId = rowId,
                Fields = fields,
                CellSources = sources,
                HasStableRowIdentity = hasStableIdentity
            };
            rowNumber++;
        }
    }

    private long EstimateRowBytesUpperBound(
        IReadOnlyList<ColumnSchema> columns,
        string sourceId,
        string table,
        string rowId,
        long totalCellBytes)
    {
        // Match BufferedSourceReader's accounting conservatively before any source
        // value is converted to a managed string/array. UTF-8 decoding can expand
        // malformed input, so reserve up to three UTF-8 bytes per SQLite cell byte.
        long estimate = Encoding.UTF8.GetByteCount(rowId) + 64L;
        var sourceIdBytes = Encoding.UTF8.GetByteCount(sourceId);
        var rowIdBytes = Encoding.UTF8.GetByteCount(rowId);
        var decimalTextColumns = 0;
        foreach (var column in columns)
        {
            var nameBytes = Encoding.UTF8.GetByteCount(column.Name);
            var locatorBytes = Encoding.UTF8.GetByteCount($"{table}.{column.Name}");
            estimate = checked(estimate + nameBytes + 48L
                + nameBytes + sourceIdBytes + locatorBytes + rowIdBytes + 48L);
            if (column.InferredShape == PortShape.Decimal) decimalTextColumns++;
        }

        return checked(estimate + (totalCellBytes * 3L) + (decimalTextColumns * 32L));
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

    private static async Task<IReadOnlyList<ColumnSchema>> ReadColumnsAsync(
        SqliteConnection db,
        SourceConnection connection,
        string table,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        var decimalOverrides = DecimalOverrides(connection);
        var columns = new List<ColumnSchema>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            var declaredType = reader.IsDBNull(2) ? "" : reader.GetString(2);
            columns.Add(new ColumnSchema(
                name,
                decimalOverrides.Contains(name) ? PortShape.Decimal : Shape(declaredType),
                columns.Count,
                name,
                reader.GetInt32(5)));
        }
        if (columns.Count == 0)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite table '{table}' does not exist or has no columns.");
        return columns;
    }

    private static string StablePrimaryKeyRowId(IReadOnlyList<(ColumnSchema Column, object Value)> keyValues)
    {
        var builder = new StringBuilder();
        foreach (var (column, rawValue) in keyValues.OrderBy(value => value.Column.PrimaryKeyOrdinal))
        {
            if (rawValue is DBNull)
                throw new AdapterException(DiagnosticCodes.IoDuplicateKey,
                    $"SQLite primary-key column '{column.Name}' contains NULL; stable source row identity cannot be computed.");

            var (kind, value) = FormatKeyValue(rawValue);
            AppendIdentityPart(builder, column.Name);
            AppendIdentityPart(builder, kind);
            AppendIdentityPart(builder, value);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "pk:sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static (string Kind, string Value) FormatKeyValue(object value) => value switch
    {
        string text => ("text", text),
        long number => ("integer", number.ToString(CultureInfo.InvariantCulture)),
        int number => ("integer", number.ToString(CultureInfo.InvariantCulture)),
        double number => ("real", number.ToString("R", CultureInfo.InvariantCulture)),
        byte[] bytes => ("blob", Convert.ToBase64String(bytes)),
        IFormattable formattable => (value.GetType().FullName ?? "value",
            formattable.ToString(null, CultureInfo.InvariantCulture) ?? ""),
        _ => (value.GetType().FullName ?? "value", value.ToString() ?? "")
    };

    private static void AppendIdentityPart(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

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
        => GetConfiguredTable(connection);

    internal static string GetConfiguredTable(SourceConnection connection)
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

/// <summary>Read-only source facts needed to review a SQLite mapping before execution.</summary>
public sealed record SqliteSourceDescriptor(
    string Table,
    string SchemaFingerprint,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<string> DecimalOverrides,
    IReadOnlyList<string> UnknownDecimalOverrides,
    string RowIdentityStrategy,
    string DecimalCulture,
    AdapterCapabilities Capabilities)
{
    public bool HasStableRowIdentity
        => StringComparer.Ordinal.Equals(RowIdentityStrategy, SqliteSourceAdapter.StablePrimaryKeyIdentityStrategy);
}
