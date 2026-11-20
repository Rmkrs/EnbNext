using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10TetraStreakTests
{
    [Theory]
    [InlineData(2190)]
    [InlineData(2191)]
    [InlineData(2192)]
    public void SkullShieldLocalPrewarmFollowsCurrentPoseWithoutInheritedMotion(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var mixed=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id).Mixed!;
        Assert.Single(mixed.Sprites);
        var child=Assert.Single(mixed.Streaks);
        Assert.Equal(3u,child.Definition.RenderMode);
        Assert.True(child.Definition.LocalSpace);
        Assert.Equal(1,child.Definition.InitialEmissionTimeSeconds);
        var a=new EnbCurrentRetailStreakBufferRuntime(child.Definition,10,31,randomStreams:new());
        var b=new EnbCurrentRetailStreakBufferRuntime(child.Definition.Copy().SetVelocityInheritanceFactor(1),10,31,randomStreams:new());
        a.Advance(0,Matrix4x4.Identity);b.Advance(0,Matrix4x4.Identity);
        Assert.Equal(49,a.EmittedParticleCount);
        Assert.Equal(12,a.RetainedParticleCount);
        Assert.All(a.Sample(0),s=>Assert.InRange(s.Head.Length(),9,9.601f));
        var moved=Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(100,200,300);
        a.Advance(100,Matrix4x4.Identity);b.Advance(100,moved);
        var original=a.Sample(100);var transformed=b.Sample(100);
        Assert.Equal(original.Count,transformed.Count);
        for(int i=0;i<original.Count;i++)
        {
            Assert.InRange(Vector3.Distance(Vector3.Transform(original[i].Head,moved),transformed[i].Head),0,.0001f);
            Assert.InRange(Vector3.Distance(Vector3.Transform(original[i].Tail,moved),transformed[i].Tail),0,.0001f);
        }
        b.Advance(100,Matrix4x4.Identity,false);
        Assert.Equal(transformed,b.Sample(100));
        Assert.Empty(b.Sample(400));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TetraUsesNativeTailPointHeadRingWindingAndUvs(bool textured)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var child=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),2190).Mixed!.Streaks);
        var d=child.Definition.Copy().SetTextureFileName(textured ? "fixture.tga" : "");
        EnbResolvedRenderCamera.TryCreate(new(100,0,0),Quaternion.CreateFromYawPitchRoll(.3f,.7f,.2f),
            new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var sample=new EnbCurrentRetailStreakSample(new(3,4,5),new(6,7,8),2,new(255,80,20,255),new(0,0,0,0));
        Assert.True(EnbCurrentRetailStreakRuntime.TryBuildPacket(child.Key,1,1,1,"tetra",0,d,[sample,sample],camera,0,
            out var packet,out var error,textured ? new("fixture.tga","fixture.mix",0,1) : null),error);
        var mesh=packet!.Mesh;
        Assert.Equal(8,mesh.Positions.Count);
        uint[] topology=[0,2,1,0,3,2,0,1,3,1,2,3,4,6,5,4,7,6,4,5,7,5,6,7];
        Assert.Equal(topology,mesh.Indices);
        for(int group=0;group<2;group++)
        {
            Assert.Equal(sample.Tail,mesh.Positions[group*4]);
            Assert.Equal(sample.TailColor,mesh.EffectiveColor1[group*4]);
            for(int v=1;v<4;v++)
            {
                var offset=mesh.Positions[group*4+v]-sample.Head;
                Assert.InRange(offset.Length(),1.9999f,2.0001f);
                Assert.InRange(MathF.Abs(Vector3.Dot(offset,camera.Basis.Forward)),0,.0001f);
                Assert.Equal(sample.HeadColor,mesh.EffectiveColor1[group*4+v]);
            }
        }
        if(textured)
            Assert.Equal(new Vector2[]{new(0,1),new(0,0),new(0,0),new(0,0),new(0,1),new(0,0),new(0,0),new(0,0)},
                Assert.Single(mesh.MaterialPasses.Single().TextureStages).EffectiveRuntimeTexCoords);
    }
}
