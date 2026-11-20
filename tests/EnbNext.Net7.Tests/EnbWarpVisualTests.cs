using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbWarpVisualTests
{
    [Fact]
    public void XProjectionUsesShipYZExtentsAndFollowsRotatedTranslatedOwner()
    {
        var owner=Matrix4x4.CreateRotationY(.7f)*Matrix4x4.CreateTranslation(120,340,560);
        Matrix4x4.Invert(owner,out var inverse);
        var d=new EnbCurrentRetailPassEffectDefinition("warp",new(2,3),false,ProjectionAxis:'X');
        var projection=d.ProjectionFromWorld(inverse,new(10,20,30),64);
        var a=Vector3.Transform(Vector3.Transform(new(0,10,10),owner),projection);
        var b=Vector3.Transform(Vector3.Transform(new(500,10,10),owner),projection);
        Assert.InRange(Math.Abs(a.X),0,.00001f);Assert.InRange(Math.Abs(a.Y),0,.00001f);
        Assert.InRange(Vector2.Distance(new(a.X,a.Y),new(b.X,b.Y)),0,.00001f);
    }

    [Fact]
    public void InstalledWarpCylinderBuildsBoundedParticlesAndStopsWithRetainedTail()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();if(paths is null)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,1764).Particle!;
        var d=asset.Definition.Copy().SetVelocityPerSecond(Vector3.Zero).SetVelocityRandomizer(new(0,Vector3.Zero));
        var runtime=new EnbCurrentRetailShipParticleRuntime(d);
        runtime.Advance(0,Matrix4x4.Identity,true);
        runtime.Advance(250,Matrix4x4.Identity,true);
        Assert.NotEmpty(runtime.Births);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(20,0,0),Quaternion.Identity,new(640,480,1),Math.PI/3,new(.1f,1000),out var camera));
        var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,d.Name,1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,EnbW3dLightEnvironment.BlackNoLights,0,0,250,
            out var packet,out _,out var error,worldBirths:runtime.Births),error);
        Assert.NotNull(packet);
        for(int i=0;i<packet.Mesh.Positions.Count;i+=4)
        {
            var center=(packet.Mesh.Positions[i]+packet.Mesh.Positions[i+1]+packet.Mesh.Positions[i+2]+packet.Mesh.Positions[i+3])/4;
            Assert.InRange(Math.Abs(center.X),0,.75001f);
            Assert.InRange(center.Y*center.Y+center.Z*center.Z,0,.062501f);
        }
        var born=runtime.Births[0];runtime.Advance(300,Matrix4x4.CreateTranslation(100,0,0),false);
        Assert.Equal(born,runtime.Births[0]);runtime.Advance(1000,Matrix4x4.Identity,false);Assert.Empty(runtime.Births);
        runtime.Advance(1100,Matrix4x4.Identity,true);runtime.Advance(100000,Matrix4x4.Identity,true);
        Assert.InRange(runtime.Births.Count,1,26);
    }
}
