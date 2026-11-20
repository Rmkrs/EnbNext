using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTargetUvPassTests
{
    [InstalledRetailFact]
    public void CreationStartTransitionsToHoldThenRetiresOnRemoval()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,10209,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,ids) in new (uint,int[])[]{(0,[10209]),(1999,[10209]),(2000,[10210]),(6000,[10210]),(6050,[]),(12000,[])})
        {
            var active=scheduler.Update(tick<6050?[root]:[],catalog,tick).VisualNodes.Where(n=>n.Active).Select(n=>n.Definition.DefinitionId).ToArray();
            Assert.Equal(ids,active);
        }
        Assert.Equal(0,scheduler.TrackedRootCount);
    }

}
