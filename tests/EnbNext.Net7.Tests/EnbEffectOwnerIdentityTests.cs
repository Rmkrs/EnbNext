using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbEffectOwnerIdentityTests
{
    [Theory]
    [InlineData(2f,.5f,1f)]
    [InlineData(1.005f,1f,1f)]
    [InlineData(2f,2f,4f)]
    public void JetScaleComposesRootAndAuthoredBeforePrototypeTolerance(float rootScale,float authored,float expected)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass=ParticleJetEffect\nDuration=1\nRObjScale={authored.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([Root() with {Scale=rootScale}],catalog,0).VisualNodes);
        Assert.Equal(expected,EnbCurrentRetailParticleJetScale.Read(node));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteSoundKeepsOriginalEndpointPositionAfterIdReuse(bool target)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nStartLinkID=2\nUnaryStartLinkToTarget={target}\n[EFFECT-2]\nEffectClass=SoundEffect\nDuration=2\nSFX=fixture.wav");
        var source=Owner(1,10,100);var dest=Owner(2,20,200);
        var root=Root() with {InitialSourceObject=source,LastSourceObject=source,PublishedEndpoints=new(source,dest)};
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0,worldObjects:[source,dest]);
        var sound=Assert.Single(scheduler.Update([root],catalog,200,worldObjects:[Owner(1,11,101),Owner(2,21,201)]).SoundNodes);
        Assert.Equal(10ul,sound.SourceRenderIdentity);Assert.Equal(20ul,sound.TargetRenderIdentity);
        Vector3? Position(uint id)=>EnbCurrentRetailEffectSoundRuntime.IsOriginalOwner(sound,id,i=>i==1?11ul:21ul)?new(900,0,0):null;
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,Position,out var point,out var follows,out var error),error);
        Assert.Equal(Vector3.Zero,point);Assert.False(follows);
        Assert.True(EnbCurrentRetailEffectSoundRuntime.IsOriginalOwner(sound,target?2u:1u,i=>i==1?10ul:20ul));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void SoundChildObservesSelectedOwnerAndPreservesUnflaggedFiniteDuration(bool target, bool infinite)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nStartLinkID=2\nUnaryStartLinkToTarget={target}\n[EFFECT-2]\nEffectClass=SoundEffect\nSFX=fixture.wav\nDuration={(infinite?"INFINITE":"2")}");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var root=Root();
        scheduler.Update([root],catalog,0,worldObjects:[Owner(1,10,100),Owner(2,20,200)]);
        scheduler.Update([root],catalog,100,worldObjects:[Owner(1,10,100),Owner(2,20,200)]);
        var after=scheduler.Update([root],catalog,200,worldObjects:target
            ? [Owner(1,10,100),Owner(2,21,201)] : [Owner(1,11,101),Owner(2,20,200)]);
        if(infinite)Assert.Empty(after.SoundNodes);else Assert.Single(after.SoundNodes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InfiniteJetsRetireOnEitherEndpointLossAsRootOrChild(bool child, bool targetLoss)
    {
        var catalog=Catalog(child
            ? "[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nStartLinkID=2\n[EFFECT-2]\nEffectClass=ParticleJetEffect\nDuration=INFINITE"
            : "[EFFECT-1]\nEffectClass=ParticleJetEffect\nDuration=INFINITE\nStartLinkID=2\n[EFFECT-2]\nEffectClass=ParticleJetEffect\nDuration=INFINITE");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var root=Root();
        scheduler.Update([root],catalog,0,worldObjects:[Owner(1,10,100),Owner(2,20,200)]);
        scheduler.Update([root],catalog,100,worldObjects:[Owner(1,10,100),Owner(2,20,200)]);
        Assert.Empty(scheduler.Update([root],catalog,200,worldObjects:targetLoss
            ? [Owner(1,10,100),Owner(2,21,201)] : [Owner(1,11,101),Owner(2,20,200)]).VisualNodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedRenderStateLinkKeepsOriginalSelectedOwner(bool toTarget)
    {
        var catalog = Catalog($"[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=1\nStartLinkID=2\nStartLinkDelay=.5\nUnaryStartLinkToTarget={toTarget}\n[EFFECT-2]\nEffectClass=RenderStateEffect\nDuration=1\nRenderStateID=16\nActionType=Ramp\nActionTime=.2");
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root = Root() with { EndpointReceipt = new(100, 200) };
        scheduler.Update([root], catalog, 0, worldObjects: [Owner(1,10,100), Owner(2,20,200)]);
        var activation = Assert.Single(scheduler.Update([root], catalog, 500,
            worldObjects: [Owner(1,11,101), Owner(2,21,201)]).RenderStateEvents);
        Assert.Equal(toTarget ? 2u : 1u, activation.BoundGameId);
        Assert.Equal(toTarget ? 20ul : 10ul, activation.BoundRenderIdentity);
        Assert.Equal(toTarget ? 200ul : 100ul, activation.BoundCreationOrdinal);
        Assert.Empty(scheduler.Update([root], catalog, 600,
            worldObjects: [Owner(1,11,101), Owner(2,21,201)]).RenderStateEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteSurfaceKeepsTheSelectedOriginalIdentityWithoutShorteningItsLifetime(bool toTarget)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nStartLinkID=2\nUnaryStartLinkToTarget={toTarget}\n[EFFECT-2]\nEffectClass=PassEffect\nDuration=.5");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var root=Root();
        var source=Owner(1,10,100);var target=Owner(2,20,200);
        var initial=scheduler.Update([root],catalog,0,worldObjects:[source,target]).VisualNodes.Single(n=>n.Definition.DefinitionId==2);
        Assert.Equal(toTarget?20ul:10ul,initial.BoundRenderIdentity);
        var retained=Assert.Single(scheduler.Update([root],catalog,150,
            worldObjects:[Owner(1,11,101),Owner(2,21,201)]).VisualNodes);
        Assert.Equal(initial.BoundRenderIdentity,retained.BoundRenderIdentity);
        Assert.Equal(500u,retained.DurationMilliseconds);
    }

    [Fact]
    public void InfiniteSurfaceChildReceivesItsOwnOwnerDestructionCallback()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=PassEffect\nDuration=.1\nNextLinkID=2\n[EFFECT-2]\nEffectClass=PassEffect\nDuration=INFINITE");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Root() with {IsObjectToObject=false,GeometryKind=EnbSceneEffectGeometryKind.Object,TargetGameId=0};
        scheduler.Update([root],catalog,0,worldObjects:[Owner(1,10,100)]);
        Assert.Equal(2,Assert.Single(scheduler.Update([root],catalog,100,worldObjects:[Owner(1,10,100)]).VisualNodes).Definition.DefinitionId);
        Assert.Empty(scheduler.Update([root],catalog,150,worldObjects:[Owner(1,11,101)]).VisualNodes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReusedIdRetiresOriginalInfiniteChildrenAndFreshGenerationBindsReplacement(bool replaceSource)
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nNextLinkID=2\n[EFFECT-2]\nEffectClass=RObjLookAtEffect\nDuration=INFINITE\nStartLinkID=3\n[EFFECT-3]\nEffectClass=LineEffect\nDuration=INFINITE");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var root=Root();
        var source=Owner(1,10,100);var target=Owner(2,20,200);
        scheduler.Update([root],catalog,0,worldObjects:[source,target]);
        Assert.Equal(2,scheduler.Update([root],catalog,100,worldObjects:[source,target]).VisualNodes.Count);
        if(replaceSource)source=Owner(1,11,101);else target=Owner(2,21,201);
        Assert.Empty(scheduler.Update([root],catalog,200,worldObjects:[source,target]).VisualNodes);
        Assert.Empty(scheduler.Update([root],catalog,250,worldObjects:[source,target]).VisualNodes);
        var fresh=root with {InstanceGeneration=2,Timestamp=300};
        Assert.Single(scheduler.Update([fresh],catalog,300,worldObjects:[source,target]).VisualNodes);
        Assert.Equal(2,scheduler.Update([fresh],catalog,400,worldObjects:[source,target]).VisualNodes.Count);
    }

    [Fact]
    public void VisualGenerationAndModelChangesDoNotDestroyTheObjectLifetime()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=INFINITE");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var root=Root();
        var source=Owner(1,10,100);var target=Owner(2,20,200);
        scheduler.Update([root],catalog,0,worldObjects:[source,target]);
        Assert.Single(scheduler.Update([root],catalog,100,worldObjects:[source with {VisualGeneration=2,BaseAsset=200},target]).VisualNodes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublishedIdentityOrPacketCreationReceiptRetiresOriginalBeforeFirstRender(bool publication)
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=INFINITE");
        var original=Owner(1,10,100);var target=Owner(2,20,200);
        var root=publication?Root() with {InitialSourceObject=original,PublishedEndpoints=new(original,target)}
            :Root() with {EndpointReceipt=new(100,200)};
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var update=scheduler.Update([root],catalog,100,worldObjects:[Owner(1,11,101),target]);
        Assert.Equal(1,update.Receipt.RemovalMarks);
        Assert.All(update.VisualNodes,n=>Assert.NotEqual(uint.MaxValue,n.DurationMilliseconds));
        // Preserve the scheduler's existing late-first-activation timing; the
        // replacement must not keep the original infinite effect alive.
        Assert.Empty(scheduler.Update([root],catalog,200,worldObjects:[Owner(1,11,101),target]).VisualNodes);
    }

    [Fact]
    public void FiniteUnflaggedBeamKeepsItsAuthoredLifetimeAfterReplacement()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=2");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();var root=Root();
        scheduler.Update([root],catalog,0,worldObjects:[Owner(1,10,100),Owner(2,20,200)]);
        Assert.Equal(2000u,Assert.Single(scheduler.Update([root],catalog,100,
            worldObjects:[Owner(1,11,101),Owner(2,21,201)]).VisualNodes).DurationMilliseconds);
    }

    [Fact]
    public void PassActivationOrderSurvivesDelayedStartsRedrawAndTickWrap()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=PassEffect\nDuration=INFINITE\nStartDelay=.2\n[EFFECT-2]\nEffectClass=PassEffect\nDuration=INFINITE");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        uint start=uint.MaxValue-100;
        var delayed=Root() with {GeometryKind=EnbSceneEffectGeometryKind.Object,Timestamp=start};
        var immediate=delayed with {EffectId=11,DefinitionId=2};
        var first=scheduler.Update([delayed,immediate],catalog,start).VisualNodes;
        Assert.Equal(0ul,first.Single(n=>n.Root.EffectId==10).ActivationSequence);
        ulong earlier=first.Single(n=>n.Root.EffectId==11).ActivationSequence;
        Assert.NotEqual(0ul,earlier);
        var next=scheduler.Update([delayed,immediate],catalog,150).VisualNodes;
        ulong later=next.Single(n=>n.Root.EffectId==10).ActivationSequence;
        Assert.True(later>earlier);
        Assert.Equal(earlier,next.Single(n=>n.Root.EffectId==11).ActivationSequence);
        Assert.Equal(later,scheduler.Update([immediate,delayed],catalog,151).VisualNodes.Single(n=>n.Root.EffectId==10).ActivationSequence);
    }

    private static EnbRetailPresentationCatalog Catalog(string text){var c=new EnbRetailPresentationCatalog();c.LoadEffectsText(text);return c;}
    private static EnbSceneEffectSnapshot Root()=>new(10,1,1,2,1,0,0,"",0,true,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);
    private static EnbSceneObjectSnapshot Owner(uint id,ulong identity,ulong ordinal)=>new(identity,1,id,false,false,1,48,1,0,0,0,0,false,"fixture",new(false,0,null),
        new(EnbSceneSpatialProviderKind.Fixed,new(0,Vector3.Zero,Quaternion.Identity,default,0,default,Vector3.UnitX),null),
        null,null,[],[],[],[],[],[],null,[],null,0,0){WorldCreationOrdinal=ordinal};
}
