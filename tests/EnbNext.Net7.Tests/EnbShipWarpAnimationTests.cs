using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbShipWarpAnimationTests
{
    private static EnbSceneObjectSnapshot Ship()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new(23,1,1620,1,0,0,0));
        return new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects[0] with
        { ShipPresentation=new(false,0,0,0,false,null,0,[],[]) };
    }

    [Theory]
    [InlineData(3)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(11)]
    public void FoldOnceHoldThenOpenOnceOnReportedExit(int exit)
    {
        var runtime=new EnbShipWarpAnimationRuntime();
        var ship=Ship(); runtime.Update([ship],0);
        Assert.Null(runtime.Get(ship.RenderIdentity));
        ship=ship with { ShipPresentation=ship.ShipPresentation! with { WarpChanges=[new(1,1,5,14)] } };
        runtime.Update([ship],1000);
        var close=runtime.Get(ship.RenderIdentity)!;
        Assert.Equal(1,close.Suffix);
        Assert.Equal(3,EnbShipWarpAnimationRuntime.Frame(close,1100,30,91));
        Assert.Equal(90,EnbShipWarpAnimationRuntime.Frame(close,10000,30,91));
        ship=ship with { ShipPresentation=ship.ShipPresentation! with { PrivateWarpState=6,
            WarpChanges=[new(1,1,5,14),new(2,2,5,14)] } };
        runtime.Update([ship],2000); Assert.Equal(close,runtime.Get(ship.RenderIdentity));
        ship=ship with { ShipPresentation=ship.ShipPresentation with
            { WarpChanges=[new(1,1,5,14),new(2,2,5,14),new(3,exit,5,14)] } };
        runtime.Update([ship],3000);
        var open=runtime.Get(ship.RenderIdentity)!;
        Assert.Equal(2,open.Suffix); Assert.Equal(3000u,open.StartTime);
        runtime.Update([ship],4000); Assert.Equal(open,runtime.Get(ship.RenderIdentity));
        runtime.Update([],5000); Assert.Null(runtime.Get(ship.RenderIdentity));
    }

    [Fact]
    public void IniPreservesFrameSoundTypeAndRegistryIdentity()
    {
        var catalog=EnbW3dAnimationSoundCatalog.Parse("[SPP22.SPP22-1]\nSound0=3, close ; note\nSound1=7, second, 2\nBoneName=root\ninvalid=no\n");
        Assert.True(catalog.TryGet("spp22.spp22-1",out var track));
        Assert.Equal("root",track.BoneName);
        Assert.Equal(new[]{new EnbW3dAnimationSoundCue(0,3,"close",false),new EnbW3dAnimationSoundCue(1,7,"second",true)},track.Cues);
        Assert.False(catalog.TryGet("spp22-1",out _));
    }

    [Theory]
    [InlineData("2D",true)]
    [InlineData(" +02D",true)]
    [InlineData("2",true)]
    [InlineData("20D",false)]
    [InlineData("-2D",false)]
    [InlineData("D2",false)]
    public void AnimationSoundTypeUsesRetailIntegerPrefix(string type,bool flat)
    {
        var catalog=EnbW3dAnimationSoundCatalog.Parse($"[FE01A.FE01A-1]\nSound0=1, Login_Hand_Slide_In, {type}\n");
        Assert.True(catalog.TryGet("fe01a.fe01a-1",out var track));
        Assert.Equal(flat,Assert.Single(track.Cues).Flat);
    }

    [Fact]
    public void ExplicitFrameSoundDispatchPreservesOrderBoundariesAndReverseSilence()
    {
        var catalog=EnbW3dAnimationSoundCatalog.Parse("[anim]\nSound0=34, late, 2D\nSound1=4, early, 2D\nSound2=4, same-frame, 2D\nSound3=0, zero, 2D\n");
        Assert.True(catalog.TryGet("anim",out var track));
        Assert.Empty(track.Crossed(0,1));
        Assert.Equal(new[]{"early","same-frame"},track.Crossed(1,4).Select(c=>c.Sound));
        Assert.Empty(track.Crossed(4,4));
        Assert.Equal(new[]{"late"},track.Crossed(4,34).Select(c=>c.Sound));
        Assert.Equal(new[]{"late","early","same-frame"},track.Crossed(1,34).Select(c=>c.Sound));
        Assert.Empty(track.Crossed(34,1));
        Assert.Empty(track.Crossed(1,0));
    }

    [Fact]
    public void NewOwnerDoesNotReplayHistoricalFoldsAndClockWrapRemainsContinuous()
    {
        var runtime=new EnbShipWarpAnimationRuntime();
        var ship=Ship();
        ship=ship with { ShipPresentation=ship.ShipPresentation! with { WarpChanges=[new(1,1,5,14),new(2,2,5,14)] } };
        runtime.Update([ship],0); Assert.Null(runtime.Get(ship.RenderIdentity));
        runtime.Clear(); Assert.Null(runtime.Get(ship.RenderIdentity));
        Assert.Equal(3,EnbShipWarpAnimationRuntime.Frame(new(1,1,uint.MaxValue-49),50,30,91));
    }
}
