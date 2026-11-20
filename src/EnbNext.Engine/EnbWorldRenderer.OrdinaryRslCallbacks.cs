using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private struct RslInvocationCounts
    {
        public int Failures,Applied,Ignored,Selections,Rejected;
        public void Add(EnbCurrentRetailEffectRslObjectRuntime.RslCallbackCounters c)
        {Failures+=c.FailClosedEvents;Applied+=c.EffectActionsApplied;Ignored+=c.EffectEventsIgnored;
         Selections+=c.DirectStateSelectionsApplied;Rejected+=c.DirectStateSelectionsRejected;}
    }
    private RslInvocationCounts _rslInvocationCounts;
    // This owner never references a LiveFrame or a per-frame closure. Its model
    // and controller binding follow the owning object's actual source lifetime.
    private sealed class OrdinaryRslCallbackOwner
    {
        private readonly EnbWorldRenderer _view;
        private readonly SceneOwner? _owner;
        public IEnbRenderObject Object {get;private set;}=null!;
        private EnbResolvedRslReference _reference=null!;
        private EnbRetailAssetBundle _assets=null!;
        private EnbCurrentRetailEffectRslObjectRuntime.OwnerBinding? _binding;
        private OwnerPublicationMap<EnbRslObjectState>? _compatUpdates;
        private EnbRslObjectState _initial=null!;
        private uint _tick;
        internal readonly Func<int,int,EnbW3dRenderHierarchyAnimation?> Animation;
        internal readonly Func<int,uint,int,bool> SoundCompleted;
        internal readonly Action Advance;
        internal OrdinaryRslCallbackOwner(EnbWorldRenderer view,SceneOwner? owner)
        {_view=view;_owner=owner;Animation=ResolveAnimation;SoundCompleted=HasCompletedSound;Advance=AdvanceCore;}
        internal void Bind(IEnbRenderObject obj,EnbResolvedRslReference reference,EnbRetailAssetBundle assets,uint tick,
            EnbCurrentRetailEffectRslObjectRuntime.OwnerBinding? binding,OwnerPublicationMap<EnbRslObjectState>? compatUpdates)
        {Object=obj;_reference=reference;_assets=assets;_tick=tick;_binding=binding;_compatUpdates=compatUpdates;}
        internal void SetInitial(EnbRslObjectState state)=>_initial=state;
        private EnbW3dRenderHierarchyAnimation? ResolveAnimation(int animation,int body)
            =>_view.ResolveRetainedRslAnimationClip(Object,_reference,_assets,animation,body);
        private bool HasCompletedSound(int id,uint tick,int ordinal)
            =>_view._rslSoundCompletions.TryGetValue(RslSoundKey(Object,id,tick),out var completion)&&completion.PlaybackOrdinal==ordinal;
        internal void Prepare()=>Publish(_initial,false);
        internal void Inactive()=>Publish(_initial,true);
        private void AdvanceCore()
        {
            var prior=_initial.Counters;
            var advanced=_view._effectRslObjectRuntime.Advance(Object,_reference,[],_tick,null,Animation,
                advanceCallbacks:true,soundCompletedAtActivation:SoundCompleted,ownerBinding:_binding);
            var after=advanced.Counters;
            advanced.Counters=after with
            {
                EffectActionsApplied=prior.EffectActionsApplied+after.EffectActionsApplied,
                EffectEventsIgnored=prior.EffectEventsIgnored+after.EffectEventsIgnored,
                DirectStateSelectionsApplied=prior.DirectStateSelectionsApplied+after.DirectStateSelectionsApplied,
                DirectStateSelectionsRejected=prior.DirectStateSelectionsRejected+after.DirectStateSelectionsRejected,
                FailClosedEvents=prior.FailClosedEvents+after.FailClosedEvents,
                Diagnostic=prior.Diagnostic is null?after.Diagnostic:after.Diagnostic is null?prior.Diagnostic:prior.Diagnostic+"; "+after.Diagnostic
            };
            Publish(advanced,true);
        }
        private void Publish(EnbRslObjectState update,bool count)
        {
            if(_owner is not null)_owner.RslUpdate=update;
            else _compatUpdates![Object.RenderIdentity]=update;
            _view.ConsumeRslBoneActions(Object,_reference,_assets,update,_owner);
            if(_owner is not null?_owner.LegoScene is not null:_view._legoScenes.ContainsKey((Object.RenderIdentity,Object.RenderObjectGeneration)))
                _view.SynchronizeLegoScene(Object,update,_assets,attached:true,owner:_owner);
            _view.ReconcileWorldSphereRslVisibility(Object,update,_owner);
            _view.ReconcileWorldRingRslVisibility(Object,update,_owner);
            if(_owner is null)_view._retainedEmitterRslUpdates[(Object.RenderIdentity,Object.RenderObjectGeneration)]=update;
            _view.PublishEffectiveActions(Object,update.Actions,_owner);
            if(count)_view._rslInvocationCounts.Add(update.Counters);
        }
    }
}
