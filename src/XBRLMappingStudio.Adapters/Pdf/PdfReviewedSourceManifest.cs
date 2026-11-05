namespace XBRLMappingStudio.Adapters.Pdf;

/// <summary>
/// Portable approval envelope consumed by <see cref="PdfSourceAdapter"/>. This is a
/// versioned data contract, not proof of organizational reviewer authority.
/// </summary>
public sealed record PdfReviewedSourceManifest
{
    public required int FormatVersion { get; init; }
    public required string SourceSha256 { get; init; }
    public required string TemplateRevision { get; init; }
    public required string Reviewer { get; init; }
    public required DateTimeOffset ReviewedUtc { get; init; }
    public required bool IsApproved { get; init; }
    public required string RowLabelHeader { get; init; }
    /// <summary>Page whose paired headings seeded the review template; absent in legacy PDF-07 sidecars.</summary>
    public int? TemplatePageNumber { get; init; }
    /// <summary>Declared row direction; absent in legacy PDF-07 sidecars.</summary>
    public bool? RowsAtLowLineCoordinate { get; init; }
    public required PdfNumericFormatPolicy NumericPolicy { get; init; }
    public required IReadOnlyList<PdfPeriodColumnAnchor> PeriodColumns { get; init; }
    public required IReadOnlyList<PdfReviewedSourceRow> Rows { get; init; }
}

public sealed record PdfReviewedSourceRow
{
    public required int Occurrence { get; init; }
    public required string LabelText { get; init; }
    public required string LabelEvidenceId { get; init; }
    public required PdfEvidenceRegion LabelEvidence { get; init; }
    public required IReadOnlyList<PdfReviewedSourceCell> Cells { get; init; }
}

public sealed record PdfReviewedSourceCell
{
    public required int ReportingYear { get; init; }
    public required string RawText { get; init; }
    public required string CanonicalValue { get; init; }
    public required string EvidenceId { get; init; }
    public required PdfEvidenceRegion Evidence { get; init; }
    public required bool IsApproved { get; init; }
    public string? CorrectionReason { get; init; }
}
