namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Checks the well-formed syntax of a BCP 47 language tag (RFC 5646). IANA registry
/// membership is not checked; profile-specific support is enforced separately.
/// </summary>
internal static class Bcp47LanguageTagValidator
{
    private static readonly HashSet<string> GrandfatheredTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "en-GB-oed", "i-ami", "i-bnn", "i-default", "i-enochian", "i-hak",
        "i-klingon", "i-lux", "i-mingo", "i-navajo", "i-pwn", "i-tao", "i-tay",
        "i-tsu", "sgn-BE-FR", "sgn-BE-NL", "sgn-CH-DE", "art-lojban", "cel-gaulish",
        "no-bok", "no-nyn", "zh-guoyu", "zh-hakka", "zh-min", "zh-min-nan", "zh-xiang"
    };

    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (GrandfatheredTags.Contains(value)) return true;

        var subtags = value.Split('-');
        if (subtags.Any(string.IsNullOrEmpty)) return false;

        var index = 0;
        if (IsX(subtags[0]))
        {
            index = 1;
            if (index == subtags.Length) return false;
            return subtags.Skip(index).All(IsPrivateUseSubtag);
        }

        var primary = subtags[index++];
        if (IsAlpha(primary) && primary.Length is >= 2 and <= 3)
        {
            var extlangCount = 0;
            while (index < subtags.Length && extlangCount < 3
                && subtags[index].Length == 3 && IsAlpha(subtags[index]))
            {
                index++;
                extlangCount++;
            }
        }
        else if (!IsAlpha(primary) || primary.Length is < 4 or > 8)
        {
            return false;
        }

        if (index < subtags.Length && subtags[index].Length == 4 && IsAlpha(subtags[index]))
            index++;

        if (index < subtags.Length && IsRegion(subtags[index]))
            index++;

        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < subtags.Length && IsVariant(subtags[index]))
        {
            if (!variants.Add(subtags[index])) return false;
            index++;
        }

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (index < subtags.Length && IsExtensionSingleton(subtags[index]))
        {
            if (!extensions.Add(subtags[index])) return false;
            index++;
            var firstExtensionSubtag = index;
            while (index < subtags.Length && IsExtensionSubtag(subtags[index]))
                index++;
            if (index == firstExtensionSubtag) return false;
        }

        if (index < subtags.Length && IsX(subtags[index]))
        {
            index++;
            var firstPrivateUseSubtag = index;
            while (index < subtags.Length && IsPrivateUseSubtag(subtags[index]))
                index++;
            if (index == firstPrivateUseSubtag) return false;
        }

        return index == subtags.Length;
    }

    private static bool IsAlpha(string value)
        => value.Length > 0 && value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static bool IsDigit(string value)
        => value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static bool IsAlphanumeric(string value)
        => value.Length > 0 && value.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9');

    private static bool IsRegion(string value)
        => value.Length == 2 && IsAlpha(value) || value.Length == 3 && IsDigit(value);

    private static bool IsVariant(string value)
        => value.Length is >= 5 and <= 8 && IsAlphanumeric(value)
            || value.Length == 4 && char.IsAsciiDigit(value[0]) && IsAlphanumeric(value);

    private static bool IsExtensionSingleton(string value)
        => value.Length == 1 && IsAlphanumeric(value) && !IsX(value);

    private static bool IsExtensionSubtag(string value)
        => value.Length is >= 2 and <= 8 && IsAlphanumeric(value);

    private static bool IsPrivateUseSubtag(string value)
        => value.Length is >= 1 and <= 8 && IsAlphanumeric(value);

    private static bool IsX(string value)
        => value.Length == 1 && (value[0] is 'x' or 'X');
}
