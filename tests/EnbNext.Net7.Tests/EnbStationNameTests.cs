using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationNameTests
{
    [Fact]
    public void NamesUseRetailRangeAndFadeInsteadOfKeepingDistantLabelsVisible()
    {
        Assert.Equal(2,EnbStationNamePresentation.Scale(5));
        Assert.Equal(1,EnbStationNamePresentation.Scale(25));
        Assert.Equal(.5,EnbStationNamePresentation.Scale(50));
        Assert.Equal(0,EnbStationNamePresentation.AdvanceOpacity(0,50.01,false,10,3,1));
        Assert.Equal(.75f,EnbStationNamePresentation.AdvanceOpacity(1,50.01,false,.25,3,1));
        Assert.Equal(0,EnbStationNamePresentation.AdvanceOpacity(1,50.01,false,1,3,1));
        Assert.Equal(1,EnbStationNamePresentation.AdvanceOpacity(0,50,false,1,3,1));
        Assert.Equal(.5f,EnbStationNamePresentation.AdvanceOpacity(1,10,true,.5,3,1));
        Assert.Equal(1,EnbStationNamePresentation.AdvanceOpacity(.5f,10,false,.5,3,1));
    }
}
