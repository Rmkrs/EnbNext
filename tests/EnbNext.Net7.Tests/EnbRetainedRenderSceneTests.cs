using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // retail/hardware verification, >=5 s; run with --filter Category=Heavy
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbRetainedRenderSceneTests
{
    private sealed class Owner(EnbSceneObjectSnapshot source)
    {public EnbSceneObjectSnapshot Source=source;public bool Released,Detached;public int Updates;}
    [Fact]
    public void NoChangeFramesDoNoOwnerLookupAndRemovalWaitsForConsumingFlush()
    {
        var world=new Net7WorldState();var publisher=new EnbPresentationSnapshotPublisher();
        world.MaterializeCreate(new(7,1,10,1,0,0,0));world.MaterializeCreate(new(264,1,10,1,0,0,0));
        var frame=publisher.Publish(world,null,0);using var subscription=publisher.RenderScene.Subscribe();
        var scene=new EnbRetainedRenderScene<Owner>(static obj=>new(obj),static (owner,obj)=>{owner.Source=obj;owner.Updates++;},static owner=>owner.Released=true,static owner=>owner.Detached=true);
        scene.ApplyThrough(subscription,frame.RenderSceneSequence);
        Assert.Equal(frame.Objects.Select(o=>o.RenderIdentity),scene.ActiveNodes.Select(n=>n.Definition.RenderIdentity));
        var queued=scene.ActiveNodes.First!.Value.State;
        long changes=scene.AppliedChanges,lookups=scene.MutationLookups;
        for(int i=0;i<100;i++){scene.ApplyThrough(subscription,frame.RenderSceneSequence);scene.CompleteConsumption();}
        Assert.Equal(changes,scene.AppliedChanges);Assert.Equal(lookups,scene.MutationLookups);
        Assert.Same(queued,scene.ActiveNodes.First!.Value.State);
        Assert.Same(queued,scene.OrderedStates[0]);
        Assert.Equal(scene.ActiveNodes.Select(n=>n.State),scene.OrderedStates);
        world.MaterializeRemove(queued.Source.GameId);frame=publisher.Publish(world,null,1);
        scene.ApplyThrough(subscription,frame.RenderSceneSequence);
        Assert.True(queued.Detached);Assert.False(queued.Released);Assert.Equal(1,scene.Count);
        Assert.DoesNotContain(queued,scene.OrderedStates);
        scene.CompleteConsumption();Assert.True(queued.Released);Assert.Equal(1,scene.ReleasedOwners);
        scene.ApplyThrough(subscription,frame.RenderSceneSequence);scene.CompleteConsumption();
        Assert.Equal(1,scene.ReleasedOwners);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public void UnchangedSourceWorkIsConstantAcrossScenePopulation(int population)
    {
        var world=new Net7WorldState();var publisher=new EnbPresentationSnapshotPublisher();
        for(uint id=1;id<=population;id++)world.MaterializeCreate(new(id,1,10,1,0,0,0));
        var frame=publisher.Publish(world,null,0);using var subscription=publisher.RenderScene.Subscribe();
        var scene=new EnbRetainedRenderScene<Owner>(static obj=>new(obj),
            static (owner,obj)=>{owner.Source=obj;owner.Updates++;},static owner=>owner.Released=true);
        scene.ApplyThrough(subscription,frame.RenderSceneSequence);scene.CompleteConsumption();
        Assert.Equal(population,scene.Count);
        for(int i=0;i<20;i++){scene.ApplyThrough(subscription,frame.RenderSceneSequence);scene.CompleteConsumption();}
        long changes=scene.AppliedChanges,lookups=scene.MutationLookups,external=scene.ExternalLookups;
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){scene.ApplyThrough(subscription,frame.RenderSceneSequence);scene.CompleteConsumption();}
        long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.Equal(0,bytes);Assert.Equal(changes,scene.AppliedChanges);
        Assert.Equal(lookups,scene.MutationLookups);Assert.Equal(external,scene.ExternalLookups);
        world.MaterializeRemove((uint)population);frame=publisher.Publish(world,null,1);
        scene.ApplyThrough(subscription,frame.RenderSceneSequence);scene.CompleteConsumption();
        Assert.Equal(population-1,scene.Count);Assert.Equal(changes+1,scene.AppliedChanges);
        Assert.Equal(lookups+1,scene.MutationLookups);Assert.Equal(1,scene.ReleasedOwners);
    }

}
