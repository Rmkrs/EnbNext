using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10StaticBoundParticleTests
{
    [Fact]
    public void InstalledAccuracyEffectRetainsAuthoredZeroBurstAndLoads()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1717);
        var children=asset.AggregateParticles??asset.Mixed!.Sprites;
        var zero=children.Where(p=>p.Definition.BurstSize==0).ToArray();
        Assert.NotEmpty(zero);Assert.All(zero,p=>Assert.Equal(1u,p.Definition.RuntimeBurstSize));
    }

    [Fact]
    public void AnimatedBirthHistoryExpiresAtTheSameTruncatedLifetimeAsPackets()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var original=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1589);
        var particle=original.AggregateParticles![0];
        particle=particle with {Definition=particle.Definition.Copy().SetEmissionRatePerSecond(100).SetLifetimeSeconds(.073f).SetMaximumEmissions(1).SetBurstSize(1)};
        var asset=original with {AggregateParticles=[particle]};
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        void Advance(uint time){history.Record(time,Matrix4x4.Identity);runtime.Advance(asset,history,1);}
        Advance(0);Advance(11);Advance(81);
        var emitter=Assert.Single(runtime.Emitters);
        Assert.Equal(10,Assert.Single(emitter.WorldBirths).Time);
        Advance(82);Assert.Single(emitter.WorldBirths);
        Advance(83);Assert.Empty(emitter.WorldBirths);Assert.Empty(emitter.Births);Assert.Empty(emitter.Worlds);
    }

    [Fact]
    public void InstalledPsionicShieldRetainsItsLowRateSprite()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1161);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(asset.Mixed);
        var sprite=Assert.Single(mixed.Sprites);
        Assert.Equal("shld_psy_p04",sprite.Definition.Name,ignoreCase:true);
        Assert.Equal(.1f,sprite.Definition.EmissionRatePerSecond);
        Assert.Equal(10000u,sprite.BirthMilliseconds);
        Assert.Equal(10,mixed.Resource.Meshes.Count);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,1,0,randomStreams:new());
        var history=new EnbCurrentRetailParticlePoseHistory();
        foreach(uint time in new uint[]{0,10000,10001})
        {history.Record(time,Matrix4x4.Identity);runtime.Advance(mixed,history,1);}
        var births=runtime.AnimatedSprites is {} animated
            ? animated.Emitters.Single().WorldBirths.ToArray()
            : runtime.Sprites.Single().Continuous!.Births.ToArray();
        Assert.Equal(10000,Assert.Single(births).Time);
    }

    [Theory]
    [InlineData(1760)]
    [InlineData(1593)]
    public void ParticleOnlyStreakAssemblyHasNoMeshBudgetAndRetainsIndependentBirths(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(asset.Mixed);
        Assert.Empty(mixed.Resource.Meshes);
        Assert.False(asset.UsesDetailBudget);
        Assert.NotEmpty(mixed.Streaks);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailMixedParticleRuntime(mixed,100,123,randomStreams:new());
        void Advance(uint time,Matrix4x4 world)
        { history.Record(time,world);runtime.Advance(mixed,history,100); }
        Advance(0,Matrix4x4.Identity);Advance(101,Matrix4x4.Identity);
        var first=runtime.Buffers.Select(b=>b.Sample(101).ToArray()).ToArray();
        Advance(101,Matrix4x4.CreateTranslation(20,30,40));
        for(int i=0;i<first.Length;i++)
        { Assert.NotEmpty(first[i]);Assert.Equal(first[i],runtime.Buffers[i].Sample(101)); }
        Advance(201,Matrix4x4.CreateTranslation(20,30,40));
        var beforeLongFrame=runtime.Buffers.Select(b=>b.EmittedParticleCount).ToArray();
        Advance(500000,Matrix4x4.CreateTranslation(20,30,40));
        Assert.Single(history.Samples);
        for(int i=0;i<runtime.Buffers.Count;i++)
        {
            var b=runtime.Buffers[i];
            Assert.True(b.EmittedParticleCount>beforeLongFrame[i]);
            Assert.InRange(b.RetainedParticleCount,1,1000);
            Assert.NotEmpty(b.Sample(500000));
            Assert.Empty(b.Sample(520000));
        }
    }

    [Theory]
    [InlineData(0,9)]
    [InlineData(5,5)]
    public void BoundBurstsKeepDistinctOrdinalsAndTruncateFiniteAllowance(int maximum,int expected)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var original=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1589);
        var particle=original.AggregateParticles![0];
        particle=particle with {Definition=particle.Definition.Copy().SetBurstSize(3).SetMaximumEmissions(maximum)};
        var asset=original with {AggregateParticles=[particle]};
        EnbCurrentRetailRObjParticleAsset.ValidateAnimated(particle.Definition);
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        history.Record(0,Matrix4x4.Identity);runtime.Advance(asset,history,1);
        history.Record(301,Matrix4x4.CreateTranslation(30,0,0));runtime.Advance(asset,history,1);
        var emitter=Assert.Single(runtime.Emitters);
        Assert.Equal(expected,emitter.EmittedCount);
        Assert.Equal(Enumerable.Range(0,expected).Select(i=>(long)i),emitter.WorldBirths.Select(b=>b.Ordinal));
        Assert.Equal(3,emitter.WorldBirths.Count(b=>b.Time==100));
        Assert.Equal(maximum==0 ? 3 : 2,emitter.WorldBirths.Count(b=>b.Time==200));
        Assert.Equal(maximum==0,emitter.IsEmitting);
        var first=emitter.WorldBirths.ToArray();
        runtime.Advance(asset,history,1);Assert.Equal(first,emitter.WorldBirths);
        history.Record(500000,Matrix4x4.Identity);runtime.Advance(asset,history,1);
        Assert.Single(history.Samples);
        if(maximum>0) {Assert.Empty(emitter.WorldBirths);Assert.Empty(emitter.Worlds);Assert.Equal(expected,emitter.EmittedCount);}
        else {Assert.InRange(emitter.WorldBirths.Count,1,15);Assert.True(emitter.EmittedCount>expected);}
    }

    [Theory]
    [InlineData(1910)]
    [InlineData(2147)]
    public void IdentityPivotsStillUseRetainedContinuousEmission(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        Assert.NotNull(asset.Resource);
        Assert.Null(asset.Animation);
        Assert.All(asset.Resource!.Hierarchies.Single().Pivots,p=>Assert.True(p.LocalTransform.IsIdentity));
        var history=new EnbCurrentRetailParticlePoseHistory();var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        history.Record(0,Matrix4x4.Identity);runtime.Advance(asset,history,1);
        history.Record(100,Matrix4x4.Identity);runtime.Advance(asset,history,1);
        var first=runtime.Emitters.Select(e=>e.WorldBirths.ToArray()).ToArray();
        history.Record(150,Matrix4x4.CreateTranslation(20,0,0));runtime.Advance(asset,history,1);
        for(int i=0;i<first.Length;i++)
        { Assert.NotEmpty(first[i]);Assert.Equal(first[i],runtime.Emitters[i].WorldBirths.Take(first[i].Length)); }
        history.Record(500000,Matrix4x4.CreateTranslation(20,0,0));runtime.Advance(asset,history,1);
        Assert.All(runtime.Emitters,e=>{Assert.True(e.IsEmitting);Assert.NotEmpty(e.WorldBirths);Assert.InRange(e.WorldBirths.Count,1,100);});
        Assert.Single(history.Samples);
    }

    [Fact]
    public void FiniteNoiseLinesStopAtAuthoredAllowanceAndDrainWithoutRestarting()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1795);
        Assert.Equal(8,asset.AggregateParticles!.Count);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        void Advance(uint age)
        { history.Record(age,Matrix4x4.CreateTranslation(age*.01f,0,0));runtime.Advance(asset,history,10); }
        Advance(0);Advance(51);
        Assert.All(runtime.Emitters,e=>{Assert.Equal(5,e.EmittedCount);Assert.True(e.IsEmitting);});
        Advance(101);
        var original=runtime.Emitters.Select(e=>e.WorldBirths.ToArray()).ToArray();
        Assert.All(runtime.Emitters,e=>{Assert.Equal(10,e.EmittedCount);Assert.False(e.IsEmitting);});
        Advance(201);
        for(int i=0;i<original.Length;i++) Assert.Equal(original[i],runtime.Emitters[i].WorldBirths);
        Advance(1000);Advance(500000);
        Assert.All(runtime.Emitters,e=>{Assert.Equal(10,e.EmittedCount);Assert.Empty(e.WorldBirths);});
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TractorInheritsPerBoneWorldVelocityAndLocalBuffersIgnoreIt(bool local)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var original=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1589);
        var asset=original with {AggregateParticles=original.AggregateParticles!
            .Select(p=>p with {Definition=p.Definition.Copy().SetLocalSpace(local)}).ToArray()};
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        history.Record(0,Matrix4x4.Identity);runtime.Advance(asset,history,10);
        history.Record(101,Matrix4x4.Identity);runtime.Advance(asset,history,10);
        var first=runtime.Emitters.Select(e=>e.WorldBirths.Single()).ToArray();
        var moved=Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(20,0,0);
        history.Record(201,moved);runtime.Advance(asset,history,10);
        for(int i=0;i<2;i++)
        {
            var e=runtime.Emitters[i];
            Assert.Equal(first[i],e.WorldBirths[0]);
            Assert.Equal(Vector3.Zero,first[i].InheritedVelocityPerMillisecond);
            var pivot=asset.Resource!.Hierarchies.Single().Pivots[asset.AggregateParticles![i].PivotIndex];
            var before=pivot.LocalTransform.Translation*10;
            var current=Vector3.Transform(before,moved);
            var expected=local ? Vector3.Zero : (current-before)*(.75f/100);
            Assert.InRange(Vector3.Distance(expected,e.WorldBirths[1].InheritedVelocityPerMillisecond),0,.000001f);
        }
    }

    [Fact]
    public void WormholeBirthsPreserveFourAuthoredPivotsAndSurviveSourceMotion()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1801);
        Assert.Null(asset.Animation);
        Assert.NotNull(asset.Resource);
        var particles=asset.AggregateParticles!;
        Assert.Equal(8,particles.Count);
        Assert.Equal(4,particles.Select(p=>p.PivotIndex).Distinct().Count());
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        void Advance(uint age,Matrix4x4 world)
        { history.Record(age,world);runtime.Advance(asset,history,10); }
        Advance(0,Matrix4x4.Identity);
        Advance(1000,Matrix4x4.Identity);
        var original=runtime.Emitters.Select(e=>e.WorldBirths.ToArray()).ToArray();
        for(int i=0;i<particles.Count;i++)
        {
            var pivot=asset.Resource!.Hierarchies.Single().Pivots[particles[i].PivotIndex];
            Assert.NotEmpty(original[i]);
            Assert.InRange(Vector3.Distance(original[i][0].World.Translation,pivot.LocalTransform.Translation*10),0,.0001f);
            Assert.InRange(original[i][0].World.M11,-10.001f,-9.999f);
        }
        var moved=Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(20,30,40);
        Advance(1100,moved);
        for(int i=0;i<particles.Count;i++)
        {
            Assert.Equal(original[i],runtime.Emitters[i].WorldBirths.Take(original[i].Length));
            var pivot=asset.Resource!.Hierarchies.Single().Pivots[particles[i].PivotIndex];
            var expected=pivot.LocalTransform*Matrix4x4.CreateScale(10)*moved;
            Assert.InRange(Vector3.Distance(expected.Translation,runtime.Emitters[i].CurrentWorld.Translation),0,.0001f);
        }
        // A long-running continuous effect consumes history and retains only live births.
        Advance(500000,moved);
        Assert.Single(history.Samples);
        Assert.All(runtime.Emitters,e=> {
            Assert.NotEmpty(e.WorldBirths);
            Assert.InRange(e.WorldBirths.Count,1,16);
            Assert.All(e.WorldBirths,b=>Assert.InRange(500000-b.Time,0,2999));
        });
    }
}
