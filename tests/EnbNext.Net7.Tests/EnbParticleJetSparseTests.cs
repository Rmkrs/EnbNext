using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleJetSparseTests
{
    [Fact]
    public void InstalledVrixInitialOffsetAdvancesBirthWithoutChangingIntervalOrLifetime()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var d=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(EnbRetailAssetBundle.Load(paths),2169).RequireParticleJet().Definition;
        Assert.Equal(1,d.InitialEmissionTimeSeconds);Assert.Equal(1,d.EmissionRatePerSecond);
        float current=(uint)((double)d.LifetimeSeconds*1000)*.001f;
        d=EnbCurrentRetailParticleJetLifetime.Retime(d,current,EnbCurrentRetailParticleJetLifetime.Select(current,current,-1,d.VelocityPerSecond.X,180));
        EnbCurrentRetailRObjEffectAsset.ValidateParticleJet(d);
        Assert.Equal(83,(int)((double)d.LifetimeSeconds*1000));Assert.Equal(1,d.InitialEmissionTimeSeconds);
        foreach(bool warm in new[]{false,true})
        {
            var definition=d.Copy().SetInitialEmissionTimeSeconds(warm?1:0);
            var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(definition);
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,0,0,Matrix4x4.Identity,true,1,true,new(180/.083f,0,0));
            Assert.Empty(state.WorldBirths);
            uint first=warm?1u:1001u;
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,first,first,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(180/.083f,0,0));
            var birth=Assert.Single(state.WorldBirths);Assert.Equal(warm?0:1000,birth.Time);
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,first+24,24,Matrix4x4.Identity,false,1);
            Assert.Single(state.WorldBirths);Assert.False(state.HasLineEndpoint);
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,first+82,58,Matrix4x4.Identity,false,1);
            Assert.Empty(state.WorldBirths);Assert.Equal(1,state.EmittedCount);
        }
    }
}
