using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleJetSubdivisionTests
{
    [Theory]
    [InlineData(2167)]
    [InlineData(2168)]
    [InlineData(2148)]
    [InlineData(2149)]
    [InlineData(2150)]
    [InlineData(2154)]
    [InlineData(2155)]
    [InlineData(2156)]
    public void InstalledSubdividedJetBuildsRetainedGeometryAndExpires(int id)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(EnbRetailAssetBundle.Load(paths),id).RequireParticleJet();
        var d=asset.Definition;Assert.True(d.LocalSpace);Assert.Equal(0,d.MaximumEmissions);
        Assert.InRange(d.LinePropertyWords[1],1u,3u);
        float original=(uint)((double)d.LifetimeSeconds*1000)*.001f;
        d=EnbCurrentRetailParticleJetLifetime.Retime(d,original,EnbCurrentRetailParticleJetLifetime.Select(original,original,-1,d.VelocityPerSecond.X,180));
        EnbCurrentRetailRObjEffectAsset.ValidateParticleJet(d);
        uint lifetime=(uint)((double)d.LifetimeSeconds*1000);
        var velocity=new Vector3(180/(lifetime*.001f),0,0);
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,Matrix4x4.Identity,true,1,true,velocity);
        uint tick=d.InitialEmissionTimeSeconds>0?20u:401u;
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,tick,tick,Matrix4x4.Identity,true,1,localVelocityPerSecond:velocity);
        Assert.NotEmpty(state.WorldBirths);
        var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,tick,out var packet,out _,out var error,
            worldBirths:state.WorldBirths,activeEmissionRun:state.EmissionRun),error);
        Assert.True(Assert.IsType<EnbW3dDrawTask>(packet).Mesh.Indices.Count>=12);
        long emitted=state.EmittedCount;
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,tick+1,1,Matrix4x4.Identity,false,1);
        Assert.False(state.HasLineEndpoint);Assert.Equal(emitted,state.EmittedCount);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,tick+lifetime+1,lifetime,Matrix4x4.Identity,false,1);
        Assert.Empty(state.WorldBirths);
    }
}
