using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingOnlyTests
{
    [Theory]
    [InlineData(1860)] [InlineData(1861)] [InlineData(1862)] [InlineData(1866)] [InlineData(1874)]
    [InlineData(2020)] [InlineData(2021)] [InlineData(2026)] [InlineData(2027)] [InlineData(2028)]
    [InlineData(2029)] [InlineData(2158)] [InlineData(2159)]
    public void RingOnlyAssetsHaveNoParticleOwnerAndUseAuthoredAnimation(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        var assembly=Assert.IsType<EnbRetailRingAssembly>(loaded.Rings);
        Assert.Null(loaded.Mixed);Assert.Null(assembly.Emitters);
        Assert.NotEmpty(assembly.Rings);Assert.Same(loaded.Animation,assembly.Animation);
        var state=new EnbRetailRingAssemblyRuntime(assembly);
        var before=state.BeforeAllocation(1,100,Matrix4x4.Identity,1,true);
        var frame=state.Prepare(1,100,50,Matrix4x4.Identity,1,true,assembly.Rings.Select(r=>r.Ordinal).ToHashSet());
        Assert.Equal(before.Rings.Count,frame.Rings.Count);
        Assert.All(frame.Rings,r=>Assert.NotNull(r.Values.Sample(20)));
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,id,new Vector3(120,0,0));
        Assert.Null(shifted.Rings!.Emitters);Assert.Same(shifted.Animation,shifted.Rings.Animation);
        Assert.All(shifted.Rings.Rings.Where(r=>r.Texture is not null),r=>Assert.Equal(EnbCurrentRetailAssetVariantName.CreateTexture(r.Asset.Header.Texture,new(120,0,0)),r.Texture!.LogicalName));
    }
}
