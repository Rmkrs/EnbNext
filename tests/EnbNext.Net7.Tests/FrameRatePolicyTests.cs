using EnbNext.Client;
using Xunit;
namespace EnbNext.Net7.Tests;
public class FrameRatePolicyTests
{
    [Theory]
    [InlineData(false,false,1000,60,60)]
    [InlineData(true,true,360,60,360)]
    [InlineData(true,false,360,60,60)]
    [InlineData(true,false,1000,60,60)]
    [InlineData(true,false,1000,0,1000)]
    [InlineData(true,false,30,60,30)]
    public void FocusOnlyLimitsPresentation(bool game,bool focus,int foreground,int background,int expected)
        => Assert.Equal(expected,EnbFrameRatePolicy.Resolve(game,focus,foreground,background));
}
