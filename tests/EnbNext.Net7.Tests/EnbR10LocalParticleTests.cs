using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10LocalParticleTests
{
    [Theory]
    [InlineData(2253,2)]
    [InlineData(2257,0)]
    [InlineData(2258,0)]
    public void InstalledLocalParticlesFollowCurrentTransformRatherThanBirthPose(int id,float initial)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        var asset=loaded.Particle ?? Assert.Single(loaded.AggregateParticles!);
        Assert.True(asset.Definition.LocalSpace);
        Assert.Equal(initial,asset.Definition.InitialEmissionTimeSeconds);
        var first=Draw(asset,asset.Definition,Matrix4x4.Identity,750);
        var moved=Draw(asset,asset.Definition,Matrix4x4.CreateTranslation(100,20,30),750);
        Assert.NotEmpty(first);
        Assert.Equal(first.Length,moved.Length);
        for(int i=0;i<first.Length;i++) Assert.InRange(Vector3.Distance(moved[i]-first[i],new(100,20,30)),0,.001f);
    }

    [Fact]
    public void LocalPrewarmUsesNegativeBirthsAndRotatesAccelerationWithoutInheritance()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2258).AggregateParticles!);
        var d=asset.Definition.Copy().SetInitialEmissionTimeSeconds(1).SetMaximumEmissions(1).SetBurstSize(1).SetEmissionRatePerSecond(10).SetLifetimeSeconds(3).SetCreationRandomizer(new(0,Vector3.Zero)).SetVelocityRandomizer(new(0,Vector3.Zero)).SetVelocityPerSecond(new(2,0,0)).SetAccelerationPerSecondSquared(new(0,4,0)).SetVelocityInheritanceFactor(99);
        var world=Matrix4x4.CreateScale(3)*Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(10,20,0);
        var vertices=Draw(asset,d,world,100,3);
        // Birth at -900 ms, sampled at 100 ms: local (2,2,0), then scale/rotate/translate.
        var center=vertices.Aggregate(Vector3.Zero,(a,b)=>a+b)/vertices.Length;
        Assert.InRange(Vector3.Distance(center,new(4,26,0)),0,.001f);
        Assert.Equal(2100u,(asset with {Definition=d}).EndMilliseconds);
        Assert.False(EnbCurrentRetailParticleEmitterRuntime.SupportsEmissionSpace(d.Copy().SetLocalSpace(false)));
    }

    [Fact]
    public void ContinuousLocalPrewarmIsConsumedOnceAndRemovalFreezesBufferTransform()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2253).Particle!;
        var runtime=new EnbCurrentRetailShipParticleRuntime(asset.Definition);
        runtime.Advance(0,Matrix4x4.Identity,true);
        Assert.Equal(9,runtime.EmittedCount); // strict 200ms interval inside 2000ms remainder
        Assert.Empty(runtime.Births); // native buffer defers its same-clock queue
        runtime.Advance(1,Matrix4x4.Identity,true);
        Assert.Equal(5,runtime.Births.Count); // four live prewarm births and the birth at zero
        Assert.All(runtime.Births,b=>{Assert.True(b.Time<=0);Assert.Equal(Matrix4x4.Identity,b.World);});
        runtime.Advance(1,Matrix4x4.Identity,true);
        Assert.Equal(10,runtime.EmittedCount);
        var moved=Matrix4x4.CreateTranslation(100,20,0);
        runtime.Advance(100,moved,true);
        Assert.Equal(10,runtime.EmittedCount);
        Assert.Equal(moved,runtime.CurrentWorld);
        runtime.Advance(101,Matrix4x4.Identity,false);
        Assert.Equal(moved,runtime.CurrentWorld);
        runtime.Advance(1200,Matrix4x4.Identity,false);
        Assert.Empty(runtime.Births);
        Assert.Equal(10,runtime.EmittedCount);
    }

    [Fact]
    public void AnimatedLocalPrewarmDoesNotRepeatAndBufferTracksItsCurrentPivot()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var original=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1814);
        var particle=Assert.Single(original.AggregateParticles!);
        particle=particle with {Definition=particle.Definition.Copy().SetRenderMode(0).SetFrameMode(0).SetLocalSpace(true).SetInitialEmissionTimeSeconds(1).SetMaximumEmissions(0).SetBurstSize(1).SetEmissionRatePerSecond(10).SetLifetimeSeconds(2)};
        EnbCurrentRetailRObjParticleAsset.ValidateAnimated(particle.Definition);
        var animation=new EnbW3dRenderHierarchyAnimation(0,0x30000,"VISIBLE",original.Resource!.Hierarchies.Single().Name,2,10,false,[],[]);
        var asset=original with {Animation=animation,AggregateParticles=[particle]};
        var history=new EnbCurrentRetailParticlePoseHistory();
        var runtime=new EnbCurrentRetailAnimatedParticleRuntime();
        history.Record(0,Matrix4x4.Identity);runtime.Advance(asset,history,2);
        var state=Assert.Single(runtime.Emitters);
        Assert.Equal(9,state.EmittedCount);
        Assert.All(state.WorldBirths,b=>Assert.True(b.Time<0));
        var first=state.WorldBirths.ToArray();
        var initialWorld=state.CurrentWorld;
        //0098EF60 scales child data/translations;00989670 keeps the owner
        //basis rigid. The local buffer must not apply this scale a second time.
        Assert.InRange(new Vector3(initialWorld.M11,initialWorld.M12,initialWorld.M13).Length(),.99999f,1.00001f);
        Assert.All(state.WorldBirths,b=>Assert.Equal(2,b.NativeBirthScale));
        runtime.Advance(asset,history,2);
        Assert.Equal(first,state.WorldBirths);
        history.Record(100,Matrix4x4.CreateTranslation(30,40,50));runtime.Advance(asset,history,2);
        Assert.Equal(10,state.EmittedCount);
        Assert.InRange(Vector3.Distance(state.CurrentWorld.Translation-initialWorld.Translation,new(30,40,50)),0,.001f);
        Assert.Equal(first,state.WorldBirths.Take(first.Length));
        Assert.Single(history.Samples);
    }

    private static Vector3[] Draw(EnbCurrentRetailRObjParticleAsset asset,EnbCurrentRetailParticleEmitterDefinition d,Matrix4x4 world,int age,float scale=1)
    {
        var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,world,"",0,0);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,age,out var packet,out var receipt,out var error,particleSizeScale:scale,
            birthWorld:_=>throw new Exception("Local particle requested a world birth"),
            inheritedVelocity:_=>throw new Exception("Local particle requested source velocity")),error);
        Assert.True(receipt.LiveParticleCount>0);
        return Assert.IsType<EnbW3dDrawTask>(packet).DeformedGeometry!.Positions.ToArray();
    }
}
