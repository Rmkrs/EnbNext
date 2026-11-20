using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z33CurrentRetailEffectRslSchedulerTests
{
    public static IEnumerable<object[]> ConstructorDurationCases()
    {
        foreach(string kind in new[]{"StraightSeekEffect","SpiralSeekEffect","SplineRandomSeekEffect","LinearRandomSeekEffect","RObjLookAtEffect","LineEffect","RObjEffect","PassEffect"})
        foreach(string duration in new[]{"INFINITE",".0001","1.125"})
        foreach(bool cycle in new[]{false,true})yield return [kind,duration,cycle];
    }

    public static IEnumerable<object[]> InfiniteSeekScheduledLossCases()
    {
        foreach(string kind in new[]{"StraightSeekEffect","SpiralSeekEffect","SplineRandomSeekEffect","LinearRandomSeekEffect"})
        foreach(bool child in new[]{false,true})
        foreach(bool target in new[]{false,true})yield return [kind,child,target];
    }
    [Theory]
    [MemberData(nameof(InfiniteSeekScheduledLossCases))]
    public void InfiniteFactoryFlagsSurviveSeekDurationNormalizationBeforeActivation(string kind,bool child,bool target)
    {
        var catalog=Catalog((child?"[EFFECT-1]\nEffectClass=LineEffect\nDuration=2\nStartLinkID=2\n":"")+
            $"[EFFECT-{(child?2:1)}]\nEffectClass={kind}\nBaseAssetID=1191\nDuration=INFINITE\nStartDelay=1\nGoAwayWhenSourceDoes=FALSE\nGoAwayWhenTargetDoes=FALSE");
        var root=Snapshot(10,1,1,0,1,2,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var first=scheduler.Update([root],catalog,0,new HashSet<uint>{1,2});
        Assert.Equal(1u,Assert.Single(first.VisualNodes,n=>n.Definition.EffectClassKind!=EnbEffectClassKind.LineEffect).DurationMilliseconds);
        var remaining=new HashSet<uint>{target?1u:2u};
        var lost=scheduler.Update([root],catalog,250,remaining);
        Assert.Equal(0u,Assert.Single(lost.VisualNodes,n=>n.Definition.EffectClassKind!=EnbEffectClassKind.LineEffect).DurationMilliseconds);
        Assert.DoesNotContain(scheduler.Update([root],catalog,1000,remaining).VisualNodes,n=>n.Definition.EffectClassKind!=EnbEffectClassKind.LineEffect);
    }

    [Fact]
    public void ConstructorCycleRoundingRetainsNativeDwordWrap()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=PassEffect\nDuration=4000000\nCycleTime=3000000");
        var root=Snapshot(10,1,1,0,1,2,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        Assert.Equal(1705032704u,Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).VisualNodes).DurationMilliseconds);
    }

    [Theory]
    [MemberData(nameof(ConstructorDurationCases))]
    public void ConstructorStoresSeekNormalizationThenCommonCycleRoundedDuration(string kind,string duration,bool cycle)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass={kind}\nBaseAssetID=1191\nDuration={duration}\nCycleTime={(cycle?".5":"0")}\nNextLinkID=2\n[EFFECT-2]\nEffectClass=PassEffect\nDuration=1");
        var root=Snapshot(10,1,1,0,1,2,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        bool seek=kind.Contains("Seek",StringComparison.Ordinal);
        uint expected=duration=="INFINITE"?seek?1u:uint.MaxValue:duration==".0001"?seek?1u:0u:1125u;
        if(cycle && expected!=uint.MaxValue)expected=expected==0?500u:((expected+499)/500)*500;
        var first=scheduler.Update([root],catalog,0,new HashSet<uint>{1,2});
        if(expected==0){Assert.Equal(2,Assert.Single(first.VisualNodes).Definition.DefinitionId);return;}
        var node=Assert.Single(first.VisualNodes);
        Assert.Equal(expected,node.DurationMilliseconds);Assert.Equal(expected,node.TravelDurationMilliseconds);
        if(seek)Assert.True(node.TryGetRObjDefinition(out _,out var error),error);
        uint last=expected==uint.MaxValue?100000u:expected-1;
        Assert.Equal(1,Assert.Single(scheduler.Update([root],catalog,last,new HashSet<uint>{1,2}).VisualNodes).Definition.DefinitionId);
        if(expected!=uint.MaxValue)Assert.Equal(2,Assert.Single(scheduler.Update([root],catalog,expected,new HashSet<uint>{1,2}).VisualNodes).Definition.DefinitionId);
    }

    public static IEnumerable<object[]> SeekRemovalCases()
    {
        foreach(var kind in new[]{"StraightSeekEffect","SpiralSeekEffect","SplineRandomSeekEffect","LinearRandomSeekEffect"})
        foreach(bool linked in new[]{false,true})
        foreach(bool target in new[]{false,true})
        foreach(bool goAway in new[]{false,true})yield return [kind,linked,target,goAway];
    }

    [Theory]
    [MemberData(nameof(SeekRemovalCases))]
    public void SeekObserverShortensLifetimeWithoutChangingTravelDuration(string kind,bool linked,bool target,bool goAway)
    {
        var catalog=Catalog((linked?"[EFFECT-1]\nEffectClass=LineEffect\nDuration=.1\nNextLinkID=2\n":"")+
            $"[EFFECT-{(linked?2:1)}]\nEffectClass={kind}\nBaseAssetID=1191\nDuration=2\nCycleTime=.5\nGoAwayWhen{(target?"Target":"Source")}Does={goAway}");
        var root=Snapshot(10,1,1,0,1,2,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0,new HashSet<uint>{1,2});
        uint start=linked?100u:0u;
        var active=Assert.Single(scheduler.Update([root],catalog,start,new HashSet<uint>{1,2}).VisualNodes);
        Assert.Equal(2000u,active.TravelDurationMilliseconds);
        Assert.True(active.TryGetRObjDefinition(out _,out var error),error);
        var remaining=new HashSet<uint>{target?1u:2u};
        var tail=Assert.Single(scheduler.Update([root],catalog,start+750,remaining).VisualNodes);
        Assert.Equal(goAway?1000u:2000u,tail.DurationMilliseconds);
        Assert.Equal(2000u,tail.TravelDurationMilliseconds);
        Assert.Equal(active.Sequence,tail.Sequence);
        Assert.Single(scheduler.Update([root],catalog,start+999,remaining).VisualNodes);
        Assert.Equal(goAway?0:1,scheduler.Update([root],catalog,start+1000,remaining).VisualNodes.Count);
        Assert.Empty(scheduler.Update([root],catalog,start+2000,remaining).VisualNodes);
    }

    [Fact]
    public void ConstructionClockIsSeparateFromRootAndLinkedActivationDelays()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=PassEffect\nStartDelay=.5\nDuration=1\nStartLinkID=2\nStartLinkDelay=.75\nNextLinkID=3\n[EFFECT-2]\nEffectClass=PassEffect\nStartDelay=.25\nDuration=2\n[EFFECT-3]\nEffectClass=PassEffect\nStartDelay=.4\nDuration=2");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,1000,42);
        var pending=Assert.Single(scheduler.Update([root],catalog,200).VisualNodes);
        Assert.Equal(200u,pending.ConstructionTick);Assert.False(pending.Active);
        var started=scheduler.Update([root],catalog,1500).VisualNodes;
        var active=Assert.Single(started,n=>n.Definition.DefinitionId==1);
        Assert.Equal(200u,active.ConstructionTick);Assert.Equal(1500u,active.ActualStartTick);
        var child=Assert.Single(started,n=>n.Definition.DefinitionId==2);
        Assert.Equal(1500u,child.ConstructionTick);Assert.False(child.Active);
        var later=scheduler.Update([root],catalog,2500).VisualNodes;
        child=Assert.Single(later,n=>n.Definition.DefinitionId==2);
        Assert.Equal(1500u,child.ConstructionTick);Assert.Equal(2500u,child.ActualStartTick);
        var next=Assert.Single(later,n=>n.Definition.DefinitionId==3);
        Assert.Equal(2500u,next.ConstructionTick);Assert.False(next.Active);
    }

    [Theory]
    [InlineData("LineEffect")]
    [InlineData("RObjLookAtEffect")]
    [InlineData("SpiralSeekEffect")]
    public void UnresolvedEffectTargetCannotBindAnObjectWithTheSameId(string effectClass)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass={effectClass}\nDuration=2\nNextLinkID=2\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=2");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,0,42,99,geometry:EnbSceneEffectGeometryKind.ObjectToEffect);
        var first=scheduler.Update([root],catalog,0,new HashSet<uint>{42,99});
        Assert.Equal(1,first.Receipt.RejectedRoots);
        Assert.Empty(first.VisualNodes);
        Assert.Contains("packet-time effect position unavailable",scheduler.LifecycleHistory);
        Assert.Empty(scheduler.Update([root],catalog,2500,new HashSet<uint>{42,99}).VisualNodes);
    }

    [Fact]
    public void ObjectSoundRetainsACarrierBeforeItsNextVisualIsConstructed()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=SoundEffect\nDuration=2\nNextLinkID=2\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=2");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,0,42);
        var carrier=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes);
        Assert.Equal(1,carrier.Definition.DefinitionId);
        Assert.Equal(42u,carrier.BoundGameId);
        Assert.Equal(carrier.Sequence,Assert.Single(scheduler.Update([root],catalog,1999).VisualNodes).Sequence);
        var child=Assert.Single(scheduler.Update([root],catalog,2000).VisualNodes);
        Assert.Equal(2,child.Definition.DefinitionId);
        Assert.Equal(carrier.Sequence,child.ParentSequence);
        Assert.Empty(scheduler.Update([root],catalog,4000).VisualNodes);
    }

    [Fact]
    public void TargetBoundUnaryForwardsTargetToSoundAndRenderStateActivation()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=.1\nUnaryNextLinkToTarget=TRUE\nNextLinkID=2\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=.1\nStartLinkID=3\nNextLinkID=4\n[EFFECT-3]\nEffectClass=SoundEffect\nDuration=1\nSFX=activation.wav\n[EFFECT-4]\nEffectClass=RenderStateEffect\nDuration=1\nRenderStateID=3\nActionType=Activate");
        var root=Snapshot(10,1,1,0,42,43,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0);
        Assert.Equal(43u,Assert.Single(scheduler.Update([root],catalog,100).SoundNodes).BoundGameId);
        Assert.Equal(43u,Assert.Single(scheduler.Update([root],catalog,200).RenderStateEvents).BoundGameId);
    }

    [Fact]
    public void UnaryLinksKeepTheEndpointSelectedByTheirBinaryAncestor()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=LineEffect\nDuration=.1\nUnaryNextLinkToTarget=TRUE\nNextLinkID=2\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=.1\nNextLinkID=3\n[EFFECT-3]\nEffectClass=PassEffect\nDuration=INFINITE\nStartLinkID=4\n[EFFECT-4]\nEffectClass=RObjEffect\nDuration=INFINITE\nNextLinkID=5\n[EFFECT-5]\nEffectClass=RObjEffect\nDuration=1\nCycleTime=1");
        var root=Snapshot(10,1,1,0,42,43,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0);
        Assert.Equal(43u,Assert.Single(scheduler.Update([root],catalog,100).VisualNodes).BoundGameId);
        var hold=scheduler.Update([root],catalog,200).VisualNodes;
        Assert.Equal(2,hold.Count);
        Assert.All(hold,n=>Assert.Equal(43u,n.BoundGameId));
        var exit=scheduler.Update([],catalog,300).VisualNodes;
        Assert.Equal(5,Assert.Single(exit).Definition.DefinitionId);
        Assert.Equal(43u,exit[0].BoundGameId);
    }

    [Fact]
    public void SubmillisecondSoundActivatesOnceWhileCompletedSnapshotRemainsPresent()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=SoundEffect\nDuration=.0001\nSFX=click.wav");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,0,1);
        Assert.True(Assert.Single(scheduler.Update([root],catalog,0).SoundNodes).EndedAtActivation);
        Assert.Empty(scheduler.Update([root],catalog,0).SoundNodes);
        Assert.Empty(scheduler.Update([root],catalog,1000).SoundNodes);
        Assert.True(Assert.Single(scheduler.Update([root with {InstanceGeneration=2}],catalog,1000).SoundNodes).EndedAtActivation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledNextSoundActivatesOnceUnlessItUsesRadiusAttempts(bool radius)
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=PassEffect\nDuration=INFINITE\nNextLinkID=2\n[EFFECT-2]\nEffectClass=SoundEffect\nDuration=2\nSFX=powerdown.wav"+
            (radius?"\nTriggerSFXRadius=100":""));
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,0,1);
        scheduler.Update([root],catalog,0);
        var ended=scheduler.Update([],catalog,100);
        if(radius)Assert.Empty(ended.SoundNodes);
        else
        {
            var sound=Assert.Single(ended.SoundNodes);
            Assert.Equal(2,sound.Definition.DefinitionId);
            Assert.True(sound.Active && sound.EndedAtActivation);
            Assert.Equal(100u,sound.ActualStartTick);
        }
        Assert.Empty(ended.VisualNodes);
        Assert.Empty(scheduler.Update([],catalog,100).SoundNodes);
        Assert.Empty(scheduler.Update([],catalog,200).SoundNodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteUnaryChildOnlyShortensWhenItsAuthoredRemovalFlagIsSet(bool goAway)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass=RObjEffect\nDuration=.1\nStartLinkID=2\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=2\nCycleTime=.5\nGoAwayWhenSourceDoes={goAway}");
        var root=Snapshot(10,1,1,0,1);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0,new HashSet<uint>{1});
        var child=Assert.Single(scheduler.Update([root],catalog,150,new HashSet<uint>()).VisualNodes);
        Assert.Equal(goAway?500u:2000u,child.DurationMilliseconds);
        Assert.Equal(goAway?0:1,scheduler.Update([root],catalog,500,new HashSet<uint>()).VisualNodes.Count);
        Assert.Empty(scheduler.Update([root],catalog,2000,new HashSet<uint>()).VisualNodes);
    }

    [Theory]
    [InlineData(118)]
    [InlineData(121)]
    [InlineData(124)]
    [InlineData(127)]
    [InlineData(130)]
    [InlineData(133)]
    public void SelfRechargeIdleRetiresOnOwnerLossAtAuthoredCycleBoundary(int id)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=Snapshot(10,1,(ushort)id,0,1);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0,new HashSet<uint>{1});
        Assert.Equal(id+1,Assert.Single(scheduler.Update([root],catalog,900,new HashSet<uint>{1}).VisualNodes).Definition.DefinitionId);
        Assert.Equal(id+1,Assert.Single(scheduler.Update([root],catalog,2000,new HashSet<uint>()).VisualNodes).Definition.DefinitionId);
        Assert.Equal(id+1,Assert.Single(scheduler.Update([root],catalog,2899,new HashSet<uint>()).VisualNodes).Definition.DefinitionId);
        Assert.Equal(id+2,Assert.Single(scheduler.Update([root],catalog,2900,new HashSet<uint>()).VisualNodes).Definition.DefinitionId);
        Assert.Empty(scheduler.Update([root],catalog,3870,new HashSet<uint>()).VisualNodes);
        Assert.Empty(scheduler.Update([root],catalog,5000,new HashSet<uint>{1}).VisualNodes);
    }

    [Fact]
    public void UnaryChildObservesItsSelectedTargetRatherThanTheBinarySource()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nStartLinkID=2\nUnaryStartLinkToTarget=TRUE\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=INFINITE\nCycleTime=.5");
        var root=Snapshot(10,1,1,0,1,2,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        scheduler.Update([root],catalog,0,new HashSet<uint>{1,2});
        var child=Assert.Single(scheduler.Update([root],catalog,150,new HashSet<uint>{2}).VisualNodes);
        Assert.Equal(2u,child.BoundGameId);Assert.Equal(uint.MaxValue,child.DurationMilliseconds);
        Assert.Equal(500u,Assert.Single(scheduler.Update([root],catalog,250,new HashSet<uint>()).VisualNodes).DurationMilliseconds);
        Assert.Empty(scheduler.Update([root],catalog,500,new HashSet<uint>()).VisualNodes);
    }

    [Theory]
    [InlineData("LineEffect")]
    [InlineData("RObjLookAtEffect")]
    public void BinaryChildHasItsOwnEndpointRemovalObserver(string childClass)
    {
        var catalog=Catalog($"[EFFECT-1]\nEffectClass=RObjLookAtEffect\nDuration=.1\nStartLinkID=2\n[EFFECT-2]\nEffectClass={childClass}\nDuration=INFINITE");
        var root=Snapshot(10,1,1,0,1,2,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Equal(2,scheduler.Update([root],catalog,0,new HashSet<uint>{1,2}).VisualNodes.Count);
        Assert.Equal(2,Assert.Single(scheduler.Update([root],catalog,100,new HashSet<uint>{1,2}).VisualNodes).Definition.DefinitionId);
        Assert.Empty(scheduler.Update([root],catalog,150,new HashSet<uint>{1}).VisualNodes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ShieldInversionChildrenRetireAfterEitherEndpointLoss(int remaining)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var catalog=EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
        var root=Snapshot(10,1,98,0,1,2,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Equal(98,Assert.Single(scheduler.Update([root],catalog,0,new HashSet<uint>{1,2}).VisualNodes).Definition.DefinitionId);
        Assert.Equal(new[]{99,100,101},scheduler.Update([root],catalog,533,new HashSet<uint>{1,2}).VisualNodes.Select(n=>n.Definition.DefinitionId));
        var lost=scheduler.Update([root],catalog,2000,new HashSet<uint>(remaining==0?[]:[(uint)remaining]));
        var shrink=Assert.Single(lost.VisualNodes);
        Assert.Equal(102,shrink.Definition.DefinitionId);Assert.Equal(355u,shrink.DurationMilliseconds);
        Assert.Equal(102,Assert.Single(scheduler.Update([root],catalog,2354,new HashSet<uint>()).VisualNodes).Definition.DefinitionId);
        Assert.Empty(scheduler.Update([root],catalog,2355,new HashSet<uint>()).VisualNodes);
        Assert.Empty(scheduler.Update([root],catalog,5000,new HashSet<uint>{1,2}).VisualNodes);
        var next=root with {InstanceGeneration=2,Timestamp=6000};
        Assert.Equal(98,Assert.Single(scheduler.Update([next],catalog,6000,new HashSet<uint>{1,2}).VisualNodes).Definition.DefinitionId);
    }

    [Theory]
    [InlineData("AttachEffect")]
    [InlineData("RObjEffect")]
    [InlineData("PassEffect")]
    [InlineData("SoundEffect")]
    public void UnaryWithObservedMissingSourceNeverCreatesSoundOrChildrenAfterLateArrival(string effectClass)
    {
        var catalog=Catalog($"[EFFECT-674]\nEffectClass={effectClass}\nDuration=INFINITE\nSFX=afterburn.wav\nStartLinkID=675\nNextLinkID=675\n[EFFECT-675]\nEffectClass=RObjEffect\nDuration=1");
        var root=Snapshot(1,1,674,0,42) with {EndpointReceipt=new(null,null)};
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(uint tick in new uint[]{0,100,5000})
        {
            var update=scheduler.Update([root],catalog,tick,new HashSet<uint>{42});
            Assert.Empty(update.SoundNodes);Assert.Empty(update.VisualNodes);
            Assert.Equal(0,update.Receipt.CreatedStartLinks);
            Assert.Equal(0,update.Receipt.CreatedNextLinks);
        }
        var fresh=root with {InstanceGeneration=2,Timestamp=5000,EndpointReceipt=new(7,null)};
        Assert.Single(scheduler.Update([fresh],catalog,5000,new HashSet<uint>{42}).SoundNodes);
        Assert.Empty(scheduler.Update([fresh],catalog,5001,new HashSet<uint>()).SoundNodes);
    }

    [Theory]
    [InlineData(false,"AttachEffect")]
    [InlineData(true,"AttachEffect")]
    [InlineData(false,"RObjEffect")]
    [InlineData(true,"RObjEffect")]
    [InlineData(false,"PassEffect")]
    [InlineData(true,"PassEffect")]
    [InlineData(false,"SoundEffect")]
    [InlineData(true,"SoundEffect")]
    public void UnaryDoesNotTreatUnobservedOrPreviouslyPresentSourceAsInitialFailure(bool observed,string effectClass)
    {
        var catalog=Catalog($"[EFFECT-674]\nEffectClass={effectClass}\nDuration=INFINITE\nSFX=afterburn.wav");
        var root=Snapshot(1,1,674,0,42) with {EndpointReceipt=observed?new(7,null):null};
        var update=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0,new HashSet<uint>());
        Assert.Single(update.SoundNodes);
    }

    [Fact]
    public void LinkedSoundParentPublishesPoseIdentityForItsVisualChildWithoutExtendingLifetime()
    {
        var catalog=Catalog("[EFFECT-61]\nEffectClass=LinearRandomSeekEffect\nDuration=2\nStartLinkID=257\n[EFFECT-257]\nEffectClass=SoundEffect\nDuration=.5\nStartLinkID=64\nSFX=launch.wav\n[EFFECT-64]\nEffectClass=RObjEffect\nDuration=1");
        var root=Snapshot(1,1,61,0,42,43,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var active=scheduler.Update([root],catalog,0);
        var sound=Assert.Single(active.VisualNodes,n=>n.Definition.DefinitionId==257);
        var child=Assert.Single(active.VisualNodes,n=>n.Definition.DefinitionId==64);
        Assert.Equal(sound.Sequence,child.ParentSequence);
        Assert.Equal(sound.Sequence,Assert.Single(active.SoundNodes).Sequence);
        var afterParent=scheduler.Update([root],catalog,500);
        Assert.DoesNotContain(afterParent.VisualNodes,n=>n.Definition.DefinitionId==257);
        Assert.Equal(child.Sequence,Assert.Single(afterParent.VisualNodes,n=>n.Definition.DefinitionId==64).Sequence);
        Assert.DoesNotContain(scheduler.Update([root],catalog,1000).VisualNodes,n=>n.Definition.DefinitionId is 257 or 64);
        Assert.Empty(scheduler.Update([root],catalog,2000).VisualNodes);
    }
    [Theory]
    [InlineData(false,true,"StraightSeekEffect")]
    [InlineData(true,false,"StraightSeekEffect")]
    [InlineData(false,false,"StraightSeekEffect")]
    [InlineData(false,true,"LinearRandomSeekEffect")]
    [InlineData(true,false,"LinearRandomSeekEffect")]
    [InlineData(false,false,"LinearRandomSeekEffect")]
    public void WeaponMountedSeekWithInitiallyAbsentEndpointCreatesNoImpact(bool source,bool target,string effectClass)
    {
        var catalog=Catalog($"[EFFECT-786]\nEffectClass={effectClass}\nDuration=.805\nStartLinkID=787\nNextLinkID=92\nUnaryNextLinkToTarget=TRUE\n[EFFECT-787]\nEffectClass=RObjEffect\nDuration=.2\n[EFFECT-92]\nEffectClass=RObjEffect\nDuration=5");
        var root=Snapshot(1,1,786,0,42,43,geometry:EnbSceneEffectGeometryKind.ObjectToObject)
            with {BoneName="~01/~WEAP_01",EndpointReceipt=new(source?1ul:null,target?2ul:null)};
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(uint tick in new uint[]{0,805,1000,6000})
        {
            var result=scheduler.Update([root],catalog,tick,new HashSet<uint>{42,43});
            Assert.Empty(result.VisualNodes);Assert.Empty(result.SoundNodes);
            Assert.Equal(0,result.Receipt.CreatedStartLinks);Assert.Equal(0,result.Receipt.CreatedNextLinks);
        }
        Assert.NotEmpty(scheduler.Update([root with {InstanceGeneration=2,Timestamp=6000,EndpointReceipt=new(3,4)}],catalog,6000).VisualNodes);
    }

    [Fact]
    public void ParticleJetIsObservableDuringItsStageWithoutSuppressingNextLink()
    {
        var catalog=Catalog("[EFFECT-1]\nEffectClass=ParticleJetEffect\nDuration=3\nNextLinkID=2\n[EFFECT-2]\nEffectClass=RObjEffect\nDuration=1");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(1,1,1,0,42,43,geometry:EnbSceneEffectGeometryKind.ObjectToObject);
        foreach(uint tick in new uint[]{0,2999})
        {
            var jet=Assert.Single(scheduler.Update([root],catalog,tick).VisualNodes);
            Assert.True(jet.Active);
            Assert.Equal(EnbEffectClassKind.ParticleJetEffect,jet.Definition.EffectClassKind);
        }
        var hit=Assert.Single(scheduler.Update([root],catalog,3000).VisualNodes);
        Assert.True(hit.Active);
        Assert.Equal(2,hit.Definition.DefinitionId);
        Assert.Empty(scheduler.Update([root],catalog,4000).VisualNodes);
    }

    [Theory]
    [InlineData(false,true,"")]
    [InlineData(true,false,"")]
    [InlineData(false,true,"MUZZLE")]
    public void InitiallyAbsentLookAtEndpointNeverReachesGpuOrAudio(bool source,bool target,string bone)
    {
        var catalog=Catalog("[EFFECT-3]\nEffectClass=RObjLookAtEffect\nDuration=.5\nStartLinkID=4\n[EFFECT-4]\nEffectClass=RObjEffect\nDuration=.2");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(1,1,3,0,42,43,geometry:EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked)
            with {BoneName=bone,EndpointReceipt=new(source?1ul:null,target?2ul:null)};
        foreach(uint time in new uint[]{0,100,600})
        {
            var result=scheduler.Update([root],catalog,time,new HashSet<uint>{42,43});
            Assert.Empty(result.VisualNodes);Assert.Empty(result.SoundNodes);
            Assert.Equal(0,result.Receipt.CreatedStartLinks);
        }
    }
    [Theory]
    [InlineData(false,true,"")]
    [InlineData(true,false,"")]
    [InlineData(false,true,"MUZZLE")]
    [InlineData(true,false,"MUZZLE")]
    public void InitiallyAbsentLineEndpointCannotActivateLinksOrRebindLateObjects(bool source,bool target,string bone)
    {
        var catalog=Catalog("[EFFECT-664]\nEffectClass=LineEffect\nDuration=1\nStartDelay=.5\nStartLinkID=665\nNextLinkID=666\nSFX=psionic_shield_delivery.wav\n[EFFECT-665]\nEffectClass=RObjEffect\nDuration=.2\n[EFFECT-666]\nEffectClass=RObjEffect\nDuration=.2");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(3333192,1,664,0,100631,100661,geometry:EnbSceneEffectGeometryKind.ObjectToObject)
            with {BoneName=bone,EndpointReceipt=new(source?64ul:null,target?65ul:null)};
        foreach(uint tick in new uint[]{0,250,500,1500,3000})
        {
            // Both numeric IDs appear later; packet-time absence still controls.
            var result=scheduler.Update([root],catalog,tick,new HashSet<uint>{100631,100661});
            Assert.Empty(result.VisualNodes);Assert.Empty(result.SoundNodes);
            Assert.Equal(0,result.Receipt.CreatedStartLinks);Assert.Equal(0,result.Receipt.CreatedNextLinks);
        }
        Assert.Equal(0,scheduler.Update([],catalog,3001).Receipt.TrackedRoots);
    }

    [Theory]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToPoint,false,true,"")]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToPoint,false,true,"MUZZLE")]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked,false,true,"")]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked,true,false,"")]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked,false,true,"MUZZLE")]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked,true,false,"MUZZLE")]
    public void OtherLinePacketInitialFailureCannotActivateLinks(EnbSceneEffectGeometryKind geometry,bool source,bool target,string bone)
    {
        var catalog=Catalog("[EFFECT-664]\nEffectClass=LineEffect\nDuration=1\nStartDelay=.5\nStartLinkID=665\nNextLinkID=666\nSFX=psionic_shield_delivery.wav\n[EFFECT-665]\nEffectClass=RObjEffect\nDuration=.2\n[EFFECT-666]\nEffectClass=RObjEffect\nDuration=.2");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(1,1,664,0,42,43,geometry:geometry) with {
            BoneName=bone,WorldPoint=new(100,0,0),DurationLink=-1,
            EndpointReceipt=new(source?1ul:null,target?2ul:null)};
        foreach(uint tick in new uint[]{0,250,500,1500,3000})
        {
            var result=scheduler.Update([root],catalog,tick,new HashSet<uint>{42,43});
            Assert.Empty(result.VisualNodes);Assert.Empty(result.SoundNodes);
            Assert.Equal(0,result.Receipt.CreatedStartLinks);Assert.Equal(0,result.Receipt.CreatedNextLinks);
        }
        Assert.Equal(0,scheduler.Update([],catalog,3001).Receipt.TrackedRoots);
    }

    [Fact]
    public void PointLineWithObservedSourceNeedsNoTargetObject()
    {
        var catalog=Catalog("[EFFECT-664]\nEffectClass=LineEffect\nDuration=1");
        var root=Snapshot(1,1,664,0,42,0,geometry:EnbSceneEffectGeometryKind.ObjectToPoint)
            with {WorldPoint=new(100,0,0),EndpointReceipt=new(1,null)};
        var result=new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0,new HashSet<uint>{42});
        Assert.Single(result.VisualNodes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InitiallyAbsentSeekEndpointsNeverActivateOrCreateLinks(bool sourcePresent, bool targetPresent)
    {
        var catalog = Catalog("[EFFECT-850]\nEffectClass=SplineRandomSeekEffect\nDuration=1\nStartLinkID=851\nNextLinkID=65\n[EFFECT-851]\nEffectClass=RObjEffect\nDuration=.13\n[EFFECT-65]\nEffectClass=RObjEffect\nDuration=5");
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root = Snapshot(1, 1, 850, 0, 42, 43, geometry: EnbSceneEffectGeometryKind.ObjectToObject)
            with { EndpointReceipt = new(sourcePresent ? 1ul : null, targetPresent ? 2ul : null) };
        foreach (uint tick in new uint[] { 0, 100, 1000, 6000 })
        {
            var update = scheduler.Update([root], catalog, tick, new HashSet<uint> { 42, 43 });
            Assert.Empty(update.VisualNodes);
            Assert.Empty(update.SoundNodes);
            Assert.Empty(update.RenderStateEvents);
            Assert.Equal(1, update.Receipt.CompletedTombstones);
            Assert.Equal(0, update.Receipt.CreatedStartLinks);
            Assert.Equal(0, update.Receipt.CreatedNextLinks);
        }
        // A fresh generation with successful lookup can run normally.
        var next = root with { InstanceGeneration = 2, Timestamp = 6000, EndpointReceipt = new(3, 4) };
        Assert.NotEmpty(scheduler.Update([next], catalog, 6000).VisualNodes);
        scheduler.Update([], catalog, 12000);
        scheduler.Update([], catalog, 18000);
        Assert.Equal(0, scheduler.TrackedRootCount);
    }

    [Theory]
    [InlineData(false,"SplineRandomSeekEffect","")]
    [InlineData(true,"SplineRandomSeekEffect","")]
    [InlineData(false,"StraightSeekEffect","~01/~WEAP_01")]
    [InlineData(true,"StraightSeekEffect","~01/~WEAP_01")]
    [InlineData(false,"LinearRandomSeekEffect","~01/~WEAP_01")]
    [InlineData(true,"LinearRandomSeekEffect","~01/~WEAP_01")]
    public void CurrentRenderAbsenceDoesNotReplacePacketTimeEvidence(bool observed,string effectClass,string bone)
    {
        var catalog = Catalog($"[EFFECT-850]\nEffectClass={effectClass}\nDuration=1");
        var root = Snapshot(1, 1, 850, 0, 42, 43, geometry: EnbSceneEffectGeometryKind.ObjectToObject)
            with { BoneName=bone, EndpointReceipt = observed ? new(1, 2) : null };
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.NotEmpty(scheduler.Update([root], catalog, 0, new HashSet<uint>()).VisualNodes);
    }

    [Fact]
    public void RepeatedNextDefinitionRetainsItsActualParentSequence()
    {
        var catalog=Catalog("""
            [EFFECT-10]
            EffectClass=RObjEffect
            BaseAssetID=512
            Duration=1
            NextLinkID=11
            [EFFECT-11]
            EffectClass=RObjEffect
            BaseAssetID=512
            Duration=1
            NextLinkID=11
            """);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(1,1,10,0,42);
        var parent=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes);
        Assert.Null(parent.ParentSequence);
        var first=Assert.Single(scheduler.Update([root],catalog,1000).VisualNodes);
        Assert.Equal(parent.Sequence,first.ParentSequence);
        var repeated=Assert.Single(scheduler.Update([root],catalog,2000).VisualNodes);
        Assert.Equal(first.Definition.DefinitionId,repeated.Definition.DefinitionId);
        Assert.Equal(first.Sequence,repeated.ParentSequence);
        Assert.NotEqual(parent.Sequence,repeated.ParentSequence);
    }

    [Fact]
    public void EffectInstanceGenerationDistinguishesSameWireEffectIdReuseAndCrossesSnapshotBoundary()
    {
        var gameplay = new Net7GameplayState();
        gameplay.AddSyntheticEffect(EffectState(77, 10, timestamp: 100));
        ulong first = gameplay.Effects[77].InstanceGeneration;
        Assert.True(first > 0);

        gameplay.AddSyntheticEffect(EffectState(77, 11, timestamp: 200));
        ulong second = gameplay.Effects[77].InstanceGeneration;
        Assert.True(second > first);

        EnbSceneSnapshot snapshot = new EnbPresentationSnapshotBuilder().Build(
            new Net7WorldState(), gameplay.Effects, assets: null, clientTime: 300);
        EnbSceneEffectSnapshot captured = Assert.Single(snapshot.Effects);
        Assert.Equal(second, captured.InstanceGeneration);
        Assert.Equal((ushort)11, captured.DefinitionId);
    }

    [Fact]
    public void ScheduledRenderStateEffectFiresExactlyOnceAtActualReachedFrame()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-10]
            EffectClass=RenderStateEffect
            RenderStateID=4
            ActionType=Activate
            StartDelay=0.050
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(1, 1, 10, timestamp: 100, source: 42);

        Assert.Empty(scheduler.Update([root], catalog, 149).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent fired = Assert.Single(
            scheduler.Update([root], catalog, 151).RenderStateEvents);
        Assert.Equal(151u, fired.ActualStartClientTick);
        Assert.Equal(42u, fired.BoundGameId);
        Assert.Equal(4, fired.RenderStateId);
        Assert.Equal(EnbRenderStateEffectActionKind.Activate, fired.ActionKind);
        Assert.Empty(scheduler.Update([root], catalog, 152).RenderStateEvents);
    }

    [Fact]
    public void DwordReachAndElapsedComparisonsRemainCorrectAcrossClockWrap()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-10]
            EffectClass=RenderStateEffect
            RenderStateID=4
            ActionType=Activate
            StartDelay=0.016
            Duration=0.032
            NextLinkID=11
            [EFFECT-11]
            EffectClass=RenderStateEffect
            RenderStateID=4
            ActionType=Deactivate
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        uint timestamp = 0xfffffff8u;
        EnbSceneEffectSnapshot root = Snapshot(1, 1, 10, timestamp, source: 42);

        Assert.Empty(scheduler.Update([root], catalog, 7).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent activate = Assert.Single(
            scheduler.Update([root], catalog, 8).RenderStateEvents);
        Assert.Equal(8u, activate.ActualStartClientTick);
        Assert.Empty(scheduler.Update([root], catalog, 39).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent deactivate = Assert.Single(
            scheduler.Update([root], catalog, 40).RenderStateEvents);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.NextLink, deactivate.LinkKind);
    }

    [Fact]
    public void StartLinkUsesActualParentStartThenStartLinkDelayAndChildStartDelay()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration=1.0
            StartLinkID=2
            StartLinkDelay=0.125
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=9
            ActionType=Activate
            StartDelay=0.125
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 1, timestamp: 100, source: 5);

        _ = scheduler.Update([root], catalog, 120); // parent actually starts late at 120
        Assert.Empty(scheduler.Update([root], catalog, 369).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent child = Assert.Single(
            scheduler.Update([root], catalog, 370).RenderStateEvents);
        Assert.Equal(370u, child.ActualStartClientTick);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.StartLink, child.LinkKind);
    }

    [Fact]
    public void StartLinkDurationForwardingOffsetsByLinkDelayWhenOffsetFlagIsClear()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration=9.0
            StartLinkID=2
            StartLinkDelay=0.200
            PassDurationToStartLink=TRUE
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Activate
            Duration=9.0
            NextLinkID=3
            [EFFECT-3]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Deactivate
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 1, timestamp: 0, source: 5, duration: 1000);

        _ = scheduler.Update([root], catalog, 0);
        EnbCurrentRetailRenderStateEffectEvent activate = Assert.Single(
            scheduler.Update([root], catalog, 200).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Activate, activate.ActionKind);
        Assert.Empty(scheduler.Update([root], catalog, 999).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent deactivate = Assert.Single(
            scheduler.Update([root], catalog, 1000).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Deactivate, deactivate.ActionKind);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.NextLink, deactivate.LinkKind);
    }

    [Fact]
    public void StartLinkDurationForwardingKeepsFullDurationWhenOffsetFlagIsSet()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration=9.0
            StartLinkID=2
            StartLinkDelay=0.200
            PassDurationToStartLink=TRUE
            OffsetDurationPassedToStartLink=TRUE
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Activate
            Duration=9.0
            NextLinkID=3
            [EFFECT-3]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Deactivate
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 1, timestamp: 0, source: 5, duration: 1000);

        _ = scheduler.Update([root], catalog, 0);
        EnbCurrentRetailRenderStateEffectEvent activate = Assert.Single(
            scheduler.Update([root], catalog, 200).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Activate, activate.ActionKind);
        Assert.Empty(scheduler.Update([root], catalog, 1199).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent deactivate = Assert.Single(
            scheduler.Update([root], catalog, 1200).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Deactivate, deactivate.ActionKind);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.NextLink, deactivate.LinkKind);
    }

    [Theory]
    [InlineData("1", "1", false, 750u)]
    [InlineData("1", "2", false, 750u)]
    [InlineData("INFINITE", ".2", false, 4294967095u)]
    [InlineData("INFINITE", ".2", true, uint.MaxValue)]
    [InlineData("4000000", ".2", false, 3999999800u)]
    public void StartLinkFactoryPreservesDwordAndResolvesZeroFromChild(string duration,string delay,bool offset,uint expected)
    {
        var catalog=Catalog($"""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration={duration}
            StartLinkID=2
            StartLinkDelay={delay}
            PassDurationToStartLink=TRUE
            OffsetDurationPassedToStartLink={offset}
            [EFFECT-2]
            EffectClass=PassEffect
            Duration=.75
            NextLinkID=3
            [EFFECT-3]
            EffectClass=PassEffect
            Duration=1
            """);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,timestamp:0,source:5);
        var child=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes,n=>n.Definition.DefinitionId==2);
        Assert.Equal(expected,child.DurationMilliseconds);
        uint start=delay=="1"?1000u:delay=="2"?2000u:200u;
        var activated=scheduler.Update([root],catalog,start);
        if(expected==750)
        {
            Assert.Contains(activated.VisualNodes,n=>n.Active && n.Definition.DefinitionId==2);
            Assert.Contains(scheduler.Update([root],catalog,start+749).VisualNodes,n=>n.Active && n.Definition.DefinitionId==2);
            Assert.Contains(scheduler.Update([root],catalog,start+750).VisualNodes,n=>n.Active && n.Definition.DefinitionId==3);
        }
        else
        {
            // Native elapsed-minus-duration uses the DWORD half-range test.
            Assert.Contains(activated.VisualNodes,n=>n.Active && n.Definition.DefinitionId==(expected==uint.MaxValue?2:3));
        }
    }

    [Theory]
    [InlineData("LineEffect", "LineEffect", 750u, 100u, 300u)]
    [InlineData("LineEffect", "PassEffect", 750u, 100u, 600u)]
    [InlineData("PassEffect", "LineEffect", 1500u, 201u, 300u)]
    [InlineData("PassEffect", "PassEffect", 1500u, 201u, 600u)]
    public void FactorySpeedupScalesOnlyLineTimingAndPropagatesThroughStartAndNext(string parentClass,string childClass,uint parentDuration,uint linkDelay,uint childDuration)
    {
        var catalog=Catalog($"""
            [EFFECT-1]
            EffectClass={parentClass}
            Duration=1.125
            CycleTime=.5
            StartDelay=.125
            StartLinkID=2
            StartLinkDelay=.2019
            NextLinkID=3
            [EFFECT-2]
            EffectClass={childClass}
            Duration=.5019
            CycleTime=.2
            StartDelay=.125
            [EFFECT-3]
            EffectClass={childClass}
            Duration=.5019
            CycleTime=.2
            StartDelay=.125
            """);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,timestamp:0,source:5) with {EffectScalar=2,GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=6};
        var scheduled=Assert.Single(scheduler.Update([root],catalog,0).VisualNodes);
        Assert.Equal(parentDuration,scheduled.DurationMilliseconds);
        Assert.False(scheduled.Active);
        Assert.DoesNotContain(scheduler.Update([root],catalog,124).VisualNodes,n=>n.Active);
        var started=scheduler.Update([root],catalog,125);
        Assert.Contains(started.VisualNodes,n=>n.Active && n.Definition.DefinitionId==1);
        Assert.Equal(childDuration,Assert.Single(started.VisualNodes,n=>n.Definition.DefinitionId==2).DurationMilliseconds);
        uint childStart=125+linkDelay+125;
        Assert.DoesNotContain(scheduler.Update([root],catalog,childStart-1).VisualNodes,n=>n.Active && n.Definition.DefinitionId==2);
        Assert.Contains(scheduler.Update([root],catalog,childStart).VisualNodes,n=>n.Active && n.Definition.DefinitionId==2);
        var next=Assert.Single(scheduler.Update([root],catalog,125+parentDuration).VisualNodes,n=>n.Definition.DefinitionId==3);
        Assert.Equal(childDuration,next.DurationMilliseconds);
        Assert.False(next.Active);
        Assert.Contains(scheduler.Update([root],catalog,250+parentDuration).VisualNodes,n=>n.Active && n.Definition.DefinitionId==3);
    }

    [Fact]
    public void LineFactorySpeedupDoesNotRescalePacketOrForwardedDuration()
    {
        var catalog=Catalog("""
            [EFFECT-1]
            EffectClass=LineEffect
            Duration=9
            StartLinkID=2
            StartLinkDelay=.2
            PassDurationToStartLink=TRUE
            [EFFECT-2]
            EffectClass=LineEffect
            Duration=9
            """);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root=Snapshot(10,1,1,timestamp:0,source:5,duration:800) with
            {EffectScalar=2,GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,TargetGameId=6};
        var nodes=scheduler.Update([root],catalog,0).VisualNodes;
        Assert.Equal(800u,Assert.Single(nodes,n=>n.Definition.DefinitionId==1).DurationMilliseconds);
        Assert.Equal(700u,Assert.Single(nodes,n=>n.Definition.DefinitionId==2).DurationMilliseconds);
        Assert.Contains(scheduler.Update([root],catalog,100).VisualNodes,n=>n.Active && n.Definition.DefinitionId==2);
        Assert.Empty(scheduler.Update([root],catalog,800).VisualNodes);
    }

    [Fact]
    public void NextLinkIsCreatedAtActualExpiryFrameAndThenHonorsChildStartDelay()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=RenderStateEffect
            RenderStateID=7
            ActionType=Activate
            Duration=0.100
            NextLinkID=2
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=7
            ActionType=Deactivate
            StartDelay=0.025
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 1, timestamp: 0, source: 5);

        Assert.Single(scheduler.Update([root], catalog, 0).RenderStateEvents);
        Assert.Empty(scheduler.Update([root], catalog, 100).RenderStateEvents);
        Assert.Empty(scheduler.Update([root], catalog, 124).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent next = Assert.Single(
            scheduler.Update([root], catalog, 125).RenderStateEvents);
        Assert.Equal(125u, next.ActualStartClientTick);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.NextLink, next.LinkKind);
    }

    [Fact]
    public void EndedParentRemainsAliveUntilItsLinkedDescendantCompletes()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration=0.050
            StartLinkID=2
            [EFFECT-2]
            EffectClass=PassEffect
            Duration=0.200
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 1, timestamp: 0, source: 5);

        _ = scheduler.Update([root], catalog, 0);
        EnbCurrentRetailEffectRslSchedulerUpdate at50 = scheduler.Update([root], catalog, 50);
        Assert.Equal(1, at50.Receipt.TrackedRoots);
        Assert.Equal(1, at50.Receipt.EndedWaitingForChildren);

        EnbCurrentRetailEffectRslSchedulerUpdate at200 = scheduler.Update([root], catalog, 200);
        Assert.Equal(1, at200.Receipt.CompletedTombstones);
        Assert.Equal(0, at200.Receipt.ActiveNodes);
    }

    [Fact]
    public void SnapshotRemovalMarksTreeAndRoundsActiveDurationToNextCycleBoundary()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration=INFINITE
            CycleTime=0.100
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 1, timestamp: 0, source: 5, duration: -1);

        _ = scheduler.Update([root], catalog, 0);
        EnbCurrentRetailEffectRslSchedulerUpdate removed = scheduler.Update([], catalog, 250);
        Assert.Equal(1, removed.Receipt.RemovalMarks);
        Assert.Equal(1, removed.Receipt.RetiringRoots);
        Assert.Equal(1, scheduler.Update([], catalog, 299).Receipt.TrackedRoots);
        Assert.Equal(0, scheduler.Update([], catalog, 300).Receipt.TrackedRoots);
    }

    [Fact]
    public void BinaryParentUnaryStartLinkCanBindRenderStateEffectToTarget()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=LineEffect
            Duration=1.0
            StartLinkID=2
            UnaryStartLinkToTarget=TRUE
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=3
            ActionType=Activate
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(
            10, 1, 1, timestamp: 0, source: 100, target: 200,
            geometry: EnbSceneEffectGeometryKind.ObjectToObject);

        EnbCurrentRetailRenderStateEffectEvent fired = Assert.Single(
            scheduler.Update([root], catalog, 0).RenderStateEvents);
        Assert.Equal(200u, fired.BoundGameId);
        Assert.Equal(100u, fired.SourceGameId);
        Assert.Equal(200u, fired.TargetGameId);
    }

    [Fact]
    public void TadaOGateSignDeactivateStartLinkActivatePairFiresInRetailOrder()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-10215]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Deactivate
            Duration=1.0
            StartLinkID=10216
            [EFFECT-10216]
            EffectClass=RenderStateEffect
            RenderStateID=2
            ActionType=Activate
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 10215, timestamp: 0, source: 500);

        EnbCurrentRetailRenderStateEffectEvent[] events = scheduler.Update([root], catalog, 0).RenderStateEvents.ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(EnbRenderStateEffectActionKind.Deactivate, events[0].ActionKind);
        Assert.Equal(1, events[0].RenderStateId);
        Assert.Equal(EnbRenderStateEffectActionKind.Activate, events[1].ActionKind);
        Assert.Equal(2, events[1].RenderStateId);
        Assert.Equal(EnbCurrentRetailEffectLinkKind.StartLink, events[1].LinkKind);
    }

    [Fact]
    public void NpcAttackLingerStartLinkRampPairCarriesExactActionTimes()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-30000]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Linger
            ActionTime=0.2
            Duration=1.0
            StartLinkID=30001
            [EFFECT-30001]
            EffectClass=RenderStateEffect
            RenderStateID=16
            ActionType=Ramp
            ActionTime=0.2
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 30000, timestamp: 0, source: 500);

        EnbCurrentRetailRenderStateEffectEvent[] events = scheduler.Update([root], catalog, 0).RenderStateEvents.ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(EnbRenderStateEffectActionKind.Linger, events[0].ActionKind);
        Assert.Equal(200, events[0].ActionTimeMilliseconds);
        Assert.Equal(EnbRenderStateEffectActionKind.Ramp, events[1].ActionKind);
        Assert.Equal(200, events[1].ActionTimeMilliseconds);
    }

    [Fact]
    public void NaturallyCompletedRootIsTombstonedWhileSnapshotStillCarriesItAndNeverRefires()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Activate
            Duration=0.010
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(10, 1, 1, timestamp: 0, source: 5);

        Assert.Single(scheduler.Update([root], catalog, 0).RenderStateEvents);
        Assert.Empty(scheduler.Update([root], catalog, 10).RenderStateEvents);
        EnbCurrentRetailEffectRslSchedulerUpdate later = scheduler.Update([root], catalog, 1000);
        Assert.Empty(later.RenderStateEvents);
        Assert.Equal(1, later.Receipt.CompletedTombstones);
    }

    [Fact]
    public void NewGenerationCanStartWhilePriorSameEffectIdTreeRetires()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Activate
            Duration=INFINITE
            CycleTime=0.100
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=2
            ActionType=Activate
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot oldRoot = Snapshot(77, 1, 1, timestamp: 0, source: 5, duration: -1);
        Assert.Single(scheduler.Update([oldRoot], catalog, 0).RenderStateEvents);

        EnbSceneEffectSnapshot newRoot = Snapshot(77, 2, 2, timestamp: 50, source: 5);
        EnbCurrentRetailEffectRslSchedulerUpdate update = scheduler.Update([newRoot], catalog, 50);
        EnbCurrentRetailRenderStateEffectEvent fired = Assert.Single(update.RenderStateEvents);
        Assert.Equal((ulong)2, fired.RootInstanceGeneration);
        Assert.Equal(2, update.Receipt.TrackedRoots);
        Assert.Equal(1, update.Receipt.RetiringRoots);
    }

    [Fact]
    public void DurationLinkedPacketWithNegativeSelectorKeepsOrdinaryAuthoredDurationPath()
    {
        EnbRetailPresentationCatalog catalog = Catalog("""
            [EFFECT-1]
            EffectClass=RenderStateEffect
            RenderStateID=9
            ActionType=Activate
            Duration=0.250
            NextLinkID=2
            [EFFECT-2]
            EffectClass=RenderStateEffect
            RenderStateID=9
            ActionType=Deactivate
            Duration=1.0
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        EnbSceneEffectSnapshot root = Snapshot(77, 1, 1, timestamp: 0, source: 5, target: 6,
            geometry: EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked, durationLink: -1);

        EnbCurrentRetailRenderStateEffectEvent activate = Assert.Single(
            scheduler.Update([root], catalog, 0).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Activate, activate.ActionKind);
        Assert.Empty(scheduler.Update([root], catalog, 249).RenderStateEvents);
        EnbCurrentRetailRenderStateEffectEvent deactivate = Assert.Single(
            scheduler.Update([root], catalog, 250).RenderStateEvents);
        Assert.Equal(EnbRenderStateEffectActionKind.Deactivate, deactivate.ActionKind);
    }

    [Theory]
    [InlineData(EnbSceneEffectGeometryKind.Object)]
    [InlineData(EnbSceneEffectGeometryKind.ObjectToObject)]
    public void CancelledParentPassesRemovalToNewNextAndStartDescendants(EnbSceneEffectGeometryKind geometry)
    {
        var catalog = Catalog("""
            [EFFECT-1]
            EffectClass=PassEffect
            Duration=.6
            NextLinkID=2
            [EFFECT-2]
            EffectClass=PassEffect
            Duration=.6
            NextLinkID=3
            StartLinkID=4
            StartLinkDelay=.125
            [EFFECT-3]
            EffectClass=PassEffect
            Duration=INFINITE
            HideBaseMesh=TRUE
            [EFFECT-4]
            EffectClass=PassEffect
            Duration=INFINITE
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root = Snapshot(10, 1, 1, 0, 5, target: 6, geometry: geometry);
        Assert.Single(scheduler.Update([root], catalog, 0).VisualNodes);
        var removed = scheduler.Update([], catalog, 100);
        Assert.DoesNotContain(removed.VisualNodes, n => n.Active);
        // Delayed children retain their callback and native cycle retirement.
        var delayed = Assert.Single(removed.VisualNodes);
        Assert.Equal(4, delayed.Definition.DefinitionId);
        Assert.False(delayed.Active);
        Assert.Equal(1, removed.Receipt.ScheduledNodes);
        Assert.Empty(scheduler.Update([], catalog, 225).VisualNodes);
        Assert.Equal(0, scheduler.TrackedRootCount);
        Assert.Empty(scheduler.Update([], catalog, 5000).VisualNodes);
        Assert.Contains("E3 NextLink duration 0ms cancel True", scheduler.LifecycleHistory);
        // The next activation is independent of the removed generation.
        Assert.Single(scheduler.Update([root with { InstanceGeneration=2, Timestamp=5001 }], catalog, 5001).VisualNodes);
    }

    [Fact]
    public void InheritedCancellationKeepsAnAuthoredChildCycleButNotItsFullDuration()
    {
        var catalog = Catalog("""
            [EFFECT-1]
            EffectClass=RObjEffect
            Duration=1
            CycleTime=.25
            NextLinkID=2
            [EFFECT-2]
            EffectClass=PassEffect
            Duration=INFINITE
            CycleTime=.125
            NextLinkID=3
            [EFFECT-3]
            EffectClass=PassEffect
            Duration=INFINITE
            """);
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root = Snapshot(10, 1, 1, 0, 5);
        scheduler.Update([root], catalog, 0);
        Assert.Equal(1, Assert.Single(scheduler.Update([], catalog, 100).VisualNodes).Definition.DefinitionId);
        Assert.Equal(2, Assert.Single(scheduler.Update([], catalog, 250).VisualNodes).Definition.DefinitionId);
        Assert.Single(scheduler.Update([], catalog, 374).VisualNodes);
        Assert.Empty(scheduler.Update([], catalog, 375).VisualNodes);
        Assert.Equal(0, scheduler.TrackedRootCount);
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(700u)]
    [InlineData(1300u)]
    [InlineData(1700u)] // Pending at its deadline: cancellation arrives before update.
    public void ManualCancellationRetainsDelayedFlashVisualAndSound(uint cancelAt)
    {
        var catalog = CloakFlashCatalog();
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root = Snapshot(10, 1, 180, 0, 5);
        for (uint tick=0; tick<cancelAt; tick+=100)
            scheduler.Update([root], catalog, tick);
        bool sawFlash = false;
        for (uint tick=cancelAt; tick<cancelAt+2000; tick+=100)
        {
            var update = scheduler.Update([], catalog, tick);
            bool visual = update.VisualNodes.Any(n => n.Active && n.Definition.DefinitionId==185);
            bool sound = update.SoundNodes.Any(n => n.Active && n.Definition.DefinitionId==185);
            Assert.Equal(visual, sound);
            sawFlash |= visual;
        }
        Assert.True(sawFlash);
        Assert.Equal(0, scheduler.TrackedRootCount);
    }

    [Fact]
    public void NormalFlashStillStartsAndAlreadyActiveFlashKeepsItsCycle()
    {
        var catalog = CloakFlashCatalog();
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root = Snapshot(10, 1, 180, 0, 5);
        for (uint tick=0; tick<1700; tick+=100) scheduler.Update([root], catalog, tick);
        var active = scheduler.Update([root], catalog, 1700);
        Assert.Contains(active.VisualNodes, n => n.Active && n.Definition.DefinitionId==185);
        Assert.Contains(active.SoundNodes, n => n.Active && n.Definition.DefinitionId==185);
        var cancelled = scheduler.Update([], catalog, 1701);
        Assert.Contains(cancelled.VisualNodes, n => n.Active && n.Definition.DefinitionId==185);
        Assert.Contains(cancelled.SoundNodes, n => n.Active && n.Definition.DefinitionId==185);
        Assert.Empty(scheduler.Update([], catalog, 2200).VisualNodes);
        Assert.Empty(scheduler.Update([], catalog, 2201).SoundNodes);
    }

    private static EnbRetailPresentationCatalog CloakFlashCatalog() => Catalog("""
        [EFFECT-180]
        EffectClass=PassEffect
        Duration=.6
        NextLinkID=181
        [EFFECT-181]
        EffectClass=PassEffect
        Duration=.6
        NextLinkID=184
        [EFFECT-184]
        EffectClass=PassEffect
        Duration=.6
        StartLinkID=185
        StartLinkDelay=.5
        [EFFECT-185]
        EffectClass=RObjEffect
        Duration=.5
        CycleTime=.5
        SFX=cloaking_flash.wav
        """);

    [Fact]
    public void LifecycleHistoryRetainsExpiredPhasesIsBoundedAndClearsWithContext()
    {
        var catalog = Catalog("[EFFECT-1]\nEffectClass=PassEffect\nDuration=.125");
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var root = Snapshot(10, 1, 1, 0, 5);
        scheduler.Update([root], catalog, 0);
        scheduler.Update([root], catalog, 125);
        string history = scheduler.LifecycleHistory;
        Assert.Contains("125ms end", history);
        scheduler.Update([root], catalog, 1000);
        Assert.Same(history, scheduler.LifecycleHistory);
        for (uint i=1; i<40; i++)
            scheduler.Update([root with { InstanceGeneration=i+1, Timestamp=i*1000 }], catalog, i*1000);
        Assert.Equal(64, scheduler.LifecycleHistory.Split('\n').Length);
        scheduler.Clear();
        Assert.Empty(scheduler.LifecycleHistory);
    }

    private static EnbRetailPresentationCatalog Catalog(string effects)
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText(effects);
        return catalog;
    }

    private static Net7EffectState EffectState(uint effectId, ushort definitionId, uint timestamp)
        => new(
            effectId, SourceId: 1, TargetId: 1, definitionId, timestamp,
            DurationMilliseconds: 1000, BoneName: string.Empty, Mask: 0, IsObjectToObject: false);

    private static EnbSceneEffectSnapshot Snapshot(
        uint effectId,
        ulong generation,
        ushort definitionId,
        uint timestamp,
        uint source,
        uint target = 0,
        int duration = 0,
        EnbSceneEffectGeometryKind geometry = EnbSceneEffectGeometryKind.Object,
        short durationLink = 0,
        float effectScalar = 1f)
        => new(
            EffectId: effectId,
            InstanceGeneration: generation,
            SourceGameId: source,
            TargetGameId: target,
            DefinitionId: definitionId,
            Timestamp: timestamp,
            DurationMilliseconds: duration,
            BoneName: string.Empty,
            Mask: 0,
            IsObjectToObject: geometry is EnbSceneEffectGeometryKind.ObjectToObject,
            Scale: 1f,
            SpatialOffset: default,
            EndpointShortA: 0,
            EndpointShortB: 0,
            DescriptorFlag: false,
            EffectOffset: default,
            EffectScalar: effectScalar,
            GeometryKind: geometry,
            WorldPoint: default,
            DurationLink: durationLink,
            HsvShift: default,
            AuxiliaryEffectFieldA: 0,
            AuxiliaryEffectFieldB: 0,
            Presentation: null);
}
