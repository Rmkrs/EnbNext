using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbShieldDrainGraphTests
{
    [InstalledRetailFact]
    public void InstalledShieldDrainPreservesOverlappingOffPhaseAndIndependentSurface()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(10,1,1,2,1027,0,0,"",0,true,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(uint tick in new uint[]{0,898,899,3898,3899,3999,4000,4499,4500,4648,4649,4868,4869})
        {
            var active=scheduler.Update([root],catalog,tick).VisualNodes.Where(n=>n.Active).ToArray();
            Assert.Equal(tick<4000,active.Any(n=>n.Definition.DefinitionId==1027));
            Assert.Equal(tick<899,active.Any(n=>n.Definition.DefinitionId==1028));
            Assert.Equal(tick>=899 && tick<4649,active.Any(n=>n.Definition.DefinitionId==1029));
            Assert.Equal(tick>=3899 && tick<4869,active.Any(n=>n.Definition.DefinitionId==1030));
            Assert.Equal(tick<4500,active.Any(n=>n.Definition.DefinitionId==1031));
            // LookAt objects default to the target; the unary PassEffect link
            // retains the binary source binding (UnaryStartLinkToTarget absent).
            foreach(var node in active)Assert.Equal(node.Definition.DefinitionId==1031?1u:2u,node.BoundGameId);
        }
        scheduler.Update([],catalog,4870);Assert.Equal(0,scheduler.TrackedRootCount);
    }
}
