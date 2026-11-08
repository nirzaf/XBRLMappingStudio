using System.Globalization;
using System.Numerics;
using System.Text;

namespace XBRLMappingStudio.XBRL;

/// <summary>
/// Parses supported XBRL numeric lexemes without accepting the rounding and underflow
/// that decimal.TryParse permits for values outside the CLR decimal domain.
/// </summary>
internal static class XbrlNumericLexicalParser
{
    private const int MaximumLexicalLength = 256;
    private const int MaximumExponentMagnitude = 128;
    private const int MaximumSignificantDigits = 29;
    private const int MaximumScale = 28;

    public static bool TryParseExact(string lexical, out decimal value)
    {
        value = 0m;
        if (lexical.Length > MaximumLexicalLength
            || !decimal.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !TryParseCoefficientAndScale(lexical, out var coefficient, out var scale))
            return false;

        Normalize(ref coefficient, ref scale);
        if (scale > MaximumScale || SignificantDigits(coefficient) > MaximumSignificantDigits)
            return false;

        var bits = decimal.GetBits(parsed);
        var parsedCoefficient = new BigInteger((uint)bits[0])
            + (new BigInteger((uint)bits[1]) << 32)
            + (new BigInteger((uint)bits[2]) << 64);
        if ((bits[3] & int.MinValue) != 0) parsedCoefficient = -parsedCoefficient;
        var parsedScale = (bits[3] >> 16) & 0x7F;
        Normalize(ref parsedCoefficient, ref parsedScale);

        if (coefficient != parsedCoefficient || scale != parsedScale)
            return false;

        value = parsed;
        return true;
    }

    private static bool TryParseCoefficientAndScale(string lexical, out BigInteger coefficient, out int scale)
    {
        coefficient = BigInteger.Zero;
        scale = 0;
        var token = lexical.AsSpan().Trim();
        if (token.IsEmpty) return false;

        var position = 0;
        var negative = false;
        if (token[position] is '+' or '-')
        {
            negative = token[position] == '-';
            position++;
            if (position == token.Length) return false;
        }

        var digits = new StringBuilder(token.Length);
        var integerDigits = ReadDigits(token, ref position, digits);
        var fractionalDigits = 0;
        if (position < token.Length && token[position] == '.')
        {
            position++;
            fractionalDigits = ReadDigits(token, ref position, digits);
        }
        if (integerDigits + fractionalDigits == 0) return false;

        var exponent = 0;
        if (position < token.Length && token[position] is 'e' or 'E')
        {
            position++;
            var exponentNegative = false;
            if (position < token.Length && token[position] is '+' or '-')
            {
                exponentNegative = token[position] == '-';
                position++;
            }

            var exponentDigits = 0;
            while (position < token.Length && char.IsAsciiDigit(token[position]))
            {
                var digit = token[position++] - '0';
                var nextExponent = (long)exponent * 10 + digit;
                if (nextExponent > MaximumExponentMagnitude)
                    return false;
                exponent = (int)nextExponent;
                exponentDigits++;
            }
            if (exponentDigits == 0) return false;
            if (exponentNegative) exponent = -exponent;
        }

        if (position != token.Length) return false;
        if (!BigInteger.TryParse(digits.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out coefficient))
            return false;
        if (negative) coefficient = -coefficient;
        scale = fractionalDigits - exponent;
        return true;
    }

    private static int ReadDigits(ReadOnlySpan<char> token, ref int position, StringBuilder target)
    {
        var count = 0;
        while (position < token.Length && char.IsAsciiDigit(token[position]))
        {
            target.Append(token[position++]);
            count++;
        }
        return count;
    }

    private static void Normalize(ref BigInteger coefficient, ref int scale)
    {
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }
        if (scale < 0)
        {
            coefficient *= BigInteger.Pow(10, -scale);
            scale = 0;
        }
    }

    private static int SignificantDigits(BigInteger coefficient)
        => coefficient.IsZero ? 1 : BigInteger.Abs(coefficient).ToString(CultureInfo.InvariantCulture).Length;
}
