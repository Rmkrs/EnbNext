using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbEffectRslParticleObservationTests
{
    [Theory]
    [InlineData(202,5000)]
    [InlineData(1985,21000)]
    [InlineData(2252,4000)]
    public void SequenceObservationsDriveAllAttachmentPosesWithoutResettingBirths(int id,uint end)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var admitted=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        {
            Assert.NotNull(admitted.Sequence);Assert.NotNull(admitted.Mixed);
            Assert.Throws<InvalidDataException>(()=>admitted.Sample(0,0));
            Assert.Single(admitted.Sample(0,0,admitted.Sequence.CreateObservations().Record(0)));
        }
        var graph=assets.BaseAssetResolver!.Resolve(id).Rsl!.ResourceGraph!;
        var obj=Assert.Single(graph.States,s=>s.Definition.Type==EnbRslStateType.Object);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,
            obj.Definition.Object!,out var model,out var error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(model,out var resource,out error),error);
        if(id==202)
        {
            Assert.Equal(59,resource.Meshes.Count);Assert.Equal(53,admitted.Resource!.Meshes.Count);
            resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
            foreach(var group in resource.Meshes.GroupBy(m=>m.ContainerName+"."+m.Name,StringComparer.OrdinalIgnoreCase))
            {
                var chosen=Assert.Single(admitted.Resource.Meshes,m=>(m.ContainerName+"."+m.Name).Equals(group.Key,StringComparison.OrdinalIgnoreCase));
                EnbDirectEffectPrototypeRegistrationTests.AssertSamePrototype(group.First(),chosen);
            }
            Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(admitted.Resource,
                Assert.Single(admitted.Resource.HierarchicalLods),_=>true,out _,out error),error);
        }
        string hierarchy=Assert.Single(resource.HierarchicalLods).HierarchyName;
        var clips=new Dictionary<int,EnbW3dRenderHierarchyAnimation>();
        foreach(var state in graph.States.Where(s=>s.Definition.Type==EnbRslStateType.Animation))
        {
            var name=EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(hierarchy,state.Definition.Animation,state.Definition.Suffix);
            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(resource,state.FileDependency,paths,assets.MixResources,
                assets.RetailMixResources,name,out var resolved,out error),error);
            clips.Add(state.Definition.StateId,resolved.Animation);
        }
        int first=obj.Definition.LinkStateId!.Value;
        graph.TryFindState(first,out var initial);
        var mixed=EnbCurrentRetailMixedEffectAsset.Load(assets,resource,model.CurrentRetailAggregate!.Definition,
            clips[first],initial.Definition.Flags);
        var clock=new EnbCurrentRetailRslAnimationSequenceRuntime(graph,first,clips,0);
        var observations=new EnbCurrentRetailRslAnimationObservations(clock);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,1,randomStreams:new());
        var history=new EnbCurrentRetailParticlePoseHistory();
        var prior=new Dictionary<(int,long),EnbCurrentRetailParticleBirth>();
        var seen=new HashSet<int>();
        for(uint age=0;age<=end;age+=50)
        {
            var world=Matrix4x4.CreateTranslation(age*.001f,0,0);
            history.Record(age,world);
            var sample=observations.Record(age);seen.Add(sample.StateId);
            Assert.Equal(sample,observations.Record(age));
            // Replay a pending initial owner-history batch after preparation.
            if(age<1000)continue;
            runtime.Advance(mixed,history,1,observations.Samples);
            Assert.True(EnbW3dHierarchyPose.TryEvaluate(resource.Hierarchies.Single(),world,sample.Animation,
                sample.Frame,null,out var pose,out error,storedTranslationScale:1),error);
            var emitters=runtime.AnimatedSprites!.Emitters;
            for(int i=0;i<emitters.Count;i++)
            {
                Assert.Equal(pose.WorldTransforms[mixed.Sprites[i].PivotIndex],emitters[i].CurrentWorld);
                if(!pose.Visibility[mixed.Sprites[i].PivotIndex])Assert.False(emitters[i].IsEmitting);
                foreach(var birth in emitters[i].WorldBirths)
                {
                    if(prior.TryGetValue((i,birth.Ordinal),out var original))Assert.Equal(original,birth);
                    prior[(i,birth.Ordinal)]=birth;
                }
            }
            var counts=emitters.Select(e=>e.EmittedCount).ToArray();
            runtime.Advance(mixed,history,1,observations.Samples);
            Assert.Equal(counts,emitters.Select(e=>e.EmittedCount));
            observations.RetainLatestSample();Assert.Single(observations.Samples);
        }
        Assert.Equal(clips.Keys.Order(),seen.Order());Assert.NotEmpty(prior);
        Assert.Throws<InvalidDataException>(()=>observations.Record(0));
        var missing=new EnbCurrentRetailMixedParticleRuntime(mixed,1,1,randomStreams:new());
        var emptyHistory=new EnbCurrentRetailParticlePoseHistory();emptyHistory.Record(0,Matrix4x4.Identity);
        Assert.Throws<InvalidDataException>(()=>missing.Advance(mixed,emptyHistory,1,observations.Samples));
    }
}
