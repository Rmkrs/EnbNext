using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTargetReticleColorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    [InlineData(1999, false)]
    [InlineData(2000, true)]
    [InlineData(4294967296, true)]
    [InlineData(4294968297, false)]
    public void OrdinaryIconSwitchesAuthoredPairAtNativeClockBoundary(long time, bool secondPhase)
    {
        var yellow = new EnbRgb(.99f, .99f, 0);
        var red = new EnbRgb(.99f, 0, 0);
        Assert.Equal(secondPhase ? red : yellow,
            EnbCurrentRetailGutterColorRuntime.AlternatingColor(yellow, red, time));
    }

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(250, .5f)]
    [InlineData(500, 0f)]
    [InlineData(750, .5f)]
    [InlineData(1000, 1f)]
    [InlineData(1500, 0f)]
    public void SelectedPulseTraversesAuthoredPairOnGlobalClock(long time, float blend)
    {
        var first = new EnbRgb(.99f,.99f,.99f);
        var second = new EnbRgb(0,.99f,0);
        var color = EnbCurrentRetailGutterColorRuntime.SelectedColor(first,second,time);
        Assert.Equal(.99f * (1-blend),color.R,5);
        Assert.Equal(.99f,color.G,5);
        Assert.Equal(color.R,color.B);
    }
}
