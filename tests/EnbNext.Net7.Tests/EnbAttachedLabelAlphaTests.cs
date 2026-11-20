using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbAttachedLabelAlphaTests
{
    [Fact]
    public void HiddenLabelPausesUnfinishedOpeningWithoutRestartingIt()
    {
        var effect=new EnbAttachedLabelAlpha();
        float Sample(long time)=>effect.Update(time,false,0,1,3,1,2);
        Sample(0);Sample(100);Assert.Equal(.15f,Sample(200),5);
        effect.Pause(200);effect.Pause(10000);
        Assert.Equal(.3f,Sample(10200),5);
        Assert.Equal(.3f,Sample(10300),5);
        Assert.Equal(.45f,Sample(10400),5);
    }
    [Fact]
    public void FadeSamplesItsPriorAccumulatorThenBlinkUsesHoverEntryClock()
    {
        var effect=new EnbAttachedLabelAlpha();
        float Sample(long time,bool hover=false)=>effect.Update(time,hover,0,1,3,1,2);
        Assert.Equal(0,Sample(0));Assert.Equal(0,Sample(100));
        Assert.Equal(.15f,Sample(200),5);
        Sample(800);Assert.Equal(1,Sample(900));Assert.True(effect.OpeningComplete);
        Assert.Equal(0,Sample(1000,true));Assert.Equal(.5f,Sample(1125,true));
        Assert.Equal(.5f,Sample(1200));Assert.Equal(.7f,Sample(1300),5);
        Assert.Equal(1,Sample(2000));
    }

    [Fact]
    public void HoverDuringOpeningCompletesStayAfterTheFadeWriter()
    {
        var effect=new EnbAttachedLabelAlpha();
        Assert.Equal(0,effect.Update(0,true,0,1,3,1,2));
        Assert.True(effect.OpeningComplete);
        Assert.Equal(1,effect.Update(250,true,0,1,3,1,2));
    }
}
