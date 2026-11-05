using System.Globalization;
using XBRLMappingStudio.Diagnostics;

namespace XBRLMappingStudio.Adapters.Pdf;

public enum PdfNumericPolicyBasis
{
    ExplicitTemplate,
    ReviewerConfirmed
}

/// <summary>
/// Numeric conventions declared for one PDF table template. Currency, separators,
/// scale and sign conventions are all required; there is no implicit USD or locale.
/// </summary>
public sealed record PdfNumericFormatPolicy(
    char DecimalSeparator,
    char? GroupSeparator,
    bool AllowGrouping,
    int ScaleFactor,
    string CurrencyCode,
    bool AllowLeadingSign,
    bool AllowParenthesizedNegative,
    bool AllowTrailingMinus,
    PdfNumericPolicyBasis Basis)
{
    public PdfNumericFormatPolicy Validate()
    {
        if (char.IsAsciiDigit(DecimalSeparator) || char.IsWhiteSpace(DecimalSeparator)
            || DecimalSeparator is '+' or '-' or '(' or ')' or '\0'
            || (GroupSeparator.HasValue && (GroupSeparator.Value == DecimalSeparator
                || char.IsAsciiDigit(GroupSeparator.Value)
                || GroupSeparator.Value is '+' or '-' or '(' or ')' or '\0'))
            || (AllowGrouping && !GroupSeparator.HasValue)
            || ScaleFactor is not (1 or 1000 or 1_000_000)
            || CurrencyCode is null || CurrencyCode.Length != 3
            || CurrencyCode.Any(character => character is < 'A' or > 'Z')
            || Basis is not (PdfNumericPolicyBasis.ExplicitTemplate or PdfNumericPolicyBasis.ReviewerConfirmed))
        {
            throw new AdapterException(DiagnosticCodes.IoPdfNumericPolicyInvalid,
                "The PDF numeric policy is incomplete or outside the supported bounds.",
                DiagnosticStage.Validation);
        }

        return this;
    }
}

public enum PdfNumericParseState
{
    Empty,
    ParsedRequiresReview,
    Rejected
}

/// <summary>
/// Exact decimal interpretation linked to the raw table-cell text and its PDF region.
/// A successful parse is still unapproved until an analyst reviews the occurrence.
/// </summary>
public sealed record PdfNumericParseResult(
    PdfNumericParseState State,
    string RawText,
    int ReportingYear,
    decimal? Value,
    string? CanonicalValue,
    string? CurrencyCode,
    int? ScaleFactor,
    PdfNumericPolicyBasis? PolicyBasis,
    PdfEvidenceRegion? Evidence,
    bool RequiresReviewerApproval,
    Diagnostic? Diagnostic);

public static class PdfNumericInterpreter
{
    private const int MaxRawTextLength = 128;

    public static PdfNumericParseResult Parse(
        PdfTableCell cell,
        PdfNumericFormatPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return Parse(cell.RawText, cell.ReportingYear, cell.Evidence, policy);
    }

    public static PdfNumericParseResult Parse(
        string? rawText,
        int reportingYear,
        PdfEvidenceRegion? evidence,
        PdfNumericFormatPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var raw = rawText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new PdfNumericParseResult(PdfNumericParseState.Empty, raw, reportingYear,
                null, null, policy.CurrencyCode, policy.ScaleFactor, policy.Basis, evidence,
                RequiresReviewerApproval: false, Diagnostic: null);
        }

        if (reportingYear is < 1900 or > 2200)
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                "The PDF table cell has an invalid reporting year.");
        if (raw.Length > MaxRawTextLength)
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                "The PDF numeric text exceeds the supported length.");

        var token = raw.Trim();
        var negative = false;
        var hasParentheses = token.StartsWith('(') || token.EndsWith(')');
        if (hasParentheses)
        {
            if (!policy.AllowParenthesizedNegative
                || token.Length < 3 || token[0] != '(' || token[^1] != ')')
            {
                return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericAmbiguous,
                    "Parentheses do not match the declared PDF negative-number policy.");
            }
            token = token[1..^1];
            negative = true;
        }

        var hasTrailingMinus = token.EndsWith('-');
        if (hasTrailingMinus)
        {
            if (!policy.AllowTrailingMinus || negative)
                return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericAmbiguous,
                    "A trailing minus is not allowed by the declared PDF numeric policy.");
            token = token[..^1];
            negative = true;
        }

        var leadingSign = token.Length > 0 && token[0] is '+' or '-';
        if (leadingSign)
        {
            if (!policy.AllowLeadingSign || negative)
                return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericAmbiguous,
                    "The leading sign is not allowed by the declared PDF numeric policy.");
            negative = token[0] == '-';
            token = token[1..];
        }

        if (token.Length == 0 || token.Any(character =>
                character != policy.DecimalSeparator
                && character != policy.GroupSeparator
                && !IsAsciiDigit(character)))
        {
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                "The PDF cell contains text outside the declared numeric syntax.");
        }

        var decimalParts = token.Split(policy.DecimalSeparator);
        if (decimalParts.Length > 2 || decimalParts.Any(part => part.Length == 0))
        {
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericAmbiguous,
                "The PDF value has an empty or repeated decimal separator.");
        }

        var integerPart = decimalParts[0];
        var fractionalPart = decimalParts.Length == 2 ? decimalParts[1] : null;
        if (fractionalPart?.Length > 28)
        {
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                "The PDF value exceeds the supported exact decimal precision.");
        }
        if (fractionalPart?.Contains(policy.GroupSeparator ?? '\0') == true)
        {
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericAmbiguous,
                "A grouping separator appears in the fractional part.");
        }

        string normalizedInteger;
        if (policy.GroupSeparator.HasValue && integerPart.Contains(policy.GroupSeparator.Value))
        {
            if (!policy.AllowGrouping)
            {
                return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericAmbiguous,
                    "Grouping separators are not allowed by the declared PDF numeric policy.");
            }

            var groups = integerPart.Split(policy.GroupSeparator.Value);
            if (groups.Length < 2 || groups[0].Length is < 1 or > 3
                || groups.Skip(1).Any(group => group.Length != 3 || !AllAsciiDigits(group)))
            {
                return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericAmbiguous,
                    "The PDF grouping separators do not form unambiguous three-digit groups.");
            }
            normalizedInteger = string.Concat(groups);
        }
        else
        {
            if (!AllAsciiDigits(integerPart))
            {
                return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                    "The PDF integer portion is not valid under the declared separator policy.");
            }
            normalizedInteger = integerPart;
        }

        var normalized = (negative ? "-" : string.Empty) + normalizedInteger
            + (fractionalPart is null ? string.Empty : "." + fractionalPart);
        var significantDigits = (normalizedInteger + (fractionalPart ?? string.Empty)).TrimStart('0').Length;
        if (significantDigits > 29)
        {
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                "The PDF value exceeds the supported exact decimal precision.");
        }
        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var unscaled))
        {
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                "The PDF value exceeds the supported exact decimal range.");
        }

        decimal scaled;
        try
        {
            scaled = checked(unscaled * policy.ScaleFactor);
        }
        catch (OverflowException)
        {
            return Reject(raw, reportingYear, evidence, DiagnosticCodes.IoPdfNumericInvalid,
                "The scaled PDF value exceeds the supported exact decimal range.");
        }

        return new PdfNumericParseResult(PdfNumericParseState.ParsedRequiresReview, raw,
            reportingYear, scaled, scaled.ToString(CultureInfo.InvariantCulture),
            policy.CurrencyCode, policy.ScaleFactor, policy.Basis, evidence,
            RequiresReviewerApproval: true, Diagnostic: null);
    }

    private static PdfNumericParseResult Reject(
        string raw,
        int reportingYear,
        PdfEvidenceRegion? evidence,
        string code,
        string message)
        => new(PdfNumericParseState.Rejected, raw, reportingYear,
            null, null, null, null, null, evidence, RequiresReviewerApproval: true,
            new Diagnostic
            {
                Code = code,
                Subsystem = DiagnosticSubsystem.Adapter,
                Severity = DiagnosticSeverity.Error,
                Stage = DiagnosticStage.Validation,
                IsBlocking = true,
                Message = message,
                Source = evidence is null
                    ? null
                    : new SourceReference("pdf", $"page={evidence.PageNumber}"),
            });

    private static bool AllAsciiDigits(string text)
        => text.Length > 0 && text.All(IsAsciiDigit);

    private static bool IsAsciiDigit(char character)
        => character is >= '0' and <= '9';
}
