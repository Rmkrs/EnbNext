using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10StandaloneStreakEffectTests
{
    [Fact]
    public void ContemplationPrewarmStartsAtInitialPoseAndRetainsIndependentBurstTails()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var child=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2411).StandaloneStreak!;
        var d=child.Definition;
        Assert.False(d.LocalSpace);Assert.Equal(1,d.InitialEmissionTimeSeconds);
        Assert.Equal(1,d.EmissionRatePerSecond);Assert.Equal(100u,d.RuntimeBurstSize);
        var moving=new EnbCurrentRetailStreakBufferRuntime(d,1,7,randomStreams:new());
        var stationary=new EnbCurrentRetailStreakBufferRuntime(d,1,7,randomStreams:new());
        var initial=Matrix4x4.CreateTranslation(20,30,40);
        foreach(var buffer in new[]{moving,stationary})
        {
            buffer.Advance(1000,initial);
            Assert.Empty(buffer.Sample(1000));
        }
        moving.Advance(1050,Matrix4x4.CreateRotationZ(1)*Matrix4x4.CreateTranslation(200,300,400));
        stationary.Advance(1050,initial);
        Assert.Equal(100,moving.EmittedParticleCount);
        Assert.Equal(100,moving.Sample(1050).Count);
        var expected=stationary.Sample(1050);var actual=moving.Sample(1050);
        for(int i=0;i<expected.Count;i++)
        {
            // 008D2580 divides by theta, including at t=0; 0097E810's
            // conjugate product retains the squared nonunit factor.
            float nativeScale=MathF.Pow(MathF.Sin(.5f)/.5f,2);
            Assert.InRange(Vector3.Distance(initial.Translation+(expected[i].Head-initial.Translation)*nativeScale,actual[i].Head),0,.0001f);
            Assert.InRange(Vector3.Distance(initial.Translation+(expected[i].Tail-initial.Translation)*nativeScale,actual[i].Tail),0,.0001f);
            Assert.Equal(expected[i].HeadColor,actual[i].HeadColor);
        }
        var repeat=moving.Sample(1050);
        moving.Advance(1050,initial);
        Assert.Equal(repeat,moving.Sample(1050));
        moving.Advance(1051,initial,false);
        Assert.Equal(100,moving.Sample(1999).Count);
        Assert.Empty(moving.Sample(2000));
        Assert.Equal(100,moving.EmittedParticleCount);
        EnbCurrentRetailStreakBufferRuntime.Validate(d.Copy().SetInitialEmissionTimeSeconds(61));
    }

    [Theory]
    [InlineData(1583)] [InlineData(1584)] [InlineData(1585)]
    [InlineData(1586)] [InlineData(1587)] [InlineData(1588)]
    public void FactoryRoutesStandaloneStreakWithoutFabricatingHierarchyAndRetainsHsv(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var plain=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        var child=Assert.IsType<EnbCurrentRetailStreakAttachment>(plain.StandaloneStreak);
        Assert.Null(plain.Resource);Assert.Null(plain.Mixed);Assert.Null(plain.Particle);
        Assert.False(plain.UsesDetailBudget);Assert.Null(child.Texture);
        Assert.Equal(3u,child.Definition.RenderMode);Assert.Equal(50,child.Definition.MaximumEmissions);
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,id,new(120,.1f,0)).StandaloneStreak!;
        Assert.Equal(child.Definition.Color.Keys.Count,shifted.Definition.Color.Keys.Count);
        Assert.NotEqual(child.Definition.Color.Keys[1].NormalizedRgb,shifted.Definition.Color.Keys[1].NormalizedRgb);
        Assert.Equal(child.Definition.Color.Keys[1].TimeSeconds,shifted.Definition.Color.Keys[1].TimeSeconds);
        var buffer=new EnbCurrentRetailStreakBufferRuntime(shifted.Definition,1,0,randomStreams:new());
        buffer.Advance(0,Matrix4x4.Identity);buffer.Advance(171,Matrix4x4.CreateTranslation(10,0,0));
        Assert.Equal(50,buffer.EmittedParticleCount);Assert.False(buffer.IsEmitting);
        Assert.Equal(50,buffer.Sample(171).Count);
        buffer.Advance(172,Matrix4x4.Identity,false);
        Assert.Empty(buffer.Sample(1171));
    }
}
