using EnbNext.Client;
using EnbNext.App;
using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbR11MouseFlightTests
{
    [Theory]
    [InlineData(.5f, .5f, true)]
    [InlineData(.26f, .21f, true)]
    [InlineData(.74f, .79f, true)]
    [InlineData(.25f, .5f, false)]
    [InlineData(.75f, .5f, false)]
    [InlineData(.5f, .2f, false)]
    [InlineData(.5f, .8f, false)]
    [InlineData(0f, .5f, false)]
    [InlineData(1f, .5f, false)]
    [InlineData(float.NaN, .5f, false)]
    public void RetailMouseThrottleRegion(float x, float y, bool expected)
        => Assert.Equal(expected, EnbMouseFlightPolicy.ShouldThrust(x, y, true, false));

    [Fact]
    public void SettingAndSpeedLockPreventMouseThrottle()
    {
        Assert.False(EnbMouseFlightPolicy.ShouldThrust(.5f, .5f, false, false));
        Assert.False(EnbMouseFlightPolicy.ShouldThrust(.5f, .5f, true, true));
        Assert.True(EnbRetailPlayerOptionsCatalog.ParseText("").CpitThrottleRightMouse);
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText("[BOOL-7]\nOptionName=Cpit_ThrottleRightMouse\nOptionValue=no").CpitThrottleRightMouse);
        Assert.True(EnbRetailPlayerOptionsCatalog.ParseText("[BOOL-7]\nOptionName=Cpit_ThrottleRightMouse\nOptionValue=yes").CpitThrottleRightMouse);
    }
}
