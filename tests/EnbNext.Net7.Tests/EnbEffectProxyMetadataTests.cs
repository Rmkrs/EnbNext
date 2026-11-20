using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbEffectProxyMetadataTests
{
    [InstalledRetailTheory]
    [InlineData("ring")]
    [InlineData("sphere")]
    [InlineData("attachment")]
    public void SpecializedParentsRetainQueryableProxyWithoutCreatingChild(string family)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        EnbW3dRenderResource original,changed;
        if(family=="attachment")
        {
            var baseline=EnbWarpPlumeAsset.LoadAttachEffect(assets,674);
            var resolved=HlodProxyMetadataFixture.Resolve(assets,assets.BaseAssetResolver!.Resolve(assets.PresentationCatalog!.FindEffect(674)!.BaseAssetId!.Value));
            var withProxy=EnbWarpPlumeAsset.LoadAttachEffect(assets,674,Vector3.Zero,resolved);
            Assert.Equal(baseline.Particles.Count,withProxy.Particles.Count);original=baseline.Resource;changed=withProxy.Resource;
        }
        else
        {
            var baseline=family=="ring"?RingMeshLevelsFixture.Load(assets):SphereMeshParentFixture.Load(assets);
            var withProxy=family=="ring"?RingMeshLevelsFixture.Load(assets,true):SphereMeshParentFixture.Load(assets,proxyMetadata:true);
            Assert.Equal(baseline.Rings!.Rings.Count,withProxy.Rings!.Rings.Count);
            Assert.Equal(baseline.Spheres?.Spheres.Count,withProxy.Spheres?.Spheres.Count);
            original=baseline.Resource!;changed=withProxy.Resource!;
        }
        Assert.Equal(original.Meshes.Count,changed.Meshes.Count);
        var h=Assert.Single(changed.HierarchicalLods);
        Assert.Equal(1,EnbW3dHlodProxyResolver.GetCount(h));
        Assert.True(EnbW3dHlodProxyResolver.TryResolve(changed,h,0,Matrix4x4.Identity,null,0,out var query,out var error),error);
        Assert.Equal("NO_SUCH_RENDER_OBJECT",query.Name);
    }
}
