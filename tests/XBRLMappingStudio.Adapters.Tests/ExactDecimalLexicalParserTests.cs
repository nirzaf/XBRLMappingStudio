using System.Globalization;
using System.Numerics;

namespace XBRLMappingStudio.Adapters.Tests;

public sealed class ExactDecimalLexicalParserTests
{
    [Fact]
    public void DecimalCoefficientBoundaries_AreExactAtEverySupportedScale()
    {
        const string maximum = "79228162514264337593543950335";
        const string aboveMaximum = "79228162514264337593543950336";
        for (byte scale = 0; scale <= 28; scale++)
        {
            foreach (var negative in new[] { false, true })
            {
                var sign = negative ? "-" : "";
                var expected = new decimal(-1, -1, -1, negative, scale);
                Assert.True(ExactDecimalLexicalParser.TryParseExact($"{sign}{maximum}e-{scale}", true, out var actual));
                Assert.Equal(expected, actual);
                Assert.True(ExactDecimalLexicalParser.TryParseExact($"{sign}{maximum}000e-{scale + 3}", true, out actual));
                Assert.Equal(expected, actual);
                Assert.False(ExactDecimalLexicalParser.TryParseExact($"{sign}{aboveMaximum}e-{scale}", true, out _));
            }
        }
    }

    [Fact]
    public void BoundedCorpus_AgreesWithIndependentBigIntegerOracle()
    {
        string[] coefficients =
        [
            "0", "1", "5", "9", "10", "1000", "12345000",
            "79228162514264337593543950334", "79228162514264337593543950335",
            "79228162514264337593543950336", "81234567890123456789012345678",
            "100000000000000000000000000000"
        ];
        int[] fractions = [0, 1, 27, 28, 29, 30, 60];
        int[] exponents = [-60, -35, -29, -28, -1, 0, 1, 28, 29, 35, 60];
        foreach (var coefficient in coefficients)
            foreach (var fraction in fractions)
                foreach (var exponent in exponents)
                    foreach (var tail in new[] { "", "0", "000" })
                        foreach (var negative in new[] { false, true })
                            CheckAgainstOracle("00" + coefficient + tail, fraction, exponent, negative);

        var random = new Random(17031);
        for (var i = 0; i < 500; i++)
        {
            var digits = new char[random.Next(1, 76)];
            for (var digit = 0; digit < digits.Length; digit++) digits[digit] = (char)('0' + random.Next(10));
            CheckAgainstOracle(new string(digits), random.Next(81), random.Next(-100, 101), random.Next(2) == 0);
        }
    }

    private static void CheckAgainstOracle(string digits, int fractionalDigits, int exponent, bool negative)
    {
        var padded = digits.PadLeft(fractionalDigits + 1, '0');
        var mantissa = fractionalDigits == 0 ? padded : padded.Insert(padded.Length - fractionalDigits, ".");
        var lexical = (negative ? "-" : "+") + mantissa + "E" + (exponent >= 0 ? "+" : "")
            + exponent.ToString(CultureInfo.InvariantCulture);

        // The oracle starts from the generated components, not the production parser
        // or decimal.TryParse. BigInteger work is bounded by this test's small corpus.
        var coefficient = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        var scale = fractionalDigits - exponent;
        if (coefficient.IsZero) scale = 0;
        else
        {
            if (scale < 0)
            {
                coefficient *= BigInteger.Pow(10, -scale);
                scale = 0;
            }
            while (scale > 0 && coefficient % 10 == 0)
            {
                coefficient /= 10;
                scale--;
            }
        }
        var fits = scale <= 28 && coefficient <= (BigInteger.One << 96) - 1;
        var actualFits = ExactDecimalLexicalParser.TryParseExact(lexical, true, out var actual);
        Assert.True(fits == actualFits, $"Exactness mismatch for {lexical}: expected {fits}, got {actualFits}.");
        if (!fits) return;

        var bits = decimal.GetBits(actual);
        var actualCoefficient = new BigInteger((uint)bits[0])
            + (new BigInteger((uint)bits[1]) << 32)
            + (new BigInteger((uint)bits[2]) << 64);
        var actualScale = (bits[3] >> 16) & 0xFF;
        Assert.Equal(coefficient * BigInteger.Pow(10, actualScale), actualCoefficient * BigInteger.Pow(10, scale));
        if (!coefficient.IsZero) Assert.Equal(negative, (bits[3] & int.MinValue) != 0);
    }

    [Fact]
    public void VeryLongZeroTailsAndExponents_DoNotImposeArtificialLimits()
    {
        const int length = 100_000;
        var zeros = new string('0', length);
        var hugeExponent = new string('9', length);
        foreach (var lexical in new[] { $"0e{hugeExponent}", $"-0e-{hugeExponent}", $"0.{zeros}" })
        {
            Assert.True(ExactDecimalLexicalParser.TryParseExact(lexical, true, out var value));
            Assert.Equal(0m, value);
        }
        foreach (var lexical in new[] { $"1e{hugeExponent}", $"1e-{hugeExponent}" })
            Assert.False(ExactDecimalLexicalParser.TryParseExact(lexical, true, out _));
        foreach (var lexical in new[] { $"1e+{zeros}0", $"1{zeros}e-{length}", $"0.{zeros}1e{length + 1}" })
        {
            Assert.True(ExactDecimalLexicalParser.TryParseExact(lexical, true, out var value));
            Assert.Equal(1m, value);
        }
        Assert.False(ExactDecimalLexicalParser.TryParseExact($"0e{hugeExponent}x", true, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("+.")]
    [InlineData("1.2.3")]
    [InlineData("--1")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e-")]
    [InlineData("1e1.5")]
    [InlineData("1e1e1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,000")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("١")]
    public void InvalidGrammar_IsRejected(string lexical)
        => Assert.False(ExactDecimalLexicalParser.TryParseExact(lexical, true, out _));

    [Theory]
    [InlineData("1e0")]
    [InlineData("0e999")]
    [InlineData("-1E-2")]
    public void ExponentGrammar_CanBeDisabled(string lexical)
        => Assert.False(ExactDecimalLexicalParser.TryParseExact(lexical, false, out _));
}
