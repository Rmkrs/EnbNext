using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbExplosionHierarchyTests
{
    [Theory]
    [InlineData(130,2)]
    [InlineData(131,3)]
    [InlineData(132,3)]
    [InlineData(136,2)]
    public void InstalledEmptyHierarchyModelRetainsEveryAuthoredEmitter(int id,int count)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var source=assets.BaseAssetResolver!.Resolve(id).W3d!;
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var compiled,out _));
        Assert.Empty(compiled.HierarchicalLods);Assert.Empty(Assert.Single(compiled.HierarchyModels).Nodes);
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        Assert.NotNull(loaded.Resource);
        Assert.Equal(count,loaded.AggregateParticles!.Count);
        var tree=Assert.Single(compiled.Hierarchies);
        foreach(var attachment in source.CurrentRetailAggregate!.Definition.Attachments)
        {
            var particle=Assert.Single(loaded.AggregateParticles,p=>p.Definition.Name.Equals(attachment.SubobjectName,StringComparison.OrdinalIgnoreCase));
            Assert.True(particle.Definition.MaximumEmissions>0);
            Assert.Equal(attachment.BoneName,tree.Pivots[particle.PivotIndex].Name,ignoreCase:true);
        }
    }
}
