using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRandomizedUvPassTests
{
    [InstalledRetailFact]
    public void HiddenFoldExpiresAtFourSecondsWithoutAnExitLink()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,10080,0,0,"",0,false,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(uint tick in new uint[]{0,3999,4000,6050})
        {
            var active=scheduler.Update(tick<6050?[root]:[],catalog,tick).VisualNodes.Where(n=>n.Active).ToArray();
            if(tick<4000)
            {
                var node=Assert.Single(active);Assert.Equal(10080,node.Definition.DefinitionId);
                Assert.True(EnbRetailIniDocument.ReadBool(node.Definition.Properties,"HideBaseMesh"));
            }
            else Assert.Empty(active);
        }
        Assert.Equal(0,scheduler.TrackedRootCount);
    }

}
