using EnbNext.Rendering;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7BoundPresentationSamplingTests
{
    [Fact]
    public void AcyclicParentChainBeyondEightRetainsItsFullTransform()
    {
        var world = new Net7WorldState();
        for (uint id = 1; id <= 12; id++)
        {
            var obj = world.MaterializeCreate(new(id, 1, 10, 1, 0, 0, 0));
            obj.Spatial = new()
            {
                Kind = id == 1 ? Net7SpatialProviderKind.Fixed : Net7SpatialProviderKind.Simple,
                Model = id == 1
                    ? new Net7FixedSpatialModel(new(10, 0, 0), Quaternion.Identity)
                    : new Net7ParentRelativeSpatialModel(id - 1, Vector3.UnitX)
            };
        }
        Assert.True(world.TrySampleObjectPresentation(12, 1000, out var sample));
        Assert.Equal(new Vector3(21, 0, 0), sample.Position);
    }

    [Fact]
    public void BindingsFollowActualFirstDuplicateAcrossMutationsAndReset()
    {
        var registry=new Net7WorldRegistry();using var binding=registry.BindFirst(7);
        Assert.Null(binding.Target);
        var random=new Random(934);
        for(int i=0;i<500;i++)
        {
            uint id=(uint)(7+257*random.Next(3));
            if(i%37==0)registry.Clear();
            else if(random.Next(3)==0)registry.RemoveFirst(id);
            else registry.Create(new(id,1,10,1,0,0,0),null);
            Assert.Same(registry.FindFirst(7),binding.Target);
        }
        binding.Dispose();registry.Create(new(7,1,10,1,0,0,0),null);
        Assert.Null(binding.Target);
    }
    private static Net7WorldState World()
    {
        var world=new Net7WorldState();
        Net7WorldObject Add(uint id,INet7SpatialModel model,Net7SpatialProviderKind kind=Net7SpatialProviderKind.Simple)
        {
            var obj=world.MaterializeCreate(new(id,1,10,1,0,0,0));
            obj.Spatial=new(){Kind=kind,Model=model};return obj;
        }
        Add(7,new Net7FixedSpatialModel(new(1,2,3),Quaternion.Identity),Net7SpatialProviderKind.Fixed);
        Add(8,new Net7TransitionSpatialModel(new Net7SimpleSpatialModel(1000,new(2,3,4),Quaternion.Identity,new(.1f,.2f,.3f)),
            new Net7SimpleSpatialModel(1200,new(5,6,7),Quaternion.Identity,new(.2f,.1f,.4f)),1000,200));
        Add(9,new Net7ParentRelativeSpatialModel(8,new(2,0,0)));
        Add(10,new Net7ParentRelativeSpatialModel(11,Vector3.One));
        Add(11,new Net7ParentRelativeSpatialModel(10,Vector3.One));
        return world;
    }
    [Fact]
    public void ReusedSamplingMatchesIndependentQueriesIncludingExpiryEarlierTimeAndCycles()
    {
        var actual=World();var expected=World();var sampling=new Net7PresentationSamplingFrame(actual);
        var bindings=Enumerable.Range(7,6).Select(id=>actual.Objects.BindFirst((uint)id)).ToArray();
        foreach(uint time in new uint[]{1000,1100,1200,1300,1050,2000,uint.MaxValue,0})
        {
            sampling.Begin(time);
            foreach(var binding in bindings)
            {
                bool wanted=expected.TrySampleObjectPresentation(binding.GameId,time,out var reference);
                Assert.Equal(wanted,sampling.TrySample(binding,out var result));
                Assert.Equal(reference,result);
            }
        }
        foreach(var binding in bindings)binding.Dispose();
    }
    [Fact]
    public void SteadyFixedSamplingAllocatesNoContextOrLookupWorkspace()
    {
        var world=World();using var binding=world.Objects.BindFirst(7);
        var sampling=new Net7PresentationSamplingFrame(world);
        for(uint i=0;i<100;i++){sampling.Begin(i);sampling.TrySample(binding,out _);}
        long before=GC.GetAllocatedBytesForCurrentThread();bool success=true;
        for(uint i=0;i<1000;i++){sampling.Begin(i);success &= sampling.TrySample(binding,out _);}
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(success);Assert.Equal(0,allocated);
    }
    [Fact]
    public void LiveFrameMatchesOldDetachedContractWithoutRebindingUnchangedPublications()
    {
        var world=World();var publisher=new EnbPresentationSnapshotPublisher();
        var initial=publisher.Publish(world,null,1000);
        var field=typeof(EnbPresentationSnapshotPublisher).GetField("_frameSampler",BindingFlags.Instance|BindingFlags.NonPublic)!;
        object sampler=field.GetValue(publisher)!;
        var count=sampler.GetType().GetProperty("BindingPreparations",BindingFlags.Instance|BindingFlags.NonPublic)!;
        for(uint time=1000;time<1050;time++)
        {
            var expected=initial.Objects.Select(obj=>
            {
                if(obj.Spatial is not {} spatial || !world.TrySampleObjectPresentation(obj.GameId,time,out var sample))return obj;
                return obj with {Spatial=spatial with {Transform=new EnbSceneTransformSnapshot(time,sample.Position,sample.Orientation,
                    sample.Velocity,sample.Speed,sample.Rotation,sample.OrientationColumn0)
                    {ProviderTransform=sample.ProviderTransform,BankHeadingTarget=sample.BankHeadingTarget}}};
            }).ToImmutableArray();
            var actual=publisher.SampleLiveFrame(world,time,0)!;
            Assert.Equal(expected.Length,actual.Objects.Length);
            for(int index=0;index<expected.Length;index++)Assert.Equal(expected[index],actual.Objects[index]);
            Assert.Equal(time,actual.ClientTime);Assert.Equal(initial.RenderSceneSequence,actual.RenderSceneSequence);
        }
        Assert.Equal(1L,count.GetValue(sampler));
        for(uint time=1050;time<1060;time++)
        {
            publisher.Publish(world,null,time);
            publisher.SampleLiveFrame(world,time,0);
        }
        Assert.Equal(1L,count.GetValue(sampler));
        // A failed/skipped publication must not pin the old first-match object.
        world.MaterializeRemove(7);
        var replacement=world.MaterializeCreate(new(7,1,10,1,0,0,0));
        replacement.Spatial=new(){Kind=Net7SpatialProviderKind.Fixed,Model=new Net7FixedSpatialModel(new(99,0,0),Quaternion.Identity)};
        var moved=publisher.SampleLiveFrame(world,1060,0)!;
        Assert.Equal(99,moved.Objects.Single(o=>o.GameId==7).Spatial!.Transform.Position.X);
        Assert.Equal(1,initial.Objects.Single(o=>o.GameId==7).Spatial!.Transform.Position.X);
        Assert.Equal(1L,count.GetValue(sampler));
        publisher.Clear();Assert.Null(publisher.SampleLiveFrame(world,1061,0));
    }
    [Fact]
    public void RemovedBoundObjectBecomesCollectibleBeforeBindingDisposal()
    {
        var (registry,binding,weak)=Removed();
        for(int i=0;i<3&&weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Assert.False(weak.IsAlive);Assert.Null(binding.Target);binding.Dispose();GC.KeepAlive(registry);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Net7WorldRegistry,Net7WorldObjectBinding,WeakReference) Removed()
    {
        var registry=new Net7WorldRegistry();var obj=registry.Create(new(7,1,10,1,0,0,0),null);
        var binding=registry.BindFirst(7);var weak=new WeakReference(obj);registry.RemoveFirst(7);
        return(registry,binding,weak);
    }
}
