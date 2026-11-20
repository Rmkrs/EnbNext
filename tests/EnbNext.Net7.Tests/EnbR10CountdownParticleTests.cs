using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10CountdownParticleTests
{
    [Fact]
    public void InstalledCountdownUsesThreeAuthoredCellsAndExpiresAfterThreeSeconds()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2410).AggregateParticles!);
        Assert.Equal(2u,asset.Definition.FrameMode);
        Assert.True(asset.Definition.LocalSpace);
        foreach(var (time,frame) in new[]{(520,7),(1520,8),(2520,9)})
        {
            var packet=Draw(asset,asset.Definition,time);
            Assert.NotNull(packet);
            AssertCell(packet,frame,4);
        }
        Assert.Null(Draw(asset,asset.Definition,3020));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FourByFourFramesWrapAndPreserveTriangleOrQuadCornerCoordinates(int mode)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2410).AggregateParticles!);
        for(int frame=-1;frame<32;frame++)
        {
            var d=asset.Definition.Copy().SetRenderMode((uint)mode).SetFrame(new(0,[new(0,frame)]));
            AssertCell(Assert.IsType<EnbW3dDrawTask>(Draw(asset,d,520)),frame&15,mode==0 ? 3 : 4);
        }
    }

    private static void AssertCell(EnbW3dDrawTask packet,int frame,int corners)
    {
        var uv=packet.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords!;
        float x=(frame%4)*.25f,y=(frame/4)*.25f;
        Assert.Equal(corners,uv.Count);
        if(corners==4)
        {
            Assert.Equal(new Vector2(x,y),uv[0]);
            Assert.Equal(new Vector2(x,y+.25f),uv[1]);
            Assert.Equal(new Vector2(x+.25f,y+.25f),uv[2]);
            Assert.Equal(new Vector2(x+.25f,y),uv[3]);
        }
        else
        {
            Assert.Equal(new Vector2(x+.125f,y),uv[0]);
            Assert.Equal(new Vector2(x,y+.2165f),uv[1]);
            Assert.Equal(new Vector2(x+.25f,y+.2165f),uv[2]);
        }
    }

    private static EnbW3dDrawTask? Draw(EnbCurrentRetailRObjParticleAsset asset,EnbCurrentRetailParticleEmitterDefinition d,int age)
    {
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var instance=new EnbW3dParticleEmitterDrawInstance(d.Name,"countdown",1,EnbRenderPassKind.OrdinaryWorld,d,asset.Texture,Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,EnbW3dLightEnvironment.BlackNoLights,1,0,age,
            out var packet,out _,out var error),error);
        return packet;
    }
}
