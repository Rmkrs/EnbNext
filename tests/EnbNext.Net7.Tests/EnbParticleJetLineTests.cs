using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleJetLineTests
{
    [Theory]
    [InlineData(2198)]
    [InlineData(2197)]
    [InlineData(2313)]
    public void AntimatterJetUsesScopedLoadingAndRetainedLineEndpoint(int id)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.NotNull(EnbCurrentRetailRObjEffectAsset.Load(assets,id).Particle);
        var asset=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(assets,id).Particle!;
        Assert.NotNull(asset);var d=asset.Definition;
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.SupportsEngineTrail(d));
        Assert.Equal(0,d.MaximumEmissions);Assert.Equal(id==2313,d.LocalSpace);
        _=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(assets,id).RequireParticleJet();
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,Matrix4x4.Identity,true,1,true,new(100,0,0));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,31,31,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(100,0,0));
        Assert.Equal(id==2313?3:6,state.WorldBirths.Count);var first=state.WorldBirths[0];
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,41,10,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(200,0,0));
        Assert.Equal(first,state.WorldBirths[0]);Assert.Equal(new Vector3(200,0,0),state.WorldBirths[^1].LocalVelocityPerSecond);
        var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        EnbResolvedRenderCamera.TryCreate(new(10,20,30),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        int Indices(bool active)
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,41,out var packet,out _,out var error,
                worldBirths:state.WorldBirths,activeEmissionRun:active?state.EmissionRun:null),error);
            return Assert.IsType<EnbW3dDrawTask>(packet).Mesh.Indices.Count;
        }
        Assert.True(Indices(true)>Indices(false));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,42,1,Matrix4x4.Identity,false,1);
        Assert.False(state.HasLineEndpoint);Assert.NotEmpty(state.WorldBirths);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,1000,958,Matrix4x4.Identity,false,1);
        Assert.Empty(state.WorldBirths);
    }
}
