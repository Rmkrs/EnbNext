using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleBirthVelocityTests
{
    [InstalledRetailFact]
    public void InstalledCygniJetIsFiniteLocalSpaceAndRetainsItsBirthVelocity()
    {
        var paths=EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2162);
        var asset=Assert.Single(loaded.AggregateParticles!);
        var authored=asset.Definition;
        Assert.True(authored.LocalSpace); Assert.Equal(1,authored.MaximumEmissions);
        Assert.Equal(100,authored.EmissionRatePerSecond);
        var life=EnbCurrentRetailParticleJetLifetime.Select(3,3,-1,333,180);
        var d=EnbCurrentRetailParticleJetLifetime.Retime(authored,3,life);
        Assert.True(d.LocalSpace);
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,Matrix4x4.Identity,true,1,true, new(180,0,0));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,10,10,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(180,0,0));
        Assert.Empty(state.WorldBirths);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,11,1,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(180,0,0));
        var birth=Assert.Single(state.WorldBirths);
        Assert.Equal(10,birth.Time); Assert.Equal(new Vector3(180,0,0),birth.LocalVelocityPerSecond);
        var moved=Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(40,50,60);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,100,89,moved,true,1,localVelocityPerSecond:new(900,0,0));
        Assert.Equal(birth,Assert.Single(state.WorldBirths)); Assert.Equal(moved,state.CurrentWorld);
        EnbResolvedRenderCamera.TryCreate(new(0,0,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Vector3[] Draw(float velocity)
        {
            var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,
                d.Copy().SetVelocityPerSecond(new(velocity,0,0)),asset.Texture,state.CurrentWorld,"",0,0);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,100,out var packet,out var receipt,out var error,
                worldBirths:state.WorldBirths),error);
            Assert.Equal(1,receipt.LiveParticleCount);
            return Assert.IsType<EnbW3dDrawTask>(packet).DeformedGeometry!.Positions.ToArray();
        }
        Assert.Equal(Draw(180),Draw(900));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,500,400,moved,false,1);
        Assert.Equal(birth,Assert.Single(state.WorldBirths)); Assert.Equal(1,state.EmittedCount);
        uint expiry=10+(uint)((double)d.LifetimeSeconds*1000);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,expiry,expiry-500,moved,false,1);
        Assert.Empty(state.WorldBirths);
    }

    [InstalledRetailTheory]
    [InlineData(0)]
    [InlineData(90)]
    public void ChangedEmitterVelocityLeavesOlderParticlesAndStoppedTailUnchanged(float yaw)
    {
        var paths=EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2163).AggregateParticles![0];
        var zero=new EnbCurrentRetailParticleRandomizer(0,Vector3.Zero);
        var d=asset.Definition.Copy().SetMaximumEmissions(0).SetBurstSize(1).SetEmissionRatePerSecond(10).SetLifetimeSeconds(1).SetVelocityPerSecond(Vector3.Zero).SetCreationRandomizer(zero).SetVelocityRandomizer(zero).SetOutwardVelocityPerSecond(0).SetVelocityInheritanceFactor(0).SetAccelerationPerSecondSquared(Vector3.Zero).SetInitialEmissionTimeSeconds(0).SetLocalSpace(false);
        var runtime=new EnbCurrentRetailShipParticleRuntime(d);
        var world=Matrix4x4.CreateRotationZ(yaw*MathF.PI/180);
        runtime.Advance(0,world,true,new(100,0,0));
        runtime.Advance(111,world,true,new(100,0,0));
        var first=Assert.Single(runtime.Births);
        runtime.Advance(211,world,true,new(200,0,0));
        runtime.Advance(211,world,true,new(999,0,0));
        Assert.Equal(first,runtime.Births[0]);
        Assert.Equal(2,runtime.Births.Count);
        Assert.Equal(new Vector3(200,0,0),runtime.Births[1].LocalVelocityPerSecond);
        runtime.Advance(250,world,false,new(999,0,0));
        Vector3[] Draw(float currentVelocity)
        {
            var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,
                d.Copy().SetVelocityPerSecond(new(currentVelocity,0,0)),asset.Texture,world,"",0,0);
            EnbResolvedRenderCamera.TryCreate(new(0,0,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,250,out var packet,out var receipt,out var error,
                worldBirths:runtime.Births),error);
            Assert.Equal(2,receipt.LiveParticleCount);
            return Assert.IsType<EnbW3dDrawTask>(packet).DeformedGeometry!.Positions.ToArray();
        }
        var positions=Draw(0); Assert.Equal(positions,Draw(999));
        Assert.Equal(8,positions.Length);
        for(int particle=0;particle<2;particle++)
        {
            var center=positions.Skip(particle*4).Take(4).Aggregate(Vector3.Zero,(sum,p)=>sum+p)/4;
            var expected=Vector3.Transform(new Vector3(particle==0?15:10,0,0),world);
            Assert.InRange(Vector3.Distance(center,expected),0,.0001f);
        }
        runtime.Advance(1099,world,false); Assert.Equal(2,runtime.Births.Count);
        runtime.Advance(1100,world,false); Assert.Single(runtime.Births);
        runtime.Advance(1200,world,false); Assert.Empty(runtime.Births);
    }
}
