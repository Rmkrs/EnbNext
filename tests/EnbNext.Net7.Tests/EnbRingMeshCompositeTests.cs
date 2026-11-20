using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingMeshCompositeTests
{
    [Fact]
    public void RecoloredRingBindingPublishesTheVariantImageRatherThanItsOriginal()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,1876);
        var changed=EnbCurrentRetailRObjEffectAsset.Load(assets,1876,new(120,0,0));
        var before=Assert.IsType<EnbRetailRingAssembly>(original.Rings);
        var after=Assert.IsType<EnbRetailRingAssembly>(changed.Rings);
        Assert.Equal(before.Rings.Count,after.Rings.Count);
        for(int i=0;i<after.Rings.Count;i++)
        {
            var binding=Assert.IsType<EnbW3dRenderTextureBinding>(after.Rings[i].Texture);
            var published=Assert.IsType<EnbResolvedTextureResource>(binding.CurrentRetailResolvedTexture);
            Assert.Equal(binding.LogicalName,published.LogicalName);
            Assert.Same(assets.Textures.Resolve(binding.LogicalName),published);
            Assert.NotSame(before.Rings[i].Texture!.CurrentRetailResolvedTexture!.Image,published.Image);
        }
    }

    [Theory]
    [InlineData(1876)] [InlineData(1877)]
    public void CompleteMeshAndSixRingsRetainIndependentBudgetCosts(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),id);
        var rings=Assert.IsType<EnbRetailRingAssembly>(loaded.Rings);
        Assert.Single(loaded.Resource!.Meshes);Assert.Equal(6,rings.Rings.Count);
        Assert.Null(loaded.Mixed);Assert.Null(rings.Emitters);Assert.False(loaded.UsesDetailBudget);
        var h=Assert.Single(loaded.Resource.HierarchicalLods);
        Assert.Equal(h.Name,loaded.Selection!.Value.Name);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(loaded.Resource,h,_=>true,out var costs,out var error),error);
        Assert.True(Assert.Single(costs)>0);
        var runtime=new EnbRetailRingAssemblyRuntime(rings);
        var before=runtime.BeforeAllocation(1,100,Matrix4x4.CreateTranslation(10,20,30),2,true);
        var pose=rings.Pose(Matrix4x4.CreateTranslation(10,20,30),2,100);
        Assert.Equal(rings.Rings.Count(r=>pose.Visibility[r.PivotIndex]),before.Rings.Count);
        int selected=before.Rings[0].Binding.Ordinal;
        Assert.True(before.Rings.Select(r=>r.World).Distinct().Count()>1);
        var drawn=runtime.Prepare(1,100,100,Matrix4x4.Identity,2,true,new HashSet<int>{selected});
        Assert.True(drawn.Rings.Single(r=>r.Binding.Ordinal==selected).Progress>0);
        Assert.All(drawn.Rings.Where(r=>r.Binding.Ordinal!=selected),r=>Assert.Equal(0,r.Progress));
    }
}
