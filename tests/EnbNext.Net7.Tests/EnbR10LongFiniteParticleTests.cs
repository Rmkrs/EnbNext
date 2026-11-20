using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10LongFiniteParticleTests
{
    [Fact]
    public void DirtCloudHasBoundedBirthsAndUsesAuthoredLongLifetime()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1693).AggregateParticles!);
        var d=asset.Definition;
        Assert.Equal(375,d.MaximumEmissions);Assert.Equal(375u,d.RuntimeBurstSize);
        Assert.Equal(5_000_000_100L,asset.EndMilliseconds);
        var instance=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,100000),out var camera);
        foreach(long age in new long[]{100,101,1000,705_032_804,uint.MaxValue,5_000_000_099,5_000_000_100})
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,age,
                out var packet,out var receipt,out var error),error);
            int count=age>100 && age<asset.EndMilliseconds?375:0;
            Assert.Equal(count,receipt.LiveParticleCount);
            if(count==0)Assert.Null(packet);
            else Assert.NotNull(packet);
        }
        Assert.False(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,
            instance with {Definition=d.Copy().SetMaximumEmissions(0)},camera,EnbW3dLightEnvironment.BlackNoLights,1,0,101,
            out _,out _,out var unboundedError));
        Assert.Contains("excessive particle buffer",unboundedError);
    }

    [Fact]
    public void ParticleElapsedTimeSurvivesTickWrapAndRepeatedFrames()
    {
        var elapsed=new EnbParticleElapsedTime(100);
        Assert.Equal(0,elapsed.Observe(100));
        Assert.Equal(2_000_000_000,elapsed.Observe(2_000_000_100));
        Assert.Equal(4_000_000_000,elapsed.Observe(4_000_000_100));
        Assert.Equal(5_000_000_000,elapsed.Observe(unchecked((uint)5_000_000_100L)));
        Assert.Equal(5_000_000_000,elapsed.Observe(unchecked((uint)5_000_000_100L)));
    }

    [Fact]
    public void MobBloodBurstCapsFinalBatchAtFourHundredAndExpires()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1977).AggregateParticles!
            .Single(p=>p.Definition.Name.Equals("mwrkblood04",StringComparison.OrdinalIgnoreCase));
        var d=asset.Definition;
        Assert.Equal(400,d.MaximumEmissions);Assert.Equal(15u,d.RuntimeBurstSize);
        var instance=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,100000),out var camera);
        foreach(var sample in new[]{(Time:260,Count:375),(Time:261,Count:390),(Time:270,Count:390),(Time:271,Count:400),(Time:1020,Count:0)})
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,sample.Time,
                out _,out var receipt,out var error),error);
            Assert.Equal(sample.Count,receipt.LiveParticleCount);
        }
    }

    [Theory]
    [InlineData(1562)]
    [InlineData(1696)]
    [InlineData(1697)]
    public void LongFiniteBuffersRetainBirthsWithoutAccumulatingFrameSamples(int id)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id).AggregateParticles!);
        var d=asset.Definition;
        Assert.True(d.LifetimeSeconds > 10);
        var history=new EnbCurrentRetailParticlePoseHistory();
        var instance=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,100000),out var camera);
        Matrix4x4? firstBirth=null;
        int maximumLive=0;
        for (uint t=0;t<=40000;t+=5)
        {
            history.Record(t,Matrix4x4.CreateTranslation(t,0,0));
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,t,
                out _,out var receipt,out var error,birthWorld:birth=>history.Resolve(birth,1)),error);
            maximumLive=Math.Max(maximumLive,receipt.LiveParticleCount);
            if(receipt.LiveParticleCount>0)
            {
                firstBirth ??= history.Resolve(asset.BirthMilliseconds,1);
                Assert.Equal(firstBirth.Value,history.Resolve(asset.BirthMilliseconds,1));
            }
            history.RetainLatestSample();
            Assert.Single(history.Samples);
            if(t>=asset.EndMilliseconds) Assert.Equal(0,receipt.LiveParticleCount);
        }
        Assert.NotNull(firstBirth);
        Assert.Equal(d.MaximumEmissions,maximumLive);
    }
}
