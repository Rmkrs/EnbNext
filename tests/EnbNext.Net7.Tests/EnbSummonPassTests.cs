using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSummonPassTests
{
    [InstalledRetailFact]
    public void SummonGraphHasDelayedFlashAndTwoHiddenPhases()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,345,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,ids) in new (uint,int[])[]{(0,[345]),(1898,[345]),(1899,[345,432]),(1999,[345,432]),
            (2000,[346,432]),(2398,[346,432]),(2399,[346]),(3999,[346]),(4000,[350]),(5499,[350]),(5500,[]),(6050,[])})
        {
            var active=scheduler.Update(tick<6050?[root]:[],catalog,tick).VisualNodes.Where(n=>n.Active).ToArray();
            Assert.Equal(ids,active.Select(n=>n.Definition.DefinitionId).Order().ToArray());
            Assert.Equal(tick is >=2000 and <5500,active.Any(n=>EnbRetailIniDocument.ReadBool(n.Definition.Properties,"HideBaseMesh")));
        }
        Assert.Equal(0,scheduler.TrackedRootCount);
    }
}
