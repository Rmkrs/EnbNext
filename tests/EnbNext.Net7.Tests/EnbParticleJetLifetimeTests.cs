using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleJetLifetimeTests
{
    [Theory]
    [InlineData(3,3,-1,333,180,.99999994f)]
    [InlineData(3,3,-1,333,10000,3)]
    [InlineData(3,3,5,333,180,5)]
    [InlineData(3,3,5,333,10000,3)]
    [InlineData(3,3,-1,0,.02f,2)]
    [InlineData(3,.8f,1,1,0,.8f)]
    [InlineData(3,1.2f,1,1,0,1.2f)]
    [InlineData(3,.799f,1,1,0,1)]
    [InlineData(3,1.201f,1,1,0,1)]
    public void SelectionPreservesMinimumBranchAndStrictTolerance(float original,float current,float minimum,float velocity,float distance,float expected)
        => Assert.Equal(expected,EnbCurrentRetailParticleJetLifetime.Select(original,current,minimum,velocity,distance),6);

    [Fact]
    public void RetimingCopiesAllSixTimelinesWithoutChangingValuesOrAuthoredDefinition()
    {
        var paths=EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,2163).AggregateParticles![0].Definition;
        var scalar=new EnbCurrentRetailParticleScalarProperty(7,[new(0,2),new(3,4),new(3,5)]);
        var color=new EnbCurrentRetailParticleColorProperty(new(1,2,3),
            [new(0,10,20,30,0),new(3,40,50,60,0){RuntimeRgb=new Vector3(.1f,.2f,.3f)}]);
        original=original.Copy().SetLifetimeSeconds(3).SetColor(color).SetOpacity(scalar).SetSize(scalar).SetRotation(scalar).SetFrame(scalar).SetBlurTime(scalar).SetInitialOrientationRandom(.7f);
        var scaled=EnbCurrentRetailParticleJetLifetime.Retime(original,3,1);
        Assert.Equal(1,scaled.LifetimeSeconds);
        Assert.Equal(3,original.LifetimeSeconds);
        Assert.Equal(3,original.Color.Keys[1].TimeSeconds);
        Assert.Equal(1,scaled.Color.Keys[1].TimeSeconds);
        Assert.Equal(original.Color.Keys[1].NormalizedRgb,scaled.Color.Keys[1].NormalizedRgb);
        Assert.Equal(original.Color.Random,scaled.Color.Random);
        foreach(var property in new[]{scaled.Opacity,scaled.Size,scaled.Rotation,scaled.Frame,scaled.BlurTime!})
        {
            Assert.Equal(new float[]{0,1,1},property.Keys.Select(k=>k.TimeSeconds));
            Assert.Equal(new float[]{2,4,5},property.Keys.Select(k=>k.Value));
            Assert.Equal(7,property.Random);
            Assert.NotSame(scalar.Keys,property.Keys);
        }
        Assert.Equal(.7f,scaled.InitialOrientationRandom);
        Assert.Equal(original.VelocityPerSecond,scaled.VelocityPerSecond);
        Assert.Equal(original.EmissionRatePerSecond,scaled.EmissionRatePerSecond);
        Assert.Null(EnbCurrentRetailParticleJetLifetime.Retime(original.Copy().SetBlurTime(null),3,1).BlurTime);
    }
}
