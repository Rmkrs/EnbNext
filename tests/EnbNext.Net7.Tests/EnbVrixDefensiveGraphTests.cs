using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbVrixDefensiveGraphTests
{
    [InstalledRetailFact]
    public void RemovalBeforeLinkDelayNeverActivatesTargetShield()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(10,1,1,2,1088,0,0,"",0,true,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0);
        Assert.False(Assert.Single(scheduler.Update([root],catalog,399).VisualNodes,n=>n.Definition.DefinitionId==1092).Active);
        var removed=scheduler.Update([],catalog,400);
        Assert.Equal(1,removed.Receipt.RemovalMarks);
        Assert.DoesNotContain(removed.VisualNodes,n=>n.Active);
        var waiting=Assert.Single(scheduler.Update([],catalog,799).VisualNodes);
        Assert.Equal(1092,waiting.Definition.DefinitionId);
        Assert.False(waiting.Active);Assert.Equal(0u,waiting.DurationMilliseconds);
        foreach (uint tick in new uint[]{800,801,2000}) Assert.Empty(scheduler.Update([],catalog,tick).VisualNodes);
        Assert.Equal(0,scheduler.TrackedRootCount);
    }

    [InstalledRetailFact]
    public void InstalledInfiniteChainDelaysTargetShieldAndRetiresFromSnapshotRemoval()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(10,1,1,2,1088,0,0,"",0,true,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var initial=scheduler.Update([root],catalog,0);
        Assert.Equal(new[]{1088,1089,1090,1091},initial.VisualNodes.Where(n=>n.Active).Select(n=>n.Definition.DefinitionId).Order());
        Assert.False(Assert.Single(initial.VisualNodes,n=>n.Definition.DefinitionId==1092).Active);
        Assert.False(Assert.Single(scheduler.Update([root],catalog,799).VisualNodes,n=>n.Definition.DefinitionId==1092).Active);
        var shield=Assert.Single(scheduler.Update([root],catalog,800).VisualNodes,n=>n.Definition.DefinitionId==1092);
        Assert.True(shield.Active);Assert.Equal(800u,shield.ActualStartTick);Assert.Equal(2u,shield.BoundGameId);
        Assert.Equal(uint.MaxValue,shield.DurationMilliseconds);
        Assert.All(scheduler.Update([root],catalog,1999).VisualNodes,n=>Assert.True(n.Active));
        var removed=scheduler.Update([],catalog,2000);
        Assert.Equal(1,removed.Receipt.RemovalMarks);Assert.DoesNotContain(removed.VisualNodes,n=>n.Active);
        Assert.Equal(0,scheduler.TrackedRootCount);
        Assert.Empty(scheduler.Update([],catalog,3000).VisualNodes);
    }
}
