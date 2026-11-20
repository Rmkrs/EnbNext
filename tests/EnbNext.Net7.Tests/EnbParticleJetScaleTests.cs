using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleJetScaleTests
{
    [Theory]
    [InlineData(.5f)]
    [InlineData(2f)]
    public void FactoryScaleChangesSpatialPropertiesBeforeJetLifetimeSelection(float scale)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var original=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(assets,2154).RequireParticleJet().Definition;
        var scaled=EnbCurrentRetailParticleJetScale.Apply(original,scale);
        Assert.Equal(original.VelocityPerSecond*scale,scaled.VelocityPerSecond);
        Assert.Equal(original.CreationRandomizer.Values*scale,scaled.CreationRandomizer.Values);
        Assert.Equal(original.VelocityRandomizer.Values*scale,scaled.VelocityRandomizer.Values);
        Assert.Equal(original.Size.Keys.Select(k=>k.Value*scale),scaled.Size.Keys.Select(k=>k.Value));
        Assert.Equal(original.Size.Keys.Select(k=>k.TimeSeconds),scaled.Size.Keys.Select(k=>k.TimeSeconds));
        Assert.Equal(original.LifetimeSeconds,scaled.LifetimeSeconds);
        Assert.Equal(original.EmissionRatePerSecond,scaled.EmissionRatePerSecond);
        Assert.Same(original.Color,scaled.Color);Assert.Same(original.Opacity,scaled.Opacity);
        Assert.Equal(BitConverter.UInt32BitsToSingle(original.LinePropertyWords[2])*scale,
            BitConverter.UInt32BitsToSingle(scaled.LinePropertyWords[2]));
        float distance=original.VelocityPerSecond.X;
        Assert.Equal(1/scale,EnbCurrentRetailParticleJetLifetime.Select(10,10,0,scaled.VelocityPerSecond.X,distance),5);
        Assert.Same(original,EnbCurrentRetailParticleJetScale.Apply(original,1));
    }
}
