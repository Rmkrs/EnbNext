using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLocalLineParticleTests
{
    [Fact]
    public void VrixLocalLineWarmsOnceAndMovesWithoutWorldBirthInterpolation()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2153);
        var asset=loaded.Mixed!.Sprites.Single(s=>s.Definition.Name=="v_def_04p2");
        var d=asset.Definition;
        Assert.True(d.LocalSpace);Assert.Equal(.5f,d.InitialEmissionTimeSeconds);
        var runtime=new EnbCurrentRetailShipParticleRuntime(d,allowInheritedMotion:true);
        runtime.Advance(0,Matrix4x4.Identity,true);
        Assert.Equal(2,runtime.EmittedCount);
        Assert.Empty(runtime.Births); // the native same-clock buffer still holds a queue
        runtime.Advance(1,Matrix4x4.Identity,true);
        Assert.Equal(new long[]{-300,-100},runtime.Births.Select(b=>b.Time));
        runtime.Advance(1,Matrix4x4.Identity,true);Assert.Equal(2,runtime.EmittedCount);
        var shift=new Vector3(100,20,30);
        Vector3[] Draw(Vector3 offset)
        {
            var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,
                Matrix4x4.CreateTranslation(offset),"",0,0);
            EnbResolvedRenderCamera.TryCreate(new Vector3(20,30,40)+offset,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,0,out var packet,out var receipt,out var error,
                worldBirths:runtime.Births,activeEmissionRun:runtime.EmissionRun,
                birthWorld:_=>throw new Exception("Local line asked for world birth"),
                inheritedVelocity:_=>throw new Exception("Local line asked for inheritance")),error);
            Assert.Equal(2,receipt.LiveParticleCount);
            return Assert.IsType<EnbW3dDrawTask>(packet).Mesh.Positions.ToArray();
        }
        var first=Draw(Vector3.Zero);var moved=Draw(shift);
        Assert.NotEmpty(first);Assert.Equal(first.Length,moved.Length);
        for(int i=0;i<first.Length;i++)Assert.InRange(Vector3.Distance(moved[i]-first[i],shift),0,.001f);
        var world=Matrix4x4.CreateTranslation(shift);
        runtime.Advance(100,world,true);
        runtime.Advance(101,Matrix4x4.Identity,false);Assert.Equal(world,runtime.CurrentWorld);
        runtime.Advance(1000,Matrix4x4.Identity,false);Assert.Empty(runtime.Births);
    }
}
