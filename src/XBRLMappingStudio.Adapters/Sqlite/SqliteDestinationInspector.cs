using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Sqlite;

public sealed record SqliteTargetTableSchema(
    string Name,
    IReadOnlyDictionary<string, string> Columns,
    IReadOnlyList<IReadOnlyList<string>> UniqueKeys);

/// <summary>Read-only schema discovery for an existing SQLite publication target.</summary>
public sealed class SqliteDestinationInspector
{
    public async Task<IReadOnlyList<string>> DiscoverTablesAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        EnsureExistingDatabase(databasePath);
        var connection = new SourceConnection(
            "destination-inspector", "sqlite", Path.GetFullPath(databasePath));
        return await new SqliteSourceAdapter().DiscoverTablesAsync(connection, cancellationToken);
    }

    public async Task<SqliteTargetTableSchema> InspectTableAsync(
        string databasePath,
        string table,
        CancellationToken cancellationToken = default)
    {
        EnsureExistingDatabase(databasePath);
        if (string.IsNullOrWhiteSpace(table))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "A SQLite destination table must be selected.", DiagnosticStage.Validation);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        var primaryKey = new List<(int Ordinal, string Name, string Type)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({Quote(table)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                columns.Add(name, type);
                if (reader.GetInt32(5) > 0)
                    primaryKey.Add((reader.GetInt32(5), name, type));
            }
        }

        if (columns.Count == 0)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite destination table '{table}' does not exist or has no columns.",
                DiagnosticStage.Validation);

        var uniqueKeys = await ReadUniqueKeysAsync(connection, transaction, table, cancellationToken);
        var orderedPrimaryKey = primaryKey.OrderBy(column => column.Ordinal).ToArray();
        var hasPrimaryKeyIndex = uniqueKeys.Any(key => key.SequenceEqual(
            orderedPrimaryKey.Select(column => column.Name), StringComparer.Ordinal));
        if (!hasPrimaryKeyIndex && orderedPrimaryKey.Length == 1
            && StringComparer.OrdinalIgnoreCase.Equals(orderedPrimaryKey[0].Type.Trim(), "INTEGER"))
            AddUniqueKey(uniqueKeys, new[] { orderedPrimaryKey[0].Name });

        return new SqliteTargetTableSchema(table, columns, uniqueKeys);
    }

    private static async Task<List<IReadOnlyList<string>>> ReadUniqueKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        var result = new List<IReadOnlyList<string>>();
        var names = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_list({Quote(table)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetInt32(2) != 0 && reader.GetInt32(4) == 0)
                    names.Add(reader.GetString(1));
        }

        foreach (var index in names)
        {
            var columns = new List<(int Ordinal, string Name)>();
            var hasExpression = false;
            var hasNonBinaryCollation = false;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA index_xinfo({Quote(index)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(5) == 0) continue;
                if (reader.IsDBNull(2) || reader.GetInt32(1) < 0)
                {
                    hasExpression = true;
                    continue;
                }
                columns.Add((reader.GetInt32(0), reader.GetString(2)));
                var collation = reader.IsDBNull(4) ? "" : reader.GetString(4);
                hasNonBinaryCollation |= !StringComparer.OrdinalIgnoreCase.Equals(collation, "BINARY");
            }

            var ordered = columns.OrderBy(column => column.Ordinal).Select(column => column.Name).ToArray();
            if (!hasExpression && !hasNonBinaryCollation && ordered.Length > 0)
                AddUniqueKey(result, ordered);
        }
        return result;
    }

    private static void AddUniqueKey(List<IReadOnlyList<string>> keys, IReadOnlyList<string> candidate)
    {
        if (keys.Any(key => key.SequenceEqual(candidate, StringComparer.OrdinalIgnoreCase))) return;
        keys.Add(candidate.ToArray());
    }

    private static void EnsureExistingDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                "SQLite destination database must already exist; schema inspection will not create it.",
                DiagnosticStage.Validation);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
