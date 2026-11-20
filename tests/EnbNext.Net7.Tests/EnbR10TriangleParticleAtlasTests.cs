using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10TriangleParticleAtlasTests
{
    [Fact]
    public void QuantizedFrameAndOpacityBoundaryReachTheDrawPacket()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1188).AggregateParticles!);
        var d=asset.Definition.Copy().SetMaximumEmissions(1).SetBurstSize(1).SetLifetimeSeconds(1).SetFrame(new(0,[new(0,0),new(.073f,1)])).SetOpacity(new(0,[new(0,0),new(.073f,1)]));
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var instance=new EnbW3dParticleEmitterDrawInstance(d.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        foreach(long age in new long[]{71,72,73})
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,
                EnbW3dLightEnvironment.BlackNoLights,0,0,age,out var packet,out _,out var error,
                worldBirths:[new(0,0,Matrix4x4.Identity)]),error);
            var mesh=Assert.IsType<EnbW3dDrawTask>(packet).Mesh;
            var uv=mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords!;
            Assert.Equal(age<73?.25f:.75f,uv[0].X);
            Assert.All(mesh.EffectiveColor1,c=>Assert.Equal(age==71?248:age==72?251:255,c.A));
        }
    }

    [Theory]
    [InlineData(1188)]
    [InlineData(1189)]
    public void HackTriangleFramesUseNativeCellCoordinatesAndWrap(int id)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var asset = Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id).AggregateParticles!);
        Assert.Equal(0u,asset.Definition.RenderMode);
        Assert.Equal(1u,asset.Definition.FrameMode);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        for (int frame=0;frame<8;frame++)
        {
            var definition=asset.Definition.Copy().SetMaximumEmissions(1).SetBurstSize(1).SetFrame(new(0,[new(0,frame)]));
            var instance=new EnbW3dParticleEmitterDrawInstance(definition.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,definition,asset.Texture,Matrix4x4.Identity,"",0,0);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,100,
                out var packet,out var receipt,out var error),error);
            Assert.Equal(1,receipt.LiveParticleCount);
            var mesh=Assert.IsType<EnbW3dDrawTask>(packet).Mesh;
            Assert.Equal(3,mesh.Positions.Count);
            var uv=mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords!;
            float x=(frame&1)*.5f,y=((frame&3)>>1)*.5f;
            Assert.Equal(new Vector2(x+.25f,y),uv[0]);
            Assert.Equal(new Vector2(x,y+.433f),uv[1]);
            Assert.Equal(new Vector2(x+.5f,y+.433f),uv[2]);
        }
    }
}
