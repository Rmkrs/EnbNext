using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingAssemblyTests
{
    [Theory]
    [InlineData(1877)] [InlineData(1856)]
    public void RingParentRetainsForcedDetailWithOrWithoutEmitterSibling(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resolved=assets.BaseAssetResolver!.Resolve(baseId);
        var source=resolved.W3d;
        if(source is null)
        {
            string name=resolved.Rsl!.ResourceGraph!.States.First(s=>s.Definition.Type==EnbRslStateType.Object).Definition.Object!;
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,name,out source,out var error),error);
        }
        var aggregate=source!.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=aggregate.RenderDefinition.Flags|1u};
        var loaded=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)},Vector3.Zero);
        Assert.True(loaded.ForceSubObjectLod);Assert.NotEmpty(loaded.Rings!.Rings);
        Assert.Equal(baseId==1856,loaded.Mixed is not null);
    }

    [Fact]
    public void MeshParentKeepsMultipleDetailLevelsBesideIndependentRings()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=RingMeshLevelsFixture.Load(assets);
        Assert.True(asset.UsesDetailBudget);Assert.Null(asset.Selection);Assert.Null(asset.Mixed);
        Assert.NotEmpty(asset.Rings!.Rings);
        var resource=asset.Resource!;var hlod=Assert.Single(resource.HierarchicalLods);
        Assert.Equal(2,hlod.Levels.Count);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,hlod,_=>true,out var costs,out var error),error);
        Assert.True(costs[0]<8000);Assert.InRange(costs[1],18000,40000);
        Assert.Equal(asset.Rings.Rings.Count,resource.CompositeRenderChildren.Count);
        // Isolate secondary children to expose missing bounds and accidental
        // uniform scaling of their raw construction extents.
        var empty=hlod with {Levels=hlod.Levels.Select(l=>l with {Members=[]}).ToArray()};
        var ringsOnly=resource.Copy().SetMeshes([]).SetHierarchicalLods([empty]);
        var post=new EnbW3dHierarchyState(hlod.HierarchyName,Matrix4x4.Identity,2,new Dictionary<int,Matrix4x4>());
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(ringsOnly,empty,Matrix4x4.Identity,
            Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),post,out var bounds,out error),error);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(resource.Hierarchies.Single(),Matrix4x4.Identity,null,0,null,
            out var poses,out error,storedTranslationScale:2),error);
        var corners=new List<Vector3>();
        foreach(var ring in asset.Rings.Rings)
        {
            var center=ring.Asset.Header.Center;var extent=new Vector3(ring.Asset.Header.SecondRadii,0);
            foreach(int x in new[]{-1,1})foreach(int y in new[]{-1,1})
                corners.Add(Vector3.Transform(center+extent*new Vector3(x,y,1),poses.WorldTransforms[ring.PivotIndex]));
        }
        var minimum=corners.Aggregate(Vector3.Min);var maximum=corners.Aggregate(Vector3.Max);
        Assert.InRange(Vector3.Distance(minimum,bounds.LocalBox.Center-bounds.LocalBox.Extents),0,.001f);
        Assert.InRange(Vector3.Distance(maximum,bounds.LocalBox.Center+bounds.LocalBox.Extents),0,.001f);
    }

    [Fact]
    public void RingAssemblyRetainsPreparedNextAnimation()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.Load(assets,next:true,baseId:1856);
        Assert.NotNull(asset.Sequence);
        Assert.Equal(8,asset.Rings!.Rings.Count);
        Assert.Single(asset.Mixed!.Streaks);
        var observed=asset.Sequence.CreateObservations();var initial=observed.Record(0);
        var sample=observed.Record(500);Assert.Equal(2,sample.StateId);
        var assembly=asset.Rings;
        var runtime=new EnbRetailRingAssemblyRuntime(assembly);
        var world=Matrix4x4.CreateRotationZ(.4f)*Matrix4x4.CreateTranslation(100,200,300);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource!.Hierarchies.Single(),world,sample.Animation,sample.Frame,
            null,out var expected,out var error,storedTranslationScale:2),error);
        var before=runtime.BeforeAllocation(1,500,world,2,true,sample);
        Assert.NotEmpty(before.Rings);
        foreach(var ring in before.Rings)Assert.Equal(expected.WorldTransforms[ring.Binding.PivotIndex],ring.World);
        var prepared=runtime.Prepare(1,500,50,world,2,true,before.Rings.Select(r=>r.Binding.Ordinal).ToHashSet(),sample);
        Assert.Equal(before.Rings.Select(r=>r.World).ToArray(),prepared.Rings.Select(r=>r.World).ToArray());
        Assert.Contains(before.Rings,r=>r.World!=assembly.Pose(world,2,0,initial).WorldTransforms[r.Binding.PivotIndex]);
    }

    [Fact]
    public void InstalledHackingAssemblyKeepsRingsStreakAndAuthoredRslAnimation()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var resolved=assets.BaseAssetResolver!.Resolve(1856);
        var states=resolved.Rsl!.ResourceGraph!.States;
        var obj=states.First(s=>s.Definition.Type==EnbRslStateType.Object);
        var anim=states.First(s=>s.Definition.Type==EnbRslStateType.Animation);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources!,obj.Definition.Object!,out var source,out var error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        string name=EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(resource.Hierarchies.Single().Name,anim.Definition.Animation,anim.Definition.Suffix);
        Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(resource,anim.FileDependency,assets.Paths,assets.MixResources,
            assets.RetailMixResources!,name,out var loaded,out error),error);
        var assembly=EnbRetailRingAssembly.Load(assets,loaded.DrawResource,source.CurrentRetailAggregate!.Definition,loaded.Animation,anim.Definition.Flags);
        Assert.NotNull(assembly.Emitters);
        Assert.Equal(8,assembly.Rings.Count);Assert.Single(assembly.Emitters.Streaks);Assert.Empty(assembly.Emitters.Sprites);
        Assert.Equal(9,assembly.Rings.Count+assembly.Emitters.Streaks.Count);
        var expanded=EnbRetailRingAssembly.Load(assets,loaded.DrawResource,source.CurrentRetailAggregate.Definition with
            {Attachments=Enumerable.Range(0,40).SelectMany(_=>source.CurrentRetailAggregate.Definition.Attachments).ToArray()},loaded.Animation,anim.Definition.Flags);
        Assert.Equal(320,expanded.Rings.Count);
        Assert.Equal(40,expanded.Emitters!.Streaks.Count);
        Assert.Equal(40,expanded.Emitters.EmitterFixedCost/EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback);
        Assert.Equal(320,expanded.Rings.Select(r=>r.Ordinal).Distinct().Count());
        var expandedRuntime=new EnbCurrentRetailMixedParticleRuntime(expanded.Emitters,1,177,randomStreams:new());
        Assert.Equal(40,expandedRuntime.Buffers.Count);
        var drawn=assembly.Rings.Select(r=>r.Ordinal).ToHashSet();
        var phases=new EnbRetailRingAssemblyRuntime(assembly);
        var initial=phases.BeforeAllocation(1,250,Matrix4x4.Identity,1,true);
        Assert.All(initial.Rings,r=>Assert.Equal(r.Binding.Asset.Header.SecondRadii.Length(),r.Bounds.Radius));
        var updated=phases.Prepare(1,250,650,Matrix4x4.Identity,1,true,drawn);
        Assert.Contains(updated.Rings,r=>r.Bounds.Radius<initial.Rings.Single(b=>b.Binding.Ordinal==r.Binding.Ordinal).Bounds.Radius);
        Assert.Same(initial,phases.BeforeAllocation(1,250,Matrix4x4.Identity,1,true));
        var nextBudget=phases.BeforeAllocation(2,250,Matrix4x4.Identity,1,true);
        Assert.Equal(updated.Rings.Select(r=>r.Bounds),nextBudget.Rings.Select(r=>r.Bounds));
        Assert.Throws<ArgumentOutOfRangeException>(()=>phases.Prepare(1,250,650,Matrix4x4.Identity,1,true,drawn));
        phases.Prepare(2,250,500000,Matrix4x4.Identity,1,true,new HashSet<int>());
        phases.Prepare(3,250,500000,Matrix4x4.Identity,1,false,drawn);
        var resetBudget=phases.BeforeAllocation(4,250,Matrix4x4.Identity,1,true);
        Assert.All(resetBudget.Rings,r=>Assert.Equal(0,r.Progress));
        Assert.Equal(updated.Rings.Select(r=>r.Values),resetBudget.Rings.Select(r=>r.Values));
        Assert.Equal(updated.Rings.Select(r=>r.Bounds),resetBudget.Rings.Select(r=>r.Bounds));
        var runtime=new EnbRetailRingAssemblyRuntime(assembly);
        var first=runtime.Prepare(1,250,50,Matrix4x4.Identity,1,true,drawn);
        Assert.Equal(4,first.Rings.Count);
        Assert.Same(first,runtime.Prepare(1,9999,9999,Matrix4x4.Identity,1,true,drawn));
        Assert.Throws<ArgumentOutOfRangeException>(()=>runtime.Prepare(0,250,50,Matrix4x4.Identity,1,true,drawn));
        var progress=first.Rings.Select(r=>r.Progress).ToArray();
        var hidden=runtime.Prepare(2,250,500000,Matrix4x4.Identity,1,false,drawn);
        Assert.Empty(hidden.Rings);
        var restored=runtime.Prepare(3,250,50,Matrix4x4.Identity,1,true,drawn);
        Assert.Equal(progress,restored.Rings.Select(r=>r.Progress));
        Assert.Equal(progress,first.Rings.Select(r=>r.Progress));
        var scaled=new EnbRetailRingAssemblyRuntime(assembly).Prepare(1,250,50,Matrix4x4.CreateTranslation(2,3,4),2,true,drawn);
        var culled=runtime.Prepare(4,250,500000,Matrix4x4.Identity,1,true,new HashSet<int>());
        Assert.Equal(progress,culled.Rings.Select(r=>r.Progress));
        var resumed=runtime.Prepare(5,250,50,Matrix4x4.Identity,1,true,drawn);
        Assert.All(resumed.Rings,r=>Assert.True(r.Progress>progress[0]));
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(0,0,-10),Quaternion.Identity,new(800,600,1),Math.PI/3,new(.01f,1000),out var camera));
        var candidates=first.Rings.Select(r=>r.Candidate("ring/"+r.Binding.Ordinal,camera,20)).ToArray();
        var limited=EnbRenderDetailBudgetAllocator.AllocateShared(100,candidates);
        Assert.InRange(limited.FinalRenderCost,0,100);
        Assert.Equal(progress,first.Rings.Select(r=>r.Progress));
        for(int i=0;i<first.Rings.Count;i++)
        {
            Assert.Equal(first.Rings[i].Bounds.Radius*2,scaled.Rings[i].Bounds.Radius);
            Assert.True(Vector3.Distance(first.Rings[i].Bounds.Center*2+new Vector3(2,3,4),scaled.Rings[i].Bounds.Center)<.0001f);
            Assert.Null(first.Rings[i].Values.Sample(0));
            Assert.Equal(98,first.Rings[i].Values.Sample(20)!.Geometry.Positions.Length);
            Assert.Equal(22,first.Rings[i].Values.Sample(1)!.Geometry.Positions.Length);
        }
        var start=assembly.Pose(Matrix4x4.Identity,1,0);
        Assert.Contains(new uint[]{100,250,500,750},age=>
        {
            var pose=assembly.Pose(Matrix4x4.Identity,1,age);
            return assembly.Rings.Any(r=>pose.WorldTransforms[r.PivotIndex]!=start.WorldTransforms[r.PivotIndex]);
        });
        var particles=new EnbCurrentRetailMixedParticleRuntime(assembly.Emitters,1,177,randomStreams:new());
        var history=new EnbCurrentRetailParticlePoseHistory();
        for(uint age=0;age<=500000;age+=100)
        {
            history.Record(age,Matrix4x4.Identity);particles.Advance(assembly.Emitters,history,1);
            Assert.Single(history.Samples);
            Assert.All(particles.Buffers,b=>Assert.InRange(b.RetainedParticleCount,0,16));
        }
        var admitted=EnbCurrentRetailRObjEffectAsset.Load(assets,1856);
        Assert.Equal(8,admitted.Rings!.Rings.Count);
        Assert.Same(admitted.Mixed,admitted.Rings.Emitters);
        var recolored=EnbCurrentRetailRObjEffectAsset.Load(assets,1856,new Vector3(120,0,0));
        Assert.Same(recolored.Mixed,recolored.Rings!.Emitters);
        Assert.Same(recolored.Animation,recolored.Mixed!.Animation);
        Assert.All(recolored.Rings.Rings,r=>Assert.Equal(EnbCurrentRetailAssetVariantName.CreateTexture(r.Asset.Header.Texture,new(120,0,0)),r.Texture!.LogicalName));
    }
}
