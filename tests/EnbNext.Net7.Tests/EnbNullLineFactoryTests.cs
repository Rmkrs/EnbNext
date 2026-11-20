using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbNullLineFactoryTests
{
    [InstalledRetailTheory]
    [InlineData("root")]
    [InlineData("StartLinkID")]
    [InlineData("NextLinkID")]
    public void RetainedBaseConstructionRejectsBeforeSoundAndLinksExactlyOnce(string placement)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        catalog.LoadEffectsText($"[EFFECT-30000]\nEffectClass=RObjEffect\nBaseAssetID=1804\nDuration=.1\n{(placement=="root"?"":placement+"=30001")}\n"+
            "[EFFECT-30001]\nEffectClass=LineEffect\nBaseAssetID=69\nDuration=1\nStartDelay=.5\nSFX=test.wav\nStartLinkID=30002\nNextLinkID=30002\n"+
            "[EFFECT-30002]\nEffectClass=RObjEffect\nBaseAssetID=1804\nDuration=1\nSFX=child.wav\n");
        var root=new EnbSceneEffectSnapshot(10,1,1,2,placement=="root"?30001:30000,0,0,"",0,true,1.7f,default,0,0,false,
            new(.1f,.2f,.3f),1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,new(5,6,7),0,0,null);
        int constructions=0;uint constructionTick=uint.MaxValue,now=0;
        string? Construct(EnbEffectDefinition effect,EnbSceneEffectSnapshot incoming)
        {
            Assert.Equal(30001,effect.DefinitionId);Assert.Equal(root,incoming);
            constructions++;constructionTick=now;return "retained non-line BASE";
        }
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(uint tick in new uint[]{0,1,99,100,101,499,500,1500,2000})
        {
            now=tick;
            var sample=scheduler.Update([root],catalog,tick,lineFactoryRejection:Construct);
            Assert.All(sample.VisualNodes,n=>Assert.Equal(30000,n.Definition.DefinitionId));
            Assert.Empty(sample.SoundNodes);
        }
        Assert.Equal(1,constructions);Assert.Equal(placement=="NextLinkID"?100u:0u,constructionTick);
        scheduler.Update([],catalog,2001,lineFactoryRejection:Construct);Assert.Equal(0,scheduler.TrackedRootCount);
    }
    [InstalledRetailTheory]
    [InlineData("StartLinkID")]
    [InlineData("NextLinkID")]
    public void WrongClassLinkedNodeIsRejectedWithoutRejectingValidNullParent(string link)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        catalog.LoadEffectsText($"[EFFECT-30000]\nEffectClass=RObjEffect\nBaseAssetID=1804\nDuration=.1\n{link}=30001\n[EFFECT-30001]\nEffectClass=LineEffect\nBaseAssetID=1803\nDuration=INFINITE\nStartLinkID=30002\n[EFFECT-30002]\nEffectClass=RObjEffect\nBaseAssetID=1804\nDuration=1");
        var root=new EnbSceneEffectSnapshot(10,1,1,2,30000,0,0,"",0,true,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Contains(scheduler.Update([root],catalog,0).VisualNodes,n=>n.Active && n.Definition.DefinitionId==30000);
        foreach(uint tick in new uint[]{1,99,100,101,1000})
            Assert.All(scheduler.Update([root],catalog,tick).VisualNodes,n=>Assert.Equal(30000,n.Definition.DefinitionId));
        scheduler.Update([],catalog,1001);Assert.Equal(0,scheduler.TrackedRootCount);
    }

    [InstalledRetailTheory]
    [InlineData(696)]
    [InlineData(1178)]
    public void InstalledWrongClassRootNeverStartsItsLinkedVisualOrSound(ushort definition)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=new EnbSceneEffectSnapshot(10,1,1,2,definition,0,0,"",0,true,1,default,0,0,false,
            default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var first=scheduler.Update([root],catalog,0);
        Assert.Equal(1,first.Receipt.RejectedRoots);
        foreach(uint tick in new uint[]{0,149,150,151,1000,5000})
        {
            var sample=scheduler.Update([root],catalog,tick);
            Assert.Empty(sample.VisualNodes);Assert.Empty(sample.SoundNodes);
        }
        scheduler.Update([],catalog,5001);Assert.Equal(0,scheduler.TrackedRootCount);
        var valid=scheduler.Update([root with {EffectId=11,DefinitionId=694}],catalog,5002);
        Assert.NotEmpty(valid.VisualNodes);Assert.Equal(0,valid.Receipt.RejectedRoots);
    }
}
