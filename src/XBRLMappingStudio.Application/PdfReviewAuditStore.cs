using System.Globalization;
using Microsoft.Data.Sqlite;

namespace XBRLMappingStudio.Application;

public enum PdfReviewAuditAction
{
    Approve,
    Correct,
    Reject,
    Undo
}

/// <summary>Append-only review history for one occurrence/year under one exact source and template.</summary>
public sealed record PdfReviewAuditEvent
{
    public required string EventId { get; init; }
    public required string SourceSha256 { get; init; }
    public required string TemplateRevision { get; init; }
    public required int RowOccurrence { get; init; }
    public required int ReportingYear { get; init; }
    public required PdfReviewAuditAction Action { get; init; }
    public required string OriginalRawText { get; init; }
    public required string ParsedValue { get; init; }
    public required string PreviousReviewedValue { get; init; }
    public string? PreviousCorrectionReason { get; init; }
    public required bool PreviousIsApproved { get; init; }
    public required string ReviewedValue { get; init; }
    public string? CorrectionReason { get; init; }
    public required bool IsApproved { get; init; }
    public required string Reviewer { get; init; }
    public required DateTimeOffset OccurredUtc { get; init; }
    public string? UndoesEventId { get; init; }
}

/// <summary>
/// Stores durable PDF review decisions locally. Entries are immutable and scoped to the
/// exact source digest, template revision, row occurrence and reporting year.
/// </summary>
public sealed class PdfReviewAuditStore(LocalApplicationDatabase database)
{
    public void Append(PdfReviewAuditEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry);
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (entry.Action == PdfReviewAuditAction.Undo)
        {
            using var undoCheck = connection.CreateCommand();
            undoCheck.Transaction = transaction;
            undoCheck.CommandText = """
                SELECT reviewed_value, correction_reason, is_approved,
                       previous_reviewed_value, previous_correction_reason, previous_is_approved,
                       sequence
                FROM pdf_review_audit
                WHERE event_id=$undo AND source_sha256=$source AND template_revision=$template
                  AND row_occurrence=$occurrence AND reporting_year=$year;
                """;
            undoCheck.Parameters.AddWithValue("$undo", entry.UndoesEventId!);
            undoCheck.Parameters.AddWithValue("$source", entry.SourceSha256);
            undoCheck.Parameters.AddWithValue("$template", entry.TemplateRevision);
            undoCheck.Parameters.AddWithValue("$occurrence", entry.RowOccurrence);
            undoCheck.Parameters.AddWithValue("$year", entry.ReportingYear);
            using var undoReader = undoCheck.ExecuteReader();
            if (!undoReader.Read())
                throw new InvalidOperationException("A PDF review undo must reference an earlier event for the same source, template and cell.");
            if (!StringComparer.Ordinal.Equals(entry.PreviousReviewedValue, undoReader.GetString(0))
                || !StringComparer.Ordinal.Equals(entry.PreviousCorrectionReason, undoReader.IsDBNull(1) ? null : undoReader.GetString(1))
                || entry.PreviousIsApproved != (undoReader.GetInt32(2) != 0)
                || !StringComparer.Ordinal.Equals(entry.ReviewedValue, undoReader.GetString(3))
                || !StringComparer.Ordinal.Equals(entry.CorrectionReason, undoReader.IsDBNull(4) ? null : undoReader.GetString(4))
                || entry.IsApproved != (undoReader.GetInt32(5) != 0))
                throw new InvalidOperationException("A PDF review undo must restore the referenced event's exact previous state.");
            var undoneSequence = undoReader.GetInt64(6);
            undoReader.Close();

            using var latestEventCheck = connection.CreateCommand();
            latestEventCheck.Transaction = transaction;
            latestEventCheck.CommandText = """
                SELECT MAX(sequence) FROM pdf_review_audit
                WHERE source_sha256=$source AND template_revision=$template
                  AND row_occurrence=$occurrence AND reporting_year=$year;
                """;
            latestEventCheck.Parameters.AddWithValue("$source", entry.SourceSha256);
            latestEventCheck.Parameters.AddWithValue("$template", entry.TemplateRevision);
            latestEventCheck.Parameters.AddWithValue("$occurrence", entry.RowOccurrence);
            latestEventCheck.Parameters.AddWithValue("$year", entry.ReportingYear);
            if (Convert.ToInt64(latestEventCheck.ExecuteScalar(), CultureInfo.InvariantCulture) != undoneSequence)
                throw new InvalidOperationException("Only the latest PDF review event for a value can be undone.");

            using var repeatedUndoCheck = connection.CreateCommand();
            repeatedUndoCheck.Transaction = transaction;
            repeatedUndoCheck.CommandText = "SELECT COUNT(*) FROM pdf_review_audit WHERE undoes_event_id=$undo";
            repeatedUndoCheck.Parameters.AddWithValue("$undo", entry.UndoesEventId!);
            if (Convert.ToInt64(repeatedUndoCheck.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("A PDF review audit event can only be undone once.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pdf_review_audit(
                event_id, source_sha256, template_revision, row_occurrence, reporting_year,
                action, original_raw_text, parsed_value, previous_reviewed_value,
                previous_correction_reason, previous_is_approved, reviewed_value,
                correction_reason, is_approved, reviewer, occurred_utc, undoes_event_id)
            VALUES(
                $id, $source, $template, $occurrence, $year, $action, $raw, $parsed,
                $previous_value, $previous_reason, $previous_approved, $value,
                $reason, $approved, $reviewer, $occurred, $undo);
            """;
        command.Parameters.AddWithValue("$id", entry.EventId);
        command.Parameters.AddWithValue("$source", entry.SourceSha256);
        command.Parameters.AddWithValue("$template", entry.TemplateRevision);
        command.Parameters.AddWithValue("$occurrence", entry.RowOccurrence);
        command.Parameters.AddWithValue("$year", entry.ReportingYear);
        command.Parameters.AddWithValue("$action", ActionText(entry.Action));
        command.Parameters.AddWithValue("$raw", entry.OriginalRawText);
        command.Parameters.AddWithValue("$parsed", entry.ParsedValue);
        command.Parameters.AddWithValue("$previous_value", entry.PreviousReviewedValue);
        command.Parameters.AddWithValue("$previous_reason", (object?)entry.PreviousCorrectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous_approved", entry.PreviousIsApproved ? 1 : 0);
        command.Parameters.AddWithValue("$value", entry.ReviewedValue);
        command.Parameters.AddWithValue("$reason", (object?)entry.CorrectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$approved", entry.IsApproved ? 1 : 0);
        command.Parameters.AddWithValue("$reviewer", entry.Reviewer);
        command.Parameters.AddWithValue("$occurred", entry.OccurredUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$undo", (object?)entry.UndoesEventId ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<PdfReviewAuditEvent> Read(string sourceSha256, string templateRevision)
    {
        ValidateDigest(sourceSha256, nameof(sourceSha256));
        ValidateDigest(templateRevision, nameof(templateRevision));
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, source_sha256, template_revision, row_occurrence, reporting_year,
                   action, original_raw_text, parsed_value, previous_reviewed_value,
                   previous_correction_reason, previous_is_approved, reviewed_value,
                   correction_reason, is_approved, reviewer, occurred_utc, undoes_event_id
            FROM pdf_review_audit
            WHERE source_sha256=$source AND template_revision=$template
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$source", sourceSha256);
        command.Parameters.AddWithValue("$template", templateRevision);
        using var reader = command.ExecuteReader();
        var events = new List<PdfReviewAuditEvent>();
        while (reader.Read())
        {
            var entry = new PdfReviewAuditEvent
            {
                EventId = reader.GetString(0),
                SourceSha256 = reader.GetString(1),
                TemplateRevision = reader.GetString(2),
                RowOccurrence = reader.GetInt32(3),
                ReportingYear = reader.GetInt32(4),
                Action = ParseAction(reader.GetString(5)),
                OriginalRawText = reader.GetString(6),
                ParsedValue = reader.GetString(7),
                PreviousReviewedValue = reader.GetString(8),
                PreviousCorrectionReason = reader.IsDBNull(9) ? null : reader.GetString(9),
                PreviousIsApproved = reader.GetInt32(10) != 0,
                ReviewedValue = reader.GetString(11),
                CorrectionReason = reader.IsDBNull(12) ? null : reader.GetString(12),
                IsApproved = reader.GetInt32(13) != 0,
                Reviewer = reader.GetString(14),
                OccurredUtc = DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                UndoesEventId = reader.IsDBNull(16) ? null : reader.GetString(16)
            };
            Validate(entry);
            events.Add(entry);
        }
        return events;
    }

    private static void Validate(PdfReviewAuditEvent entry)
    {
        if (!Guid.TryParseExact(entry.EventId, "N", out _))
            throw new ArgumentException("A PDF review audit event must have a unique compact identifier.", nameof(entry));
        ValidateDigest(entry.SourceSha256, nameof(entry.SourceSha256));
        ValidateDigest(entry.TemplateRevision, nameof(entry.TemplateRevision));
        if (entry.RowOccurrence <= 0 || entry.ReportingYear is < 1900 or > 2200
            || !Enum.IsDefined(entry.Action)
            || string.IsNullOrWhiteSpace(entry.OriginalRawText) || entry.OriginalRawText.Length > 128
            || entry.OriginalRawText.Any(char.IsControl)
            || !IsCanonicalDecimal(entry.ParsedValue)
            || !IsCanonicalDecimal(entry.PreviousReviewedValue)
            || !IsCanonicalDecimal(entry.ReviewedValue)
            || (entry.PreviousCorrectionReason is { Length: > 2048 })
            || (entry.CorrectionReason is { Length: > 2048 })
            || string.IsNullOrWhiteSpace(entry.Reviewer) || entry.Reviewer.Length > 128
            || entry.Reviewer.Any(char.IsControl)
            || entry.OccurredUtc == default || entry.OccurredUtc.Offset != TimeSpan.Zero
            || (entry.Action == PdfReviewAuditAction.Undo
                ? !Guid.TryParseExact(entry.UndoesEventId, "N", out _)
                : entry.UndoesEventId is not null))
            throw new ArgumentException("A PDF review audit event is incomplete or outside the supported bounds.", nameof(entry));
        if (entry.Action == PdfReviewAuditAction.Correct
            && (entry.ReviewedValue == entry.PreviousReviewedValue
                || string.IsNullOrWhiteSpace(entry.CorrectionReason) || entry.IsApproved))
            throw new ArgumentException("A corrected PDF value must change the accepted value, include a reason, and require reapproval.", nameof(entry));
        if (entry.Action == PdfReviewAuditAction.Approve && (entry.IsApproved == false || entry.PreviousIsApproved))
            throw new ArgumentException("A PDF approval event must change a rejected value to approved.", nameof(entry));
        if (entry.Action == PdfReviewAuditAction.Reject && (entry.IsApproved || !entry.PreviousIsApproved))
            throw new ArgumentException("A PDF rejection event must change an approved value to rejected.", nameof(entry));
    }

    private static bool IsCanonicalDecimal(string value)
        => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed)
            && StringComparer.Ordinal.Equals(parsed.ToString(CultureInfo.InvariantCulture), value);

    private static void ValidateDigest(string value, string parameterName)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || !value.AsSpan(7).ToString().All(Uri.IsHexDigit))
            throw new ArgumentException("A PDF review audit digest must be a SHA-256 value.", parameterName);
    }

    private static string ActionText(PdfReviewAuditAction action) => action switch
    {
        PdfReviewAuditAction.Approve => "approve",
        PdfReviewAuditAction.Correct => "correct",
        PdfReviewAuditAction.Reject => "reject",
        PdfReviewAuditAction.Undo => "undo",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static PdfReviewAuditAction ParseAction(string action) => action switch
    {
        "approve" => PdfReviewAuditAction.Approve,
        "correct" => PdfReviewAuditAction.Correct,
        "reject" => PdfReviewAuditAction.Reject,
        "undo" => PdfReviewAuditAction.Undo,
        _ => throw new InvalidDataException("The PDF review audit action is not recognized.")
    };
}
