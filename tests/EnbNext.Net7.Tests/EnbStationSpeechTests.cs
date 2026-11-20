using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationSpeechTests
{
    [Fact]
    public void RetailTextRulesBoundAndDeduplicateFacialPhonemes()
    {
        Assert.Equal(new[]{9},EnbStationSpeechPhonemes.Parse("mmm"));
        Assert.Equal(new[]{10,6},EnbStationSpeechPhonemes.Parse("THR"));
        Assert.Equal(new[]{12,14},EnbStationSpeechPhonemes.Parse("x"));
        Assert.Equal(255,EnbStationSpeechPhonemes.Parse(string.Concat(Enumerable.Repeat("mpx",1000))).Length);
        Assert.Empty(EnbStationSpeechPhonemes.Parse(""));
    }

}
