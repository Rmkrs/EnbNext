using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbHmodelPrimitiveAttachmentTests
{
    [Theory]
    [InlineData(130)] [InlineData(230)]
    public void TerminalDeactivationResetsAllSiblingPosesWithoutStoppingEmitters(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,primitiveSibling:"both",modelSibling:true,forceChild:false,deactivate:true);
        var mixed=asset.Mixed!;var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,2,3,randomStreams:new());
        var history=new EnbCurrentRetailParticlePoseHistory();var observations=asset.Sequence!.CreateObservations();
        var world=Matrix4x4.CreateTranslation(10,20,30);
        for(uint age=0;age<=1000;age+=50)
        {
            history.Record(age,world);observations.Record(age);runtime.Advance(mixed,history,2,observations.Samples);
        }
        var sample=observations.Record(1000);Assert.False(sample.Active);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource!.Hierarchies.Single(),world,null,0,null,out var bind,out var why,storedTranslationScale:2),why);
        var rings=asset.Rings!.Pose(world,2,1000,sample);var spheres=asset.Spheres!.Pose(world,2,1000,sample);
        Assert.True(bind.WorldTransforms.SequenceEqual(rings.WorldTransforms));
        //98EF60/98A0A0 scale the stored pivot translations, not the basis.
        //Sphere and ring terminal poses therefore share the same rigid tree.
        Assert.True(bind.WorldTransforms.SequenceEqual(spheres.WorldTransforms));
        foreach(var model in asset.ModelAttachments)Assert.Equal(bind.WorldTransforms[model.PivotIndex],model.Pose(asset,world,0,1000,2,sample).World);
        for(int i=0;i<mixed.Streaks.Count;i++)
        {
            var buffer=runtime.Buffers[i];Assert.True(buffer.EmittedParticleCount>0);
            var position=(Vector3)buffer.GetType().GetField("_position",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(buffer)!;
            Assert.Equal(bind.WorldTransforms[mixed.Streaks[i].PivotIndex].Translation,position);
        }
        for(int i=0;i<mixed.Sprites.Count;i++)
        {
            var emitter=runtime.AnimatedSprites!.Emitters[i];Assert.True(emitter.EmittedCount>0);
            Assert.Equal(bind.WorldTransforms[mixed.Sprites[i].PivotIndex],emitter.CurrentWorld);
        }
    }

    [Theory]
    [InlineData(130,"ring")] [InlineData(230,"ring")]
    [InlineData(130,"sphere")] [InlineData(230,"sphere")]
    [InlineData(130,"both")] [InlineData(230,"both")]
    public void HmodelPrimitiveSiblingsUseObservedNextPose(int baseId,string kind)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,true,primitiveSibling:kind,forceChild:false);
        Assert.True(Assert.Single(asset.Resource!.HierarchicalLods).IsConvertedHierarchyModel);
        Assert.False(asset.ForceSubObjectLod);Assert.False(asset.UsesDetailBudget);
        Assert.Single(asset.Mixed!.Streaks);
        var observations=asset.Sequence!.CreateObservations();var initial=observations.Record(0);observations.Record(500);var sample=observations.Record(1000);
        Assert.Equal(2,sample.StateId);Assert.Equal(1,sample.Frame);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),Matrix4x4.Identity,
            sample.Animation,sample.Frame,null,out var pose,out var error),error);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),Matrix4x4.Identity,out var original,out error),error);
        if(kind is "ring" or "both")
        {
            var runtime=new EnbRetailRingAssemblyRuntime(asset.Rings!);
            runtime.BeforeAllocation(1,0,Matrix4x4.Identity,1,true,initial);
            var ring=Assert.Single(runtime.BeforeAllocation(2,1000,Matrix4x4.Identity,1,true,sample).Rings);
            Assert.Equal(pose.WorldTransforms[ring.Binding.PivotIndex].Translation,ring.World.Translation);
            Assert.True(Vector3.Distance(original[ring.Binding.PivotIndex].Translation,ring.World.Translation)>1);
        }
        if(kind is "sphere" or "both")
        {
            var runtime=new EnbRetailSphereAssemblyRuntime(asset.Spheres!);
            runtime.BeforeAllocation(1,0,Matrix4x4.Identity,1,true,initial);
            var sphere=Assert.Single(runtime.BeforeAllocation(2,1000,Matrix4x4.Identity,1,true,sample).Spheres);
            Assert.Equal(pose.WorldTransforms[sphere.Binding.PivotIndex].Translation,sphere.World.Translation);
            Assert.True(Vector3.Distance(original[sphere.Binding.PivotIndex].Translation,sphere.World.Translation)>1);
        }
    }
}
