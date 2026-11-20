using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10MixedSpriteEffectTests
{
    [InstalledRetailFact]
    public void AnimatedShrineRetainsWorldBirthsAndUsesFourByFourSpriteCells()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(EnbCurrentRetailRObjEffectAsset.Load(assets,1203).Mixed);
        Assert.NotNull(mixed.Animation);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,1,randomStreams:new());
        var history=new EnbCurrentRetailParticlePoseHistory();
        history.Record(0,Matrix4x4.Identity);runtime.Advance(mixed,history,1);
        history.Record(1000,Matrix4x4.Identity);runtime.Advance(mixed,history,1);
        Assert.NotNull(runtime.AnimatedSprites);
        var oldBirths=runtime.AnimatedSprites.Emitters.Select(e=>e.WorldBirths.ToArray()).ToArray();
        history.Record(1500,Matrix4x4.CreateTranslation(100,20,0));runtime.Advance(mixed,history,1);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,100000),out var camera);
        int atlasEmitters=0;
        for(int i=0;i<mixed.Sprites.Count;i++)
        {
            var child=mixed.Sprites[i];var state=runtime.AnimatedSprites.Emitters[i];
            if(child.Definition.FrameMode!=2)continue;
            atlasEmitters++;
            Assert.False(child.Definition.LocalSpace);
            Assert.NotEmpty(oldBirths[i]);
            foreach(var birth in oldBirths[i])Assert.Contains(birth,state.WorldBirths);
            var instance=new EnbW3dParticleEmitterDrawInstance(child.Definition.Name,"atlas-fixture",1,EnbRenderPassKind.OrdinaryWorld,
                child.Definition,child.Texture,Matrix4x4.Identity,"",child.PivotIndex,i);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(child.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,1500,
                out var packet,out var receipt,out var error,worldBirths:state.WorldBirths,motionHistory:state.MotionHistory),error);
            Assert.True(receipt.LiveParticleCount>0);
            var uv=packet!.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords!;
            Assert.Equal(receipt.LiveParticleCount*4,uv.Count);
            for(int j=0;j<uv.Count;j+=4)
            {
                var origin=uv[j];
                Assert.InRange(origin.X,0,.75f);Assert.InRange(origin.Y,0,.75f);
                Assert.Equal(MathF.Floor(origin.X*4),origin.X*4);Assert.Equal(MathF.Floor(origin.Y*4),origin.Y*4);
                Assert.Equal(origin+new Vector2(0,.25f),uv[j+1]);
                Assert.Equal(origin+new Vector2(.25f,.25f),uv[j+2]);
                Assert.Equal(origin+new Vector2(.25f,0),uv[j+3]);
            }
        }
        Assert.True(atlasEmitters>0);
    }

    [Theory]
    [InlineData(518)]
    [InlineData(516)]
    [InlineData(525)]
    [InlineData(526)]
    [InlineData(528)]
    [InlineData(534)]
    [InlineData(535)]
    [InlineData(537)]
    [InlineData(538)]
    [InlineData(1782)]
    [InlineData(1784)]
    public void InstalledMixedSpritesKeepAuthoredPivotsAndBuildIndependentPackets(int id)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(EnbCurrentRetailRObjEffectAsset.Load(assets,id).Mixed);
        Assert.NotEmpty(mixed.Resource.Meshes);
        Assert.NotEmpty(mixed.Sprites);
        Assert.Equal((mixed.Streaks.Count+mixed.Sprites.Count)*EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback,mixed.EmitterFixedCost);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,3,1,randomStreams:new());
        history.Record(0,Matrix4x4.Identity); runtime.Advance(mixed,history,3);
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(mixed.Resource.Hierarchies.Single(),Matrix4x4.Identity,null,0,null,out var initial,out var error,storedTranslationScale:3),error);
        for(int i=0;i<mixed.Sprites.Count;i++)
            if(runtime.Sprites[i].Continuous is null)
                Assert.Equal(initial.WorldTransforms[mixed.Sprites[i].PivotIndex].Translation,Assert.Single(runtime.Sprites[i].Poses.Samples).Position);
        history.Record(100,Matrix4x4.CreateTranslation(10,20,0)); runtime.Advance(mixed,history,3);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,100000),out var camera);
        for(int i=0;i<mixed.Sprites.Count;i++)
        {
            var child=mixed.Sprites[i]; var state=runtime.Sprites[i];
            if(state.Continuous is {} continuous)Assert.All(continuous.Births,b=>Assert.Equal(3,b.NativeBirthScale));
            var instance=new EnbW3dParticleEmitterDrawInstance(child.Definition.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,child.Definition,child.Texture,child.Definition.LocalSpace?state.CurrentWorld:Matrix4x4.Identity,"",child.PivotIndex,i);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(child.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,100,
                out var packet,out var receipt,out error,particleSizeScale:3,
                birthWorld:state.Continuous is null ? birth=>state.Poses.Resolve(birth,3) : null,
                worldBirths:state.Continuous?.Births,motionHistory:state.Continuous?.MotionHistory,
                inheritedVelocity:state.Continuous is null ? birth=>state.Poses.ResolveInheritedVelocity(birth,child.Definition.VelocityInheritanceFactor) : null),error);
            Assert.True(receipt.LiveParticleCount>0);
            Assert.NotNull(packet);
            Assert.NotSame(mixed.Resource.Meshes[0],packet.Mesh);
            state.Poses.RetainLatestSample();
        }
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,id,new(90,-.1f,-.1f)).Mixed!;
        Assert.Equal(mixed.Sprites.Count,shifted.Sprites.Count);
        for(int i=0;i<mixed.Sprites.Count;i++)
        {
            Assert.Equal(mixed.Sprites[i].PivotIndex,shifted.Sprites[i].PivotIndex);
            Assert.NotEqual(mixed.Sprites[i].Definition.TextureFileName,shifted.Sprites[i].Definition.TextureFileName);
            Assert.Same(shifted.Sprites[i].Definition,shifted.Resource.ExternalRenderObjects.Single(e=>e.Name==shifted.Sprites[i].Definition.Name).ParticleEmitterDefinition);
        }
    }
}
