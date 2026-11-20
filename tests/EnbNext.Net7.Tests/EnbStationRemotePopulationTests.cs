using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Net7.Protocol;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationRemotePopulationTests
{
    [Fact]
    public void TeleportConstructionConsumesEveryIngressEventBeforeRemoval()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();
        Pose(world,42,flags:0x40);Leave(world,42,uint.MaxValue);
        var events=new List<string>();
        void Effect(EnbStationRemotePopulation.Actor actor)
        {events.Add("effect"+actor.RequestedTeleport);actor.RequestedTeleport=0;}
        roster.Receive(Snapshot(world),0,100,attached:_=>events.Add("actor"),teleportRequested:Effect);
        Assert.Equal(new[]{"actor","effect2","effect3"},events);
        roster.Receive(Snapshot(world),0,100,teleportRequested:Effect);
        Assert.Equal(3,events.Count);
        roster.RemoveCompleted(0);Assert.Single(roster.Ordered);

        var failed=new EnbStationRemotePopulation();
        failed.Receive(Snapshot(world),0,100,teleportRequested:actor=>
        {actor.Teleport.Clear();actor.RequestedTeleport=0;});
        failed.RemoveCompleted(0);Assert.Empty(failed.Ordered);
    }

    [Fact]
    public void InteractionUsesReversedWidgetConstructionWithoutChangingActorUpdateOrder()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();
        foreach(uint id in new uint[]{194,1,387,2})Pose(world,id);
        roster.Receive(Snapshot(world),0,0);
        Assert.Equal(new uint[]{2,387,1,194},roster.InteractionOrdered.Select(a=>a.Pose.AvatarId));
        Assert.Equal(new uint[]{387,1,194,2},roster.Ordered.Select(a=>a.Pose.AvatarId));
        Pose(world,194,x:10);roster.Receive(Snapshot(world),0,0);
        Assert.Equal(new uint[]{2,387,1,194},roster.InteractionOrdered.Select(a=>a.Pose.AvatarId));
        world.ReplaceAvatarAppearance(1,world.StationAvatarEvents[0].Avatar!);
        roster.Receive(Snapshot(world),0,0);
        Assert.Equal(new uint[]{1,2,387,194},roster.InteractionOrdered.Select(a=>a.Pose.AvatarId));
        Leave(world,2);roster.Receive(Snapshot(world),0,0);
        Assert.Equal(new uint[]{1,2,387,194},roster.InteractionOrdered.Select(a=>a.Pose.AvatarId));
        roster.RemoveCompleted(1);
        Assert.Equal(new uint[]{1,387,194},roster.InteractionOrdered.Select(a=>a.Pose.AvatarId));
    }
    [Fact]
    public void SpeakersResolveAtTheirIngressPositionAndSelectFirstNativeHashMatch()
    {
        var template=new Net7WorldState();Pose(template,42);
        string name=template.StationAvatarEvents[0].Avatar!.FirstName;
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();
        world.PublishStationSpeaker(name,7);Pose(world,42);
        roster.Receive(Snapshot(world),0,0);
        Assert.False(Assert.Single(roster.Ordered).Speaker.Flash);
        world.PublishStationSpeaker(name.ToLowerInvariant(),7);Pose(world,194);
        roster.Receive(Snapshot(world),0,0);
        Assert.True(roster.Ordered.Single(a=>a.Pose.AvatarId==42).Speaker.Flash);
        Assert.False(roster.Ordered.Single(a=>a.Pose.AvatarId==194).Speaker.Flash);
        world.PublishStationSpeaker(name,3);
        roster.Receive(Snapshot(world),0,0);
        Assert.Equal(EnbNext.Assets.EnbRetailChatColors.Default(3),roster.Ordered[0].Speaker.Color);
        Assert.Single(roster.Ordered,a=>a.Speaker.Color==EnbNext.Assets.EnbRetailChatColors.Default(3));
        var selected=roster.Ordered[0];selected.Speaker.Advance(250,false,new(1,1,1));
        roster.Receive(Snapshot(world),0,0);
        Assert.Equal(1.75f,selected.Speaker.Remaining); // Retained snapshot cannot restart the timer.
    }
    private static void Cache(Net7WorldState world,uint id)
    {
        byte[] avatar=Net7PresentationWireTests.BuildAvatarData("");
        byte[] payload=new byte[4+avatar.Length];BinaryPrimitives.WriteUInt32LittleEndian(payload,id);
        avatar.CopyTo(payload,4);world.Apply(Packet.ForOpcode(0x61,payload),0);
    }
    private static void Pose(Net7WorldState world,uint id,uint room=0,uint flags=0,float x=0,bool cached=true)
    {
        if(cached)Cache(world,id);
        world.Apply(Packet.ForOpcode(0x9e,Net7PresentationWireTests.BuildStarbaseAvatarChange(id,room,0,new(x,0,0),flags)),0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAppearanceDoesNotAdmitOrReplayTheInitialPose(bool publishBeforeCache)
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();
        Pose(world,42,x:10,cached:false);
        if(publishBeforeCache){roster.Receive(Snapshot(world),0,0);Assert.Empty(roster.Ordered);}
        Cache(world,42);roster.Receive(Snapshot(world),0,0);Assert.Empty(roster.Ordered);
        Assert.Null(world.StationAvatarEvents[0].Avatar);
        Pose(world,42,x:20);roster.Receive(Snapshot(world),0,0);
        var actor=Assert.Single(roster.Ordered);Assert.Equal(20,actor.Motion.Transform.M41);
        Assert.Equal(0,actor.Motion.Remaining);
    }
    [Fact]
    public void AppearanceEventsRetainTheirOwnIdentityAcrossBatchedReplacements()
    {
        var world=new Net7WorldState();Pose(world,42);
        var original=world.StationAvatarEvents[0].Avatar!;
        world.ReplaceAvatarAppearance(42,original with {FirstName="First"});
        world.ReplaceAvatarAppearance(42,original with {FirstName="Second"});
        var scene=Snapshot(world);var roster=new EnbStationRemotePopulation();
        roster.Receive(scene with {StationAvatarEvents=scene.StationAvatarEvents.RemoveAt(2)},0,0);
        Assert.Equal("First",Assert.Single(roster.Ordered).Pose.Avatar!.FirstName);
        roster.Receive(scene,0,0);
        Assert.Equal("Second",Assert.Single(roster.Ordered).Pose.Avatar!.FirstName);
        Assert.Equal(original.FirstName,world.StationAvatarEvents[0].Avatar!.FirstName);
    }
    [Fact]
    public void ConstructionConsumesEveryAdmittedEventBeforeHashOrderedUpdates()
    {
        var world=new Net7WorldState();Pose(world,1);Pose(world,194);
        var first=world.StationAvatarEvents[0].Avatar!;
        world.ReplaceAvatarAppearance(1,first with {FirstName="ReplacementOne"});
        world.ReplaceAvatarAppearance(1,first with {FirstName="ReplacementTwo"});
        var roster=new EnbStationRemotePopulation();var constructed=new List<(uint,string)>();
        void Construct(EnbStationRemotePopulation.Actor actor)=>constructed.Add((actor.Pose.AvatarId,actor.Pose.Avatar!.FirstName));
        roster.Receive(Snapshot(world),0,0,Construct);
        Assert.Equal(new[]{(1u,first.FirstName),(194u,first.FirstName),(1u,"ReplacementOne"),(1u,"ReplacementTwo")},constructed);
        roster.Receive(Snapshot(world),0,0,Construct);Assert.Equal(4,constructed.Count);
        Pose(world,194,x:10);roster.Receive(Snapshot(world),0,0,Construct);Assert.Equal(4,constructed.Count);
    }
    private static void Leave(Net7WorldState world,uint id,uint destination=1,uint oldRoom=0)
    {
        byte[] payload=new byte[12];BinaryPrimitives.WriteUInt32LittleEndian(payload,id);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),destination);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),oldRoom);
        world.Apply(Packet.ForOpcode(0xa0,payload),0);
    }
    private static EnbSceneSnapshot Snapshot(Net7WorldState world)=>new EnbPresentationSnapshotPublisher().Publish(world,null,null,0);
    [Fact]
    public void SpeakerKeepsIngressColorAcrossPaletteChangesBeforePopulationDrain()
    {
        var world=new Net7WorldState();Pose(world,42);
        string first=world.StationAvatarEvents[0].Avatar!.FirstName;
        var original=new EnbNext.Assets.EnbRgb(1,.6f,0);
        world.PublishStationSpeaker(first,7,new(original.R,original.G,original.B));
        var roster=new EnbStationRemotePopulation();
        roster.Receive(Snapshot(world),0,0,chatColor:_=>new(0,1,0));
        Assert.Equal(original,Assert.Single(roster.Ordered).Speaker.Color);
    }
    [Fact]
    public void InitialAdmissionUsesIncomingOrderThenNativeBucketsAndPrepending()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();
        foreach(uint id in new uint[]{194,1,387,2})Pose(world,id,flags:4);
        var scene=Snapshot(world);roster.Receive(scene,0,0);
        Assert.Equal(new uint[]{387,1,194,2},roster.Ordered.Select(a=>a.Pose.AvatarId));
        var actor=roster.Ordered[0];Pose(world,387,flags:5,x:10);roster.Receive(Snapshot(world),0,0);
        Assert.Same(actor,roster.Ordered[0]);Assert.Equal(1,actor.Motion.Remaining);
        roster.Receive(Snapshot(world),0,0);Assert.Equal(1,actor.Motion.Remaining);
        Assert.Equal(new uint[]{387,1,194,2},roster.Ordered.Select(a=>a.Pose.AvatarId));
        Assert.Equal(new ulong[]{1,2,3,4},scene.StationAvatarEvents.Select(e=>e.Sequence));
        Assert.Equal(5,world.StationAvatarEvents.Count); // earlier publication is immutable.
    }
    [Fact]
    public void DepartureWaitsForTheQueuedPathAndRejectsUpdatesWhilePending()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();Pose(world,42);Pose(world,42,x:10);
        Leave(world,42,oldRoom:99);Pose(world,42,x:100);
        roster.Receive(Snapshot(world),0,0);roster.RemoveCompleted(0);
        var actor=Assert.Single(roster.Ordered);Assert.True(actor.Departing);Assert.Equal(10,actor.Pose.Position.X);Assert.Equal(1,actor.Motion.Remaining);
        for(int time=50;time<5000 && actor.Motion.Remaining!=0;time+=50)
        {actor.Motion.Advance(time);if(actor.Motion.RequestWalk)actor.Motion.WalkAnimationSelected();}
        Assert.Equal(0,actor.Motion.Remaining);roster.RemoveCompleted(0);Assert.Empty(roster.Ordered);
        roster.Receive(Snapshot(world),0,5000);Assert.Empty(roster.Ordered); // no stale pose resurrection.
        Pose(world,42,x:200);roster.Receive(Snapshot(world),0,5000);
        Assert.Equal(200,Assert.Single(roster.Ordered).Motion.Transform.M41);
    }
    [Fact]
    public void PendingUnknownDepartureSuppressesSameFrameArrivalOnly()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();
        Leave(world,42);Pose(world,42);roster.Receive(Snapshot(world),0,0);Assert.Empty(roster.Ordered);
        roster.RemoveCompleted(0);Pose(world,42);roster.Receive(Snapshot(world),0,0);Assert.Single(roster.Ordered);
    }
    [Fact]
    public void TeleportDepartureIsHangarOnlyAndRemovalFollowsThePreviousActorState()
    {
        foreach(uint room in new uint[]{0,1})
        {
            var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();Pose(world,42,room);Leave(world,42,uint.MaxValue);
            roster.Receive(Snapshot(world),room,0);roster.RemoveCompleted(0);
            if(room==1){Assert.Empty(roster.Ordered);continue;}
            var actor=Assert.Single(roster.Ordered);Assert.Equal(3,actor.RequestedTeleport);
            actor.Teleport.Tick(499);Assert.False(actor.Teleport.Hidden);roster.RemoveCompleted(499);Assert.Single(roster.Ordered);
            actor.Teleport.Tick(1);Assert.True(actor.Teleport.Hidden);Assert.True(actor.Teleport.Active);
            actor.Teleport.Tick(501);Assert.False(actor.Teleport.Active);Assert.Single(roster.Ordered);
            roster.RemoveCompleted(0);Assert.Empty(roster.Ordered);
        }
    }
    [Fact]
    public void DepartureTimeoutIsStrictlyGreaterThanTenSecondsAndDuplicateDoesNotRestartIt()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();Pose(world,42);Pose(world,42,x:100);Leave(world,42);
        roster.Receive(Snapshot(world),0,0);roster.RemoveCompleted(10000);Assert.Single(roster.Ordered);
        Leave(world,42);roster.Receive(Snapshot(world),0,0);roster.RemoveCompleted(0);Assert.Single(roster.Ordered);
        roster.RemoveCompleted(1);Assert.Empty(roster.Ordered);
    }
    [Fact]
    public void RoomDetachRetainsGrownCapacityButDoesNotReplayOldArrivals()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();
        for(uint id=1;id<=194;id++)Pose(world,id);
        var scene=Snapshot(world);roster.Receive(scene,0,0);Assert.Equal(389u,roster.BucketCount);
        roster.ChangeRoom(scene);roster.Receive(scene,1,0);Assert.Empty(roster.Ordered);Assert.Equal(389u,roster.BucketCount);
        Pose(world,390,1);Pose(world,1,1);roster.Receive(Snapshot(world),1,0);
        Assert.Equal(new uint[]{1,390},roster.Ordered.Select(a=>a.Pose.AvatarId));
        world.ResetForCharacterSelection();roster.Receive(Snapshot(world),1,0);Assert.Empty(roster.Ordered);Assert.Equal(193u,roster.BucketCount);
    }
    [Fact]
    public void AppearanceReplacementReinsertsAtCurrentPoseAndDropsOldWaypoints()
    {
        var world=new Net7WorldState();var roster=new EnbStationRemotePopulation();Pose(world,1);Pose(world,194);
        roster.Receive(Snapshot(world),0,0);Pose(world,1,x:50);roster.Receive(Snapshot(world),0,0);
        var before=roster.Ordered.Single(a=>a.Pose.AvatarId==1);
        before.Motion.Advance(100);before.Motion.WalkAnimationSelected();before.Motion.Advance(200);
        float position=before.Motion.Transform.M41;Assert.InRange(position,.001f,49);
        var (appearance,_)=Net7AvatarRosterCodec.DecodeAvatarData(Net7PresentationWireTests.BuildAvatarData(""),"fixture");
        world.ReplaceAvatarAppearance(1,appearance);roster.Receive(Snapshot(world),0,200);
        Assert.Equal(new uint[]{1,194},roster.Ordered.Select(a=>a.Pose.AvatarId));
        var after=roster.Ordered[0];Assert.NotSame(before,after);Assert.True(after.ReplaceCachedActor);
        Assert.Equal(position,after.Motion.Transform.M41);Assert.Equal(0,after.Motion.Remaining);
    }
}
