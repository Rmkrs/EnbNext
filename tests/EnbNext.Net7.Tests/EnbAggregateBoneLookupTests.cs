using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbAggregateBoneLookupTests
{
    [InstalledRetailFact]
    public void EffectAggregateUsesSameMissingBoneRuleAsOrdinaryAggregate()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();
        Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,2201);
        var mixed=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(loaded.Mixed);
        var tree=Assert.Single(mixed.Resource.Hierarchies);
        Assert.DoesNotContain(tree.Pivots,p=>p.Name.Equals("EX01",StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3,mixed.Sprites.Count+mixed.Streaks.Count);
        Assert.All(mixed.Sprites,p=>Assert.Equal(0,p.PivotIndex));
        Assert.All(mixed.Streaks,p=>Assert.Equal(0,p.PivotIndex));
    }

    [Fact]
    public void FirstMatchAndMissingNameFollowRetailIndexReturn()
    {
        EnbW3dRenderPivot Pivot(int i,string name)=>new(i,name,uint.MaxValue,default,default,Quaternion.Identity,Matrix4x4.Identity);
        var hierarchy=new EnbW3dRenderHierarchy(0,"test",default,false,
            [Pivot(0,"ROOT"),Pivot(1,"EMITT"),Pivot(2,"emitt")],[]);
        Assert.Equal(1,EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,"emitt"));
        Assert.Equal(0,EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,"absent"));
        Assert.True(EnbCurrentRetailAggregateBoneLookup.TryResolve(hierarchy,"absent",out int root));
        Assert.Equal(0,root);
    }

    [Fact]
    public void EmptyHierarchyHasNoBoneZeroToResolve()
    {
        var empty=new EnbW3dRenderHierarchy(0,"empty",default,false,[],[]);
        Assert.False(EnbCurrentRetailAggregateBoneLookup.TryResolve(empty,"CAM_TERMINAL00",out _));
    }

    [InstalledRetailFact]
    public void InstalledBst35MissingEmitterBoneResolvesToRoot()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();
        Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.RetailMixResources,"bst35",out var source,out var error),error);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out error),error);
        var hierarchy=Assert.Single(resource.Hierarchies);
        Assert.DoesNotContain(hierarchy.Pivots,p=>p.Name.Equals("EMITT",StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(hierarchy.Pivots);
        Assert.Equal(0,EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,"EMITT"));
    }
}
