using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbAttachedLabelHoverTests
{
    [Fact]
    public void CycleUsesIntegerChannelHalvesAndAuthoredSpeed()
    {
        var yellow=new EnbNext.Assets.EnbRgb(1,1,0);
        var green=new EnbNext.Assets.EnbRgb(0,1,0);
        var midpoint=EnbAttachedLabelCycle.Color(yellow,green,0,1);
        Assert.InRange(midpoint.R,.498f,.499f);
        Assert.InRange(midpoint.G,.999f,1f);
        Assert.InRange(EnbAttachedLabelCycle.Color(yellow,green,250,1).R,0,.001f);
        Assert.InRange(EnbAttachedLabelCycle.Color(yellow,green,750,1).R,.995f,.997f);
        Assert.Equal(EnbAttachedLabelCycle.Color(yellow,green,250,1),EnbAttachedLabelCycle.Color(yellow,green,125,2));
        Assert.Equal(0,midpoint.B);
    }

    [Fact]
    public void BlinkUsesEntryClockAndAuthoredSpeedThenRevertsAfterOneOutsideFrame()
    {
        var effect=new EnbAttachedLabelHover();
        Assert.Equal(1,effect.Update("row",false,5000,1,2));
        Assert.Equal(0,effect.Update("row",true,6000,1,2));
        Assert.Equal(.5f,effect.Update("row",true,6125,1,2));
        Assert.Equal(1,effect.Update("row",true,6250,1,2));
        Assert.Equal(.5f,effect.Update("row",true,6375,1,2));
        Assert.Equal(.5f,effect.Update("row",false,6400,1,2));
        Assert.Equal(.7f,effect.Update("row",false,6500,1,2),5);
        Assert.Equal(1,effect.Update("row",false,7000,1,2));
        Assert.Equal(0,effect.Update("row",true,7123,2,2));
        Assert.Equal(1,effect.Update("row",true,7248,2,2));
    }

    [Fact]
    public void TextRefreshAndSameValueReceiptResetTheHoverClock()
    {
        var effect=new EnbAttachedLabelHover();
        effect.Update("row",true,0,1,2);
        Assert.Equal(1,effect.Update("row",true,250,1,2));
        Assert.Equal(0,effect.Update("changed",true,300,1,2));
        effect.Update("changed",true,550,1,2);
        effect.Clear();
        Assert.Equal(0,effect.Update("changed",true,600,1,2));
    }
}
