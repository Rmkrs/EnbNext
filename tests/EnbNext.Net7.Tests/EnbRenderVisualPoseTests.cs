using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbRenderVisualPoseTests
{
    private static void SameVisualInput(EnbSceneSnapshot scene)
    {
        foreach(var obj in scene.Objects)
        {
            if(obj.Spatial is null)continue;
            Assert.InRange(obj.RenderPoseSlot,0,scene.RenderPoses.Length-1);
            var pose=scene.RenderPoses[obj.RenderPoseSlot];
            Assert.Equal(obj.Spatial.Transform,pose.ToTransform());
            if(pose.HasVisualInputs)
            {Assert.Equal(obj.ShipVisualBankRadians,pose.ShipVisualBankRadians);Assert.Equal(obj.ShipEmote,pose.ShipEmote);}
        }
    }
    [Fact]
    public void VisualBankPublishesCompactPoseAndJetInputWithoutChangingPhysicsOrDefinitions()
    {
        var world=EnbR11FlightTests.FlightWorld();
        world.ApplyLocalFlightControls(7,1000,1,null,null);
        var publisher=new EnbPresentationSnapshotPublisher();publisher.Publish(world,null,1000);
        long definitions=publisher.RenderScene.DefinitionProjections;
        var bank=new EnbShipVisualBankRuntime();var legacy=new EnbShipVisualBankRuntime();
        for(uint time=1000;time<=1300;time+=20)
        {
            var raw=publisher.SampleLiveFrame(world,time,null)!;
            var before=raw.RenderPoses.Detach();
            var visual=bank.ApplyFrame(raw,20);
            var expected=legacy.ApplyFrame(raw with {RenderPoses=default},20);
            Assert.Equal(expected.Objects.ToArray(),visual.Objects.ToArray());SameVisualInput(visual);
            Assert.Same(raw.RenderPoses.Owner(0),visual.RenderPoses.Owner(0));
            Assert.Equal(raw.Objects[0].Spatial!.Transform,before[raw.Objects[0].RenderPoseSlot].ToTransform());
            if(time>1000)
            {
                Assert.NotEqual(0f,visual.Objects[0].ShipVisualBankRadians);
                Assert.NotEqual(before[visual.Objects[0].RenderPoseSlot].Sample.ProviderTransform,
                    visual.RenderPoses[visual.Objects[0].RenderPoseSlot].Sample.ProviderTransform);
            }
        }
        Assert.Equal(definitions,publisher.RenderScene.DefinitionProjections);
    }
    [Fact]
    public void EmoteThenBankPublishesCurrentPoseAndCompletionWithoutReplayingOrFreezing()
    {
        var world=EnbR11FlightTests.FlightWorld();
        var gameplay=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        gameplay.Apply(Packet.ForOpcode(0x00A2,new byte[]{0,0,0,7,5,0,0,0}));
        var publisher=new EnbPresentationSnapshotPublisher();publisher.Publish(world,null,null,1000,gameplay:gameplay);
        var emote=new EnbShipEmoteRuntime();var legacyEmote=new EnbShipEmoteRuntime();
        var bank=new EnbShipVisualBankRuntime();var legacyBank=new EnbShipVisualBankRuntime();
        EnbSceneSnapshot? retained=null;
        foreach(uint time in new uint[]{1000,1250,1500,1999,2000,2001,2017})
        {
            var raw=publisher.SampleLiveFrame(world,time,null)!;
            var visual=bank.ApplyFrame(emote.Apply(raw,time),16);
            var expected=legacyBank.ApplyFrame(legacyEmote.Apply(raw with {RenderPoses=default},time),16);
            Assert.Equal(expected.Objects.ToArray(),visual.Objects.ToArray());SameVisualInput(visual);
            if(time==1250){retained=visual with {RenderPoses=visual.RenderPoses.Detach()};Assert.NotNull(visual.Objects[0].ShipEmote);}
            if(time>2000){Assert.Null(visual.Objects[0].ShipEmote);Assert.Null(visual.RenderPoses[visual.Objects[0].RenderPoseSlot].ShipEmote);}
        }
        Assert.NotNull(retained!.Objects[0].ShipEmote);SameVisualInput(retained);
    }
    [Fact]
    public void CameraOverrideKeepsVisualClockAndOtherSlotsAndDoesNotMutateQueuedFrame()
    {
        var world=EnbR11FlightTests.FlightWorld();
        var publisher=new EnbPresentationSnapshotPublisher();publisher.Publish(world,null,1000);
        var scene=publisher.SampleLiveFrame(world,1000,null)!;
        var obj=scene.Objects[0] with {ShipVisualBankRadians=.25f,ShipEmote=new(7,3,.75)};
        scene.RenderPoses.Write(obj);var queued=scene.RenderPoses.Detach();
        var next=obj.Spatial!.Transform with {Position=new(11,22,33),ProviderTransform=Matrix4x4.CreateTranslation(11,22,33)};
        var overridden=scene.RenderPoses.WithTransform(obj.RenderPoseSlot,next);
        Assert.Equal(obj.Spatial.Transform,queued[obj.RenderPoseSlot].ToTransform());
        Assert.Equal(next,overridden[obj.RenderPoseSlot].ToTransform());
        Assert.Equal(obj.ShipEmote,overridden[obj.RenderPoseSlot].ShipEmote);
        Assert.Equal(.25f,overridden[obj.RenderPoseSlot].ShipVisualBankRadians);
        Assert.Equal(queued,queued.WithTransform(-1,next));
    }
}
