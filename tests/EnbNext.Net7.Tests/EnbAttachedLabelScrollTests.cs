using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbAttachedLabelScrollTests
{
    [Fact]
    public void HiddenFramesDoNotAccumulateScrollDistance()
    {
        var scroll=new EnbAttachedLabelScroll();
        scroll.Update("long",0,200,50,1.5f);
        Assert.Equal(-30,scroll.Update("long",1000,200,50,1.5f),3);
        scroll.Pause(10000);
        Assert.Equal(-33,scroll.Update("long",10100,200,50,1.5f),3);
    }

    [Fact]
    public void AttachedLabelOnlyScrollsBeyondTruncatedMeshThreshold()
    {
        var scroll=new EnbAttachedLabelScroll();
        Assert.Equal(0,scroll.Update("fits",0,95,50.1f,1.5f));
        Assert.Equal(0,scroll.Update("fits",1000,95,50.1f,1.5f));
        Assert.Equal(0,scroll.Update("overflows",1000,96,50.1f,1.5f));
        Assert.Equal(-30,scroll.Update("overflows",2000,96,50.1f,1.5f),3);
    }

    [Fact]
    public void WrapOccursStrictlyAfterTextLeavesAndPreservesFrameOvershoot()
    {
        var scroll=new EnbAttachedLabelScroll();
        scroll.Update("long",0,120,50,1.5f);
        Assert.Equal(-120,scroll.Update("long",4000,120,50,1.5f),3);
        Assert.Equal(92,scroll.Update("long",4100,120,50,1.5f),3);
        Assert.Equal(62,scroll.Update("long",5100,120,50,1.5f),3);
        Assert.Equal(-118,scroll.Update("long",11100,120,50,1.5f),3);
        Assert.Equal(-121,scroll.Update("long",11200,120,50,1.5f),3);
        Assert.Equal(91,scroll.Update("long",11300,120,50,1.5f),3);
    }

    [Fact]
    public void UnchangedTextKeepsMotionWhileTextChangeAndHideResetIt()
    {
        var scroll=new EnbAttachedLabelScroll();
        scroll.Update("first",100,200,50,2);
        Assert.Equal(-40,scroll.Update("first",1100,200,50,2),3);
        Assert.Equal(-80,scroll.Update("first",2100,200,50,2),3);
        Assert.Equal(0,scroll.Update("second",3100,200,50,2));
        Assert.Equal(-40,scroll.Update("second",4100,200,50,2),3);
        scroll.Clear();
        Assert.Equal(0,scroll.Update("second",5100,200,50,2));
    }
}
