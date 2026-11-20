using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMissileHmodelEffectTests
{
    [InstalledRetailTheory]
    [InlineData(130,false)] [InlineData(130,true)] [InlineData(230,false)] [InlineData(230,true)]
    public void HmodelLineAttachmentRetainsAnimatedEndpointAndWorldBirths(int baseId,bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        string name=EnbCurrentRetailRObjEffectAsset.Load(assets,1570).Mixed!.Sprites.Single().Definition.Name;
        var asset=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,next,continuousOnly:true,particlePrototype:name);
        var particle=Assert.Single(asset.AggregateParticles!);Assert.Equal(2u,particle.Definition.RenderMode);
        var history=new EnbCurrentRetailParticlePoseHistory();var observations=asset.Sequence?.CreateObservations();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        for(uint age=0;age<=500;age+=50)
        {history.Record(age,Matrix4x4.Identity);observations?.Record(age);runtime.Advance(asset,history,1,observations?.Samples);}
        var emitter=Assert.Single(runtime.Emitters);Assert.True(emitter.EmittedCount>0);Assert.True(emitter.HasLineEndpoint);
        var sample=observations?.Record(500);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource!.Hierarchies.Single(),Matrix4x4.Identity,sample?.Animation??asset.Animation,sample?.Frame??1,null,out var pose,out var why),why);
        Assert.Equal(pose.WorldTransforms[particle.PivotIndex],emitter.CurrentWorld);
        Assert.True(emitter.CurrentWorld.Translation.Length()>1);
        Assert.Contains(emitter.WorldBirths,b=>Vector3.Distance(b.World.Translation,emitter.CurrentWorld.Translation)>1);
        var cameraOk=EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Assert.True(cameraOk);
        var instance=new EnbW3dParticleEmitterDrawInstance(name,name,1,EnbRenderPassKind.OrdinaryWorld,particle.Definition,particle.Texture,emitter.CurrentWorld,"",particle.PivotIndex,0);
        EnbW3dDrawTask Draw(bool active)
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(particle.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,500,
                out var packet,out _,out var error,worldBirths:emitter.WorldBirths,activeEmissionRun:active?emitter.EmissionRun:null),error);
            return Assert.IsType<EnbW3dDrawTask>(packet);
        }
        Assert.Equal(Draw(false).Mesh.Positions.Count+2,Draw(true).Mesh.Positions.Count);
    }

    [InstalledRetailTheory]
    [InlineData(130,false)] [InlineData(130,true)] [InlineData(230,false)] [InlineData(230,true)]
    public void HmodelStreakAndSpriteSiblingsFollowObservedParent(int baseId,bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.HmodelStreak(assets,baseId,next);
        Assert.Empty(asset.Resource!.HierarchicalLods);Assert.Single(asset.Resource.HierarchyModels);
        Assert.Equal(baseId==130,asset.Resource.Meshes.Count==0);
        Assert.True(asset.ForceSubObjectLod);Assert.Null(asset.AggregateParticles);
        var mixed=asset.Mixed!;Assert.Same(asset.Resource,mixed.Resource);
        var streak=Assert.Single(mixed.Streaks);Assert.NotEmpty(mixed.Sprites);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,2,3,randomStreams:new());
        var history=new EnbCurrentRetailParticlePoseHistory();var observations=asset.Sequence?.CreateObservations();
        for(uint age=0;age<=500;age+=50)
        {
            history.Record(age,Matrix4x4.CreateTranslation(age*.1f,20,30));observations?.Record(age);
            runtime.Advance(mixed,history,2,observations?.Samples);
        }
        var sample=observations?.Record(500);
        if(next)Assert.Equal(2,sample!.Value.StateId);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),Matrix4x4.CreateTranslation(50,20,30),
            sample?.Animation??asset.Animation,sample?.Frame??1,null,out var pose,out var error,storedTranslationScale:2),error);
        var buffer=Assert.Single(runtime.Buffers);Assert.True(buffer.EmittedParticleCount>0);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),Matrix4x4.CreateTranslation(50,20,30),out var staticPose,out error),error);
        Assert.True(Vector3.Distance(pose.WorldTransforms[streak.PivotIndex].Translation,staticPose[streak.PivotIndex].Translation)>1);
        var position=(Vector3)buffer.GetType().GetField("_position",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(buffer)!;
        Assert.Equal(pose.WorldTransforms[streak.PivotIndex].Translation,position);
        Assert.Equal(mixed.Sprites.Count,runtime.AnimatedSprites!.Emitters.Count);
        for(int i=0;i<mixed.Sprites.Count;i++)
        {
            Assert.True(runtime.AnimatedSprites.Emitters[i].EmittedCount>0);
            Assert.Equal(pose.WorldTransforms[mixed.Sprites[i].PivotIndex],runtime.AnimatedSprites.Emitters[i].CurrentWorld);
        }
    }

    [InstalledRetailTheory]
    [InlineData(false)] [InlineData(true)]
    public void ExplicitAnimationMovesHmodelMeshAndAttachedParticlePivot(bool next)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EffectAggregateAnimationFixture.Load(assets,next);
        Assert.Equal("MOVE",asset.Animation!.Name);Assert.Null(asset.Mixed);
        var tree=asset.Resource!.Hierarchies.Single();
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        var observations=asset.Sequence?.CreateObservations();
        for(uint age=0;age<=500;age+=50){history.Record(age,Matrix4x4.Identity);observations?.Record(age);runtime.Advance(asset,history,2,observations?.Samples);}
        var observed=observations?.Record(500);
        if(next){Assert.NotNull(observed);Assert.Equal(2,observed.Value.StateId);Assert.Equal("MOVE2",observed.Value.Animation.Name);}
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(tree,Matrix4x4.Identity,observed?.Animation??asset.Animation,observed?.Frame??1,null,out var pose,out var error,storedTranslationScale:2),error);
        var emitter=Assert.Single(runtime.Emitters);var particle=Assert.Single(asset.AggregateParticles!);
        Assert.Equal(pose.WorldTransforms[particle.PivotIndex],emitter.CurrentWorld);
        Assert.True(emitter.EmittedCount>0);
        Assert.Contains(emitter.WorldBirths,b=>Vector3.Distance(b.World.Translation,emitter.CurrentWorld.Translation)>1);
        Assert.Equal(next?0:1,Assert.Single(asset.Sample(0,500,observed)).Frame);
    }

    [InstalledRetailFact]
    public void Ms03KeepsStaticModelAndSeparateAuthoredParticlePivot()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,230);
        Assert.Null(asset.Mixed);Assert.Null(asset.Selection);Assert.False(asset.UsesDetailBudget);
        var resource=asset.Resource!;Assert.Single(resource.HierarchyModels);Assert.Empty(resource.HierarchicalLods);
        var particle=Assert.Single(asset.AggregateParticles!);
        Assert.Equal("ms03a-pat",particle.Definition.Name,ignoreCase:true);
        Assert.Equal("BOX02",resource.Hierarchies.Single().Pivots[particle.PivotIndex].Name,ignoreCase:true);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        for(uint t=0;t<=1000;t+=10)
        {
            history.Record(t,Matrix4x4.CreateRotationZ(t*.001f)*Matrix4x4.CreateTranslation(t*.1f,0,0));
            runtime.Advance(asset,history,2);
        }
        var state=Assert.Single(runtime.Emitters);
        Assert.True(state.EmittedCount>0);
        Assert.Contains(state.WorldBirths,b=>Vector3.Distance(b.World.Translation,state.CurrentWorld.Translation)>.1f);
        var missing=EnbCurrentRetailRObjEffectAsset.Load(assets,229);
        Assert.Equal(new[]{4,7},missing.AggregateParticles!.Select(p=>p.PivotIndex));
        Assert.Equal(EnbTextureResolutionStatus.MissingResource,assets.Textures.Resolve("MISSILE.TGA").Status);
    }
    [Theory]
    [InlineData(228)] [InlineData(235)] [InlineData(236)]
    public void StaticMissileRetainsAuthoredHmodelPivotTransforms(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        Assert.Equal(EnbTextureResolutionStatus.MissingResource,assets.Textures.Resolve("MISSILE.TGA").Status);
        // Structural packet verification cannot stand in for a textured GPU draw.
        var resource=asset.Resource!;
        var model=Assert.Single(resource.HierarchyModels);
        Assert.Equal(2,resource.Meshes.Count);Assert.Empty(resource.HierarchicalLods);
        var owner=new EnbSceneObjectSnapshot(1,1,1,false,false,0,id,1,0,0,0,0,false,"missile fixture",new(false,0,null),null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        var world=Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(100,200,300);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(resource.Hierarchies.Single(),world,out var poses,out var why),why);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,world,500,EnbRenderPassKind.OrdinaryWorld,
            null,EnbW3dLightEnvironment.BlackNoLights,[],out var list,out var error),error);
        Assert.NotEmpty(list.Packets);Assert.Empty(list.ParticleEmitters);
        foreach(var packet in list.Packets)
        {
            Assert.Null(packet.DeformedGeometry);
            Assert.Contains(model.Nodes,n=>packet.WorldTransform==poses[n.PivotIndex]);
        }
        Assert.Contains(list.Packets,p=>p.WorldTransform!=world);
    }
}
