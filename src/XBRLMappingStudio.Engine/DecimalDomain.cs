using System.Globalization;
using System.Numerics;
using XBRLMappingStudio.Project;

namespace XBRLMappingStudio.Engine;

/// <summary>
/// Exact-decimal boundary for the engine. CLR decimal supports at most 28 fractional
/// places; the engine limits coefficients to 29 digits so parsing never relies on
/// decimal.TryParse rounding a longer financial lexeme.
/// </summary>
internal static class DecimalDomain
{
    public const int MaximumScale = 28;
    public const int MaximumSignificantDigits = 29;

    public const NumberStyles InvariantNumberStyles =
        NumberStyles.AllowLeadingWhite
        | NumberStyles.AllowTrailingWhite
        | NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint;

    public static decimal ParseInvariant(string text)
    {
        if (!TryValidateLexical(text, CultureInfo.InvariantCulture.NumberFormat,
                MaximumSignificantDigits, out var reason))
            throw new NumericOverflowException(reason);

        if (!decimal.TryParse(text, InvariantNumberStyles, CultureInfo.InvariantCulture, out var value))
            throw new NumericOverflowException(
                "The text is not an in-range invariant decimal; use ParseDecimal with an explicit culture for grouped values.");
        return value;
    }

    public static bool TryValidateLexical(string text, NumberFormatInfo numberFormat,
        int maxSignificantDigits, out string reason)
    {
        var token = text.Trim();
        if (token.Contains('(') || token.Contains(')'))
        {
            reason = "Accounting parentheses are not supported; use an explicit negative sign or ChangeSign node.";
            return false;
        }

        token = StripOneSign(token, numberFormat);
        var decimalSeparator = numberFormat.NumberDecimalSeparator;
        var decimalIndex = token.IndexOf(decimalSeparator, StringComparison.Ordinal);
        if (decimalIndex >= 0
            && token.IndexOf(decimalSeparator, decimalIndex + decimalSeparator.Length,
                StringComparison.Ordinal) >= 0)
        {
            reason = "The decimal lexeme contains more than one decimal separator.";
            return false;
        }

        var groupSeparator = numberFormat.NumberGroupSeparator;
        var scaleToken = decimalIndex < 0
            ? string.Empty
            : token[(decimalIndex + decimalSeparator.Length)..];
        if (groupSeparator.Length > 0)
            scaleToken = scaleToken.Replace(groupSeparator, string.Empty, StringComparison.Ordinal);
        var scale = scaleToken.Count(char.IsAsciiDigit);
        if (scale > MaximumScale)
        {
            reason = $"The decimal scale {scale} exceeds the supported maximum {MaximumScale}.";
            return false;
        }

        var digitsToken = token;
        if (groupSeparator.Length > 0)
            digitsToken = digitsToken.Replace(groupSeparator, string.Empty, StringComparison.Ordinal);
        if (decimalSeparator.Length > 0)
            digitsToken = digitsToken.Replace(decimalSeparator, string.Empty, StringComparison.Ordinal);
        var digits = digitsToken.Where(char.IsAsciiDigit).ToArray();
        var firstNonZero = Array.FindIndex(digits, digit => digit != '0');
        var significantDigits = firstNonZero < 0 ? 1 : digits.Length - firstNonZero;
        if (significantDigits > maxSignificantDigits)
        {
            reason = $"The decimal coefficient has {significantDigits} significant digits; "
                + $"the configured maximum is {maxSignificantDigits}.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool FitsSignificantDigitLimit(decimal value, int maxSignificantDigits)
    {
        var bits = decimal.GetBits(value);
        var coefficient = new BigInteger((uint)bits[0])
            + (new BigInteger((uint)bits[1]) << 32)
            + (new BigInteger((uint)bits[2]) << 64);
        var coefficientDigits = coefficient.IsZero
            ? 1
            : coefficient.ToString(CultureInfo.InvariantCulture).Length;
        return coefficientDigits <= maxSignificantDigits;
    }

    public static void ValidateValue(DataValue value, int maxSignificantDigits, string nodeId)
    {
        if (value.Presence != ValuePresence.Present) return;
        if (value.Shape == PortShape.Decimal
            && !FitsSignificantDigitLimit(value.AsDecimal(), maxSignificantDigits))
        {
            throw new NumericOverflowException(
                $"Decimal output on node '{nodeId}' exceeds MaxSignificantDigits={maxSignificantDigits}.");
        }

        if (value.Raw is IReadOnlyList<DataValue> collection)
        {
            foreach (var item in collection)
                ValidateValue(item, maxSignificantDigits, nodeId);
        }
        else if (value.Raw is IReadOnlyDictionary<string, DataValue> fields)
        {
            foreach (var field in fields.Values)
                ValidateValue(field, maxSignificantDigits, nodeId);
        }
    }

    private static string StripOneSign(string token, NumberFormatInfo numberFormat)
    {
        if (numberFormat.NegativeSign.Length > 0
            && token.StartsWith(numberFormat.NegativeSign, StringComparison.Ordinal))
            return token[numberFormat.NegativeSign.Length..];
        if (numberFormat.PositiveSign.Length > 0
            && token.StartsWith(numberFormat.PositiveSign, StringComparison.Ordinal))
            return token[numberFormat.PositiveSign.Length..];
        if (numberFormat.NegativeSign.Length > 0
            && token.EndsWith(numberFormat.NegativeSign, StringComparison.Ordinal))
            return token[..^numberFormat.NegativeSign.Length];
        if (numberFormat.PositiveSign.Length > 0
            && token.EndsWith(numberFormat.PositiveSign, StringComparison.Ordinal))
            return token[..^numberFormat.PositiveSign.Length];
        return token;
    }
}
