using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbFocusModePlacementTests
{
    private static readonly EnbFocusModePlacement.Area Monitor=new(0,0,2560,1400);

    [Fact]
    public void FullGroupSplitsTheBandIntoFiveMonitorShapedThumbnails()
    {
        var main=EnbFocusModePlacement.Main(Monitor,5);
        Assert.Equal(new EnbFocusModePlacement.Area(0,0,2560,1120),main);
        for(int i=0;i<5;i++)
        {
            var thumb=EnbFocusModePlacement.Thumbnail(Monitor,i,5);
            Assert.Equal(new EnbFocusModePlacement.Area(i*512,1120,512,280),thumb);
        }
    }

    [Fact]
    public void FewerClientsKeepTheThumbnailSizeAndCentreInTheBand()
    {
        var first=EnbFocusModePlacement.Thumbnail(Monitor,0,2);
        var second=EnbFocusModePlacement.Thumbnail(Monitor,1,2);
        Assert.Equal(512,first.Width);Assert.Equal(first.Right(),second.X);
        Assert.Equal(Monitor.Width-second.X-second.Width,first.X);
        Assert.Equal(EnbFocusModePlacement.Main(Monitor,5),EnbFocusModePlacement.Main(Monitor,2));
    }

    [Fact]
    public void AloneTheMainClientFillsTheMonitor()
        => Assert.Equal(Monitor,EnbFocusModePlacement.Main(Monitor,0));
}

internal static class AreaExtensions
{
    public static int Right(this EnbFocusModePlacement.Area area)=>area.X+area.Width;
}
