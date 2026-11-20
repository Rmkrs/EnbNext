using EnbNext.App;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbR11FrameStallHistoryTests
{
    [Fact]
    public void SplitsGapAroundPublicationWithoutCallingItGpuTime()
    {
        var sample = new LiveFrameStallHistory.Sample(1, 8, 24, 0, 0, 0, 0, "");
        Assert.Contains("22.00/2.00ms", (sample with { SceneAgeAtCallbackMs = 2 }).PublicationTiming);
        Assert.Contains("2.00/22.00ms", (sample with { SceneAgeAtCallbackMs = 22 }).PublicationTiming);
        Assert.Contains("no new scene during gap", (sample with { SceneAgeAtCallbackMs = 30 }).PublicationTiming);
        Assert.Equal("", sample.PublicationTiming);
    }

    [Fact]
    public void KeepsSlowCpuAndOutsideCallbackGapsButNotNormalFrames()
    {
        var history = new LiveFrameStallHistory();
        history.Record(new(1, 5, 5, 1024, 0, 0, 1000, "normal"));
        history.Record(new(2, 20, 0, 2048, 1, 3, 2000, "slow-cpu"));
        history.Record(new(3, 5, 24, 1024, 0, 0, 3000, "slow-gap"));
        string text = history.Describe(4);
        Assert.DoesNotContain("normal", text);
        Assert.Contains("slow-cpu", text);
        Assert.Contains("interval 29.00ms = CPU 5.00 + gap 24.00", text);
        Assert.Contains("GC0 1 · world GPU new 3", text);
        Assert.Equal("slow-gap",history.Worst(4)?.Profile);
        Assert.Null(history.Worst(34));
    }

    [Fact]
    public void RetainsWorstEightAndExpiresWithoutNewSlowFrames()
    {
        var history = new LiveFrameStallHistory();
        for (int i = 0; i < 12; i++) history.Record(new(i, 30+i, 0, 0, 0, 0, (uint)i, $"sample#{i};"));
        string text = history.Describe(12);
        Assert.DoesNotContain("sample#3;", text);
        Assert.Contains("sample#4;", text);
        Assert.Equal(8, text.Split("sample#").Length-1);
        Assert.EndsWith("none", history.Describe(42));
        history.Record(new(43, 40, 0, 0, 0, 0, 0, "new"));
        history.Clear();
        Assert.EndsWith("none", history.Describe(43));
    }
}
