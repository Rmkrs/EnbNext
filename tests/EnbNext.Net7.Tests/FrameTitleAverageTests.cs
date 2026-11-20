using System.Diagnostics;
using System.Text.Json;
using EnbNext.App;
namespace EnbNext.Net7.Tests;

public sealed class FrameTitleAverageTests
{
    [Fact]
    public void AveragingUsesIntervalsAndDoesNotUpdateBeforeHalfSecond()
    {
        var average=new FrameTitleAverage();long start=Stopwatch.Frequency;
        Assert.False(average.Add(start,out _));
        for(int i=1;i<125;i++)Assert.False(average.Add(start+i*Stopwatch.Frequency/250,out _));
        Assert.True(average.Add(start+Stopwatch.Frequency/2,out double ms));
        Assert.Equal(4,ms,8);
        Assert.False(average.Add(start+Stopwatch.Frequency*3/4,out _));
        Assert.True(average.Add(start+Stopwatch.Frequency,out ms));
        Assert.Equal(250,ms,8);
        average.Reset();
        Assert.False(average.Add(start+Stopwatch.Frequency*10,out _));
    }
}
