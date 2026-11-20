using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class StationAmbientActor(EnbStationAmbientNpcDefinition definition,uint variant)
    {
        public readonly EnbStationAmbientNpcSchedule Schedule=new(definition,variant);
        public readonly EnbStationAmbientNpcMotion Motion=new(definition.InitialTransform);
        public bool Interacting;
        public EnbW3dRenderResource? Prop;
        public EnbW3dRenderHierarchyAnimation? PropAnimation;
        public string? Diagnostic;
        public readonly EnbCurrentRetailHierarchyAnimationState PropClock=new(){Mode=2,HasHierarchy=true,HasAnimation=true,Playback=2};
        public EnbW3dAnimationSample? Sample(uint now)
        {
            if(PropAnimation is null)return null;
            PropClock.RefreshSingle(()=>now,()=>PropAnimation.FrameRate,()=>checked((int)PropAnimation.NumFrames),_=>{},()=>0,()=>0,(_,_,_)=>0);
            return new(PropAnimation.Name,PropClock.CurrentFrame);
        }
        public void ClearProp(){Prop=null;PropAnimation=null;}
    }
    // 0069D030 retains each room controller; ambient motion/idle owners survive
    // switching rooms. GPU buffers are still released by the normal people path.
    private readonly Dictionary<(uint Room,uint Slot),StationPerson> _stationAmbientPeople=[];
    private uint? _stationPeoplePreviousClock;
    private Matrix4x4 AdvanceStationAmbient(StationPerson person,uint slot,uint delta,long elapsed)
    {
        var actor=person.Ambient!;var speech=person.Speech!;
        bool interacting=_stationConversationOwner?.Slot==slot;
        if(interacting!=actor.Interacting)
        {
            actor.ClearProp();actor.Interacting=interacting;
            if(interacting)
            {
                actor.Motion.BeginInteraction(StationConversationNpcTransform(slot,actor.Motion.Transform,person));
                speech.SetLocomotion(null);
            }
            else
            {
                actor.Motion.EndInteraction();person.InteractionTransform=null;speech.ClearPendingCommands();
            }
        }
        bool idle=!speech.Active;
        if(idle && !interacting)actor.ClearProp();
        if(actor.Schedule.Advance(delta,idle,interacting,speech.RandomInclusive) is {} decision)
        {
            actor.Motion.Append(decision.Route,decision.TrimHistory);
            if(decision.Action is {} action)
            {
                speech.PlayAmbientAction(action.Number,elapsed*.001);
                if(action.ExtraName.Length>0 && action.ExtraAnimation.Length>0)
                    BindStationAmbientProp(actor,action);
            }
        }
        actor.Motion.Advance(delta*.001f,!interacting);
        if(actor.Motion.RequestIdle)speech.SetLocomotion(null);
        if(actor.Motion.RequestWalk)speech.SetLocomotion(actor.Motion.Backward?"walk_backward":"walk_forward");
        // Global frame delta, not the time spent in another room, drives actors.
        speech.Pause(elapsed*.001-delta*.001);
        return actor.Motion.Transform;
    }
    private void BindStationAmbientProp(StationAmbientActor actor,EnbStationAmbientNpcAction action)
    {
        actor.ClearProp();actor.Diagnostic=null;
        try
        {
            var assets=_retailAssetsAccessor()!;
            actor.Prop=StationFixtureAsset(action.ExtraName);
            if(EnbCurrentRetailRslHAnimResolver.TryResolve(actor.Prop,new("",EnbRslFileDependencyStatus.NotAuthored),
                assets.Paths,assets.MixResources,assets.RetailMixResources!,action.ExtraAnimation,out var resolved,out var error))
            {
                actor.Prop=resolved.DrawResource;actor.PropAnimation=resolved.Animation;
                actor.PropClock.CurrentFrame=0;actor.PropClock.PreviousTime=ReadMapperClock();actor.PropClock.Playback=2;
            }
            else actor.Diagnostic=error;
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {actor.Diagnostic=ex.Message;}
    }
}
