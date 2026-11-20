using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailCrtFormatTests
{
    [Theory]
    // _cftof/_fptostr: 17 CRT digits, then zero padding and digit-string rounding of specials.
    [InlineData("%f", 0.1f, "0.100000")]
    [InlineData("%.20f", 0.1f, "0.10000000149011612000")]
    [InlineData("%.2f", 0.125f, "0.13")]
    [InlineData("%.0f", 2.5f, "3")]
    [InlineData("%.2f", 0.004f, "0.00")]
    [InlineData("%.2f", 0.006f, "0.01")]
    [InlineData("%.3f", -0f, "0.000")]
    [InlineData("%f", float.PositiveInfinity, "1.#INF00")]
    [InlineData("%.2f", float.PositiveInfinity, "1.#J")]
    [InlineData("%.0f", float.NegativeInfinity, "-1")]
    [InlineData("%e", 1f, "1.000000e+000")]
    [InlineData("%E", -12345.5f, "-1.234550E+004")]
    [InlineData("%+.1e", -0f, "+0.0e+000")]
    [InlineData("%g", 0.0001f, "0.0001")]
    [InlineData("%g", 0.00001f, "1e-005")]
    [InlineData("%g", 123456789f, "1.23457e+008")]
    [InlineData("%G", 100000f, "100000")]
    [InlineData("%#g", 1f, "1.00000")]
    [InlineData("%#.0f", 3f, "3.")]
    [InlineData("%#.0e", -5f, "-5.e+000")]
    [InlineData("%5.1f", 3.14159f, "  3.1")]
    [InlineData("%-6.1f|", 1f, "1.0   |")]
    [InlineData("%06.1f", -1f, "-001.0")]
    [InlineData("% .1f", 2f, " 2.0")]
    // Formats outside the float conversions still read the promoted double's words.
    [InlineData("%2.2", 16.7f, "")]
    [InlineData("%2.2F", 16.7f, "")]
    [InlineData("%", 1f, "")]
    [InlineData("%0.1f units", 4f, "4.0 units")]
    [InlineData("%d", 1f, "0")]
    [InlineData("%u", 1.0000001f, "536870912")]
    [InlineData("%I64x", 1f, "3ff0000000000000")]
    [InlineData("%#X", 0f, "0")]
    [InlineData("%p", 1f, "00000000")]
    [InlineData("%*d", 1f, "1072693248")]
    [InlineData("%s", 1f, "(null)")]
    [InlineData("%.3s", 1f, "(nu")]
    [InlineData("%ls", 1f, "(null)")]
    [InlineData("%5B", 1f, "     ")]
    [InlineData("%Iq", 1f, "Iq")]
    [InlineData("%2.2.5f", 1f, ".5f")]
    public void FormatsLikeTheStaticVs2003Crt(string format, float value, string expected)
        => Assert.Equal(expected, EnbRetailCrtFormat.Format(format, value));

    [Theory]
    [InlineData("%n")]
    [InlineData("%*f")]
    [InlineData("%*s")]
    [InlineData("%40f")]
    public void UndefinedNativeConversionsHaveNoResult(string format)
        => Assert.Null(EnbRetailCrtFormat.Format(format, 1.5f, 32));

    [Fact]
    public void CharacterConversionWritesTheNul()
        => Assert.Equal("a\0b", EnbRetailCrtFormat.Format("a%cb", 1f));
}
