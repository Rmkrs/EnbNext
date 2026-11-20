using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbWarpTunnelTests
{
    [Fact]
    public void BrightnessFollowsDistinctUpdatesAndEndsBelowRetailThreshold()
    {
        var fade=new EnbWarpTunnelFade();fade.Update(1,true);Assert.Equal(.05f,fade.Brightness);
        fade.Update(1,true);Assert.Equal(.05f,fade.Brightness);
        for(uint tick=2;tick<=20;tick++)fade.Update(tick,true);
        Assert.Equal(1,fade.Brightness);fade.Update(21,false);Assert.InRange(fade.Brightness,.9499f,.9501f);
        for(uint tick=22;tick<=40;tick++)fade.Update(tick,false);
        Assert.True(fade.Ended);float ended=fade.Brightness;fade.Update(41,true);Assert.Equal(ended,fade.Brightness);
        var outsideWarp=new EnbWarpTunnelFade();outsideWarp.Update(0,false);Assert.True(outsideWarp.Ended);
    }
    [Fact]
    public void SchedulerKeepsOverlappingTunnelInstancesAndRemovalIndependent()
    {
        var catalog=new EnbRetailPresentationCatalog();catalog.LoadEffectsText("[EFFECT-23]\nEffectClass=WarpEffect\nBaseAssetID=216\nDuration=INFINITE");
        var first=new EnbSceneEffectSnapshot(10,1,1,0,23,0,0,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var second=first with {EffectId=11,InstanceGeneration=2};var runtime=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var nodes=runtime.Update([first,second],catalog,0).VisualNodes;Assert.Equal(2,nodes.Count);Assert.NotEqual(nodes[0].Root.EffectId,nodes[1].Root.EffectId);
        Assert.All(nodes,n=>Assert.True(n.Active));
        var remaining=runtime.Update([second],catalog,25).VisualNodes;Assert.DoesNotContain(remaining,n=>n.Root.EffectId==10);Assert.Contains(remaining,n=>n.Root.EffectId==11);
    }
    [Fact]
    public void InstalledHierarchyModelAndOlderEmitterDefaultsArePreserved()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();if(paths is null)return;var assets=EnbRetailAssetBundle.Load(paths);
        var tunnel=EnbWarpTunnelAsset.Load(assets,216);Assert.Single(tunnel.Resource.HierarchyModels);Assert.Equal(5,tunnel.Resource.Meshes.Count);
        Assert.Equal(7,tunnel.Particles.Count);Assert.Equal(2,tunnel.Particles.Count(p=>p.Asset.Definition.MaximumEmissions==1));
        foreach(var p in tunnel.Particles.Where(p=>p.Asset.Definition.Name is "pwarp01" or "pwarp02"))
        {
            Assert.Equal(0,Assert.Single(p.Asset.Definition.Rotation.Keys).Value);Assert.Equal(0,Assert.Single(p.Asset.Definition.Frame.Keys).Value);
            Assert.Equal(1,p.Asset.Definition.VelocityInheritanceFactor);
        }
    }
}
