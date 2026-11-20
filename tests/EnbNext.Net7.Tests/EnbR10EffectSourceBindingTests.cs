using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10EffectSourceBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutableSourceRetainsUntrackedBirthPoseAndTrackedFinalPose(bool tracks)
    {
        var definition=Owner();var owner=new EnbRenderObjectState(definition);
        var spatial=new EnbRenderSpatialState();spatial.AdoptDefinition(definition.Spatial!);owner.Spatial=spatial;
        var root=new EnbSceneEffectSnapshot(1,1,42,0,1,0,0,"",0,false,1,default,0,0,false,default,1,
            EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=RObjEffect\nBaseAssetID=1502\nDuration=2\nTrackSource={tracks}");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var node=Assert.Single(scheduler.Update([root],catalog,0,worldObjects:[owner]).VisualNodes);
        var binding=new EffectSourceOwnerFixture();binding.Update([node],[owner]);
        spatial.SetWorldTransform(spatial.Transform,Matrix4x4.CreateTranslation(100,200,300));
        binding.Update([node],[owner]);
        var expected=tracks?new Vector3(100,200,300):definition.Spatial!.Transform.Position;
        Assert.Equal(expected,binding.Resolve(node,[owner])!.Spatial!.Transform.Position);
        binding.Update([node],[]);Assert.True(binding.IsDetached(node));
        Assert.Equal(expected,binding.Resolve(node,[])!.Spatial!.Transform.Position);
        binding.Update([],[]);Assert.Equal(0,binding.Count);
    }

    [InstalledRetailFact]
    public void InstalledSeek10158TracksItsOriginalSourceAndRetainsItAfterLoss()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var owner=Owner();
        var target=owner with {GameId=43,RenderIdentity=2};
        var moved=owner with {Spatial=owner.Spatial! with {Transform=owner.Spatial.Transform with {Position=new(90,20,30)}}};
        var root=new EnbSceneEffectSnapshot(1,1,42,43,10158,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var bindings=new EffectSourceOwnerFixture();
        var initial=Assert.Single(scheduler.Update([root],assets.PresentationCatalog!,0,worldObjects:[owner,target]).VisualNodes);
        Assert.True(initial.TryGetRObjDefinition(out var definition,out var error),error);
        Assert.True(definition.TrackSource);
        bindings.Update([initial],[owner,target]);
        var active=Assert.Single(scheduler.Update([root],assets.PresentationCatalog!,500,worldObjects:[moved,target]).VisualNodes);
        bindings.Update([active],[moved,target]);Assert.Equal(moved,bindings.Resolve(active,[moved,target]));
        bindings.Update([active],[target]);Assert.True(bindings.IsDetached(active));
        var replacement=owner with {RenderIdentity=3};
        bindings.Update([active],[replacement,target]);Assert.Equal(moved,bindings.Resolve(active,[replacement,target]));
        bindings.Update([],[]);Assert.Equal(0,bindings.Count);
    }

    [Theory]
    [InlineData("2", 0, false, 2000)]
    [InlineData("2", 0, true, 1000)]
    [InlineData("INFINITE", 0, false, 1000)]
    [InlineData("2", -1, false, 1000)]
    public void UntrackedObjectKeepsCaptureButStillObservesSourceRemoval(string duration, int packetDuration, bool goAway, uint end)
    {
        var owner=Owner();
        var moved=owner with { Spatial=owner.Spatial! with {Transform=owner.Spatial.Transform with {Position=new(900,0,0)}} };
        var root=new EnbSceneEffectSnapshot(1,1,42,0,1,0,packetDuration,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=RObjEffect\nBaseAssetID=1502\nDuration={duration}\nCycleTime=.5\nTrackSource=FALSE\nGoAwayWhenSourceDoes={goAway}");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var bindings=new EffectSourceOwnerFixture();
        var initial=Assert.Single(scheduler.Update([root],catalog,0,worldObjects:[owner]).VisualNodes);
        Assert.True(initial.TryGetRObjDefinition(out _,out var error),error);
        bindings.Update([initial],[owner]);
        var active=Assert.Single(scheduler.Update([root],catalog,500,worldObjects:[moved]).VisualNodes);
        bindings.Update([active],[moved]);
        Assert.Equal(owner,bindings.Resolve(active,[moved]));
        var detached=Assert.Single(scheduler.Update([root],catalog,750,worldObjects:[]).VisualNodes);
        bindings.Update([detached],[]);
        Assert.True(bindings.IsDetached(detached));
        Assert.Equal(owner,bindings.Resolve(detached,[]));
        var replacement=moved with {RenderIdentity=2};
        var tail=Assert.Single(scheduler.Update([root],catalog,end-1,worldObjects:[replacement]).VisualNodes);
        bindings.Update([tail],[replacement]);
        Assert.Equal(owner,bindings.Resolve(tail,[replacement]));
        var completed=scheduler.Update([root],catalog,end,worldObjects:[replacement]);
        Assert.Empty(completed.VisualNodes);
        bindings.Update(completed.VisualNodes,[replacement]);
        Assert.Equal(0,bindings.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetLinkedVisualRetainsOriginalPublicationWhenFirstFrameMissesTarget(bool tracks)
    {
        var original = Owner() with { GameId=43, RenderIdentity=2 };
        var replacement = original with { RenderIdentity=3,
            Spatial=original.Spatial! with {Transform=original.Spatial.Transform with {Position=new(900,0,0)}} };
        var root = new EnbSceneEffectSnapshot(1,1,42,43,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null)
            { PublishedEndpoints=new(Owner(),original) };
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nStartLinkID=2\nUnaryStartLinkToTarget=TRUE\n[EFFECT-2]\nEffectClass=RObjEffect\nBaseAssetID=1502\nDuration=2\nTrackSource={tracks}");
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var node = scheduler.Update([root],catalog,0,worldObjects:[Owner(),replacement]).VisualNodes.Single(n=>n.Definition.DefinitionId==2);
        var bindings = new EffectSourceOwnerFixture();
        Assert.Null(bindings.Resolve(node,[replacement]));
        bindings.Update([node],[replacement]);
        Assert.Equal(original,bindings.Resolve(node,[replacement]));
        Assert.True(bindings.IsDetached(node));
        bindings.Update([],[]);
        Assert.Equal(0,bindings.Count);
        // A fresh generation is allowed to bind the new target lifetime.
        var freshRoot = root with { InstanceGeneration=2, PublishedEndpoints=new(Owner(),replacement) };
        var fresh = scheduler.Update([freshRoot],catalog,10,worldObjects:[Owner(),replacement]).VisualNodes.Single(n=>n.Definition.DefinitionId==2 && n.Root.InstanceGeneration==2);
        bindings.Update([fresh],[replacement]);
        Assert.Equal(replacement,bindings.Resolve(fresh,[replacement]));
        Assert.False(bindings.IsDetached(fresh));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedEffectCapturesAtBindingAndOnlyTrackedSourceFollows(bool tracks)
    {
        var root = new EnbSceneEffectSnapshot(1,1,42,0,1,0,0,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null);
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText($"[EFFECT-1]\nEffectClass=RObjEffect\nBaseAssetID=1502\nStartDelay=.5\nDuration=2\nTrackSource={tracks}");
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var bindings = new EffectSourceOwnerFixture();
        var owner = Owner();
        var waiting = Assert.Single(scheduler.Update([root], catalog, 0).VisualNodes);
        Assert.False(waiting.Active);
        bindings.Update([waiting], [owner]);
        var moved = owner with { Spatial=owner.Spatial! with { Transform=owner.Spatial.Transform with { Position=new(100,200,300), Orientation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,1) } } };
        var active = Assert.Single(scheduler.Update([root], catalog, 500).VisualNodes);
        Assert.True(active.Active);
        bindings.Update([active], [moved]);
        Assert.Equal(tracks ? moved.Spatial : owner.Spatial, bindings.Resolve(active, [moved])!.Spatial);
        bindings.Update([active], []);
        Assert.Equal(tracks ? moved : owner, bindings.Resolve(active, []));
        Assert.True(bindings.IsDetached(active));
        bindings.Update([active], [owner]);
        Assert.True(bindings.IsDetached(active));
        Assert.Equal(tracks ? moved : owner, bindings.Resolve(active, [owner]));
        var replacement = EffectOwnerFixture.Copy(active,copy=>{copy.Root=root with { InstanceGeneration=2 };});
        bindings.Update([replacement], [moved]);
        Assert.Equal(moved, bindings.Resolve(replacement, [moved]));
        Assert.False(bindings.IsDetached(replacement));
        bindings.Update([], []);
        Assert.Equal(0, bindings.Count);
    }

    private static EnbSceneObjectSnapshot Owner() => new(1,1,42,false,false,1,48,1,0,0,0,0,false,"fixture",new(false,0,null),
        new(EnbSceneSpatialProviderKind.Fixed,new(0,new(10,20,30),Quaternion.Identity,default,0,default,Vector3.UnitX),null),
        null,null,[],[],[],[],[],[],null,[],null,0,0);
}
