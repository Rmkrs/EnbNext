using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStaticAttachedHlodTests
{
    [Fact]
    public void WaveAssemblyRetainsNestedPoseAndFiniteLineAllowance()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,1884);
        Assert.Empty(loaded.Resource!.Meshes);
        var attachment=Assert.Single(loaded.ModelAttachments);
        Assert.Single(attachment.Asset.Resource!.Meshes);
        Assert.Equal(2,loaded.CompositeEmitters.Count);
        var original=assets.BaseAssetResolver!.Resolve(1884).W3d!;
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(original,out var parent,out _));
        var child=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,"WE_WAVE_EMA",Vector3.Zero).Resource!;
        var world=Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(12,23,34);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(parent.Hierarchies.Single(),world,null,0,out var parentPose,out _));
        int bone=parent.Hierarchies.Single().Pivots.Single(p=>p.Name=="EXPLOSION BONE").Ordinal;
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(child.Hierarchies.Single(),parentPose.WorldTransforms[bone],null,0,out var childPose,out _));
        var attached=attachment.Pose(loaded,world,0,0);
        Assert.Equal(parentPose.WorldTransforms[bone],attached.World);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(attachment.Asset.Resource.Hierarchies.Single(),attached.World,null,0,out var combined,out _));
        int meshPivot=(int)attachment.Asset.Resource.HierarchicalLods.Single().Levels.Single().Members.Single().ReferenceIndex;
        var expected=Vector3.Transform(new Vector3(3,4,5),childPose.WorldTransforms[1]);
        var actual=Vector3.Transform(new Vector3(3,4,5),combined.WorldTransforms[meshPivot]);
        Assert.True(Vector3.Distance(expected,actual)<.0001f);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(child,child.HierarchicalLods.Single(),_=>true,out var childCost,out _));
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(attachment.Asset.Resource,attachment.Asset.Resource.HierarchicalLods.Single(),_=>true,out var combinedCost,out _));
        Assert.Equal(childCost[0],combinedCost[0]);
        foreach(var particle in loaded.CompositeEmitters)
        {
            var runtime=new EnbRetailParticleSceneLifetime(particle.Asset.Definition,0);
            runtime.Prepare(0,world,1,true,true);runtime.Callback(0,0,world,1);
            runtime.Callback(101,101,world,1);runtime.AdvanceForRender(101);
            runtime.Callback(201,100,world,1);runtime.AdvanceForRender(201);
            Assert.Equal(1,runtime.Emitter.EmittedCount);Assert.False(runtime.Emitter.IsEmitting);
            Assert.Single(runtime.LiveBirths);
            //Scene removal clears the finite emitter endpoint independently
            //of its surviving buffer. Do not test the obsolete replay path.
            runtime.Destroy(201,world,1);Assert.False(runtime.Emitter.HasLineEndpoint);
            runtime.AdvanceForRender(601);Assert.Empty(runtime.LiveBirths);Assert.True(runtime.CanRetire);
        }
    }
}
