namespace XBRLMappingStudio.Adapters;

/// <summary>
/// Proves that an invariant numeric lexeme is exactly representable by CLR decimal.
/// Parsing is linear in the input length and arithmetic is bounded to 96-bit decimal
/// coefficients. Long zero tails and cancelling exponents need no large integers or
/// arbitrary lexical-length limit. Callers retain their own numeric-inference grammar
/// and any stricter lexical digit budget.
/// </summary>
internal static class ExactDecimalLexicalParser
{
    public static bool TryParseExact(string lexical, bool allowExponent, out decimal value)
    {
        value = 0m;
        var token = lexical.AsSpan();
        if (token.IsEmpty) return false;

        var position = 0;
        var negative = false;
        if (token[position] is '+' or '-')
        {
            negative = token[position++] == '-';
            if (position == token.Length) return false;
        }

        var digits = 0;
        var fractionalDigits = 0;
        var afterPoint = false;
        var firstNonzeroPosition = -1;
        var firstNonzeroDigit = 0;
        var lastNonzeroPosition = -1;
        var lastNonzeroDigit = 0;
        while (position < token.Length)
        {
            var ch = token[position];
            if (char.IsAsciiDigit(ch))
            {
                digits++;
                if (afterPoint) fractionalDigits++;
                if (ch != '0')
                {
                    if (firstNonzeroPosition < 0)
                    {
                        firstNonzeroPosition = position;
                        firstNonzeroDigit = digits;
                    }
                    lastNonzeroPosition = position;
                    lastNonzeroDigit = digits;
                }
                position++;
            }
            else if (ch == '.' && !afterPoint)
            {
                afterPoint = true;
                position++;
            }
            else break;
        }
        if (digits == 0) return false;

        long exponent = 0;
        if (allowExponent && position < token.Length && token[position] is 'e' or 'E')
        {
            position++;
            var exponentNegative = false;
            if (position < token.Length && token[position] is '+' or '-')
                exponentNegative = token[position++] == '-';
            var exponentStart = position;
            // The mantissa can offset at most token.Length decimal places. A larger
            // exponent cannot fit a nonzero decimal, regardless of its actual size.
            // Saturation still scans every digit, validating grammar even for zero.
            var exponentLimit = (long)token.Length + 29;
            while (position < token.Length && char.IsAsciiDigit(token[position]))
            {
                exponent = Math.Min(exponentLimit, exponent * 10 + token[position] - '0');
                position++;
            }
            if (position == exponentStart) return false;
            if (exponentNegative) exponent = -exponent;
        }
        if (position != token.Length) return false;

        if (firstNonzeroPosition < 0)
        {
            value = new decimal(0, 0, 0, negative, 0);
            return true;
        }

        var coefficientDigits = lastNonzeroDigit - firstNonzeroDigit + 1;
        var trailingZeros = digits - lastNonzeroDigit;
        var scale = fractionalDigits - exponent - trailingZeros;
        if (coefficientDigits > 29 || scale > 28
            || (scale < 0 && coefficientDigits - scale > 29))
            return false;

        UInt128 coefficient = 0;
        for (var i = firstNonzeroPosition; i <= lastNonzeroPosition; i++)
            if (token[i] != '.') coefficient = coefficient * 10 + (uint)(token[i] - '0');
        while (scale < 0)
        {
            coefficient *= 10;
            scale++;
        }

        var maximumCoefficient = ((UInt128)1 << 96) - 1;
        if (coefficient > maximumCoefficient) return false;

        value = new decimal(unchecked((int)(uint)coefficient),
            unchecked((int)(uint)(coefficient >> 32)),
            unchecked((int)(uint)(coefficient >> 64)), negative, (byte)scale);
        return true;
    }
}
