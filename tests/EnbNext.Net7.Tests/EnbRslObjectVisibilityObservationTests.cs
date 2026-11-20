using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRslObjectVisibilityObservationTests
{
    [Fact]
    public void LegoQueriesCapturePacketPrefixesAndLaterNotificationsAtTheSamePrefix()
    {
        var (runtime,obj,rsl)=Fixture(second:true,animation:true);
        EnbNext.Net7.World.Net7LegoControllerMutation Change(ulong sequence,int boundary)=>
            new(sequence,EnbNext.Net7.World.Net7LegoControllerMutationKind.Create,"aux",0,sequence,"A",null,1,48)
                {RenderStateActionCount=boundary};
        obj=obj with {RenderStateActions=[new(0x30,1,10),new(0x30,2,11),new(0x32,2,12),new(0x30,0,13)],
            LegoControllerMutations=[Change(1,0),Change(2,1),Change(3,2),Change(4,3),Change(5,4)]};
        var update=runtime.Update(obj,rsl,[],20,null,advanceCallbacks:false);
        var queries=update.LegoControllerQueryUpdates;
        for(int packet=0;packet<4;packet++)
        {
            int begin=queries[(ulong)packet+1].Actions.Count,end=queries[(ulong)packet+2].Actions.Count;
            Assert.NotEqual(begin,end);
            foreach(int action in Enumerable.Range(begin,end-begin))
                Assert.Equal(packet,update.EffectiveActionPacketOrdinals[action]);
        }
        Assert.All(queries[1].Actions.Select((_,i)=>i),i=>Assert.False(update.EffectiveActionPacketOrdinals.ContainsKey(i)));
        Assert.Equal(new int?[]{0,1,1,1,0},Enumerable.Range(1,5).Select(i=>queries[(ulong)i].CurrentObjectStateId));
        Assert.Contains(2,queries[3].ActiveListStateIds);
        Assert.DoesNotContain(2,queries[4].ActiveListStateIds);
        Assert.True(queries[3].Actions.Count<queries[4].Actions.Count);
        Assert.Empty(queries[3].LegoControllerQueryUpdates);
        var controlled=runtime.Update(obj,rsl,[],21,new EnbCurrentRetailRslDirectStateSelection(
            1,EnbCurrentRetailRslDirectStateSelectionKind.HarvestablePercentFull,true,null),advanceCallbacks:false);
        Assert.Equal(1,controlled.CurrentObjectStateId);
        obj=obj with {LegoControllerMutations=[..obj.LegoControllerMutations,Change(6,4)]};
        var later=runtime.Update(obj,rsl,[],22,null,advanceCallbacks:false);
        Assert.Equal(1,later.LegoControllerQueryUpdates[6].CurrentObjectStateId);
        Assert.Same(queries[5],later.LegoControllerQueryUpdates[5]);
        Assert.Equal(0,queries[5].CurrentObjectStateId);
    }

    [Fact]
    public void ObjectSwitchObservationsPreserveUnhideDetachAttachThenOldHideOrder()
    {
        var (runtime,obj,rsl)=Fixture(true);
        var initial=runtime.Update(obj,rsl,[],0);
        Assert.Equal(initial.ObjectSceneEvents.Length,initial.InitialObjectSceneEventCount);
        Assert.All(initial.ObjectSceneEvents,e=>Assert.Equal(EnbCurrentRetailRslObjectSceneEventPhase.Initialization,e.Phase));
        obj=obj with {RenderStateActions=[new(0x30,1,10),new(0x30,0,20)]};
        var update=runtime.Update(obj,rsl,[],20);
        var events=update.ObjectSceneEvents.Skip(initial.ObjectSceneEvents.Length).ToArray();
        Assert.Equal(6,events.Length);
        Assert.Equal(new int?[]{0,0,0,1,1,1},events.Select(e=>e.PacketActionOrdinal));
        Assert.All(events,e=>Assert.Equal(EnbCurrentRetailRslObjectSceneEventPhase.Packet,e.Phase));
        Assert.Equal(new uint[]{10,10,10,20,20,20},events.Select(e=>e.ClientTick));
        Assert.Equal(new[]{1,1,0,0,0,1},events.Select(e=>e.StateId));
        Assert.Equal(new[]{EnbCurrentRetailRslObjectSceneEventKind.Hidden,EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent,
            EnbCurrentRetailRslObjectSceneEventKind.Hidden,EnbCurrentRetailRslObjectSceneEventKind.Hidden,
            EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent,EnbCurrentRetailRslObjectSceneEventKind.Hidden},events.Select(e=>e.Kind));
        Assert.Equal(0,events[1].PreviousStateId);Assert.Equal(1,events[4].PreviousStateId);
        Assert.False(events[0].Hidden);Assert.True(events[2].Hidden);
        Assert.False(events[3].Hidden);Assert.True(events[5].Hidden);
        Assert.Equal(initial.ObjectSceneEvents.Length,initial.InitialObjectSceneEventCount);
        Assert.Equal(update.ObjectSceneEvents.ToArray(),runtime.Update(obj,rsl,[],30).ObjectSceneEvents.ToArray());
    }

    [Fact]
    public void FirstFramePacketSwitchesRemainDistinctFromFactorySelection()
    {
        var (runtime,obj,rsl)=Fixture(true);
        obj=obj with {RenderStateActions=[new(0x30,1,10),new(0x30,0,10)]};
        var update=runtime.Update(obj,rsl,[],10,null,advanceCallbacks:false);
        var switches=update.ObjectSceneEvents.Where(e=>e.Kind==EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent).ToArray();
        Assert.Equal(new[]{0,1,0},switches.Select(e=>e.StateId));
        Assert.Equal(new int?[]{null,0,1},switches.Select(e=>e.PacketActionOrdinal));
        Assert.Equal(0,update.FactoryObjectStateId);
    }

    [Fact]
    public void SetterSnapshotsKeepAnimationStateAndActionPrefixAtTheActualEvent()
    {
        var (runtime,obj,rsl)=Fixture(animation:true);
        var initial=runtime.Update(obj,rsl,[],0);
        obj=obj with {RenderStateActions=[new(0x30,2,10),new(0x32,0,11),new(0x32,2,12),new(0x30,0,13)]};
        var update=runtime.Update(obj,rsl,[],20);
        var events=update.ObjectSceneEvents.Skip(initial.ObjectSceneEvents.Length).ToArray();
        Assert.Equal(2,events.Length);
        Assert.Equal(new[]{0,2},events[0].ActiveListStateIds.ToArray());
        Assert.Contains(2,events[0].ActiveStateIds);
        Assert.True((events[0].AnimationTransitionFlags[2]&0x10)!=0);
        Assert.DoesNotContain(2,events[1].ActiveStateIds);
        Assert.True((events[1].AnimationTransitionFlags[2]&0x10)==0);
        Assert.True(events[0].EffectiveActionCount<events[1].EffectiveActionCount);
        Assert.Same(update.ResourceGraph,events[0].ResourceGraph);
        var controller=runtime.Update(obj,rsl,[],30,new EnbCurrentRetailRslDirectStateSelection(
            0,EnbCurrentRetailRslDirectStateSelectionKind.HarvestablePercentFull,true,null));
        Assert.Equal(EnbCurrentRetailRslObjectSceneEventPhase.Controller,controller.ObjectSceneEvents[^1].Phase);
        Assert.Equal(30u,controller.ObjectSceneEvents[^1].ClientTick);
        Assert.Null(controller.ObjectSceneEvents[^1].PacketActionOrdinal);
        Assert.True((events[0].AnimationTransitionFlags[2]&0x10)!=0);
    }

    [Fact]
    public void RepeatedHiddenSetterIsObservedEvenWhenEffectiveFlagDoesNotChange()
    {
        var (runtime,obj,rsl)=Fixture();
        var initial=runtime.Update(obj,rsl,[],0);
        obj=obj with {RenderStateActions=[new(0x32,0,10),new(0x32,0,20)]};
        var update=runtime.Update(obj,rsl,[],20);
        var events=update.ObjectSceneEvents.Skip(initial.ObjectSceneEvents.Length).ToArray();
        Assert.Equal(2,events.Length);
        Assert.All(events,e=>{Assert.Equal(EnbCurrentRetailRslObjectSceneEventKind.Hidden,e.Kind);Assert.True(e.Hidden);});
        Assert.Equal(initial.ObjectVisibilityTransitionCounts.GetValueOrDefault(0)+1,update.ObjectVisibilityTransitionCounts[0]);
    }
    [Fact]
    public void HiddenAndVisibleEdgesSurviveOneUpdateAndPriorSnapshotsRemainImmutable()
    {
        var (runtime,obj,rsl)=Fixture();
        var initial=runtime.Update(obj,rsl,[],0);
        ulong before=initial.ObjectVisibilityTransitionCounts.GetValueOrDefault(0);
        Assert.False(initial.CurrentObjectHidden);
        obj=obj with {RenderStateActions=[new(0x32,0,10),new(0x30,0,20)]};
        var cycled=runtime.Update(obj,rsl,[],20);
        Assert.False(cycled.CurrentObjectHidden);
        Assert.DoesNotContain(0,cycled.HiddenObjectStateIds);
        Assert.Equal(before+2,cycled.ObjectVisibilityTransitionCounts[0]);
        Assert.Equal(before,initial.ObjectVisibilityTransitionCounts.GetValueOrDefault(0));
        Assert.DoesNotContain(0,initial.HiddenObjectStateIds);
        var unchanged=runtime.Update(obj,rsl,[],30);
        Assert.Equal(cycled.ObjectVisibilityTransitionCounts,unchanged.ObjectVisibilityTransitionCounts);
        Assert.Equal(cycled.Actions,unchanged.Actions);
    }

    [Fact]
    public void SameCurrentSelectionUnhidesWithoutReactivationAndRepeatedHideDoesNotAddEdges()
    {
        var (runtime,obj,rsl)=Fixture();
        var initial=runtime.Update(obj,rsl,[],0);
        ulong before=initial.ObjectVisibilityTransitionCounts.GetValueOrDefault(0);
        obj=obj with {RenderStateActions=[new(0x32,0,10),new(0x32,0,20)]};
        var hidden=runtime.Update(obj,rsl,[],20);
        Assert.True(hidden.CurrentObjectHidden);
        Assert.Contains(0,hidden.HiddenObjectStateIds);
        Assert.Equal(before+1,hidden.ObjectVisibilityTransitionCounts[0]);
        var visible=runtime.Update(obj,rsl,[],30,new EnbCurrentRetailRslDirectStateSelection(
            0,EnbCurrentRetailRslDirectStateSelectionKind.HarvestablePercentFull,true,null));
        Assert.False(visible.CurrentObjectHidden);
        Assert.Equal(before+2,visible.ObjectVisibilityTransitionCounts[0]);
        Assert.Equal(hidden.ActivationVirtualActionOrdinals,visible.ActivationVirtualActionOrdinals);
        Assert.Contains(0,hidden.HiddenObjectStateIds);
        Assert.Equal(before+1,hidden.ObjectVisibilityTransitionCounts[0]);
        var repeated=runtime.Update(obj,rsl,[],40,new EnbCurrentRetailRslDirectStateSelection(
            0,EnbCurrentRetailRslDirectStateSelectionKind.HarvestablePercentFull,true,null));
        Assert.Equal(visible.ObjectVisibilityTransitionCounts,repeated.ObjectVisibilityTransitionCounts);
    }

    private static (EnbCurrentRetailEffectRslObjectRuntime Runtime,EnbSceneObjectSnapshot Object,EnbResolvedRslReference Rsl) Fixture(bool second=false,bool animation=false)
    {
        var catalog=EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\nID=0\nTYPE=object\nOBJECT=BODY\n"+
            (second?"[0\\STATE1]\nID=1\nTYPE=object\nOBJECT=OTHER\n":"")+
            (animation?$"[0\\STATE{(second?2:1)}]\nID=2\nTYPE=anim\nANIM=BODY.A\n":""));
        Assert.True(catalog.TryGetLibrary(0,out var library));
        var graph=new EnbRetailRslResourceResolver(resources:null).Resolve(library);
        var rsl=new EnbResolvedRslReference(0,null,0,library,
            EnbRslRenderStateSemantics.BuildInitialCommands(null,0),graph);
        var obj=new EnbSceneObjectSnapshot(
            RenderIdentity:1,VisualGeneration:1,GameId:500,IsLocalShip:false,IsCurrentTarget:false,
            ObjectType:1,BaseAsset:48,Scale:1,HsvH:0,HsvS:0,HsvV:0,RelationshipRaw:0,IsAttacking:false,
            Name:"visibility",Radar:new(false,0,null),Spatial:null,BaseAssetReference:null,ClientBaseAssetReference:null,
            Decals:[],ColorizationEntries:[],RenderStateLinks:[],ActiveRenderStates:[],RenderStateActions:[],
            Attachments:[],NameDecal:null,SubParts:[],Avatar:null,SpecialTargetEndpointId:0,SpecialTargetTransition:0);
        return(new(),obj,rsl);
    }
}
