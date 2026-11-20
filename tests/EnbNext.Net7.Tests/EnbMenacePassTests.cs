using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMenacePassTests
{
    [InstalledRetailFact]
    public void GateHoldCancelsSurfaceAndObjectTailIndependently()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,319,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,ids) in new (uint,int[])[]{(0,[319,320]),(6000,[319,320]),(6050,[319,321]),(6999,[319,321]),(7000,[321]),(7448,[321]),(7449,[])})
        {
            var nodes=scheduler.Update(tick<6050?[root]:[],catalog,tick).VisualNodes.Where(n=>n.Active).Select(n=>n.Definition.DefinitionId).Order().ToArray();
            Assert.Equal(ids,nodes);
        }
        Assert.Equal(0,scheduler.TrackedRootCount);
    }

}
