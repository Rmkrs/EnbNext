using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailCameraControllerTests
{
    private static EnbSceneCameraStateSnapshot Options => EnbCameraControlTests.Camera() with { GateCameraEnabled = true };
    private static EnbRetailCameraController Follow()
    {
        var camera = new EnbRetailCameraController();
        camera.Reset(0,true);
        Assert.Equal(EnbRetailCameraState.Wait,camera.Update(Options,0,true).State);
        Assert.Equal(EnbRetailCameraState.Follow,camera.Update(Options,1,true).State);
        return camera;
    }

    [Theory]
    [InlineData(0,0)] [InlineData(1,0)] [InlineData(2,2)] [InlineData(3,1)] [InlineData(4,1)] [InlineData(5,1)]
    [InlineData(6,3)] [InlineData(7,1)] [InlineData(8,5)] [InlineData(9,0)] [InlineData(10,6)] [InlineData(11,0)]
    [InlineData(12,6)] [InlineData(13,7)] [InlineData(14,1)] [InlineData(15,8)] [InlineData(16,1)] [InlineData(17,9)]
    [InlineData(18,0)] [InlineData(19,1)] [InlineData(20,10)] [InlineData(21,1)] [InlineData(22,1)] [InlineData(23,11)]
    [InlineData(24,0)] [InlineData(25,1)]
    public void EveryNativeEventHasItsProvenFollowDisposition(int value,int state)
    {
        var camera=Follow();camera.Enqueue(value);
        var result=camera.Update(Options,100,true,lootTargetEligible:true);
        Assert.Equal(value,result.Event); Assert.Equal((EnbRetailCameraState)state,result.State);
        Assert.NotEqual(EnbRetailCameraState.Random,result.State);
    }

    [Fact]
    public void CoalescingPrecedesPriorityAndOneEventIsConsumedPerFrame()
    {
        var camera=Follow();
        camera.Enqueue(0);camera.Enqueue(12);camera.Enqueue(1);camera.Enqueue(13);
        camera.Enqueue(2);camera.Enqueue(5);camera.Enqueue(4);
        camera.Enqueue(23);camera.Enqueue(24);camera.Enqueue(6);camera.Enqueue(23);
        Assert.Equal((1u<<13)|(1u<<4)|(1u<<23)|(1u<<24)|(1u<<6),camera.Pending);
        Assert.Equal(4,camera.Update(Options,10,true).Event);
        Assert.Equal(6,camera.Update(Options,11,true).Event);
        Assert.Equal(24,camera.Update(Options,12,true).Event);
        Assert.Equal(13,camera.Update(Options,13,true).Event);
        Assert.Equal(23,camera.Update(Options,14,true).Event);
        Assert.Equal(0u,camera.Pending);
        camera.Enqueue(2);camera.Enqueue(5);
        Assert.Equal((1u<<2)|(1u<<5),camera.Pending);
    }

    [Fact]
    public void WarpSignalsAreOnlyDeliveredToWarpAndGatedSelectionConsumesFailures()
    {
        var camera=Follow();camera.Enqueue(2);
        Assert.Equal(EnbRetailCameraState.Follow,camera.Update(Options,10,true,suppressWarp:true).State);
        camera.Enqueue(3);camera.Update(Options,11,true);
        camera.Enqueue(2);camera.Update(Options,12,true);
        camera.Enqueue(3);camera.Update(Options,13,true);camera.Enqueue(4);camera.Update(Options,14,true);
        camera.Enqueue(5);Assert.True(camera.Update(Options,15,true).ForceSmooth);
        camera.Enqueue(15);Assert.Equal(EnbRetailCameraState.Follow,camera.Update(Options,16,true,lootTargetEligible:false).State);
        camera.Enqueue(15);Assert.Equal(EnbRetailCameraState.Follow,camera.Update(Options,17,true,true,false,true).State);
    }

    [Theory]
    [InlineData(6,0x13)] [InlineData(8,8)] [InlineData(10,7)]
    public void GatedActionsDistinguishWaitRepeatedStateAndAttacking(int value,int action)
    {
        var camera=Follow();camera.Enqueue(1);camera.Update(Options,2,true);
        camera.Enqueue(value);var waiting=camera.Update(Options,3,true);
        Assert.Null(waiting.Action);Assert.Equal(EnbRetailCameraState.Wait,waiting.State);
        camera=Follow();camera.Enqueue(value);Assert.Null(camera.Update(Options,4,true).Action);
        camera.Enqueue(value);var repeated=camera.Update(Options,5,true);
        Assert.Equal(action,repeated.Action);Assert.Equal(EnbRetailCameraState.Wait,repeated.State);
        camera=Follow();camera.Enqueue(value);Assert.Equal(action,camera.Update(Options,6,true,flaggedObject:true).Action);
    }

    [Theory]
    [InlineData(2,2)] [InlineData(6,3)] [InlineData(8,5)] [InlineData(10,6)] [InlineData(12,6)]
    [InlineData(13,7)] [InlineData(15,8)] [InlineData(17,9)]
    public void OptionGatesNeverAdmitDisabledStates(int value,int state)
    {
        var options=Options with {WarpCameraEnabled=false,GateCameraEnabled=false,LandCameraEnabled=false,DockCameraEnabled=false,LootCameraEnabled=false};
        var camera=Follow();camera.Enqueue(value);
        Assert.NotEqual((EnbRetailCameraState)state,camera.Update(options,2,true,lootTargetEligible:true).State);
        Assert.False(EnbRetailCameraController.Enabled(EnbRetailCameraState.Random,Options));
    }

    [Fact]
    public void WaitRearmsAndCountsFromShipAppearanceAcrossUnsignedWrap()
    {
        var camera=Follow();camera.Enqueue(1);camera.Update(Options,uint.MaxValue-100,false);
        camera.Update(Options,100,true);
        camera.Update(Options,15100,true);Assert.Equal(0u,camera.Pending);
        camera.Update(Options,15101,true);Assert.Equal(1u<<22,camera.Pending);
        camera.Update(Options,15102,true);camera.Enqueue(1);camera.Update(Options,15103,true);
        camera.Enqueue(0);camera.Update(Options,15104,true);Assert.Equal(1u<<22,camera.Pending);
    }

    [Fact]
    public void LeavingDistanceCancelsOnlyRunningEmoteAndResumesPendingFifo()
    {
        var world=EnbR11FlightTests.FlightWorld();
        var gameplay=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        var packet=Packet.ForOpcode(0x00A2,new byte[]{0,0,0,7,3,0,0,0});
        gameplay.Apply(packet);gameplay.Apply(packet);
        var scene=new EnbPresentationSnapshotBuilder().Build(world,null,null,100,gameplay:gameplay);
        var emotes=new EnbShipEmoteRuntime();var camera=Follow();
        emotes.Observe(scene);ulong owner=scene.Objects[0].RenderIdentity;
        Assert.True(emotes.HasPending(owner));camera.Enqueue(20);camera.Update(Options,100,true);
        Assert.Equal(1,emotes.Apply(scene,100).Objects[0].ShipEmote!.Sequence);
        camera.Enqueue(23);var decision=camera.Update(Options,500,true);Assert.True(decision.InterruptEmote);
        emotes.Interrupt(owner);
        Assert.Null(emotes.Apply(scene,600).Objects[0].ShipEmote);Assert.True(emotes.HasPending(owner));
        camera.Enqueue(25);camera.Update(Options,601,true);emotes.Resume(owner);
        camera.Enqueue(20);camera.Update(Options,602,true);
        Assert.Equal(2,emotes.Apply(scene,602).Objects[0].ShipEmote!.Sequence);
        emotes.Clear();Assert.False(emotes.HasPending(owner));
    }

    [Fact]
    public void TakeoffRetriesTenBlockedPathsAndRestoresPhysicalOwnershipOnStop()
    {
        var physical=EnbCameraControlTests.Ship(0,0).Spatial!.Transform;
        var runtime=new EnbTakeoffCameraRuntime(new EnbRetailCameraRandomSource(() => 0x80000000u));
        int attempts=0;
        runtime.Start(physical,100,(a,b,v)=>{attempts++;Assert.InRange(v.Length(),999999,1000001);return .5f;});
        Assert.Equal(10,attempts);
        runtime.Update(1100,default,out var complete);Assert.False(complete);
        var turning=runtime.ShipTransform!.Orientation;
        Assert.Equal(new uint[]{0,3192374722,0,1065030846},new[]{
            BitConverter.SingleToUInt32Bits(turning.X),BitConverter.SingleToUInt32Bits(turning.Y),
            BitConverter.SingleToUInt32Bits(turning.Z),BitConverter.SingleToUInt32Bits(turning.W)});
        Assert.InRange(runtime.ShipTransform!.Position.Z-physical.Position.Z,174.99f,175.01f);
        Assert.NotEqual(Vector3.Zero,runtime.ShipTransform.Rotation);
        Assert.Equal(new Vector3(0,0,.17500001f),runtime.ShipTransform.Velocity);
        runtime.Update(1100,default,out _);
        var repeated=runtime.ShipTransform.ProviderTransform!.Value;
        //7190E0 rounds the current angle before subtracting the retained previous
        //angle. Equal matrices can retain a tiny difference even at zero delta.
        Assert.Equal(EnbRetailTransformHistory.AngularVelocity(repeated,repeated,0),runtime.ShipTransform.Rotation);
        Assert.Equal(Vector3.Zero,runtime.ShipTransform.Velocity);
        runtime.Update(2100,default,out complete);Assert.False(complete);
        Assert.InRange(runtime.ShipTransform!.Position.Z-physical.Position.Z,349.99f,350.01f);
        //Actual8C29B0 ->8D2710 native +Z control (PC24), also consumed by
        //796440 after the ten rejected sweeps select the vertical fallback.
        var departure=runtime.ShipTransform.Orientation;
        Assert.Equal(new uint[]{0,3207922931,0,1060439283},new[]{
            BitConverter.SingleToUInt32Bits(departure.X),BitConverter.SingleToUInt32Bits(departure.Y),
            BitConverter.SingleToUInt32Bits(departure.Z),BitConverter.SingleToUInt32Bits(departure.W)});
        runtime.Update(5100,default,out complete);Assert.True(complete);
        runtime.Update(5101,default,out complete);Assert.False(complete);
        runtime.Reset();Assert.Null(runtime.ShipTransform);Assert.Null(runtime.Update(5200,default,out _));
        runtime.StartUnavailable(5300);
        Assert.Null(runtime.Update(10299,default,out complete));Assert.False(complete);
        Assert.Null(runtime.Update(10300,default,out complete));Assert.True(complete);
        Assert.Null(runtime.Update(10301,default,out complete));Assert.False(complete);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RotatingViewRetainsTranslationAndRotatesAllThreeNativeAxes(int axis)
    {
        var runtime=new EnbRotatingCameraRuntime((min,max)=>max==2?axis:0);
        var ship=EnbCameraControlTests.Ship(0,0).Spatial!.Transform;
        runtime.Start(ship,100);
        var first=runtime.Update(100)!.Value;var later=runtime.Update(20100)!.Value;
        Assert.Equal(first.Position,later.Position);
        Assert.Equal(new Vector3(37.5f,131.25f,0)+ship.Position,first.Position);
        var nativeBasis=EnbRetailViewCamera.BetweenPoints(first.Position!.Value,ship.Position);
        Assert.Equal(EnbRetailViewCamera.Rotate(nativeBasis,0,-.000005f,axis),first.RetailCameraOrientation);
        Assert.Equal(EnbRetailViewCamera.Rotate(nativeBasis,20000,-.000005f,axis),later.RetailCameraOrientation);
        Assert.InRange(MathF.Abs(Quaternion.Dot(first.Orientation!.Value,later.Orientation!.Value)),.9987f,.9988f);
        runtime.Reset();Assert.Null(runtime.Update(20101));
    }

    [Fact]
    public void ExplicitSmoothReturnOverridesFirstPersonButLargeJumpStillSnaps()
    {
        var normal=new EnbNormalCameraController(true);
        var camera=EnbCameraControlTests.Camera();var ship=EnbCameraControlTests.Ship(0,0).Spatial!.Transform;
        Assert.True(normal.TryResolveFrame(camera,ship,1,10,new(800,600,1),out _,new(new(100,0,0),Quaternion.Identity,true,true)));
        Assert.True(normal.TryResolveFrame(camera,ship,1,10,new(800,600,1),out var smooth,new(null,null,false,ForceSmooth:true)));
        Assert.True(smooth.IsFirstPerson);Assert.InRange(smooth.Camera.Position.X,90,99.99f);
        Assert.True(normal.TryResolveFrame(camera,ship with {Position=new(5000,0,0)},1,10,new(800,600,1),out var jump,
            new(null,null,false,ForceSmooth:true)));
        Assert.Equal(new Vector3(5000,0,0),jump.Camera.Position);
    }

    [Fact]
    public void GameplayEventsCrossImmutableSnapshotAndHandoffBoundary()
    {
        var world=EnbR11FlightTests.FlightWorld();var gameplay=new Net7GameplayState();
        gameplay.AddCameraEvent(3,99);gameplay.BeginDockDeparture(100,10);gameplay.BeginTakeoffCamera();
        var builder=new EnbPresentationSnapshotBuilder();var before=builder.Build(world,null,null,10,gameplay:gameplay);
        Assert.Equal(new[]{15,12,17},before.Camera.Events.Select(e=>e.Event));
        Assert.NotNull(before.Camera.TakeoffRevision);
        gameplay.ResetForServerHandoff();var after=builder.Build(world,null,null,11,gameplay:gameplay);
        Assert.Empty(after.Camera.Events);Assert.Null(after.Camera.TakeoffRevision);Assert.Equal(3,before.Camera.Events.Length);
    }
}
