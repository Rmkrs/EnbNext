using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10EffectHlodTests
{
    [Fact]
    public void InstalledEffectMeshFamiliesParticipateInDetailBudget()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();if(paths is null)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(-250,0,0),Quaternion.Identity,new(800,600,1),Math.PI/3,new(1,100000),out var camera));
        int budgeted=0, responsive=0;
        foreach(int id in new[]{1160,1175,1701,1702,1703,1706,1707,1513,1770,1786,1785,1787,1799,1797,1800,1595,1598,2014,2015,2016,2032,2033,2034,2383,2384,2385,74,2040})
        {
            var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
            Assert.NotNull(asset.Resource);Assert.Null(asset.Mixed);
            if(!asset.UsesDetailBudget)continue;
            budgeted++;Assert.Null(asset.Selection);
            var candidate=asset.Candidate(Matrix4x4.CreateScale(100),100,camera,0,0,250);
            var ample=EnbRenderDetailBudgetAllocator.Allocate(100000,[candidate],0);
            var constrained=EnbRenderDetailBudgetAllocator.Allocate(1,[candidate],0);
            int high=Assert.Single(ample.HlodSelections).LevelOrdinal;
            int low=Assert.Single(constrained.HlodSelections).LevelOrdinal;
            Assert.True(high>=low);
            if(high>low)responsive++;
        }
        Assert.True(budgeted>=20);
        Assert.True(responsive>=20);
    }
}
