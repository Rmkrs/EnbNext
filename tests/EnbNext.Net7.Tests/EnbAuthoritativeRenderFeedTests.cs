using EnbNext.Rendering;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using System.Numerics;

namespace EnbNext.Net7.Tests;

public sealed class EnbAuthoritativeRenderFeedTests
{
    private static Net7CreateUpdate Create(uint id)=>new(id,1,10,1,0,0,0);
    [Fact]
    public void SteadyPublicationsProjectZeroNodesRegardlessOfPopulation()
    {
        foreach(int count in new[]{1,64,1024})
        {
            var world=new Net7WorldState();
            for(uint id=1;id<=count;id++)world.MaterializeCreate(Create(id));
            var publisher=new EnbPresentationSnapshotPublisher();
            publisher.Publish(world,null,0);
            long projections=publisher.RenderScene.DefinitionProjections,changes=publisher.RenderScene.SourceChanges;
            for(uint frame=1;frame<=8;frame++)publisher.Publish(world,null,frame);
            Assert.Equal(projections,publisher.RenderScene.DefinitionProjections);
            Assert.Equal(changes,publisher.RenderScene.SourceChanges);
            world.Objects.FindFirst(1)!.Name="one mutation";
            publisher.Publish(world,null,9);
            Assert.Equal(projections+1,publisher.RenderScene.DefinitionProjections);
            Assert.Equal(changes+1,publisher.RenderScene.SourceChanges);
        }
    }
    [Fact]
    public void NonVisualMutationsAndCollectionRevisionsAreExplicitAndCoalesced()
    {
        var world=new Net7WorldState();world.MaterializeCreate(Create(7));
        var publisher=new EnbPresentationSnapshotPublisher();var first=publisher.Publish(world,null,0);
        using var feed=publisher.RenderScene.Subscribe();Assert.True(feed.TryReadThrough(first.RenderSceneSequence,out _));
        var source=world.Objects.FindFirst(7)!;
        source.Name="changed";source.IsAttacking=1;source.SpecialTargetEndpointId=19;
        source.RenderStateProjectionRevision++;source.AttachmentProjectionRevision++;
        var next=publisher.Publish(world,null,1);
        Assert.True(feed.TryReadThrough(next.RenderSceneSequence,out var batch));
        var change=Assert.Single(batch.Changes);
        Assert.Equal(EnbRenderObjectChangeKind.Definition,change.Kind);
        Assert.Equal("changed",change.Definition!.Name);Assert.True(change.Definition.IsAttacking);
        Assert.Equal(19u,change.Definition.SpecialTargetEndpointId);
        Assert.Null(first.Objects[0].Name);
        world.MaterializeRemove(7);publisher.Publish(world,null,2);
        long changes=publisher.RenderScene.SourceChanges;
        source.Name="retired mutation";publisher.Publish(world,null,3);
        Assert.Equal(changes,publisher.RenderScene.SourceChanges);
    }
    [Fact]
    public void NativeDuplicateOrderSlotReuseAndResetAreExplicit()
    {
        var world=new Net7WorldState();world.MaterializeCreate(Create(7));world.MaterializeCreate(Create(264));
        world.MaterializeCreate(Create(7));
        var publisher=new EnbPresentationSnapshotPublisher();var first=publisher.Publish(world,null,0);
        using var feed=publisher.RenderScene.Subscribe();Assert.True(feed.TryReadThrough(first.RenderSceneSequence,out var batch));
        var adds=batch.Changes.Where(c=>c.Kind==EnbRenderObjectChangeKind.Add).ToArray();
        Assert.Equal(first.Objects.Select(o=>o.RenderIdentity),adds.Select(c=>c.Identity));
        Assert.Equal(3,adds.Select(c=>c.Slot).Distinct().Count());
        int retired=adds[0].Slot;
        world.MaterializeRemove(7);world.MaterializeCreate(Create(8));
        var next=publisher.Publish(world,null,1);Assert.True(feed.TryReadThrough(next.RenderSceneSequence,out batch));
        Assert.Equal(retired,Assert.Single(batch.Changes.Where(c=>c.Kind==EnbRenderObjectChangeKind.Add)).Slot);
        world.Objects.Clear();var empty=publisher.Publish(world,null,2);
        Assert.True(feed.TryReadThrough(empty.RenderSceneSequence,out batch));
        Assert.Equal(EnbRenderObjectChangeKind.Reset,Assert.Single(batch.Changes).Kind);
    }
    [Fact]
    public void OwnedPosesAdvanceInPlaceWhileExplicitDiagnosticAndPhysicalSnapshotsStayDetached()
    {
        var world=new Net7WorldState();world.MaterializeCreate(Create(7));
        world.Objects.FindFirst(7)!.Spatial=new(){Kind=Net7SpatialProviderKind.Fixed,Model=new Net7FixedSpatialModel(new(2,3,4),Quaternion.Identity)};
        var publisher=new EnbPresentationSnapshotPublisher();publisher.Publish(world,null,0);
        using var feed=publisher.RenderScene.Subscribe();Assert.True(feed.TryReadThrough(ulong.MaxValue,out var batch));
        int slot=Assert.Single(batch.Changes.Where(c=>c.Kind==EnbRenderObjectChangeKind.Add)).Slot;
        var a=publisher.SampleLiveFrame(world,10,0)!;var diagnostic=a.RenderPoses.Detach();var b=publisher.SampleLiveFrame(world,11,0)!;
        Assert.Same(a.RenderPoses.Owner(slot),b.RenderPoses.Owner(slot));
        Assert.True(a.RenderPoses[slot].HasSample);
        Assert.Equal(a.Objects[0].Spatial!.Transform.Position,a.RenderPoses[slot].Sample.Position);
        Assert.Equal(10u,diagnostic[slot].Sample.ClientTime);Assert.Equal(11u,a.RenderPoses[slot].Sample.ClientTime);Assert.Equal(11u,b.RenderPoses[slot].Sample.ClientTime);
        Assert.Equal(10u,a.Objects[0].Spatial!.Transform.ClientTime);
    }
    [Fact]
    public void SlotReuseBindsANewOwnerWithoutMutatingTheRetiredGeneration()
    {
        var world=new Net7WorldState();world.MaterializeCreate(Create(7));
        world.Objects.FindFirst(7)!.Spatial=new(){Kind=Net7SpatialProviderKind.Fixed,Model=new Net7FixedSpatialModel(new(2,3,4),Quaternion.Identity)};
        var publisher=new EnbPresentationSnapshotPublisher();publisher.Publish(world,null,0);
        using var feed=publisher.RenderScene.Subscribe();Assert.True(feed.TryReadThrough(ulong.MaxValue,out var initial));
        var add=Assert.Single(initial.Changes.Where(c=>c.Kind==EnbRenderObjectChangeKind.Add));
        publisher.SampleLiveFrame(world,10,0);
        var retired=add.PoseOwner!;
        world.MaterializeRemove(7);world.MaterializeCreate(Create(8));
        world.Objects.FindFirst(8)!.Spatial=new(){Kind=Net7SpatialProviderKind.Fixed,Model=new Net7FixedSpatialModel(new(9,8,7),Quaternion.Identity)};
        publisher.Publish(world,null,11);Assert.True(feed.TryReadThrough(ulong.MaxValue,out var changed));
        var replacement=Assert.Single(changed.Changes.Where(c=>c.Kind==EnbRenderObjectChangeKind.Add));
        Assert.Equal(add.Slot,replacement.Slot);Assert.NotSame(retired,replacement.PoseOwner);
        var frame=publisher.SampleLiveFrame(world,12,0)!;
        Assert.Same(replacement.PoseOwner,frame.RenderPoses.Owner(replacement.Slot));
        Assert.Equal(new Vector3(2,3,4),retired.Value.Sample.Position);
        Assert.Equal(10u,retired.Value.Sample.ClientTime);
        Assert.Equal(new Vector3(9,8,7),replacement.PoseOwner!.Value.Sample.Position);
    }

}
