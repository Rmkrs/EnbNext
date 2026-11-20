using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMixedHsvAnimationTests
{
    [Theory]
    [InlineData(1154)]
    [InlineData(1712)]
    [InlineData(1716)]
    [InlineData(1597)]
    [InlineData(1984)]
    [InlineData(1986)]
    [InlineData(1724)]
    public void RecoloringPreservesAnimatedParticlePlacementAndBirths(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,id).Mixed!;
        if(id==1724)Assert.Equal(18,original.Sprites.Count);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,id,new Vector3(120,.1f,0));
        var shifted=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(loaded.Mixed);
        Assert.NotNull(shifted.Animation);
        Assert.Same(loaded.Animation,shifted.Animation);
        Assert.Same(loaded.AnimationFlags,shifted.AnimationFlags);
        var a=new EnbCurrentRetailMixedParticleRuntime(original,10,123,randomStreams:new());
        var b=new EnbCurrentRetailMixedParticleRuntime(shifted,10,123,randomStreams:new());
        var ah=new EnbCurrentRetailParticlePoseHistory();
        var bh=new EnbCurrentRetailParticlePoseHistory();
        for(uint age=0;age<=1500;age+=20)
        {
            var world=Matrix4x4.CreateRotationZ(age*.001f)*Matrix4x4.CreateTranslation(age*.01f,0,0);
            ah.Record(age,world);bh.Record(age,world);
            a.Advance(original,ah,10);b.Advance(shifted,bh,10);
        }
        for(int i=0;i<original.Streaks.Count;i++)
        {
            Assert.Equal(a.Buffers[i].EmittedParticleCount,b.Buffers[i].EmittedParticleCount);
            Assert.Equal(a.Buffers[i].Sample(1500).Select(s=>(s.Head,s.Tail,s.Size,s.Frame)),
                b.Buffers[i].Sample(1500).Select(s=>(s.Head,s.Tail,s.Size,s.Frame)));
        }
        for(int i=0;i<original.Sprites.Count;i++)
        {
            var first=a.AnimatedSprites!.Emitters[i];var second=b.AnimatedSprites!.Emitters[i];
            Assert.Equal(first.EmittedCount,second.EmittedCount);
            Assert.Equal(first.WorldBirths,second.WorldBirths);
            Assert.Equal(first.CurrentWorld,second.CurrentWorld);
        }
    }
}
