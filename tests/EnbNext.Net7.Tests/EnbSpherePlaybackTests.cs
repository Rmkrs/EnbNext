using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpherePlaybackTests
{
    [Fact]
    public void VisibilityTransitionsRestartWithoutHiddenTimeCatchup()
    {
        var clock=new EnbRetailSpherePlayback();Assert.True(clock.Enabled);
        clock.Advance(1,0,true,250);Assert.Equal(.25f,clock.Progress);
        clock.ReconcileVisibility(true);Assert.Equal(.25f,clock.Progress);
        clock.ReconcileVisibility(false);clock.Advance(1,0,true,500000);
        Assert.False(clock.Enabled);Assert.Equal(0,clock.Progress);
        clock.ReconcileVisibility(true);clock.Advance(1,0,true,16);
        Assert.InRange(clock.Progress,.015999f,.016001f);
        clock=new EnbRetailSpherePlayback();Assert.True(clock.Enabled);Assert.Equal(0,clock.Progress);
    }

    [Fact]
    public void SphereUsesBitEightAndSubtractsOnlyOnce()
    {
        var clock=new EnbRetailSpherePlayback();
        clock.Advance(1,8,true,1000);Assert.Equal(1,clock.Progress);
        clock.Advance(1,8,true,0);Assert.Equal(1,clock.Progress);
        clock.Advance(1,8,true,3500);Assert.Equal(3.5f,clock.Progress);
        clock.Advance(1,8,true,0);Assert.Equal(2.5f,clock.Progress);
        clock=new EnbRetailSpherePlayback();clock.Advance(1,2,true,1500);Assert.InRange(clock.Progress,1.49999f,1.50001f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void NonpositiveDurationSelectsTerminalProgress(float duration)
    {
        var clock=new EnbRetailSpherePlayback();clock.Advance(duration,8,true,99);
        Assert.Equal(1,clock.Progress);
    }

    [Fact]
    public void StaticObjectsDoNotAdvanceAndInvalidActiveDurationsFail()
    {
        var clock=new EnbRetailSpherePlayback();clock.Advance(1,8,false,uint.MaxValue);
        Assert.Equal(0,clock.Progress);
        Assert.Throws<InvalidDataException>(()=>clock.Advance(float.NaN,8,true,1));
        for(int i=0;i<31250;i++)clock.Advance(1,8,true,16);
        Assert.InRange(clock.Progress,0,1);
    }
}
