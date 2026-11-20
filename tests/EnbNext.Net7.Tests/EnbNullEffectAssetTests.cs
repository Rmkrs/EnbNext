using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbNullEffectAssetTests
{
    [Theory]
    [InlineData(135)]
    [InlineData(1804)]
    [InlineData(1805)]
    [InlineData(2202)]
    public void AuthoredNullEffectRemainsNoDrawAfterHsv(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.Equal(EnbBaseAssetResolutionStatus.NullObject,assets.BaseAssetResolver!.Resolve(id).Status);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,id,new Vector3(45,.2f,.1f));
        Assert.True(asset.IsNullObject);
        Assert.Null(asset.Resource);
        Assert.False(asset.UsesDetailBudget);
    }

    [Fact]
    public void MissingAssetIsNotAcceptedAsAuthoredNull()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,900001);
        Assert.False(asset.IsNullObject);
        Assert.True(asset.IsMissingFactoryObject);
        Assert.Equal("MISSING",Assert.Single(asset.Resource!.HierarchicalLods).Name);
        Assert.NotEmpty(asset.Resource.Meshes);
    }
}
