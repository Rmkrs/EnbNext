using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleJetCrystalTests
{
    [InstalledRetailFact]
    public void InstalledCrystalBurstsRetainVelocityFollowLocalPoseAndDrain()
    {
        var paths=EnbInstalledAssetLocator.TryLocate(); Assert.NotNull(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(EnbRetailAssetBundle.Load(paths),2311).RequireParticleJet();
        var authored=asset.Definition;
        Assert.True(authored.LocalSpace); Assert.Equal(0,authored.MaximumEmissions);
        Assert.Equal(5u,authored.RuntimeBurstSize); Assert.Equal(1u,authored.FrameMode);
        float current=(uint)((double)authored.LifetimeSeconds*1000)*.001f;
        float life=EnbCurrentRetailParticleJetLifetime.Select(current,current,-1,authored.VelocityPerSecond.X,180);
        var d=EnbCurrentRetailParticleJetLifetime.Retime(authored,current,life);
        EnbCurrentRetailRObjEffectAsset.ValidateParticleJet(d);
        Assert.Equal(166,(int)((double)d.LifetimeSeconds*1000));
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(d);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,0,0,Matrix4x4.Identity,true,1,true,new(100,0,0));
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,20,20,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(100,0,0));
        Assert.Empty(state.WorldBirths);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,21,1,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(100,0,0));
        Assert.Equal(5,state.WorldBirths.Count);
        var first=state.WorldBirths.ToArray();
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,41,20,Matrix4x4.Identity,true,1,localVelocityPerSecond:new(200,0,0));
        Assert.Equal(first,state.WorldBirths.Take(5)); Assert.Equal(10,state.WorldBirths.Count);
        Assert.All(state.WorldBirths.Skip(5),b=>Assert.Equal(new Vector3(200,0,0),b.LocalVelocityPerSecond));
        Assert.All(state.WorldBirths,b=>Assert.Equal(Vector3.Zero,b.InheritedVelocityPerMillisecond));
        EnbResolvedRenderCamera.TryCreate(new(0,0,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Vector3[] Draw(Matrix4x4 world,float velocity)
        {
            var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,
                d.Copy().SetVelocityPerSecond(new(velocity,0,0)),asset.Texture,world,"",0,0);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,100,out var packet,out var receipt,out var error,
                worldBirths:state.WorldBirths),error);
            Assert.Equal(10,receipt.LiveParticleCount);
            return Assert.IsType<EnbW3dDrawTask>(packet).DeformedGeometry!.Positions.ToArray();
        }
        var original=Draw(Matrix4x4.Identity,100);
        Assert.Equal(original,Draw(Matrix4x4.Identity,999));
        var delta=new Vector3(40,50,60);
        var moved=Draw(Matrix4x4.CreateTranslation(delta),999);
        for(int i=0;i<original.Length;i++)Assert.InRange(Vector3.Distance(original[i]+delta,moved[i]),0,.0001f);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,100,59,Matrix4x4.Identity,false,1);
        Assert.Equal(10,state.EmittedCount); Assert.False(state.HasLineEndpoint);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,186,86,Matrix4x4.Identity,false,1);
        Assert.Equal(5,state.WorldBirths.Count);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,d,206,20,Matrix4x4.Identity,false,1);
        Assert.Empty(state.WorldBirths);
    }
}
