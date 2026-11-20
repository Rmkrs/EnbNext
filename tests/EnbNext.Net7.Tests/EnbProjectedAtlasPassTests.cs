using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbProjectedAtlasPassTests
{
    [Fact]
    public void GenericSkillGraphHasIndependentObjectAndSurfaceLifetimes()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,640,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,ids) in new (uint,int[])[]{(0,[640,643]),(948,[640,643]),(949,[641,643]),
            (2499,[641,643]),(2500,[641]),(2948,[641]),(2949,[642]),(3897,[642]),(3898,[]),(6050,[])})
        {
            var active=scheduler.Update(tick<6050?[root]:[],catalog,tick).VisualNodes.Where(n=>n.Active).Select(n=>n.Definition.DefinitionId).Order().ToArray();
            Assert.Equal(ids,active);
        }
        Assert.Equal(0,scheduler.TrackedRootCount);
    }

}
