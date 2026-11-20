using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetardWeaponsGraphTests
{
    [InstalledRetailFact]
    public void InstalledChainKeepsDelayedRootAndThreeTargetPassPhases()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        // E1037 is unary: the packet binds the affected object directly, as in
        // HackChainProbe. Unary 006E78A0 forwards that object to each link;
        // UnaryStartLinkToTarget does not turn RObjEffect into a binary parent.
        var root=new EnbSceneEffectSnapshot(10,1,2,0,1037,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(var (tick,passId) in new (uint,int)[]{(0,0),(249,0),(250,1038),(1148,1038),(1149,1039),(2249,1039),(2250,1039),(3148,1039),(3149,1040),(4047,1040),(4048,0)})
        {
            var update=scheduler.Update([root],catalog,tick);
            var passes=update.VisualNodes.Where(n=>n.Active && n.Definition.EffectClassKind==EnbEffectClassKind.PassEffect).ToArray();
            if(passId==0)Assert.Empty(passes);
            else
            {
                var pass=Assert.Single(passes);Assert.Equal(passId,pass.Definition.DefinitionId);
                Assert.Equal(2u,pass.BoundGameId);
                Assert.True(EnbCurrentRetailPassEffectDefinition.TryRead(pass,out _,out var error),error);
            }
            Assert.Equal(tick>=250 && tick<2250,update.VisualNodes.Any(n=>n.Active && n.Definition.DefinitionId==1037));
        }
        scheduler.Update([],catalog,4049);Assert.Equal(0,scheduler.TrackedRootCount);
    }
}
