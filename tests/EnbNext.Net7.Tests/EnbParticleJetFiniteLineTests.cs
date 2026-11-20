using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleJetFiniteLineTests
{
    [Fact]
    public void InstalledFizzleExhaustsOneFourParticleBurstWithoutDroppingEndpointOrRestarting()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var bundle=EnbRetailAssetBundle.Load(paths);
        Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(bundle,2235).AggregateParticles!);
        var asset=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(bundle,2235).RequireParticleJet();
        var d=asset.Definition;
        Assert.Equal(4,d.MaximumEmissions);Assert.Equal(4u,d.RuntimeBurstSize);
        Assert.Equal(2.5f,d.InitialEmissionTimeSeconds);Assert.Equal(3333,(int)(1000d/d.EmissionRatePerSecond));
        float original=(uint)((double)d.LifetimeSeconds*1000)*.001f;
        d=EnbCurrentRetailParticleJetLifetime.Retime(d,original,EnbCurrentRetailParticleJetLifetime.Select(original,original,-1,d.VelocityPerSecond.X,180));
        EnbCurrentRetailRObjEffectAsset.ValidateParticleJet(d);
        Assert.Equal(833,(int)((double)d.LifetimeSeconds*1000));
        Assert.NotEqual(Vector3.Zero,EnbRetailParticleColorCurve.Sample(d.Color.Keys,167,833));
        Assert.Equal(Vector3.Zero,EnbRetailParticleColorCurve.Sample(d.Color.Keys,567,833));
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,Matrix4x4.Identity,true,1,true,new(180/.833f,0,0));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,833,833,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(180/.833f,0,0));
        Assert.Empty(state.WorldBirths);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,834,1,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(180/.833f,0,0));
        // Retimed native capacity is two; the queue keeps the final two of the four births.
        Assert.Equal(4,state.EmittedCount);
        Assert.Equal(2,state.WorldBirths.Count);Assert.All(state.WorldBirths,b=>Assert.Equal(833,b.Time));
        Assert.Equal(new long[]{2,3},state.WorldBirths.Select(b=>b.Ordinal));
        Assert.False(state.IsEmitting);Assert.True(state.HasLineEndpoint);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,1665,831,Matrix4x4.CreateTranslation(10,20,30),true,1,localVelocityPerSecond:new(999,0,0));
        Assert.Equal(2,state.WorldBirths.Count);
        Assert.All(state.WorldBirths,b=>Assert.Equal(new Vector3(180/.833f,0,0),b.LocalVelocityPerSecond));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,1666,1,Matrix4x4.Identity,true,1);
        Assert.Empty(state.WorldBirths);Assert.True(state.HasLineEndpoint);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,5000,3334,Matrix4x4.Identity,true,1);
        Assert.Empty(state.WorldBirths);Assert.Equal(4,state.EmittedCount);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,6000,1000,Matrix4x4.Identity,false,1);
        Assert.False(state.HasLineEndpoint);
    }
}
