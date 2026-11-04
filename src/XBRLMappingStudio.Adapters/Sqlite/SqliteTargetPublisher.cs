using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;

namespace XBRLMappingStudio.Adapters.Sqlite;

public sealed record SqlitePublishReceipt(string OperationKey, string IntentFingerprint, int WrittenRows, DateTimeOffset CompletedUtc);

/// <summary>Transactional SQLite upsert with a durable operation receipt in the destination database.</summary>
public sealed class SqliteTargetPublisher
{
    public async Task<SqlitePublishReceipt> PublishAsync(SourceConnection destination,
        IReadOnlyList<DataRecord> records, IReadOnlyList<string> businessKey,
        string operationKey, string intentFingerprint, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0) throw new ArgumentException("At least one record is required.", nameof(records));
        if (businessKey.Count == 0 || businessKey.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A non-empty business key is required.", nameof(businessKey));
        if (string.IsNullOrWhiteSpace(operationKey) || string.IsNullOrWhiteSpace(intentFingerprint))
            throw new ArgumentException("Operation key and intent fingerprint are required.");
        var table = SqliteSourceAdapter.GetConfiguredTable(destination);
        if (string.IsNullOrWhiteSpace(destination.Path) || !File.Exists(destination.Path))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, "SQLite target database must already exist.");
        var columns = records.SelectMany(r => r.Fields.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (records.Any(r => r.Fields.Count != columns.Length || columns.Any(c => !r.Fields.ContainsKey(c))))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, "Input records have inconsistent field sets.");
        if (businessKey.Except(columns, StringComparer.Ordinal).Any())
            throw new AdapterException(DiagnosticCodes.IoColumnMissing, "Every business-key field must be present in the input records.");
        var effectiveIntent = Fingerprint(destination, table, records, businessKey, intentFingerprint, columns);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(destination.Path),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TABLE IF NOT EXISTS xms_idempotency_receipts(operation_key TEXT PRIMARY KEY NOT NULL,intent_fingerprint TEXT NOT NULL,written_rows INTEGER NOT NULL,completed_utc TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var prior = connection.CreateCommand())
        {
            prior.Transaction = transaction;
            prior.CommandText = "SELECT intent_fingerprint,written_rows,completed_utc FROM xms_idempotency_receipts WHERE operation_key=$key";
            prior.Parameters.AddWithValue("$key", operationKey);
            await using var reader = await prior.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!StringComparer.Ordinal.Equals(reader.GetString(0), effectiveIntent))
                    throw new AdapterException(DiagnosticCodes.PubReceiptConflict,
                        "Operation key was already used for a different input intent.", DiagnosticStage.Publication);
                var receipt = new SqlitePublishReceipt(operationKey, effectiveIntent, reader.GetInt32(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return receipt;
            }
        }

        var actualColumns = await ReadColumnsAsync(connection, transaction, table, cancellationToken);
        var missing = columns.Except(actualColumns, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new AdapterException(DiagnosticCodes.IoColumnMissing,
                $"SQLite target '{table}' is missing columns: {string.Join(", ", missing)}.");
        foreach (var key in businessKey)
            if (!actualColumns.Contains(key, StringComparer.Ordinal))
                throw new AdapterException(DiagnosticCodes.IoColumnMissing, $"Business-key column '{key}' does not exist in '{table}'.");
        if (!await HasUniqueBusinessKeyAsync(connection, transaction, table, businessKey, cancellationToken))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite target '{table}' must have a primary or unique index on the configured business key.");

        var updates = columns.Except(businessKey, StringComparer.Ordinal).ToArray();
        if (updates.Length == 0) throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation, "Upsert requires at least one non-key column.");
        var insert = $"INSERT INTO {Quote(table)} ({string.Join(",", columns.Select(Quote))}) VALUES ({string.Join(",", columns.Select((_, i) => "$v" + i))}) "
            + $"ON CONFLICT ({string.Join(",", businessKey.Select(Quote))}) DO UPDATE SET {string.Join(",", updates.Select(c => $"{Quote(c)}=excluded.{Quote(c)}"))}";
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = insert;
        var parameters = columns.Select((_, i) =>
        {
            var parameter = upsert.CreateParameter();
            parameter.ParameterName = "$v" + i;
            upsert.Parameters.Add(parameter);
            return parameter;
        }).ToArray();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < columns.Length; i++) parameters[i].Value = ToSql(record.Fields[columns[i]]);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        var completed = DateTimeOffset.UtcNow;
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT INTO xms_idempotency_receipts(operation_key,intent_fingerprint,written_rows,completed_utc) VALUES($key,$intent,$rows,$time)";
            receipt.Parameters.AddWithValue("$key", operationKey);
            receipt.Parameters.AddWithValue("$intent", effectiveIntent);
            receipt.Parameters.AddWithValue("$rows", records.Count);
            receipt.Parameters.AddWithValue("$time", completed.ToString("O", CultureInfo.InvariantCulture));
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SqlitePublishReceipt(operationKey, effectiveIntent, records.Count, completed);
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(1));
        if (names.Count == 0) throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, $"SQLite target table '{table}' does not exist.");
        return names;
    }

    private static async Task<bool> HasUniqueBusinessKeyAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var primaryKey = new List<(int Ordinal, string Name)>();
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({Quote(table)})";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetInt32(5) > 0) primaryKey.Add((reader.GetInt32(5), reader.GetString(1)));
        }
        if (primaryKey.OrderBy(x => x.Ordinal).Select(x => x.Name).SequenceEqual(keys, StringComparer.Ordinal)) return true;

        await using var indexes = connection.CreateCommand();
        indexes.Transaction = transaction;
        indexes.CommandText = $"PRAGMA index_list({Quote(table)})";
        await using var indexReader = await indexes.ExecuteReaderAsync(cancellationToken);
        var uniqueNames = new List<string>();
        while (await indexReader.ReadAsync(cancellationToken))
            if (indexReader.GetInt32(2) != 0) uniqueNames.Add(indexReader.GetString(1));
        await indexReader.DisposeAsync();
        foreach (var index in uniqueNames)
        {
            var columns = new List<(int Ordinal, string Name)>();
            await using var details = connection.CreateCommand();
            details.Transaction = transaction;
            details.CommandText = $"PRAGMA index_info({Quote(index)})";
            await using var detailReader = await details.ExecuteReaderAsync(cancellationToken);
            while (await detailReader.ReadAsync(cancellationToken))
                if (!detailReader.IsDBNull(2)) columns.Add((detailReader.GetInt32(0), detailReader.GetString(2)));
            if (columns.OrderBy(x => x.Ordinal).Select(x => x.Name).SequenceEqual(keys, StringComparer.Ordinal)) return true;
        }
        return false;
    }

    private static object ToSql(DataValue value)
        => value.Presence switch
        {
            ValuePresence.Missing or ValuePresence.ExplicitNil => DBNull.Value,
            ValuePresence.Blank => "",
            _ when value.Raw is decimal number => number.ToString(CultureInfo.InvariantCulture),
            _ when value.Raw is DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ when value.Raw is DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ when value.Raw is bool boolean => boolean ? 1 : 0,
            _ when value.Raw is string or long or int => value.Raw,
            _ => throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                $"SQLite target does not support value shape '{value.Shape}'.")
        };

    private static string Fingerprint(SourceConnection destination, string table, IReadOnlyList<DataRecord> records,
        IReadOnlyList<string> keys, string upstreamFingerprint, IReadOnlyList<string> columns)
    {
        var builder = new StringBuilder();
        AppendPart(builder, Path.GetFullPath(destination.Path!));
        AppendPart(builder, table);
        AppendPart(builder, upstreamFingerprint);
        builder.Append(keys.Count).Append(':');
        foreach (var key in keys.Order(StringComparer.Ordinal)) AppendPart(builder, key);
        builder.Append(records.Count).Append(':').Append(columns.Count).Append(':');
        foreach (var record in records)
        {
            foreach (var column in columns)
            {
                var value = record.Fields[column];
                AppendPart(builder, column);
                builder.Append((int)value.Presence).Append(':');
                AppendPart(builder, ValueText(value.Raw));
            }
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void AppendPart(StringBuilder builder, string value) => builder.Append(value.Length).Append(':').Append(value);

    private static string ValueText(object? value) => value switch
    {
        null => "",
        string text => text,
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        bool boolean => boolean ? "1" : "0",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        long or int => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
            $"SQLite target does not support value type '{value.GetType().Name}'.")
    };

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
