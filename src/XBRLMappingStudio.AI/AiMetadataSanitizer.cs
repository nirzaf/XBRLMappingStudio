using System.Text.RegularExpressions;

namespace XBRLMappingStudio.AI;

/// <summary>Redacts value-like and credential-like text before AI transport or audit storage.</summary>
internal static class AiMetadataSanitizer
{
    private static readonly Regex Controls = new("[\\u0000-\\u001F\\u007F]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CredentialAssignments = new(
        @"(?i)\b(?:password|passwd|token|secret|api[_-]?key|authorization)\s*[:=]\s*[^\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BearerCredential = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CommonCredential = new(
        @"(?i)\b(?:sk-(?:live|test)-|gh[pousr]_|xox[baprs]-|AKIA)[A-Za-z0-9_-]{12,}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EmailAddress = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ValueLikeNumber = new(
        @"(?<![\p{L}\d])(?:[$€£¥]\s*)?-?(?:\d{1,3}(?:,\d{3})+|\d{4,})(?:\.\d+)?%?(?![\p{L}\d])|(?<![\p{L}\d])(?:[$€£¥]\s*)?-?\d+\.\d{2,}%?(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeUnitToken = new("^[A-Za-z][A-Za-z0-9._:/-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Redact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[redacted]";
        var text = Controls.Replace(value, " ");
        text = CredentialAssignments.Replace(text, "[redacted]");
        text = BearerCredential.Replace(text, "[redacted]");
        text = CommonCredential.Replace(text, "[redacted]");
        text = EmailAddress.Replace(text, "[redacted]");
        text = ValueLikeNumber.Replace(text, "[redacted]");
        text = Whitespace.Replace(text, " ").Trim();
        if (text.Length > 256) text = text[..256];
        return string.IsNullOrWhiteSpace(text) ? "[redacted]" : text;
    }

    public static string? SafeUnit(string? value, IReadOnlySet<string> supportedUnits) =>
        value is not null
            && supportedUnits.Contains(value)
            && SafeUnitToken.IsMatch(value)
            && StringComparer.Ordinal.Equals(value, Redact(value))
                ? value
                : null;

    public static string SafeDataType(string value) => value.ToLowerInvariant() switch
    {
        "monetary" => "monetary",
        "shares" => "shares",
        "pure" => "pure",
        "decimal" => "decimal",
        "integer" => "integer",
        "boolean" => "boolean",
        "string" => "string",
        "date" => "date",
        "datetime" => "dateTime",
        _ => "other"
    };

    public static string SafePeriodType(string value) => value switch
    {
        "instant" => "instant",
        "duration" => "duration",
        _ => "other"
    };
}
