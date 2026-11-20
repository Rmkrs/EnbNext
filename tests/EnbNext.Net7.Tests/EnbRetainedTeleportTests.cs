using System.Numerics;
using System.Reflection;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetainedTeleportTests
{
    [Theory]
    [InlineData("av_tele01",3200)]
    [InlineData("av_tele02",1000)]
    [InlineData("av_tele03",1000)]
    [InlineData("av_tele04",500)]
    public void AvatarTimerRetiresEmitterWhileParticlesRetainActivationWorld(string name,int duration)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var beam=new EnbStationBeamEffect(assets,name);
        Assert.Equal(duration,beam.OwnerDurationMilliseconds);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,
            new(800,600,1),Math.PI/2,new(1,100000),out var camera));
        var origin=Matrix4x4.CreateTranslation(10,20,30);
        beam.Build(0,origin,camera);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var particles=(List<EnbRetailParticleSceneLifetime>)typeof(EnbStationBeamEffect).GetField("_particles",flags)!.GetValue(beam)!;
        Assert.NotEmpty(particles);
        Assert.All(particles,p=>Assert.Equal(0,p.Emitter.EmittedCount));
        int age=0;
        for(age=17;age<duration;age+=17)beam.Build(age,Matrix4x4.CreateTranslation(10000,20000,30000),camera);
        Assert.Equal(origin,(Matrix4x4)typeof(EnbStationBeamEffect).GetField("_world",flags)!.GetValue(beam)!);
        Assert.Contains(particles,p=>p.Emitter.EmittedCount>0);
        //7897D0 removes the owner at the frame-delta countdown endpoint.
        beam.Build(age+17,origin,camera);
        Assert.All(particles,p=>Assert.True(p.Destroyed));
        long emitted=particles.Sum(p=>p.Emitter.EmittedCount);
        for(int time=age+34;time<beam.TailDurationMilliseconds+100;time+=17)beam.Build(time,origin,camera);
        Assert.Equal(emitted,particles.Sum(p=>p.Emitter.EmittedCount));
        Assert.True(beam.Retired);
        Assert.Empty(beam.Build((long)Math.Ceiling(beam.TailDurationMilliseconds)+200,origin,camera));
    }

    [Fact]
    public void SharedSceneKeepsDestroyedOwnersBuffersAcrossReplacement()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var prototype=new EnbStationBeamEffect(assets,"av_tele04");
        var first=prototype.CreateInstance();var second=prototype.CreateInstance();
        var scene=new EnbRetailTeleportScene();
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var particles=(List<EnbRetailParticleSceneLifetime>)typeof(EnbStationBeamEffect).GetField("_particles",flags)!.GetValue(first)!;
        uint replacement=0;
        for(;replacement<500;replacement+=17)
        {
            first.Prepare(scene,replacement,replacement,Matrix4x4.Identity);
            scene.Advance(replacement,replacement==0?0u:17u);
            if(particles.Any(p=>p.LiveBirths.Count>0))break;
        }
        Assert.True(replacement<500,"Authored entrance visibility window emitted no live particles.");
        replacement++;
        first.Destroy(replacement);
        long emitted=particles.Sum(p=>p.Emitter.EmittedCount);
        second.Prepare(scene,replacement,0,Matrix4x4.CreateTranslation(500,0,0));scene.Advance(replacement,1);
        Assert.False(first.Retired);
        for(uint time=replacement+17;time<2000;time+=17)
        {
            second.Prepare(scene,time,time-replacement,Matrix4x4.Identity);scene.Advance(time,17);
        }
        Assert.Equal(emitted,particles.Sum(p=>p.Emitter.EmittedCount));
        Assert.True(first.Retired);Assert.True(second.Retired);
    }
}
