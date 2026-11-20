using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbPowerdownGraphTests
{
    [Theory]
    [InlineData(338,339)]
    [InlineData(340,341)]
    [InlineData(342,343)]
    public void InstalledPowerdownRetiresSurfaceAndHulkAtNextCycle(int id,int child)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(10,1,1,0,(ushort)id,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(uint tick in new uint[]{0,4999,5000,6000,6050,9999,10000})
        {
            var active=scheduler.Update(tick<6050?[root]:[],catalog,tick).VisualNodes.Where(n=>n.Active).ToArray();
            Assert.Equal(tick<10000?new[]{id,child}:Array.Empty<int>(),active.Select(n=>n.Definition.DefinitionId).Order());
            foreach(var node in active)Assert.Equal(1u,node.BoundGameId);
        }
        Assert.Equal(0,scheduler.TrackedRootCount);
    }
}
