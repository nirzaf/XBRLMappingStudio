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
        int maxSignificantDigits, out string reason, bool allowAccountingParentheses = false)
    {
        var token = text.Trim();
        if (token.Contains('(') || token.Contains(')'))
        {
            if (!allowAccountingParentheses)
            {
                reason = "Accounting parentheses are not supported by this decimal parser.";
                return false;
            }

            if (token.Length < 3
                || token[0] != '('
                || token[^1] != ')'
                || token[1..^1].Contains('(')
                || token[1..^1].Contains(')'))
            {
                reason = "Accounting parentheses must wrap the complete numeric lexeme exactly once.";
                return false;
            }

            token = token[1..^1].Trim();
            if (!StringComparer.Ordinal.Equals(token, StripOneSign(token, numberFormat)))
            {
                reason = "Accounting parentheses cannot be combined with an explicit positive or negative sign.";
                return false;
            }
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

        if (!HasValidGrouping(token, numberFormat, decimalIndex, out reason))
            return false;

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

    private static bool HasValidGrouping(
        string token,
        NumberFormatInfo numberFormat,
        int decimalIndex,
        out string reason)
    {
        var groupSeparator = numberFormat.NumberGroupSeparator;
        if (groupSeparator.Length == 0)
        {
            reason = string.Empty;
            return true;
        }

        if (decimalIndex >= 0)
        {
            var fractionStart = decimalIndex + numberFormat.NumberDecimalSeparator.Length;
            if (token.IndexOf(groupSeparator, fractionStart, StringComparison.Ordinal) >= 0)
            {
                reason = "Group separators are only valid between integer digits.";
                return false;
            }
        }

        var integerPart = decimalIndex < 0 ? token : token[..decimalIndex];
        if (!integerPart.Contains(groupSeparator, StringComparison.Ordinal))
        {
            reason = string.Empty;
            return true;
        }

        var groupSizes = numberFormat.NumberGroupSizes;
        if (groupSizes.Length == 0)
        {
            reason = "The selected culture does not allow grouped decimal input.";
            return false;
        }

        var groups = integerPart.Split(groupSeparator, StringSplitOptions.None);
        if (groups.Any(group => group.Length == 0 || group.Any(character => !char.IsAsciiDigit(character))))
        {
            reason = "The decimal lexeme contains an empty or non-numeric digit group.";
            return false;
        }

        var groupSizeIndex = 0;
        for (var groupIndex = groups.Length - 1; groupIndex > 0; groupIndex--)
        {
            if (groupSizeIndex >= groupSizes.Length)
                groupSizeIndex = groupSizes.Length - 1;

            var expectedSize = groupSizes[groupSizeIndex];
            if (expectedSize == 0 || groups[groupIndex].Length != expectedSize)
            {
                reason = "The decimal lexeme contains group separators in invalid positions for the selected culture.";
                return false;
            }

            if (groupSizeIndex < groupSizes.Length - 1)
                groupSizeIndex++;
        }

        var leadingGroupLimit = groupSizes[Math.Min(groupSizeIndex, groupSizes.Length - 1)];
        if (leadingGroupLimit > 0 && groups[0].Length > leadingGroupLimit)
        {
            reason = "The leading digit group is too large for the selected culture.";
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

    public static int FractionalScaleOf(decimal value)
        => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    /// <summary>Checks CLR's product against the exact base-10 product without binary floating point.</summary>
    public static bool ProductIsExact(decimal left, decimal right, decimal product)
    {
        var leftCoefficient = GetCoefficient(left, out var leftScale);
        var rightCoefficient = GetCoefficient(right, out var rightScale);
        var productCoefficient = GetCoefficient(product, out var productScale);
        var exactCoefficient = leftCoefficient * rightCoefficient;
        var exactScale = leftScale + rightScale;

        return exactCoefficient * BigInteger.Pow(10, productScale)
            == productCoefficient * BigInteger.Pow(10, exactScale);
    }

    /// <summary>Checks whether a decimal quotient survives an exact base-10 round trip.</summary>
    public static bool QuotientIsExact(decimal numerator, decimal denominator, decimal quotient)
    {
        try
        {
            var roundTrip = quotient * denominator;
            return ProductIsExact(quotient, denominator, roundTrip)
                && roundTrip == numerator;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Divides and rounds the exact base-10 quotient to a declared scale.</summary>
    public static decimal DivideToScale(
        decimal numerator, decimal denominator, int scale, string roundingMode)
    {
        if (scale < 0 || scale > MaximumScale)
            throw new ArgumentOutOfRangeException(nameof(scale));

        var numeratorCoefficient = GetCoefficient(numerator, out var numeratorScale);
        var denominatorCoefficient = GetCoefficient(denominator, out var denominatorScale);
        if (denominatorCoefficient.IsZero)
            throw new DivideByZeroException("A ratio denominator cannot be zero.");

        var negative = numeratorCoefficient.Sign != denominatorCoefficient.Sign;
        var scaledNumerator = BigInteger.Abs(numeratorCoefficient)
            * BigInteger.Pow(10, denominatorScale + scale);
        var scaledDenominator = BigInteger.Abs(denominatorCoefficient)
            * BigInteger.Pow(10, numeratorScale);
        var coefficient = BigInteger.DivRem(scaledNumerator, scaledDenominator, out var remainder);

        var comparison = (remainder * 2).CompareTo(scaledDenominator);
        var roundUp = roundingMode switch
        {
            "HalfUp" => comparison >= 0,
            "HalfEven" => comparison > 0 || (comparison == 0 && !coefficient.IsEven),
            "Truncate" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(roundingMode))
        };
        if (roundUp)
            coefficient++;

        var resultScale = scale;
        var maxCoefficient = (BigInteger.One << 96) - BigInteger.One;
        while (coefficient > maxCoefficient && resultScale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            resultScale--;
        }

        if (coefficient > maxCoefficient)
            throw new OverflowException("The rounded quotient exceeds the CLR decimal coefficient range.");

        var low = unchecked((int)(uint)(coefficient & uint.MaxValue));
        var middle = unchecked((int)(uint)((coefficient >> 32) & uint.MaxValue));
        var high = unchecked((int)(uint)((coefficient >> 64) & uint.MaxValue));
        return new decimal(low, middle, high, negative && !coefficient.IsZero, (byte)resultScale);
    }

    private static BigInteger GetCoefficient(decimal value, out int scale)
    {
        var bits = decimal.GetBits(value);
        scale = (bits[3] >> 16) & 0xFF;
        var coefficient = new BigInteger((uint)bits[0])
            + (new BigInteger((uint)bits[1]) << 32)
            + (new BigInteger((uint)bits[2]) << 64);
        return (bits[3] & int.MinValue) == 0 ? coefficient : -coefficient;
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
