using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Desktop.ViewModels;

/// <summary>One proposed numeric cell in the keyboard-accessible PDF review list.</summary>
public sealed class PdfReviewValueRow : ViewModelBase
{
    private bool _isApproved;
    private string _reviewedValue;
    private string? _correctionReason;
    private string _correctionValue = "";
    private string _correctionReasonInput = "";

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
        _reviewedValue = parseResult.CanonicalValue ?? "";
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
    public string ReviewedValue => _reviewedValue;
    public string? CorrectionReason => _correctionReason;
    public string CorrectionValue
    {
        get => _correctionValue;
        set
        {
            if (!Set(ref _correctionValue, value)) return;
            Raise(nameof(CanApplyCorrection));
            Raise(nameof(HasPendingCorrectionDraft));
        }
    }
    public string CorrectionReasonInput
    {
        get => _correctionReasonInput;
        set
        {
            if (!Set(ref _correctionReasonInput, value)) return;
            Raise(nameof(CanApplyCorrection));
            Raise(nameof(HasPendingCorrectionDraft));
        }
    }
    public PdfEvidenceRegion Evidence => SourceCell.Evidence;
    public bool CanApprove => TemplateIsAnchored
        && ParseResult.State == PdfNumericParseState.ParsedRequiresReview
        && ParseResult.Value is not null
        && IsCanonicalDecimal(_reviewedValue)
        && (StringComparer.Ordinal.Equals(_reviewedValue, ProposedValue)
            || !string.IsNullOrWhiteSpace(_correctionReason));
    public bool CanApplyCorrection => TemplateIsAnchored && ParseResult.Value is not null
        && IsCanonicalDecimal(CorrectionValue)
        && !StringComparer.Ordinal.Equals(CorrectionValue, _reviewedValue)
        && !string.IsNullOrWhiteSpace(CorrectionReasonInput)
        && CorrectionReasonInput.Trim().Length <= 2_048;
    public bool HasPendingCorrectionDraft => !string.IsNullOrWhiteSpace(CorrectionValue)
        || !string.IsNullOrWhiteSpace(CorrectionReasonInput);
    public bool IsCorrected => !StringComparer.Ordinal.Equals(_reviewedValue, ProposedValue);
    public string ParseStatus => CanApprove
            ? IsCorrected ? "Corrected value; reviewer approval required" : "Parsed; reviewer approval required"
        : ParseResult.State == PdfNumericParseState.Empty
            ? "Empty cells cannot be approved as numeric values"
            : ParseResult.Diagnostic?.Message ?? "Value needs review";
    public string Display => $"{RowLabel} · {ReportingYear}: {RawText} → {ReviewedValue}"
        + (IsCorrected ? $" (parsed proposal {ProposedValue})" : string.Empty);
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

    public string ApprovalStatus => IsApproved ? "Approved" : IsCorrected ? "Corrected; not approved" : "Not approved";

    internal void SetReviewedState(string value, string? reason, bool isApproved)
    {
        if (Set(ref _reviewedValue, value))
        {
            Raise(nameof(ReviewedValue));
            Raise(nameof(IsCorrected));
            Raise(nameof(Display));
            Raise(nameof(CanApprove));
            Raise(nameof(ParseStatus));
        }
        if (Set(ref _correctionReason, reason))
        {
            Raise(nameof(CorrectionReason));
            Raise(nameof(CanApprove));
            Raise(nameof(IsCorrected));
            Raise(nameof(Display));
            Raise(nameof(ApprovalStatus));
        }
        if (Set(ref _isApproved, isApproved, nameof(IsApproved))) Raise(nameof(IsApproved));
        Raise(nameof(ApprovalStatus));
    }

    internal void ClearCorrectionDraft()
    {
        CorrectionValue = "";
        CorrectionReasonInput = "";
    }

    internal static bool IsCanonicalDecimal(string? value)
        => value is not null
            && decimal.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign
                    | System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && StringComparer.Ordinal.Equals(parsed.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
}

public sealed record PdfReviewWord(string Text, string AccessibleName);
