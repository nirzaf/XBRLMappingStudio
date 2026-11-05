using Microsoft.Data.Sqlite;
using XBRLMappingStudio.Application;

namespace XBRLMappingStudio.Ops.Tests;

public sealed class PdfReviewAuditStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xms-pdf-audit-" + Guid.NewGuid().ToString("N"));

    public PdfReviewAuditStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AppendsApprovalCorrectionRejectionAndUndoAsImmutableHistory()
    {
        var path = Path.Combine(_directory, "state.db");
        var store = new PdfReviewAuditStore(new LocalApplicationDatabase(path));
        var source = "sha256:" + new string('a', 64);
        var template = "sha256:" + new string('b', 64);
        var approved = Event(source, template, PdfReviewAuditAction.Approve,
            "1250", null, false, "1250", null, true);
        var corrected = Event(source, template, PdfReviewAuditAction.Correct,
            "1250", null, true, "1250.5", "Transcription corrected against filing", false);
        var reapproved = Event(source, template, PdfReviewAuditAction.Approve,
            "1250.5", "Transcription corrected against filing", false,
            "1250.5", "Transcription corrected against filing", true);
        var rejected = Event(source, template, PdfReviewAuditAction.Reject,
            "1250.5", "Transcription corrected against filing", true,
            "1250.5", "Transcription corrected against filing", false);
        var undone = Event(source, template, PdfReviewAuditAction.Undo,
            "1250.5", "Transcription corrected against filing", false,
            "1250.5", "Transcription corrected against filing", true, rejected.EventId);

        foreach (var entry in new[] { approved, corrected, reapproved, rejected, undone })
            store.Append(entry);

        var history = store.Read(source, template);
        Assert.Equal(new[]
        {
            PdfReviewAuditAction.Approve,
            PdfReviewAuditAction.Correct,
            PdfReviewAuditAction.Approve,
            PdfReviewAuditAction.Reject,
            PdfReviewAuditAction.Undo
        }, history.Select(entry => entry.Action));
        Assert.Equal(undone, history[^1]);
        Assert.Equal(rejected.EventId, history[^1].UndoesEventId);
        Assert.Equal("Transcription corrected against filing", history[1].CorrectionReason);

        using var connection = new LocalApplicationDatabase(path).OpenConnection();
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE pdf_review_audit SET reviewer='changed' WHERE event_id=$id";
        update.Parameters.AddWithValue("$id", approved.EventId);
        Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM pdf_review_audit WHERE event_id=$id";
        delete.Parameters.AddWithValue("$id", approved.EventId);
        Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());

        var duplicateUndo = undone with
        {
            EventId = Guid.NewGuid().ToString("N"),
            OccurredUtc = DateTimeOffset.UtcNow.AddSeconds(1)
        };
        Assert.Throws<InvalidOperationException>(() => store.Append(duplicateUndo));
    }

    private static PdfReviewAuditEvent Event(
        string source,
        string template,
        PdfReviewAuditAction action,
        string previousValue,
        string? previousReason,
        bool previousApproved,
        string value,
        string? reason,
        bool approved,
        string? undoes = null)
        => new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            SourceSha256 = source,
            TemplateRevision = template,
            RowOccurrence = 1,
            ReportingYear = 2025,
            Action = action,
            OriginalRawText = "1,250",
            ParsedValue = "1250",
            PreviousReviewedValue = previousValue,
            PreviousCorrectionReason = previousReason,
            PreviousIsApproved = previousApproved,
            ReviewedValue = value,
            CorrectionReason = reason,
            IsApproved = approved,
            Reviewer = "reviewer-fixture",
            OccurredUtc = DateTimeOffset.UtcNow,
            UndoesEventId = undoes
        };
}
