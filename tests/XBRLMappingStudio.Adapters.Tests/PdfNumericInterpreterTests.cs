using XBRLMappingStudio.Adapters.Pdf;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class PdfNumericInterpreterTests
{
    private static readonly PdfEvidenceRegion Evidence = new(2, 0.4, 0.6, 0.5, 0.62);

    [Fact]
    public void ExplicitLocaleScaleAndCurrency_PreserveExactDecimalAndSourceEvidence()
    {
        var cell = new PdfTableCell(2025, "1,250.75", Evidence);

        var result = PdfNumericInterpreter.Parse(cell, Policy(scaleFactor: 1000));

        Assert.Equal(PdfNumericParseState.ParsedRequiresReview, result.State);
        Assert.Equal(1_250_750.00m, result.Value);
        Assert.Equal("QAR", result.CurrencyCode);
        Assert.Equal(2025, result.ReportingYear);
        Assert.Equal(1000, result.ScaleFactor);
        Assert.Equal(Evidence, result.Evidence);
        Assert.True(result.RequiresReviewerApproval);
    }

    [Fact]
    public void ParenthesesAreNegativeOnlyWhenTheTemplateAllowsThem()
    {
        var accepted = PdfNumericInterpreter.Parse("(1,250.75)", 2025, Evidence, Policy());
        var rejected = PdfNumericInterpreter.Parse("(1,250.75)", 2025, Evidence,
            Policy(allowParentheses: false));

        Assert.Equal(-1_250.75m, accepted.Value);
        Assert.Equal(PdfNumericParseState.Rejected, rejected.State);
        Assert.Equal(DiagnosticCodes.IoPdfNumericAmbiguous, rejected.Diagnostic!.Code);
    }

    [Fact]
    public void RemovedNegativeSignChangesTheProposalAndStillRequiresReviewerAcknowledgment()
    {
        var negative = PdfNumericInterpreter.Parse("(125.00)", 2025, Evidence, Policy());
        var signMissing = PdfNumericInterpreter.Parse("125.00", 2025, Evidence, Policy());

        Assert.Equal(-125.00m, negative.Value);
        Assert.Equal(125.00m, signMissing.Value);
        Assert.True(negative.RequiresReviewerApproval);
        Assert.True(signMissing.RequiresReviewerApproval);
        Assert.NotEqual(negative.CanonicalValue, signMissing.CanonicalValue);
    }

    [Fact]
    public void TrailingMinusAndLeadingMinusRequireExplicitPolicies()
    {
        var trailing = PdfNumericInterpreter.Parse("1,250.50-", 2025, Evidence, Policy());
        var leading = PdfNumericInterpreter.Parse("-1,250.50", 2025, Evidence, Policy());
        var forbidden = PdfNumericInterpreter.Parse("1,250.50-", 2025, Evidence,
            Policy(allowTrailingMinus: false));

        Assert.Equal(-1_250.50m, trailing.Value);
        Assert.Equal(-1_250.50m, leading.Value);
        Assert.Equal(PdfNumericParseState.Rejected, forbidden.State);
    }

    [Fact]
    public void DecimalTieIsPreservedWithoutRounding()
    {
        var result = PdfNumericInterpreter.Parse("1.005", 2025, Evidence, Policy());

        Assert.Equal(1.005m, result.Value);
        Assert.Equal("1.005", result.CanonicalValue);
    }

    [Fact]
    public void ZeroRemainsAValueAndIsNotChangedToAnEmptyCell()
    {
        var result = PdfNumericInterpreter.Parse("0", 2025, Evidence, Policy());

        Assert.Equal(PdfNumericParseState.ParsedRequiresReview, result.State);
        Assert.Equal(0m, result.Value);
        Assert.Equal("0", result.CanonicalValue);
    }

    [Fact]
    public void ExcessiveFractionalPrecisionIsRejectedInsteadOfRounded()
    {
        var result = PdfNumericInterpreter.Parse("0.12345678901234567890123456789",
            2025, Evidence, Policy());

        Assert.Equal(PdfNumericParseState.Rejected, result.State);
        Assert.Equal(DiagnosticCodes.IoPdfNumericInvalid, result.Diagnostic!.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void EmptyCellIsNotConvertedToZeroOrNil()
    {
        var result = PdfNumericInterpreter.Parse("  ", 2025, Evidence, Policy());

        Assert.Equal(PdfNumericParseState.Empty, result.State);
        Assert.Null(result.Value);
        Assert.Null(result.CanonicalValue);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void InvalidGroupingIsRejectedAsAmbiguous()
    {
        var result = PdfNumericInterpreter.Parse("12,34.5", 2025, Evidence, Policy());

        Assert.Equal(PdfNumericParseState.Rejected, result.State);
        Assert.Equal(DiagnosticCodes.IoPdfNumericAmbiguous, result.Diagnostic!.Code);
        Assert.True(result.Diagnostic.IsBlocking);
    }

    [Fact]
    public void CurrencyTextInCellIsNotGuessedFromAHeadingOrDefaulted()
    {
        var result = PdfNumericInterpreter.Parse("QAR 125", 2025, Evidence, Policy());

        Assert.Equal(PdfNumericParseState.Rejected, result.State);
        Assert.Equal(DiagnosticCodes.IoPdfNumericInvalid, result.Diagnostic!.Code);
        Assert.Null(result.CurrencyCode);
    }

    [Fact]
    public void MissingCurrencyPolicyAndScaledOverflowAreBlocking()
    {
        var invalidPolicy = new PdfNumericFormatPolicy('.', ',', true, 1, null!,
            true, true, true, PdfNumericPolicyBasis.ExplicitTemplate);
        var invalid = Assert.Throws<AdapterException>(() => invalidPolicy.Validate());
        var overflow = PdfNumericInterpreter.Parse("79228162514264337593543950335", 2025,
            Evidence, Policy(scaleFactor: 1000));

        Assert.Equal(DiagnosticCodes.IoPdfNumericPolicyInvalid, invalid.Code);
        Assert.Equal(PdfNumericParseState.Rejected, overflow.State);
        Assert.Equal(DiagnosticCodes.IoPdfNumericInvalid, overflow.Diagnostic!.Code);
    }

    private static PdfNumericFormatPolicy Policy(
        int scaleFactor = 1,
        bool allowParentheses = true,
        bool allowTrailingMinus = true)
        => new(
            DecimalSeparator: '.',
            GroupSeparator: ',',
            AllowGrouping: true,
            ScaleFactor: scaleFactor,
            CurrencyCode: "QAR",
            AllowLeadingSign: true,
            AllowParenthesizedNegative: allowParentheses,
            AllowTrailingMinus: allowTrailingMinus,
            Basis: PdfNumericPolicyBasis.ExplicitTemplate);
}
