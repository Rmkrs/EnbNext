using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingAssetTests
{
    [Fact]
    public void InstalledRingSamplesAuthoredColorRadiiAndExistingDetailCosts()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var bundle=EnbRetailAssetBundle.Load(paths);
        Assert.True(bundle.RetailMixResources!.TryReadResource("sk_pd01p1.w3d",out var bytes,out _));
        var ring=EnbRetailRingAsset.Decode(bytes);
        Assert.Equal("sk_pd01p1",ring.Header.Name);Assert.Null(ring.Sample(.5f,0));
        Assert.Equal(Vector3.Zero,ring.Sample(0,20)!.Color);
        var colorKeys=ring.Tracks[2];
        var half=ring.Sample(colorKeys[1].Time*.5f,20)!;
        Assert.InRange(MathF.Abs(half.Color.X-colorKeys[1].Value[0]*.5f),0,.000001f);
        Assert.Equal(1,half.Opacity);
        for(int level=1;level<=20;level++)
        {
            var sample=ring.Sample(.5f,level)!;
            Assert.Equal((int)EnbSphereRingDetailBudgetSemantics.RingLevelCosts[level],sample.Geometry.Indices.Length/3);
            Assert.All(sample.Geometry.Positions,p=>Assert.True(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&p.Z==0));
        }
        Assert.Equal(Vector3.Zero,ring.Sample(10,20)!.Color);
    }

    [Theory]
    [InlineData("sk_clx03p1",5)]
    [InlineData("bst62_shockp1",2)]
    public void AuthoredKeysBeyondOneRemainReachableWithoutClamping(string name,uint track)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var bundle=EnbRetailAssetBundle.Load(paths);
        Assert.True(bundle.RetailMixResources!.TryReadResource(name+".w3d",out var bytes,out _));
        var ring=EnbRetailRingAsset.Decode(bytes);
        Assert.True(ring.Tracks[track][^1].Time>1);
        var sample=ring.Sample(ring.Tracks[track][^1].Time,20)!;
        if(track==2)Assert.Equal(new Vector3(ring.Tracks[2][^1].Value),sample.Color);
        else Assert.InRange(MathF.Abs(sample.Geometry.Positions[1].Y-ring.Header.SecondRadii.Y*ring.Tracks[5][^1].Value[1]),0,.000001f);
        // Random-access sampling must not retain an interval from the later call.
        Assert.NotNull(ring.Sample(.1f,20));
        Assert.Throws<InvalidDataException>(()=>EnbRetailRingAsset.Decode(bytes[..^1]));
    }
}
