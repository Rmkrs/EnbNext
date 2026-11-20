using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpiritExplosionTests
{
    [Fact]
    public void InstalledSpiritMissileUsesBoundedContinuousScrollingTrails()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2254);
        Assert.Equal(3,asset.AggregateParticles!.Count);
        Assert.Equal(2,asset.AggregateParticles.Count(p=>p.Definition.RenderMode==2));
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        long[]? priorCounts = null;
        foreach(uint time in new uint[]{0,100,1000,500000})
        {
            history.Record(time,Matrix4x4.CreateTranslation(time*.01f,0,0));runtime.Advance(asset,history,1);
            var counts = runtime.Emitters.Select(e => e.EmittedCount).ToArray();
            if (priorCounts is not null)
                for (int i = 0; i < counts.Length; i++) Assert.True(counts[i] >= priorCounts[i]);
            priorCounts = counts;
        }
        Assert.Single(history.Samples);
        //0097E540..59D reduces long frames by the capacity/gcd repeat window.
        //The exact long-frame counts are covered by native caller controls;
        //this installed-asset check covers continuity and retained live births.
        Assert.All(runtime.Emitters,e=>{Assert.True(e.IsEmitting);Assert.True(e.EmittedCount>0);Assert.InRange(e.WorldBirths.Count,1,32);});
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledLargeFiniteAggregateRetainsAllowanceAfterLongFrameAndCompletesWithUpdates(bool delayed)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2256);
        Assert.Equal(25,asset.AggregateParticles!.Count);
        Assert.Equal(12,asset.AggregateParticles.Count(p=>p.Definition.RenderMode==2));
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        void Advance(uint time)
        {
            history.Record(time,Matrix4x4.CreateTranslation(time*.001f,0,0));runtime.Advance(asset,history,1);
            Assert.Single(history.Samples);
            Assert.All(runtime.Emitters,e=>Assert.InRange(e.WorldBirths.Count,0,12));
        }
        Advance(0);
        if(!delayed)for(uint time=100;time<=60000;time+=100)Advance(time);
        else
        {
            Advance(60000);
            //Whole0097E530/0097EB80 native-burst9-{single,double}-precision
            //controls: capacity9/burst9/rate100 reduces60000ms to zero.
            var first = asset.AggregateParticles[0].Definition;
            Assert.Equal("spirit_p8", first.Name);
            Assert.Equal(100f, first.EmissionRatePerSecond);
            Assert.Equal(9u, first.RuntimeBurstSize);
            Assert.Equal(9u, EnbRetailParticleEmissionArithmetic.Capacity(first));
            Assert.Equal(0, runtime.Emitters[0].EmittedCount);
            Assert.True(runtime.Emitters[0].IsEmitting);
            Advance(60010);
            Assert.Equal(0, runtime.Emitters[0].EmittedCount);
            Advance(60011);
            Assert.Equal(9, runtime.Emitters[0].EmittedCount);
            for(uint time=60111;time<=120011;time+=100)Advance(time);
        }
        for(int i=0;i<runtime.Emitters.Count;i++)
        {
            var state=runtime.Emitters[i];
            Assert.Equal(asset.AggregateParticles[i].Definition.MaximumEmissions,state.EmittedCount);
            Assert.False(state.IsEmitting);Assert.Empty(state.WorldBirths);Assert.Empty(state.Worlds);
        }
        Advance(500000);
        for(int i=0;i<runtime.Emitters.Count;i++)Assert.Equal(asset.AggregateParticles[i].Definition.MaximumEmissions,runtime.Emitters[i].EmittedCount);
    }
}
