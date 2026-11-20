using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbEmptyAggregateEffectTests
{
    [InstalledRetailTheory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyHmodelRetainsContinuousSpriteWithoutStreakSibling(bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.HmodelStreak(assets,130,next,continuousOnly:true);
        Assert.Empty(asset.Resource!.Meshes);Assert.Single(asset.Resource.HierarchyModels);Assert.Null(asset.Mixed);
        var particle=Assert.Single(asset.AggregateParticles!);Assert.Equal(0,particle.Definition.MaximumEmissions);
        var observations=asset.Sequence?.CreateObservations();var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        for(uint age=0;age<=1000;age+=50)
        {
            history.Record(age,System.Numerics.Matrix4x4.CreateTranslation(age*.1f,0,0));observations?.Record(age);
            runtime.Advance(asset,history,1,observations?.Samples);
        }
        var sample=observations?.Record(1000);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),System.Numerics.Matrix4x4.CreateTranslation(100,0,0),
            sample?.Animation??asset.Animation,sample?.Frame??1,null,out var pose,out var error),error);
        var emitter=Assert.Single(runtime.Emitters);Assert.True(emitter.EmittedCount>0);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),System.Numerics.Matrix4x4.CreateTranslation(100,0,0),out var staticPose,out error),error);
        Assert.True(System.Numerics.Vector3.Distance(pose.WorldTransforms[particle.PivotIndex].Translation,staticPose[particle.PivotIndex].Translation)>1);
        Assert.Equal(pose.WorldTransforms[particle.PivotIndex],emitter.CurrentWorld);
        Assert.Contains(emitter.WorldBirths,b=>System.Numerics.Vector3.Distance(b.World.Translation,emitter.CurrentWorld.Translation)>1);
    }

    [InstalledRetailTheory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyHmodelAnimationMovesItsParticleChildren(bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.Load(assets,next,130);
        Assert.Empty(asset.Resource!.Meshes);
        Assert.Empty(Assert.Single(asset.Resource.HierarchyModels).Nodes);
        Assert.NotEmpty(asset.AggregateParticles!);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var observations=asset.Sequence?.CreateObservations();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        for(uint t=0;t<=500;t+=50)
        {
            history.Record(t,System.Numerics.Matrix4x4.Identity);
            observations?.Record(t);
            runtime.Advance(asset,history,2,observations?.Samples);
        }
        var sample=observations?.Record(500);
        if(next)Assert.Equal(2,sample!.Value.StateId);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),System.Numerics.Matrix4x4.Identity,
            sample?.Animation??asset.Animation,sample?.Frame??1,null,out var pose,out var error,storedTranslationScale:2),error);
        Assert.Equal(asset.AggregateParticles!.Count,runtime.Emitters.Count);
        Assert.Contains(asset.AggregateParticles,p=>p.PivotIndex>0);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),System.Numerics.Matrix4x4.Identity,
            asset.Animation,0,null,out var initial,out error,storedTranslationScale:2),error);
        for(int i=0;i<runtime.Emitters.Count;i++)
        {
            Assert.Equal(pose.WorldTransforms[asset.AggregateParticles[i].PivotIndex],runtime.Emitters[i].CurrentWorld);
            int pivot=asset.AggregateParticles[i].PivotIndex;
            if(pivot>0)Assert.True(System.Numerics.Vector3.Distance(initial.WorldTransforms[pivot].Translation,
                runtime.Emitters[i].CurrentWorld.Translation)>1);
        }
    }

    [InstalledRetailFact]
    public void NamedEmptyHierarchyModelRetainsItsHierarchyWithoutGeometry()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,"EXPLSK01",System.Numerics.Vector3.Zero);
        Assert.False(asset.IsNullObject);Assert.False(asset.UsesDetailBudget);
        Assert.Empty(asset.Resource!.Meshes);Assert.Empty(asset.Resource.HierarchicalLods);
        var model=Assert.Single(asset.Resource.HierarchyModels);
        Assert.Empty(model.Nodes);
        Assert.Equal(model.HierarchyName,Assert.Single(asset.Resource.Hierarchies).Name);
        Assert.Null(asset.Particle);Assert.Null(asset.AggregateParticles);Assert.Null(asset.Mixed);
        Assert.Empty(asset.Sample(0,100000));
        var converted=EnbW3dHmodelConversion.Create(model,_=>true);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(asset.Resource,converted,_=>true,out var costs,out var error),error);
        Assert.Equal(0,Assert.Single(costs));
        Assert.Equal(1e-6f,EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(costs[0]));
    }

    [Theory]
    [InlineData(212,30,false)] [InlineData(212,30,true)]
    [InlineData(214,10,false)] [InlineData(214,10,true)]
    public void EmptyAggregateRetainsOrdinaryHlod(int id,int meshCount,bool forceChild)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var resolution=assets.BaseAssetResolver!.Resolve(id);
        Assert.Empty(resolution.W3d!.CurrentRetailAggregate!.Definition.Attachments);
        var source=resolution.W3d!;var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition with {Flags=forceChild ? aggregate.RenderDefinition.Flags|1u : aggregate.RenderDefinition.Flags&~1u};
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,resolution with {W3d=source with {CurrentRetailAggregate=new(definition,
            aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)}});
        Assert.Equal(forceChild,asset.ForceSubObjectLod);
        Assert.Null(asset.Mixed);
        Assert.Equal(meshCount,asset.Resource!.Meshes.Count);
        Assert.Single(asset.Resource.HierarchicalLods);
        Assert.True(asset.UsesDetailBudget);
    }
}
