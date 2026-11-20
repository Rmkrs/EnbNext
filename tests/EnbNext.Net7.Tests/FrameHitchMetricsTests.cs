using System.Text.Json;
using EnbNext.App;
namespace EnbNext.Net7.Tests;
public sealed class FrameHitchMetricsTests
{
    [Fact] public void NearestRankAndRatesIncludeTheHitch()
    {
        var samples=Enumerable.Repeat(5d,98).Concat(new[]{25d,100d});
        var j=JsonSerializer.SerializeToElement(FrameHitchMetrics.Summarize(samples));
        Assert.Equal(5,j.GetProperty("P95Ms").GetDouble());
        Assert.Equal(25,j.GetProperty("P99Ms").GetDouble());
        Assert.Equal(100,j.GetProperty("MaxMs").GetDouble());
        var h=j.GetProperty("Hitches")[1];
        Assert.Equal(1,h.GetProperty("Count").GetInt32());
        Assert.Equal(60000d/615,h.GetProperty("PerMinute").GetDouble(),8);
    }
    [Fact] public void EmptyWindowDoesNotClaimZeroLatency()
    {
        var j=JsonSerializer.SerializeToElement(FrameHitchMetrics.Summarize([]));
        Assert.Equal(JsonValueKind.Null,j.GetProperty("P99Ms").ValueKind);
        Assert.Equal(JsonValueKind.Null,j.GetProperty("Hitches")[0].GetProperty("PerMinute").ValueKind);
    }
}
