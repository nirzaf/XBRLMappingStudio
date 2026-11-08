using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Engine;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Adapters.Sqlite;

public sealed record SqlitePublishReceipt(string OperationKey, string IntentFingerprint, int WrittenRows, DateTimeOffset CompletedUtc);
public enum SqliteReceiptReconciliation
{
    Found,
    /// <summary>The target exists, but the requested transaction's receipt does not.</summary>
    NotFound,
    /// <summary>The target database is missing or cannot be opened.</summary>
    TargetUnavailable,
    IntentConflict
}
public sealed record SqliteTargetChangeSample(int CandidateRow, string Outcome);
public sealed record SqliteTargetChangePreview(
    int CandidateRows,
    int InspectedRows,
    int Inserts,
    int Updates,
    int UnchangedValueUpdates,
    int Conflicts,
    int SampleLimit,
    IReadOnlyList<SqliteTargetChangeSample> Samples,
    IReadOnlyList<string> SchemaDiscrepancies)
{
    public int UncertainRows => CandidateRows - InspectedRows;
    public bool CountsAreComplete => UncertainRows == 0;
}

/// <summary>Transactional SQLite upsert with a durable operation receipt in the destination database.</summary>
public sealed class SqliteTargetPublisher
{
    public const int MaximumPreviewRows = 5_000;
    public const int MaximumPreviewBytes = 4 * 1024 * 1024;
    public const int DefaultPreviewSampleLimit = 10;

    private readonly Action<int>? _afterRecordWritten;

    public SqliteTargetPublisher()
    {
    }

    /// <summary>Provides a deterministic post-upsert boundary for transactional fault tests.</summary>
    internal SqliteTargetPublisher(Action<int> afterRecordWritten)
    {
        _afterRecordWritten = afterRecordWritten ?? throw new ArgumentNullException(nameof(afterRecordWritten));
    }

    public async Task<SqlitePublishReceipt> PublishAsync(SourceConnection destination,
        IReadOnlyList<DataRecord> records, IReadOnlyList<string> businessKey,
        string operationKey, string intentFingerprint, CancellationToken cancellationToken = default,
        Action? beforeCommit = null)
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
        if (records.Any(record => businessKey.Any(key => record.Fields[key].Presence is ValuePresence.Missing or ValuePresence.ExplicitNil
                || record.Fields[key].Raw is null)))
            throw new AdapterException(DiagnosticCodes.IoDuplicateKey, "SQLite business-key values cannot be missing or null.", DiagnosticStage.Publication);
        var effectiveIntent = ComputeEffectiveIntentFingerprint(destination, table, records, businessKey, intentFingerprint, columns);

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
                beforeCommit?.Invoke();
                await transaction.CommitAsync(cancellationToken);
                return receipt;
            }
        }

        var actualColumns = await ReadColumnsAsync(connection, transaction, table, cancellationToken);
        var missing = columns.Except(actualColumns.Keys, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new AdapterException(DiagnosticCodes.IoColumnMissing,
                $"SQLite target '{table}' is missing columns: {string.Join(", ", missing)}.");
        foreach (var key in businessKey)
            if (!actualColumns.ContainsKey(key))
                throw new AdapterException(DiagnosticCodes.IoColumnMissing, $"Business-key column '{key}' does not exist in '{table}'.");
        foreach (var column in columns.Where(c => records.Any(r => r.Fields[c].Shape == PortShape.Decimal)))
            if (Affinity(actualColumns[column]) != "TEXT")
                throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                    $"Exact decimal column '{column}' must have TEXT affinity in SQLite target '{table}'.", DiagnosticStage.Publication);
        var conflictTarget = await GetBusinessKeyConflictTargetAsync(connection, transaction, table, businessKey, cancellationToken);
        if (conflictTarget is null)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite target '{table}' must have a primary or unique index on the configured business key.");
        await EnsureUniqueBatchKeysAsync(connection, transaction, table, businessKey, records, cancellationToken);

        var updates = columns.Except(businessKey, StringComparer.Ordinal).ToArray();
        if (updates.Length == 0) throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation, "Upsert requires at least one non-key column.");
        var insert = $"INSERT INTO {Quote(table)} ({string.Join(",", columns.Select(Quote))}) VALUES ({string.Join(",", columns.Select((_, i) => "$v" + i))}) "
            + $"ON CONFLICT ({conflictTarget}) DO UPDATE SET {string.Join(",", updates.Select(c => $"{Quote(c)}=excluded.{Quote(c)}"))}";
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
        var writtenRows = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < columns.Length; i++) parameters[i].Value = ToSql(record.Fields[columns[i]]);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
            _afterRecordWritten?.Invoke(++writtenRows);
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
        beforeCommit?.Invoke();
        await transaction.CommitAsync(cancellationToken);
        return new SqlitePublishReceipt(operationKey, effectiveIntent, records.Count, completed);
    }

    /// <summary>
    /// Reconcile a durable destination receipt without writing. Rows and their receipt
    /// commit in the same SQLite transaction, so absence proves no committed receipt only
    /// for an intact destination. It cannot disprove historical confirmation if the receipt
    /// or database was subsequently deleted or replaced.
    /// </summary>
    public async Task<(SqliteReceiptReconciliation State, SqlitePublishReceipt? Receipt)> ReconcileReceiptAsync(
        SourceConnection destination,
        string operationKey,
        string expectedIntentFingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(operationKey) || string.IsNullOrWhiteSpace(expectedIntentFingerprint))
            return (SqliteReceiptReconciliation.IntentConflict, null);
        if (string.IsNullOrWhiteSpace(destination.Path) || !File.Exists(destination.Path))
            return (SqliteReceiptReconciliation.TargetUnavailable, null);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(destination.Path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var table = connection.CreateCommand();
        table.Transaction = transaction;
        table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='xms_idempotency_receipts' COLLATE NOCASE";
        if (await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            return (SqliteReceiptReconciliation.NotFound, null);

        var columns = await ReadColumnsAsync(connection, transaction, "xms_idempotency_receipts", cancellationToken)
            .ConfigureAwait(false);
        if (new[] { "operation_key", "intent_fingerprint", "written_rows", "completed_utc" }
            .Except(columns.Keys, StringComparer.OrdinalIgnoreCase).Any())
            return (SqliteReceiptReconciliation.IntentConflict, null);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A damaged/replaced table may have lost its primary key. Never accept one
        // arbitrarily selected receipt when multiple rows match the operation.
        command.CommandText = "SELECT operation_key,intent_fingerprint,written_rows,completed_utc FROM xms_idempotency_receipts WHERE operation_key=$key LIMIT 2";
        command.Parameters.AddWithValue("$key", operationKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (SqliteReceiptReconciliation.NotFound, null);
        if (!TryReadReceipt(reader, operationKey, expectedIntentFingerprint, out var receipt)
            || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (SqliteReceiptReconciliation.IntentConflict, null);
        return (SqliteReceiptReconciliation.Found, receipt);
    }

    private static bool TryReadReceipt(SqliteDataReader reader, string operationKey,
        string expectedIntentFingerprint, out SqlitePublishReceipt? receipt)
    {
        receipt = null;
        // GetValue preserves SQLite's storage classes; typed getters can silently
        // coerce TEXT/REAL into integers or truncate a 64-bit row count.
        if (reader.GetValue(0) is not string key || string.IsNullOrWhiteSpace(key)
            || !StringComparer.Ordinal.Equals(key, operationKey)
            || reader.GetValue(1) is not string intent || string.IsNullOrWhiteSpace(intent)
            || !StringComparer.Ordinal.Equals(intent, expectedIntentFingerprint)
            || reader.GetValue(2) is not long writtenRows || writtenRows is <= 0 or > int.MaxValue
            || reader.GetValue(3) is not string completedText || string.IsNullOrWhiteSpace(completedText)
            || !DateTimeOffset.TryParse(completedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var completed))
            return false;

        receipt = new SqlitePublishReceipt(key, intent, (int)writtenRows, completed);
        return true;
    }

    /// <summary>
    /// Estimates the outcomes of a future upsert without modifying the target database.
    /// Candidate normalization and duplicate detection happen in a private in-memory
    /// database; the target is opened read-only and held in one SQLite read snapshot.
    /// </summary>
    public async Task<SqliteTargetChangePreview> PreviewAsync(
        SourceConnection destination,
        IReadOnlyList<DataRecord> records,
        IReadOnlyList<string> businessKey,
        int maximumCandidateRows = MaximumPreviewRows,
        int sampleLimit = DefaultPreviewSampleLimit,
        CancellationToken cancellationToken = default)
    {
        if (records.Count == 0) throw new ArgumentException("At least one record is required.", nameof(records));
        if (businessKey.Count == 0 || businessKey.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A non-empty business key is required.", nameof(businessKey));
        if (maximumCandidateRows <= 0 || maximumCandidateRows > MaximumPreviewRows)
            throw new ArgumentOutOfRangeException(nameof(maximumCandidateRows),
                $"Preview row limit must be between 1 and {MaximumPreviewRows}.");
        if (sampleLimit < 0 || sampleLimit > MaximumPreviewRows)
            throw new ArgumentOutOfRangeException(nameof(sampleLimit),
                $"Preview sample limit must be between 0 and {MaximumPreviewRows}.");

        var table = SqliteSourceAdapter.GetConfiguredTable(destination);
        if (string.IsNullOrWhiteSpace(destination.Path) || !File.Exists(destination.Path))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, "SQLite target database must already exist.", DiagnosticStage.Validation);
        var columns = records.SelectMany(record => record.Fields.Keys)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (records.Any(record => record.Fields.Count != columns.Length || columns.Any(column => !record.Fields.ContainsKey(column))))
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, "Input records have inconsistent field sets.", DiagnosticStage.Validation);
        if (businessKey.Except(columns, StringComparer.Ordinal).Any())
            throw new AdapterException(DiagnosticCodes.IoColumnMissing,
                "Every business-key field must be present in the input records.", DiagnosticStage.Validation);
        if (records.Any(record => businessKey.Any(key => record.Fields[key].Presence is ValuePresence.Missing or ValuePresence.ExplicitNil
                || record.Fields[key].Raw is null)))
            throw new AdapterException(DiagnosticCodes.IoDuplicateKey,
                "SQLite business-key values cannot be missing or null.", DiagnosticStage.Validation);

        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(destination.Path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
            ForeignKeys = true
        }.ToString());
        await target.OpenAsync(cancellationToken);
        await using var targetTransaction = target.BeginTransaction(deferred: true);

        var actualColumns = await ReadColumnsAsync(target, targetTransaction, table, cancellationToken);
        var missing = columns.Except(actualColumns.Keys, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new AdapterException(DiagnosticCodes.IoColumnMissing,
                $"SQLite target '{table}' is missing columns: {string.Join(", ", missing)}.", DiagnosticStage.Validation);
        foreach (var column in columns.Where(column => records.Any(record => record.Fields[column].Shape == PortShape.Decimal)))
            if (Affinity(actualColumns[column]) != "TEXT")
                throw new AdapterException(DiagnosticCodes.IoNumberOutOfBudget,
                    $"Exact decimal column '{column}' must have TEXT affinity in SQLite target '{table}'.", DiagnosticStage.Validation);
        if (await GetBusinessKeyConflictTargetAsync(target, targetTransaction, table, businessKey, cancellationToken) is null)
            throw new AdapterException(DiagnosticCodes.IoSchemaMismatch,
                $"SQLite target '{table}' must have a primary or unique index on the configured business key.",
                DiagnosticStage.Validation);
        if (columns.Except(businessKey, StringComparer.Ordinal).Count() == 0)
            throw new AdapterException(DiagnosticCodes.IoUnsupportedOperation,
                "Upsert requires at least one non-key column.", DiagnosticStage.Validation);

        var normalizedNames = columns.Select((_, index) => "c" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        var candidateTable = Quote("preview_candidates");
        var createColumns = normalizedNames.Select((name, index) =>
            $"{Quote(name)} {Affinity(actualColumns[columns[index]])}");
        var keyIndexes = businessKey.Select(key => Array.IndexOf(columns, key)).ToArray();

        await using var normalized = new SqliteConnection("Data Source=:memory:;Pooling=False");
        await normalized.OpenAsync(cancellationToken);
        await using var normalizedTransaction = normalized.BeginTransaction();
        await using (var create = normalized.CreateCommand())
        {
            create.Transaction = normalizedTransaction;
            create.CommandText = $"CREATE TABLE {candidateTable} ({Quote("row_number")} INTEGER PRIMARY KEY,{string.Join(",", createColumns)});"
                + $"CREATE UNIQUE INDEX {Quote("preview_candidate_keys")} ON {candidateTable} ({string.Join(",", keyIndexes.Select(index => $"{Quote(normalizedNames[index])} COLLATE BINARY"))});";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insertCandidate = normalized.CreateCommand();
        insertCandidate.Transaction = normalizedTransaction;
        insertCandidate.CommandText = $"INSERT INTO {candidateTable} ({Quote("row_number")},{string.Join(",", normalizedNames.Select(Quote))}) "
            + $"VALUES ($row,{string.Join(",", normalizedNames.Select((_, index) => "$v" + index.ToString(CultureInfo.InvariantCulture)))})";
        insertCandidate.Parameters.Add("$row", SqliteType.Integer);
        for (var index = 0; index < columns.Length; index++)
        {
            var parameter = insertCandidate.CreateParameter();
            parameter.ParameterName = "$v" + index.ToString(CultureInfo.InvariantCulture);
            insertCandidate.Parameters.Add(parameter);
        }

        await using var readCandidate = normalized.CreateCommand();
        readCandidate.Transaction = normalizedTransaction;
        readCandidate.CommandText = $"SELECT {string.Join(",", normalizedNames.Select(Quote))} FROM {candidateTable} WHERE {Quote("row_number")}=$row";
        readCandidate.Parameters.Add("$row", SqliteType.Integer);

        await using var readTarget = target.CreateCommand();
        readTarget.Transaction = targetTransaction;
        readTarget.CommandText = $"SELECT {string.Join(",", columns.Select(Quote))} FROM {Quote(table)} WHERE "
            + string.Join(" AND ", businessKey.Select((key, index) =>
                $"{Quote(key)} COLLATE BINARY = $k{index.ToString(CultureInfo.InvariantCulture)}"));
        for (var index = 0; index < businessKey.Count; index++)
        {
            var parameter = readTarget.CreateParameter();
            parameter.ParameterName = "$k" + index.ToString(CultureInfo.InvariantCulture);
            readTarget.Parameters.Add(parameter);
        }

        var samples = new List<SqliteTargetChangeSample>(Math.Min(sampleLimit, records.Count));
        var inserts = 0;
        var updates = 0;
        var unchangedValueUpdates = 0;
        var conflicts = 0;
        var inspectedRows = 0;
        long inspectedBytes = 0;
        var scanCount = Math.Min(records.Count, maximumCandidateRows);
        for (var recordIndex = 0; recordIndex < scanCount; recordIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[recordIndex];
            var recordBytes = EstimatePreviewBytes(record, columns);
            if (inspectedBytes + recordBytes > MaximumPreviewBytes) break;
            inspectedBytes += recordBytes;
            inspectedRows++;

            insertCandidate.Parameters[0].Value = recordIndex + 1;
            for (var index = 0; index < columns.Length; index++)
                insertCandidate.Parameters[index + 1].Value = ToSql(record.Fields[columns[index]]);
            try
            {
                await insertCandidate.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                conflicts++;
                AddSample(recordIndex + 1, "conflict");
                continue;
            }

            readCandidate.Parameters[0].Value = recordIndex + 1;
            var candidateValues = new object[columns.Length];
            await using (var candidateReader = await readCandidate.ExecuteReaderAsync(cancellationToken))
            {
                if (!await candidateReader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("The normalized candidate row could not be read back.");
                for (var index = 0; index < columns.Length; index++) candidateValues[index] = candidateReader.GetValue(index);
            }

            for (var index = 0; index < keyIndexes.Length; index++)
                readTarget.Parameters[index].Value = candidateValues[keyIndexes[index]];
            string outcome;
            await using (var targetReader = await readTarget.ExecuteReaderAsync(cancellationToken))
            {
                if (!await targetReader.ReadAsync(cancellationToken))
                {
                    inserts++;
                    outcome = "insert";
                }
                else
                {
                    var same = true;
                    for (var index = 0; index < columns.Length; index++)
                        if (!SqliteValuesEqual(candidateValues[index], targetReader.GetValue(index)))
                        {
                            same = false;
                            break;
                        }
                    if (same)
                    {
                        updates++;
                        unchangedValueUpdates++;
                        outcome = "unchanged";
                    }
                    else
                    {
                        updates++;
                        outcome = "update";
                    }
                }
            }
            AddSample(recordIndex + 1, outcome);
        }

        await normalizedTransaction.CommitAsync(cancellationToken);
        return new SqliteTargetChangePreview(records.Count, inspectedRows, inserts, updates, unchangedValueUpdates,
            conflicts, sampleLimit, samples, Array.Empty<string>());

        void AddSample(int candidateRow, string outcome)
        {
            if (samples.Count < sampleLimit) samples.Add(new SqliteTargetChangeSample(candidateRow, outcome));
        }
    }

    private static long EstimatePreviewBytes(DataRecord record, IReadOnlyList<string> columns)
    {
        long bytes = 0;
        foreach (var column in columns)
        {
            var value = ToSql(record.Fields[column]);
            bytes += value switch
            {
                string text => Encoding.UTF8.GetByteCount(text),
                DBNull => 1,
                _ => 8
            };
            if (bytes > MaximumPreviewBytes) return bytes;
        }
        return bytes;
    }

    private static bool SqliteValuesEqual(object left, object right)
        => left is byte[] leftBytes && right is byte[] rightBytes
            ? leftBytes.AsSpan().SequenceEqual(rightBytes)
            : Equals(left, right);

    private static async Task<Dictionary<string, string>> ReadColumnsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2));
        if (names.Count == 0) throw new AdapterException(DiagnosticCodes.IoSchemaMismatch, $"SQLite target table '{table}' does not exist.");
        return names;
    }

    private static async Task<string?> GetBusinessKeyConflictTargetAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var primaryKey = new List<(int Ordinal, string Name, string Type)>();
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = $"PRAGMA table_info({Quote(table)})";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetInt32(5) > 0) primaryKey.Add((reader.GetInt32(5), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
        }
        var orderedPrimaryKey = primaryKey.OrderBy(x => x.Ordinal).ToArray();

        await using var indexes = connection.CreateCommand();
        indexes.Transaction = transaction;
        indexes.CommandText = $"PRAGMA index_list({Quote(table)})";
        await using var indexReader = await indexes.ExecuteReaderAsync(cancellationToken);
        var uniqueNames = new List<string>();
        var hasPrimaryKeyIndex = false;
        while (await indexReader.ReadAsync(cancellationToken))
        {
            hasPrimaryKeyIndex |= StringComparer.Ordinal.Equals(indexReader.GetString(3), "pk");
            if (indexReader.GetInt32(2) != 0 && indexReader.GetInt32(4) == 0) uniqueNames.Add(indexReader.GetString(1));
        }
        await indexReader.DisposeAsync();
        foreach (var index in uniqueNames)
        {
            var columns = new List<(int Ordinal, string Name)>();
            var collations = new List<string>();
            var hasExpression = false;
            await using var details = connection.CreateCommand();
            details.Transaction = transaction;
            details.CommandText = $"PRAGMA index_xinfo({Quote(index)})";
            await using var detailReader = await details.ExecuteReaderAsync(cancellationToken);
            while (await detailReader.ReadAsync(cancellationToken))
            {
                if (detailReader.GetInt32(5) == 0) continue;
                if (detailReader.IsDBNull(2)) { hasExpression = true; continue; }
                columns.Add((detailReader.GetInt32(0), detailReader.GetString(2)));
                collations.Add(detailReader.GetString(4));
            }
            if (!hasExpression && columns.OrderBy(x => x.Ordinal).Select(x => x.Name).SequenceEqual(keys, StringComparer.Ordinal))
            {
                if (collations.All(c => StringComparer.OrdinalIgnoreCase.Equals(c, "BINARY")))
                    return string.Join(",", keys.Select(c => $"{Quote(c)} COLLATE BINARY"));
            }
        }
        var isRowIdPrimaryKey = !hasPrimaryKeyIndex && orderedPrimaryKey.Length == 1 && keys.Count == 1
            && StringComparer.Ordinal.Equals(orderedPrimaryKey[0].Name, keys.SingleOrDefault())
            && StringComparer.OrdinalIgnoreCase.Equals(orderedPrimaryKey[0].Type.Trim(), "INTEGER");
        return isRowIdPrimaryKey
            ? string.Join(",", keys.Select(Quote))
            : null;
    }

    private static async Task EnsureUniqueBatchKeysAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, IReadOnlyList<string> keys, IReadOnlyList<DataRecord> records, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"CREATE TEMP TABLE \"__xms_batch_keys\" AS SELECT {string.Join(",", keys.Select(Quote))} FROM {Quote(table)} WHERE 0; CREATE UNIQUE INDEX \"__xms_batch_keys_unique\" ON \"__xms_batch_keys\" ({string.Join(",", keys.Select(c => $"{Quote(c)} COLLATE BINARY"))});";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = $"INSERT INTO temp.\"__xms_batch_keys\" ({string.Join(",", keys.Select(Quote))}) VALUES ({string.Join(",", keys.Select((_, i) => "$k" + i))})";
        for (var i = 0; i < keys.Count; i++) command.Parameters.AddWithValue("$k" + i, DBNull.Value);
        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < keys.Count; i++) command.Parameters[i].Value = ToSql(record.Fields[keys[i]]);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new AdapterException(DiagnosticCodes.IoDuplicateKey,
                $"SQLite input batch contains duplicate business-key values ({ex.Message}).", DiagnosticStage.Publication);
        }
    }

    private static string Affinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return "INTEGER";
        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("CLOB", StringComparison.Ordinal) || type.Contains("TEXT", StringComparison.Ordinal)) return "TEXT";
        if (type.Length == 0 || type.Contains("BLOB", StringComparison.Ordinal)) return "BLOB";
        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal) || type.Contains("DOUB", StringComparison.Ordinal)) return "REAL";
        return "NUMERIC";
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

    public static string ComputeEffectiveIntentFingerprint(SourceConnection destination, string table,
        IReadOnlyList<DataRecord> records,
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
                AppendPart(builder, value.Shape.ToString());
                builder.Append((int)value.Presence).Append(':');
                AppendPart(builder, ValueText(value.Raw));
                AppendPart(builder, value.Raw?.GetType().FullName ?? "null");
                AppendPart(builder, Convert.ToString(ToSql(value), CultureInfo.InvariantCulture) ?? "null");
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
