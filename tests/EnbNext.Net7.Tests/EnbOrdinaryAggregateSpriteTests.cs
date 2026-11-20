using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbOrdinaryAggregateSpriteTests
{
    [InstalledRetailFact]
    public void InstalledAtmosphereSpriteRetainsBirthGeometryAndDetachedTail()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.RetailMixResources!.TryReadResource("ol01p.w3d",out var raw,out var location));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(raw,out var d,out var error),error);
        Assert.False(d.LocalSpace);
        Assert.Equal(0u,d.RenderMode);
        Assert.Equal(0,d.VelocityInheritanceFactor);
        Assert.Equal(0,d.InitialEmissionTimeSeconds);
        Assert.True(assets.RetailMixResources.TryLocateResource(d.TextureFileName,out var texture));
        var buffer=new EnbCurrentRetailShipParticleRuntime(d,allowInheritedMotion:true);
        for(int time=0;time<=1000;time+=10)
            buffer.Advance(time,Matrix4x4.CreateRotationZ(time*.001f)*Matrix4x4.CreateTranslation(time,0,100),true);
        var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,location.Name,1,EnbRenderPassKind.OrdinaryWorld,
            d,new(texture.Name,texture.ArchivePath,texture.DataOffset,texture.DataLength),buffer.CurrentWorld,"fixture",0,0);
        Assert.True(EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera));
        EnbW3dDrawTask Packet(EnbW3dParticleEmitterDrawInstance instance,int time)
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(EnbW3dRenderResourceKey.From(location),1,1,
                instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,time,out var packet,out var receipt,out var why,
                worldBirths:buffer.Births,activeEmissionRun:buffer.IsEmitting?buffer.EmissionRun:null),why);
            Assert.True(receipt.LiveParticleCount>0);Assert.NotNull(packet);return packet;
        }
        var original=Packet(emitter,1000);
        var movedWorld=emitter.WorldTransform;
        movedWorld.Translation=new(9000,0,100);
        var moved=Packet(emitter with {WorldTransform=movedWorld},1000);
        Assert.Equal(original.DeformedGeometry!.Positions.ToArray(),moved.DeformedGeometry!.Positions.ToArray());
        long count=buffer.EmittedCount;
        buffer.Advance(1000,buffer.CurrentWorld,true);
        Assert.Equal(count,buffer.EmittedCount);
        buffer.Advance(1010,buffer.CurrentWorld,false);
        Assert.False(buffer.IsEmitting);Assert.NotEmpty(buffer.Births);
        _=Packet(emitter,1010);
        buffer.Advance(2600,buffer.CurrentWorld,false);
        Assert.Equal(count,buffer.EmittedCount);Assert.Empty(buffer.Births);
    }
}
