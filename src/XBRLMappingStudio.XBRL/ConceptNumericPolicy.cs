using System.Globalization;

namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Collector-declared numeric policy, not a regulatory qualification. Precision counts
/// significant digits of the numeric value (trailing zeroes excluded, zero counts as one).
/// Rounding is validation-only: values must already lie on the declared decimal grid.
/// Accuracy bounds constrain the XBRL decimals assertion, independently of value precision.
/// Nil facts bypass value constraints. No value or accuracy assertion is ever rewritten.
/// </summary>
public sealed record ConceptNumericPolicy
{
    public int? MaximumSignificantDigits { get; init; }
    public int? RoundingDecimals { get; init; }
    public string RoundingMode { get; init; } = "reject";
    public int? MinimumDecimals { get; init; }
    public int? MaximumDecimals { get; init; }
    public bool AllowInfiniteAccuracy { get; init; } = true;

    public bool IsValid(out string reason)
    {
        if (MaximumSignificantDigits is < 1 or > 29
            || RoundingDecimals is < -28 or > 28
            || MinimumDecimals is < -30 or > 30 || MaximumDecimals is < -30 or > 30
            || MinimumDecimals > MaximumDecimals
            || RoundingMode != "reject")
        {
            reason = "Numeric policy has unsupported precision, rounding or accuracy constraints.";
            return false;
        }
        reason = "";
        return true;
    }

    public bool Validate(decimal value, int? decimals, out string reason)
    {
        if (!IsValid(out reason)) return false;
        if (decimals is null ? !AllowInfiniteAccuracy
            : decimals is < -30 or > 30 || decimals < MinimumDecimals || decimals > MaximumDecimals)
        {
            reason = "Numeric fact accuracy is outside the concept policy.";
            return false;
        }
        if (MaximumSignificantDigits is { } maximum)
        {
            var digits = value.ToString("0.############################", CultureInfo.InvariantCulture)
                .Replace("-", "").Replace(".", "").Trim('0');
            if (Math.Max(1, digits.Length) > maximum)
            {
                reason = "Numeric value exceeds the concept significant-digit limit.";
                return false;
            }
        }
        if (RoundingDecimals is { } places)
        {
            var onGrid = places >= 0 ? decimal.Round(value, places) == value
                : value % PowerOfTen(-places) == 0;
            if (!onGrid)
            {
                reason = "Numeric value requires rounding forbidden by the concept policy.";
                return false;
            }
        }
        reason = "";
        return true;
    }

    private static decimal PowerOfTen(int exponent)
    {
        decimal value = 1;
        for (var index = 0; index < exponent; index++) value *= 10;
        return value;
    }
}
