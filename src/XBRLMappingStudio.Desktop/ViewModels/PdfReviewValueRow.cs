using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Desktop.ViewModels;

/// <summary>One proposed numeric cell in the keyboard-accessible PDF review list.</summary>
public sealed class PdfReviewValueRow : ViewModelBase
{
    private bool _isApproved;

    internal PdfReviewValueRow(
        PdfTableRow sourceRow,
        PdfTableCell sourceCell,
        PdfNumericParseResult parseResult,
        bool templateIsAnchored)
    {
        SourceRow = sourceRow;
        SourceCell = sourceCell;
        ParseResult = parseResult;
        TemplateIsAnchored = templateIsAnchored;
    }

    internal PdfTableRow SourceRow { get; }
    internal PdfTableCell SourceCell { get; }
    internal PdfNumericParseResult ParseResult { get; }
    private bool TemplateIsAnchored { get; }

    public string RowLabel => SourceRow.LabelText;
    public int ReportingYear => SourceCell.ReportingYear;
    public int PageNumber => SourceCell.Evidence.PageNumber;
    public string RawText => SourceCell.RawText;
    public string ProposedValue => ParseResult.CanonicalValue ?? "Unresolved";
    public PdfEvidenceRegion Evidence => SourceCell.Evidence;
    public bool CanApprove => TemplateIsAnchored
        && ParseResult.State == PdfNumericParseState.ParsedRequiresReview
        && ParseResult.Value is not null;
    public string ParseStatus => CanApprove
        ? "Parsed; reviewer approval required"
        : ParseResult.State == PdfNumericParseState.Empty
            ? "Empty cells cannot be approved as numeric values"
            : ParseResult.Diagnostic?.Message ?? "Value needs review";
    public string Display => $"{RowLabel} · {ReportingYear}: {RawText} → {ProposedValue}";
    public string EvidenceSummary => $"PDF page {PageNumber}; normalized region "
        + $"{Evidence.LeftNormalized:P1}, {Evidence.BottomNormalized:P1} to "
        + $"{Evidence.RightNormalized:P1}, {Evidence.TopNormalized:P1}. {ParseStatus}";

    public bool IsApproved
    {
        get => _isApproved;
        set
        {
            if (value && !CanApprove) return;
            if (Set(ref _isApproved, value)) Raise(nameof(ApprovalStatus));
        }
    }

    public string ApprovalStatus => IsApproved ? "Approved" : "Not approved";
}

public sealed record PdfReviewWord(string Text, string AccessibleName);
