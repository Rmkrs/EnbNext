using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbBoundBurstTests
{
    [InstalledRetailTheory]
    [InlineData(20)]
    [InlineData(25)]
    public void BurstRetainsEachBirthAndClampsFinalAllowance(int allowance)
    {
        var paths=EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,2163);
        Assert.Equal(2,loaded.AggregateParticles!.Count);
        var authored=Assert.Single(loaded.AggregateParticles,p=>p.Definition.Name.Equals("cyg_bub_02p1",StringComparison.OrdinalIgnoreCase)).Definition;
        Assert.Equal(20,authored.MaximumEmissions); Assert.Equal(20u,authored.RuntimeBurstSize);
        Assert.Equal(0,authored.InitialEmissionTimeSeconds);
        var definition=authored.Copy().SetMaximumEmissions(allowance);
        EnbCurrentRetailRObjParticleAsset.ValidateAnimated(definition);
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(definition);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,0,0,Matrix4x4.Identity,true,1,true);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,10,10,Matrix4x4.Identity,true,1);
        Assert.Empty(state.WorldBirths);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,11,1,Matrix4x4.CreateTranslation(1,0,0),true,1);
        Assert.Equal(20,state.WorldBirths.Count);
        Assert.All(state.WorldBirths,b=>Assert.Equal(10,b.Time));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,21,10,Matrix4x4.CreateTranslation(11,0,0),true,1);
        Assert.Equal(allowance,state.EmittedCount);
        Assert.Equal(allowance,state.WorldBirths.Count);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,100,79,Matrix4x4.Identity,true,1);
        Assert.Equal(allowance,state.EmittedCount);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,521,421,Matrix4x4.Identity,false,1);
        Assert.Empty(state.WorldBirths);
    }
}
