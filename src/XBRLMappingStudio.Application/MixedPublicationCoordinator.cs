using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Adapters;
using XBRLMappingStudio.Adapters.Sqlite;
using XBRLMappingStudio.Diagnostics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Application;

/// <summary>Stable intent shared by the file and SQLite legs of one mixed publication.</summary>
public sealed record MixedPublicationIntent(
    string OperationKey,
    string Fingerprint,
    string FilePath,
    string FileDigest,
    long FileBytes,
    string SqlitePath,
    string SqliteTable,
    string FileKind = "xbrl-json")
{
    public static MixedPublicationIntent Create(
        MappingProject project,
        JobResult result,
        string filePath,
        long fileBytes,
        string sqlitePath,
        string sqliteTable,
        string fileKind = "xbrl-json")
    {
        if (fileKind is not ("xbrl-json" or "xbrl-xml"))
            throw new ArgumentOutOfRangeException(nameof(fileKind), fileKind,
                "Mixed publication supports xBRL-JSON and xBRL-XML file artifacts.");

        var manifest = result.Manifest;
        var builder = new StringBuilder();
        Append(builder, project.ProjectId);
        Append(builder, project.SemanticRevision.ToString(CultureInfo.InvariantCulture));
        Append(builder, project.ComputeSemanticDigest());
        Append(builder, manifest.ProfileDigest);
        Append(builder, manifest.EngineVersion);
        Append(builder, Path.GetFullPath(filePath));
        Append(builder, Path.GetFullPath(sqlitePath));
        Append(builder, sqliteTable);
        var report = manifest.Outputs.SingleOrDefault(output => output.Kind == fileKind)
            ?? throw new InvalidDataException($"Run manifest has no unique '{fileKind}' file output.");
        if (report.Bytes != fileBytes)
            throw new InvalidDataException($"The '{fileKind}' output length does not match the mixed publication payload.");
        // Keep existing xBRL-JSON operation keys stable across the v8 migration so
        // retries still find their v7 reconciliation row. XML needs a discriminator.
        if (fileKind == "xbrl-xml") Append(builder, fileKind);
        Append(builder, report.Digest);
        Append(builder, fileBytes.ToString(CultureInfo.InvariantCulture));
        foreach (var input in manifest.Inputs.OrderBy(input => input.SourceId, StringComparer.Ordinal))
        {
            Append(builder, input.SourceId);
            Append(builder, input.Kind);
            Append(builder, input.Digest ?? string.Empty);
            Append(builder, input.IsReplayable ? "1" : "0");
            Append(builder, input.RowIdentityStrategy ?? string.Empty);
            Append(builder, input.SnapshotStrategy ?? string.Empty);
            if (input.SnapshotStrategy == SqliteSourceSnapshotStore.Strategy)
                Append(builder, input.SnapshotDigest ?? string.Empty);
        }
        foreach (var parameter in manifest.FrozenParameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal))
        {
            Append(builder, parameter.Key);
            Append(builder, parameter.Value);
        }

        var fingerprint = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
        return new MixedPublicationIntent("mix-" + fingerprint["sha256:".Length..], fingerprint,
            Path.GetFullPath(filePath), report.Digest, fileBytes, Path.GetFullPath(sqlitePath), sqliteTable,
            fileKind);
    }

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);
}

/// <summary>Persisted independent state of both publication destinations.</summary>
public sealed record MixedPublicationRecord(
    string OperationKey,
    string IntentFingerprint,
    string FilePath,
    string FileDigest,
    DestinationPublicationState FileState,
    string? FileFailureCode,
    string SqlitePath,
    string SqliteTable,
    string? SqliteOperationKey,
    string? SqliteIntentFingerprint,
    DestinationPublicationState SqliteState,
    int? SqliteRowsWritten,
    string? SqliteFailureCode,
    DateTimeOffset UpdatedUtc,
    string FileKind = "xbrl-json")
{
    public IReadOnlyList<DestinationPublicationOutcome> ToManifestOutcomes() => new[]
    {
        new DestinationPublicationOutcome(FileKind, FilePath, FileState,
            FileState == DestinationPublicationState.Confirmed ? FileDigest : null,
            FailureCode: FileFailureCode),
        new DestinationPublicationOutcome("sqlite", SqlitePath, SqliteState,
            OperationKey: SqliteOperationKey, RowsWritten: SqliteRowsWritten,
            FailureCode: SqliteFailureCode, IntentFingerprint: SqliteIntentFingerprint)
    };
}

/// <summary>SQLite-backed intent and outcome journal for non-atomic mixed publication.</summary>
public sealed class MixedPublicationStore
{
    private readonly LocalApplicationDatabase _database;

    public MixedPublicationStore(LocalApplicationDatabase database) => _database = database;

    public MixedPublicationRecord GetOrCreate(MixedPublicationIntent intent)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO mixed_publication_reconciliation(
                    operation_key,intent_fingerprint,file_path,file_digest,file_kind,file_state,file_failure_code,
                    sqlite_path,sqlite_table,sqlite_operation_key,sqlite_intent_fingerprint,
                    sqlite_state,sqlite_rows_written,sqlite_failure_code,updated_utc)
                VALUES($key,$intent,$file,$digest,$fileKind,'Pending',NULL,$sqlite,$table,NULL,NULL,'Pending',NULL,NULL,$updated)
                ON CONFLICT(operation_key) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$key", intent.OperationKey);
            insert.Parameters.AddWithValue("$intent", intent.Fingerprint);
            insert.Parameters.AddWithValue("$file", intent.FilePath);
            insert.Parameters.AddWithValue("$digest", intent.FileDigest);
            insert.Parameters.AddWithValue("$fileKind", intent.FileKind);
            insert.Parameters.AddWithValue("$sqlite", intent.SqlitePath);
            insert.Parameters.AddWithValue("$table", intent.SqliteTable);
            insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
        var record = Load(connection, transaction, intent.OperationKey)
            ?? throw new InvalidDataException("Mixed publication intent was not persisted.");
        if (!StringComparer.Ordinal.Equals(record.IntentFingerprint, intent.Fingerprint)
            || !PathComparer.Equals(record.FilePath, intent.FilePath)
            || !StringComparer.Ordinal.Equals(record.FileDigest, intent.FileDigest)
            || !StringComparer.Ordinal.Equals(record.FileKind, intent.FileKind)
            || !PathComparer.Equals(record.SqlitePath, intent.SqlitePath)
            || !StringComparer.Ordinal.Equals(record.SqliteTable, intent.SqliteTable))
            throw new InvalidDataException("Mixed publication key is already bound to a different intent.");
        transaction.Commit();
        return record;
    }

    public MixedPublicationRecord? Find(string operationKey)
    {
        using var connection = _database.OpenConnection();
        return Load(connection, null, operationKey);
    }

    public MixedPublicationRecord SetFile(string operationKey, DestinationPublicationState state, string? failureCode = null)
        => Update(operationKey, "file_state=$state,file_failure_code=$failure", state, failureCode);

    public MixedPublicationRecord SetSqlitePrepared(string operationKey, string sqliteOperationKey, string intentFingerprint)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = Load(connection, transaction, operationKey)
            ?? throw new InvalidDataException("Mixed publication intent was not found.");
        if (current.SqliteOperationKey is not null
            && (!StringComparer.Ordinal.Equals(current.SqliteOperationKey, sqliteOperationKey)
                || !StringComparer.Ordinal.Equals(current.SqliteIntentFingerprint, intentFingerprint)))
            throw new InvalidDataException("SQLite retry changed the durable operation key or input intent.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE mixed_publication_reconciliation SET sqlite_operation_key=$dbkey,sqlite_intent_fingerprint=$dbintent,sqlite_state='InProgress',sqlite_failure_code=NULL,updated_utc=$updated WHERE operation_key=$key";
        command.Parameters.AddWithValue("$dbkey", sqliteOperationKey);
        command.Parameters.AddWithValue("$dbintent", intentFingerprint);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$key", operationKey);
        command.ExecuteNonQuery();
        transaction.Commit();
        return Find(operationKey)!;
    }

    public MixedPublicationRecord SetSqlite(
        string operationKey,
        DestinationPublicationState state,
        int? rowsWritten = null,
        string? failureCode = null)
        => Update(operationKey,
            "sqlite_state=$state,sqlite_rows_written=$rows,sqlite_failure_code=$failure",
            state, failureCode, rowsWritten);

    private MixedPublicationRecord Update(
        string operationKey,
        string assignment,
        DestinationPublicationState state,
        string? failureCode,
        int? rowsWritten = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE mixed_publication_reconciliation SET {assignment},updated_utc=$updated WHERE operation_key=$key";
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$failure", (object?)failureCode ?? DBNull.Value);
        if (assignment.Contains("sqlite_rows_written", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$rows", (object?)rowsWritten ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$key", operationKey);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Mixed publication intent was not found.");
        return Find(operationKey)!;
    }

    private static MixedPublicationRecord? Load(SqliteConnection connection, SqliteTransaction? transaction, string operationKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_key,intent_fingerprint,file_path,file_digest,file_state,file_failure_code,
                   sqlite_path,sqlite_table,sqlite_operation_key,sqlite_intent_fingerprint,
                   sqlite_state,sqlite_rows_written,sqlite_failure_code,updated_utc,file_kind
            FROM mixed_publication_reconciliation WHERE operation_key=$key
            """;
        command.Parameters.AddWithValue("$key", operationKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new MixedPublicationRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            Enum.Parse<DestinationPublicationState>(reader.GetString(4)), NullableString(reader, 5),
            reader.GetString(6), reader.GetString(7), NullableString(reader, 8), NullableString(reader, 9),
            Enum.Parse<DestinationPublicationState>(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetInt32(11), NullableString(reader, 12),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture), reader.GetString(14));
    }

    private static string? NullableString(SqliteDataReader reader, int column)
        => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public sealed record MixedPublicationAttempt(MixedPublicationRecord Record, Exception? Failure);

/// <summary>
/// Coordinates two independent destinations. Each leg is persisted before writing;
/// SQLite retries reuse its receipt-bound operation key and file retries never replace
/// different bytes.
/// </summary>
public sealed class MixedPublicationCoordinator
{
    private readonly MixedPublicationStore _store;

    public MixedPublicationCoordinator(MixedPublicationStore store) => _store = store;

    public async Task<MixedPublicationAttempt> PublishAsync(
        MixedPublicationIntent intent,
        Func<CancellationToken, Task<PublicationReceipt>> publishFileAsync,
        Func<Action<string, string>, CancellationToken, Task<SqlitePublishReceipt>> publishSqliteAsync,
        CancellationToken cancellationToken = default)
    {
        var record = _store.GetOrCreate(intent);
        var fileMatches = TryDigest(intent.FilePath, out var currentDigest)
            && StringComparer.Ordinal.Equals(currentDigest, intent.FileDigest);
        if (fileMatches)
        {
            record = _store.SetFile(intent.OperationKey, DestinationPublicationState.Confirmed);
        }
        else if (record.FileState is DestinationPublicationState.InProgress
            or DestinationPublicationState.Confirmed or DestinationPublicationState.Unknown)
        {
            record = _store.SetFile(intent.OperationKey, DestinationPublicationState.Unknown, DiagnosticCodes.PubPartialFailure);
            return new MixedPublicationAttempt(record, new PublicationException(
                DiagnosticCodes.PubPartialFailure,
                "The prior file outcome cannot be confirmed from the destination digest; reconcile the file before retrying.",
                intent.OperationKey, intent.FilePath));
        }
        else
        {
            record = _store.SetFile(intent.OperationKey, DestinationPublicationState.InProgress);
            try
            {
                var receipt = await publishFileAsync(cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(receipt.FinalDigest, intent.FileDigest)
                    || !StringComparer.Ordinal.Equals(receipt.DestinationPath, intent.FilePath))
                    throw new PublicationException(DiagnosticCodes.PubDigestMismatch,
                        "The file publisher receipt does not match the mixed publication intent.",
                        intent.OperationKey, intent.FilePath);
                record = _store.SetFile(intent.OperationKey, DestinationPublicationState.Confirmed);
            }
            catch (Exception ex) when (IsPublicationFailure(ex))
            {
                record = _store.SetFile(intent.OperationKey, Classify(ex), Code(ex));
                return new MixedPublicationAttempt(record, ex);
            }
        }

        if (record.SqliteState == DestinationPublicationState.Confirmed)
            return new MixedPublicationAttempt(record, null);

        record = _store.SetSqlite(intent.OperationKey, DestinationPublicationState.InProgress);
        try
        {
            string? preparedOperationKey = null;
            string? preparedIntentFingerprint = null;
            var receipt = await publishSqliteAsync(
                (operationKey, fingerprint) =>
                {
                    _ = _store.SetSqlitePrepared(intent.OperationKey, operationKey, fingerprint);
                    preparedOperationKey = operationKey;
                    preparedIntentFingerprint = fingerprint;
                },
                cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(receipt.OperationKey, preparedOperationKey)
                || !StringComparer.Ordinal.Equals(receipt.IntentFingerprint, preparedIntentFingerprint))
                throw new PublicationException(DiagnosticCodes.PubReceiptConflict,
                    "The SQLite publisher receipt does not match the persisted mixed publication intent.",
                    intent.OperationKey, intent.SqlitePath);
            record = _store.SetSqlite(intent.OperationKey, DestinationPublicationState.Confirmed, receipt.WrittenRows);
            return new MixedPublicationAttempt(record, null);
        }
        catch (Exception ex) when (IsPublicationFailure(ex))
        {
            record = _store.SetSqlite(intent.OperationKey, Classify(ex), failureCode: Code(ex));
            return new MixedPublicationAttempt(record, ex);
        }
    }

    public async Task<MixedPublicationRecord?> ReconcileAsync(string operationKey, CancellationToken cancellationToken = default)
    {
        var record = _store.Find(operationKey);
        if (record is null) return null;

        if (TryDigest(record.FilePath, out var fileDigest)
            && StringComparer.Ordinal.Equals(fileDigest, record.FileDigest))
            record = _store.SetFile(operationKey, DestinationPublicationState.Confirmed);
        else if (record.FileState is DestinationPublicationState.InProgress
            or DestinationPublicationState.Confirmed or DestinationPublicationState.Unknown)
            record = _store.SetFile(operationKey, DestinationPublicationState.Unknown, DiagnosticCodes.PubPartialFailure);

        if (record.SqliteOperationKey is null || record.SqliteIntentFingerprint is null)
        {
            if (record.SqliteState is DestinationPublicationState.InProgress or DestinationPublicationState.Unknown)
                record = _store.SetSqlite(operationKey, DestinationPublicationState.Rejected, failureCode: "PUB_NOT_ATTEMPTED");
            return record;
        }

        try
        {
            var destination = new SourceConnection("destination", "sqlite", record.SqlitePath,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["table"] = record.SqliteTable });
            var (state, receipt) = await new SqliteTargetPublisher().ReconcileReceiptAsync(
                destination, record.SqliteOperationKey, record.SqliteIntentFingerprint, cancellationToken).ConfigureAwait(false);
            record = state switch
            {
                SqliteReceiptReconciliation.Found => _store.SetSqlite(operationKey,
                    DestinationPublicationState.Confirmed, receipt!.WrittenRows),
                SqliteReceiptReconciliation.NotFound => _store.SetSqlite(operationKey,
                    DestinationPublicationState.Rejected, failureCode: "PUB_NOT_COMMITTED"),
                SqliteReceiptReconciliation.TargetUnavailable => _store.SetSqlite(operationKey,
                    DestinationPublicationState.Unknown, failureCode: DiagnosticCodes.PubPartialFailure),
                _ => _store.SetSqlite(operationKey, DestinationPublicationState.Unknown,
                    failureCode: DiagnosticCodes.PubReceiptConflict)
            };
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            record = _store.SetSqlite(operationKey, DestinationPublicationState.Unknown, failureCode: DiagnosticCodes.PubPartialFailure);
        }
        return record;
    }

    private static bool TryDigest(string path, out string? digest)
    {
        try
        {
            digest = File.Exists(path) ? DestinationLock.DigestFile(path) : null;
            return digest is not null;
        }
        catch (IOException) { digest = null; return false; }
        catch (UnauthorizedAccessException) { digest = null; return false; }
    }

    private static bool IsPublicationFailure(Exception ex)
        => ex is PublicationException or AdapterException or SqliteException or IOException
            or UnauthorizedAccessException or OperationCanceledException;

    private static DestinationPublicationState Classify(Exception ex)
        => ex is OperationCanceledException or SqliteException
            || ex is PublicationException publication && publication.Code is DiagnosticCodes.PubPartialFailure or DiagnosticCodes.PubDigestMismatch
            || ex is IOException or UnauthorizedAccessException
            ? DestinationPublicationState.Unknown
            : DestinationPublicationState.Rejected;

    private static string Code(Exception ex) => ex switch
    {
        PublicationException publication => publication.Code,
        AdapterException adapter => adapter.Code,
        _ => DiagnosticCodes.PubPartialFailure
    };
}
