using System.Collections.Frozen;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Persistent per-object RSL state layered over the immutable packet snapshot.
/// Server render-state actions are consumed once, then RenderStateEffect start
/// events and scene callbacks run in retail order. BeginFrame supplies retail's
/// shared current/previous clock pair, including newly constructed states.
/// </summary>
public sealed partial class EnbCurrentRetailEffectRslObjectRuntime
{
    private readonly Dictionary<ulong, ObjectState> _objects = [];
    private readonly HashSet<ulong> _unownedObjects=[];
    private ulong _epoch;
    public long OwnerStateLookups {get;private set;}
    public long CompatibilitySweepVisits {get;private set;}
    public sealed class OwnerBinding
    {
        internal OwnerBinding(EnbCurrentRetailEffectRslObjectRuntime runtime,ulong identity,Action? changed,Action? sceneChanged)
        {Runtime=runtime;Identity=identity;Changed=changed;SceneChanged=sceneChanged;}
        internal readonly EnbCurrentRetailEffectRslObjectRuntime Runtime;
        internal readonly ulong Identity;
        internal object? State;
        internal ulong Epoch;
        internal bool Released;
        internal Action? Changed,SceneChanged;
    }
    public OwnerBinding BindOwner(ulong identity,Action? structureChanged=null,Action? sceneChanged=null)=>new(this,identity,structureChanged,sceneChanged);
    public void ReleaseOwner(OwnerBinding binding)
    {
        if(!ReferenceEquals(binding.Runtime,this))throw new ArgumentException("RSL owner belongs to another runtime.",nameof(binding));
        if(binding.State is ObjectState state)
        {
            state.AnimationBinder=null;
            if(_objects.TryGetValue(binding.Identity,out var current)&&ReferenceEquals(state,current))
            {_objects.Remove(binding.Identity);_unownedObjects.Remove(binding.Identity);}
        }
        binding.State=null;binding.Released=true;binding.Changed=null;binding.SceneChanged=null;
    }
    private uint? _frameTick;
    private uint _previousFrameTick;

    /// <summary>00918200 publishes one current/previous clock pair for every
    /// scene callback. Construction and detached owners do not own that clock.
    /// Callers without a scene clock retain the standalone Update interval.</summary>
    public void BeginFrame(uint clientTick)
    {
        _previousFrameTick = _frameTick ?? clientTick;
        _frameTick = clientTick;
    }

    public int TrackedObjectCount => _objects.Count;

    public void Clear() {foreach(var state in _objects.Values)state.AnimationBinder=null;_objects.Clear();_unownedObjects.Clear();_epoch++;}
    /// <summary>Retains the concrete HAnim loaded by00459D50, independently of
    ///enabled membership.00459D20 uses this retained clip's rate on NEXT.</summary>
    public void RecordBoundAnimation(IEnbRenderObject obj, int stateId,
        EnbW3dRenderHierarchyAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (_objects.TryGetValue(obj.RenderIdentity, out var state) && state.VisualGeneration == obj.RenderObjectGeneration)
        {
            if (!state.BoundAnimations.ContainsKey(stateId)) state.AnimationFramePublication = null;
            state.BoundAnimations[stateId] = animation;
            state.BoundAnimationObjectStates[stateId] = state.CurrentObjectStateId;
        }
    }

    public void Prune(IReadOnlySet<ulong> liveRenderIdentities)
    {
        ArgumentNullException.ThrowIfNull(liveRenderIdentities);
        foreach (ulong key in _unownedObjects.ToArray())
        {
            CompatibilitySweepVisits++;
            if(!liveRenderIdentities.Contains(key)){if(_objects.Remove(key,out var retired))retired.AnimationBinder=null;_unownedObjects.Remove(key);}
        }
    }

    public EnbCurrentRetailEffectRslObjectUpdate Update(
        IEnbRenderObject obj,
        EnbResolvedRslReference rsl,
        IReadOnlyList<EnbCurrentRetailRenderStateEffectEvent> effectEvents,
        uint currentClientTickMilliseconds)
        => Update(obj, rsl, effectEvents, currentClientTickMilliseconds, directStateSelection: null);

    public EnbCurrentRetailEffectRslObjectUpdate Update(
        IEnbRenderObject obj,
        EnbResolvedRslReference rsl,
        IReadOnlyList<EnbCurrentRetailRenderStateEffectEvent> effectEvents,
        uint currentClientTickMilliseconds,
        EnbCurrentRetailRslDirectStateSelection? directStateSelection,
        Func<int, int, EnbW3dRenderHierarchyAnimation?>? animationBinder = null,
        bool advanceCallbacks = true,
        Func<int,uint,bool>? soundCompleted = null,
        Func<int,uint,int,bool>? soundCompletedAtActivation = null,
        OwnerBinding? ownerBinding = null)
        => Advance(obj,rsl,effectEvents,currentClientTickMilliseconds,directStateSelection,animationBinder,advanceCallbacks,soundCompleted,soundCompletedAtActivation,ownerBinding).CaptureDiagnostics();

    public ObjectState Advance(
        IEnbRenderObject obj,
        EnbResolvedRslReference rsl,
        IReadOnlyList<EnbCurrentRetailRenderStateEffectEvent> effectEvents,
        uint currentClientTickMilliseconds,
        EnbCurrentRetailRslDirectStateSelection? directStateSelection,
        Func<int, int, EnbW3dRenderHierarchyAnimation?>? animationBinder = null,
        bool advanceCallbacks = true,
        Func<int,uint,bool>? soundCompleted = null,
        Func<int,uint,int,bool>? soundCompletedAtActivation = null,
        OwnerBinding? ownerBinding = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(rsl);
        ArgumentNullException.ThrowIfNull(effectEvents);

        EnbResolvedRslLibraryResourceGraph? graph = rsl.ResourceGraph;
        if (graph is null)
        {
            return new ObjectState(obj.RenderObjectGeneration,0,currentClientTickMilliseconds,null,null!)
            {Counters=new(0,0,0,0,0,0,0,1,"RSL resource graph is unavailable.")};
        }

        //002F is an ordered class initializer, never a state-ID alias map.
        string? initializationDiagnostic = null;
        ObjectState? state=null;
        if(ownerBinding is not null)
        {
            if(!ReferenceEquals(ownerBinding.Runtime,this)||ownerBinding.Identity!=obj.RenderIdentity||ownerBinding.Released)
                throw new ArgumentException("Invalid RSL owner binding.",nameof(ownerBinding));
            if(ownerBinding.Epoch==_epoch)state=ownerBinding.State as ObjectState;
        }
        if(state is null){OwnerStateLookups++;_objects.TryGetValue(obj.RenderIdentity,out state);}
        if (state is null ||
            state.VisualGeneration != obj.RenderObjectGeneration ||
            obj.RenderStateActions.Length < state.PacketActionCount)
        {
            state = CreateState(obj, rsl, graph, currentClientTickMilliseconds, out initializationDiagnostic, animationBinder);
            _objects[obj.RenderIdentity] = state;
            if(ownerBinding is null)_unownedObjects.Add(obj.RenderIdentity);
        }
        else
        {
            graph=state.Graph;
            state.AnimationBinder = animationBinder;
            ConsumeNewPacketActions(state, obj, graph, out initializationDiagnostic);
        }
        if(ownerBinding is not null)
        {
            if(!ReferenceEquals(ownerBinding.State,state)||ownerBinding.Epoch!=_epoch)
            {_unownedObjects.Remove(obj.RenderIdentity);state.BindOwner(ownerBinding);ownerBinding.Changed?.Invoke();}
            ownerBinding.State=state;ownerBinding.Epoch=_epoch;
        }

        graph=state.Graph;
        int directSelectionsApplied = 0;
        int directSelectionsRejected = 0;
        int eventActions = 0;
        int ignoredEvents = 0;
        int failClosedEvents = 0;
        using var scratch=state.CallbackScratch.Rent();
        var diagnostics=scratch.Diagnostics;
        if (initializationDiagnostic is not null)
        {
            failClosedEvents++;
            diagnostics.Add(initializationDiagnostic);
        }

        if (directStateSelection is EnbCurrentRetailRslDirectStateSelection selection &&
            state.LastDirectStateSelection != selection.StateId)
        {
            // Current retail's ClientGameObject caches the selected integer at
            // +0x148 before calling the generic RSL selector.  A missing or
            // otherwise blocked state therefore is not retried every frame;
            // PercentFull must cross into another bucket before another select.
            state.LastDirectStateSelection = selection.StateId;

            if (!selection.IsResolvable)
            {
                directSelectionsRejected++;
                failClosedEvents++;
                diagnostics.Add(selection.Diagnostic ?? $"Direct RSL state selection {selection.StateId} is unresolved.");
            }
            else if (state.CurrentObjectStateId == selection.StateId)
            {
                //004604D0 unhides the same current Object without enabling it
                //or replaying LINK. Keep the retained Bone graph in sync too.
                if (SetObjectHidden(state,selection.StateId,false,currentClientTickMilliseconds,true))
                    state.Actions.Add(new(0x0030, selection.StateId,
                        currentClientTickMilliseconds, EffectAuthored: false));
                directSelectionsApplied++;
            }
            else if (graph.TryFindState(selection.StateId,out var selectedState) && IsStateEnabled(state,selectedState))
            {
                //Another enabled state returns before calling its getter.
                directSelectionsApplied++;
            }
            else
            {
                int directActions = 0;
                if (ActivateChain(
                        state,
                        graph,
                        selection.StateId,
                        currentClientTickMilliseconds,
                        ref directActions,
                        out string? directDiagnostic))
                {
                    directSelectionsApplied++;
                }
                else
                {
                    directSelectionsRejected++;
                    failClosedEvents++;
                    diagnostics.Add(directDiagnostic ?? $"Direct RSL state selection {selection.StateId} failed closed.");
                }
            }
        }
        var orderedEvents=scratch.OrderEvents(effectEvents);
        for(int eventIndex=0;eventIndex<orderedEvents.Count;eventIndex++)
        {
            var effectEvent=orderedEvents[eventIndex];
            if (effectEvent.BoundGameId != obj.GameId ||
                effectEvent.BoundRenderIdentity is ulong identity && identity != obj.RenderIdentity ||
                effectEvent.BoundCreationOrdinal is ulong ordinal && ordinal != obj.WorldCreationOrdinal)
                continue;

            if (!ApplyEffectEvent(
                    state,
                    graph,
                    effectEvent,
                    ref eventActions,
                    out string? eventDiagnostic))
            {
                failClosedEvents++;
                if (!string.IsNullOrWhiteSpace(eventDiagnostic))
                    diagnostics.Add(eventDiagnostic);
            }
            else if (eventDiagnostic is not null)
            {
                ignoredEvents++;
                diagnostics.Add(eventDiagnostic);
            }
        }

        bool currentChildVisible = state.CurrentObjectStateId is int currentChild &&
            !state.HiddenObjectStateIds.Contains(currentChild);
        uint previousCallbackTick = _frameTick == currentClientTickMilliseconds
            ? _previousFrameTick : state.LastClientTick;
        float objectDeltaSeconds = (float)((double)unchecked(currentClientTickMilliseconds - previousCallbackTick) *
            BitConverter.Int32BitsToSingle(0x3a83126f));
        if (currentChildVisible && advanceCallbacks)
        {
            var visitor = new ActiveCallback(this,state,graph,currentClientTickMilliseconds,
                objectDeltaSeconds,soundCompleted,soundCompletedAtActivation,diagnostics,failClosedEvents);
            state.ActiveList.Visit(ref visitor);
            failClosedEvents=visitor.FailClosedEvents;
        }
        //0045A9F0 gathers the remaining active states after callbacks. A state
        //whose update was skipped retains its previous +38 contribution.
        var activeOrder = state.ActiveList.Snapshot();
        var activeEnvelopes = state.GetActiveEnvelopes(activeOrder);
        foreach (var entry in activeEnvelopes)
        {
            // Keep the callback clock current even when its update was skipped.
            entry.Envelope.LastUpdateTick = currentClientTickMilliseconds;
        }
        state.LastClientTick = currentClientTickMilliseconds;
        string? diagnostic = diagnostics.Count == 0 ? null : string.Join(" | ", diagnostics.Distinct(StringComparer.Ordinal));
        state.Counters = new RslCallbackCounters(
                ActiveStates: state.ActiveStateIds.Count,
                EffectiveActions: state.Actions.Count,
                EffectActionsApplied: eventActions,
                EffectEventsIgnored: ignoredEvents,
                AnimationWeights: activeEnvelopes.Length,
                DirectStateSelectionsApplied: directSelectionsApplied,
                DirectStateSelectionsRejected: directSelectionsRejected,
                FailClosedEvents: failClosedEvents,
                Diagnostic: diagnostic);
        return state;
    }

    private void AdvanceActiveState(int stateId,ObjectState state,EnbResolvedRslLibraryResourceGraph graph,
        uint currentClientTickMilliseconds,float objectDeltaSeconds,Func<int,uint,bool>? soundCompleted,
        Func<int,uint,int,bool>? soundCompletedAtActivation,List<string> diagnostics,ref int failClosedEvents)
    {
            if (!graph.TryFindState(stateId, out EnbResolvedRslStateResource activeState)) return;
            if(activeState.Definition.Type==EnbRslStateType.Sound)
            {
                int ordinal=-1;
                for(int i=state.Actions.Count-1;i>=0;i--)
                    if(state.Actions[i].StateId==stateId && state.Actions[i].Opcode==0x0030 && state.ActivationVirtualActionOrdinals.Contains(i))
                    {ordinal=i;break;}
                uint activation=ordinal>=0?state.Actions[ordinal].ClientTime:0;
                bool finished=soundCompletedAtActivation?.Invoke(stateId,activation,ordinal+1) ?? soundCompleted?.Invoke(stateId,activation) ?? false;
                if(state.CompletedSounds.Contains(stateId) || !finished)return;
                uint soundFlags=state.SoundFlags[stateId];
                //0045FC50: stopped ordinary sound unlinks directly. Flag8
                //instead completes through4584B0 with LINK (never NEXT),
                //sets DELETE|STOP and finally releases its retained handle.
                if((soundFlags&8)==0){DeactivateOne(state,stateId,currentClientTickMilliseconds,true,preserveSoundHandle:true);return;}
                if(!TryResolveLinkChain(graph,stateId,out var soundChain,out var soundFailure))
                {failClosedEvents++;diagnostics.Add(soundFailure!);return;}
                state.SoundFlags[stateId]=soundFlags|0x85;
                foreach(int id in soundChain)DeactivateOne(state,id,currentClientTickMilliseconds,true);
                state.OwnerDeleteRequested=true;
                if(activeState.Definition.LinkStateId is int nextSound)
                {
                    if(state.AnimationFrames.TryGetValue(nextSound,out var entry))entry.Frame=0;
                    int actions=0;
                    if(!ActivateChain(state,graph,nextSound,currentClientTickMilliseconds,ref actions,out soundFailure))
                    {failClosedEvents++;diagnostics.Add(soundFailure!);}
                }
                state.SoundHandleReleases.Add(new(stateId,state.Actions.Count));
                if (state.CompletedSounds.Add(stateId)) state.CompletedSoundPublication = null;
                return;
            }
            if (activeState.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Bone)
            {
                var lifetime = state.ObjectLifetimes[stateId];
                bool hasNext = activeState.Definition.NextStateId is int nextId && graph.TryFindState(nextId, out _);
                var durationUpdate = EnbCurrentRetailRslObjectLifetimeRuntime.Update(lifetime.Elapsed,
                    activeState.Definition.Duration ?? 0, lifetime.Flags, objectDeltaSeconds, hasNext);
                lifetime.Elapsed = durationUpdate.ElapsedSeconds;
                if (!durationUpdate.TerminalCallback) return;
                int[] deactivation = [];
                string? failure = null;
                if (durationUpdate.DeactivateLinkChain && !TryResolveLinkChain(graph, stateId, out deactivation, out failure))
                {
                    failClosedEvents++; diagnostics.Add(failure!); return;
                }
                int? next = durationUpdate.ActivateNext ? activeState.Definition.NextStateId : null;
                if (next is int targetId)
                {
                    var traversal = EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, targetId);
                    var classPlan = EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal, state.CurrentObjectStateId);
                    if (traversal.Termination is not (EnbCurrentRetailRslLinkActivationTermination.NoFurtherLink or
                        EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted) ||
                        !classPlan.IsExecutableWithKnownCurrentObject ||
                        graph.TryFindState(targetId, out var target) && target.Definition.Type == EnbRslStateType.Animation &&
                            state.AnimationBinder is null && state.PreviouslyBoundAnimations.Contains(targetId) &&
                            !state.BoundAnimations.ContainsKey(targetId))
                    {
                        failClosedEvents++;
                        diagnostics.Add($"Object/Bone state {stateId} NEXT preactivation remains unresolved for target {targetId}.");
                        return;
                    }
                }
                lifetime.Flags = durationUpdate.Flags;
                foreach (int id in deactivation)
                {
                    DeactivateOne(state, id, currentClientTickMilliseconds, effectAuthored: true);
                }
                //004584DE only writes this retained owner bit. Retirement needs
                //a proven consumer; do not manufacture one from the flag name.
                state.OwnerDeleteRequested |= durationUpdate.DeleteOwner;
                if (next is int activateNext)
                {
                    bool nextAnimation = graph.TryFindState(activateNext, out var nextState) &&
                        nextState.Definition.Type == EnbRslStateType.Animation;
                    float entryFrame = state.BoundAnimations.TryGetValue(activateNext, out var boundClip)
                        ? (float)((float)boundClip.PlaybackFrameRate * (double)durationUpdate.LeftoverSeconds) : 0;
                    int actions = 0;
                    if (!ActivateChain(state, graph, activateNext, currentClientTickMilliseconds, ref actions, out failure))
                    { failClosedEvents++; diagnostics.Add(failure!); }
                    else if (nextAnimation)
                    {
                        state.SetAnimationEntry(activateNext, new(entryFrame, currentClientTickMilliseconds));
                        var nextFrame = state.AnimationFrames[activateNext];
                        nextFrame.PreviousFrame = nextFrame.Frame;
                        nextFrame.Frame = entryFrame;
                    }
                }
                return;
            }
            if (activeState.Definition.Type != EnbRslStateType.Animation) return;

            BindAnimation(state, stateId);
            //00459B1B returns before envelope/frame callbacks for a null clip.
            if (state.AnimationBinder is not null && !state.BoundAnimations.ContainsKey(stateId)) return;

            state.AnimationFrames.TryGetValue(stateId, out var frameState);
            bool clipLoaded = state.BoundAnimations.TryGetValue(stateId, out var frameClip);
            float oldFrame = frameState?.Frame ?? 0; //captured before fade00459B23

            if (!state.AnimationEnvelopes.TryGetValue(stateId, out AnimationEnvelopeState? envelopeState) || envelopeState is null)
            {
                envelopeState = new AnimationEnvelopeState(state,
                    EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation,
                    currentClientTickMilliseconds);
                state.AnimationEnvelopes[stateId] = envelopeState;
                state.InvalidateEnvelopeMembership();
            }

            EnbCurrentRetailRslAnimationWeightUpdate update =
                EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(
                    envelopeState.Envelope,
                    currentClientTickMilliseconds,
                    _frameTick == currentClientTickMilliseconds ? _previousFrameTick : envelopeState.LastUpdateTick);
            envelopeState.Envelope = update.EnvelopeAfterUpdate;
            envelopeState.LastUpdateTick = currentClientTickMilliseconds;
            envelopeState.ContributionWeight = update.ContributionWeight;

            if (update.DeactivatedBeforeAggregateGather)
            {
                if (TryResolveLinkChain(graph, stateId, out int[] chain, out string? chainDiagnostic))
                {
                    foreach (int linkedStateId in chain)
                        DeactivateOne(state, linkedStateId, currentClientTickMilliseconds, effectAuthored: true);
                }
                else
                {
                    failClosedEvents++;
                    diagnostics.Add(chainDiagnostic ?? $"Animation state {stateId} Linger deactivation LINK chain is unresolved.");
                }
            }
            if (!clipLoaded || frameState is null) return;
            var frameAdvance = EnbCurrentRetailRslAnimationFrameRuntime.Advance(oldFrame,
                frameClip!.PlaybackFrameRate, frameClip.NumFrames, objectDeltaSeconds);
            if (frameAdvance.TerminalCallback)
            {
                int[] terminalDeactivation = [];
                if ((frameState.Flags & 0x2a) != 0 &&
                    !TryResolveLinkChain(graph, stateId, out terminalDeactivation, out var failure))
                { failClosedEvents++; diagnostics.Add(failure!); return; }
                int? nextId = (frameState.Flags & 0x28) != 0 ? activeState.Definition.NextStateId : null;
                if (nextId is int targetId)
                {
                    var traversal = EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, targetId);
                    var plan = EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal, state.CurrentObjectStateId);
                    if (traversal.Termination is not (EnbCurrentRetailRslLinkActivationTermination.NoFurtherLink or
                        EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted) || !plan.IsExecutableWithKnownCurrentObject)
                    { failClosedEvents++; diagnostics.Add($"Animation {stateId} NEXT activation remains unresolved."); return; }
                }
                if ((frameState.Flags & 0x6c) != 0) frameState.Flags |= 0x80;
                foreach (int id in terminalDeactivation)
                    DeactivateOne(state, id, currentClientTickMilliseconds, effectAuthored: true);
                state.OwnerDeleteRequested |= (frameState.Flags & 1) != 0;
                if ((frameState.Flags & 0x28) != 0)
                {
                    frameState.Flags &= ~0x20u;
                    if (nextId is int next)
                    {
                        if (state.AnimationFrames.TryGetValue(next, out var nextFrame) &&
                            state.BoundAnimations.TryGetValue(next, out var nextClip))
                        {
                            nextFrame.PreviousFrame = nextFrame.Frame;
                            nextFrame.Frame = (float)((double)(float)nextClip.PlaybackFrameRate * frameAdvance.LeftoverSeconds);
                            state.SetAnimationEntry(next, new(nextFrame.Frame, currentClientTickMilliseconds));
                        }
                        int actions = 0;
                        if (!ActivateChain(state, graph, next, currentClientTickMilliseconds, ref actions, out var activationFailure))
                        { failClosedEvents++; diagnostics.Add(activationFailure!); }
                    }
                }
            }
            frameState.PreviousFrame = frameState.Frame;
            frameState.Frame = EnbCurrentRetailRslAnimationFrameRuntime.Publish(frameAdvance, frameState.Flags);
    }

    // Explicit diagnostic boundary. Production traversal calls Advance and reads
    // the owner; only a caller requesting a historical receipt enters this copy.
    private static EnbCurrentRetailEffectRslObjectUpdate CaptureState(ObjectState state)
    {
        if(state.Graph is null)return new([],[],FrozenDictionary<int,float>.Empty,null,state.Counters.CaptureDiagnostics());
        var activeOrder=state.ActiveList.Snapshot();
        var weights=state.SnapshotWeights(state.GetActiveEnvelopes(activeOrder));
        var membership=state.GetRenderMembership(activeOrder);
        return new EnbCurrentRetailEffectRslObjectUpdate(
            membership.Render,
            state.SnapshotActions,
            weights,
            state.CurrentObjectStateId,
            state.Counters.CaptureDiagnostics()) {
                    AnimationEntryFrames = state.SnapshotAnimationEntries,
                    AnimationFrames = state.SnapshotAnimationFrames,
                    AnimationActivationTicks = state.SnapshotAnimationTicks,
                    AnimationTransitionFlags = state.SnapshotAnimationFlags,
                    AnimationPreviousFrames = state.SnapshotPreviousFrames,
                    ActiveListStateIds = activeOrder,
                    RenderStructure = membership.Structure,
                    CompletedSoundStateIds = state.SnapshotCompletedSounds,
                    SoundHandleReleases = state.SnapshotSoundReleases,
                    ActiveEffectPassStateIds = membership.EffectPasses,
                    EffectPassTransitionFlags = state.SnapshotEffectPassFlags,
                    ObjectElapsedSeconds = state.SnapshotObjectElapsed,
                    ObjectTransitionFlags = state.SnapshotObjectFlags,
                    OwnerDeleteRequested = state.OwnerDeleteRequested,
                    ResourceGraph = state.Graph,
                    ActivationVirtualActionOrdinals = state.SnapshotActivationOrdinals,
                    EffectiveActionPacketOrdinals = state.SnapshotPacketOrdinals,
                    HiddenObjectStateIds = state.SnapshotHiddenObjects,
                    ObjectVisibilityTransitionCounts = state.SnapshotVisibilityCounts,
                    ObjectSceneEvents = state.SnapshotSceneEvents,
                    LegoControllerQueryUpdates = state.SnapshotLegoQueries,
                    FactoryLegoQuery = state.FactoryLegoQuery?.CaptureDiagnostics(),
                    InitialObjectSceneEventCount = state.InitialObjectSceneEventCount,
                    FactoryActionCount = state.FactoryActionCount,
                    FactoryObjectStateId = state.FactoryObjectStateId,
                    CurrentObjectHidden = state.CurrentObjectStateId is int current && state.HiddenObjectStateIds.Contains(current) };
    }

    // Preserve every native setter invocation for particle scene replay; the
    // existing edge count still observes only effective mutations for primitives.
    private static bool SetObjectHidden(ObjectState state,int stateId,bool hidden,uint tick,bool effectAuthored)
    {
        ObserveObjectSceneEvent(state,EnbCurrentRetailRslObjectSceneEventKind.Hidden,stateId,hidden,null,tick,effectAuthored);
        bool changed=hidden?state.HiddenObjectStateIds.Add(stateId):state.HiddenObjectStateIds.Remove(stateId);
        if(changed)state.InvalidateRenderMembership();
        if(changed)
        {
            state.ObjectVisibilityTransitionCounts[stateId]=state.ObjectVisibilityTransitionCounts.GetValueOrDefault(stateId)+1;
            state.HiddenObjectPublication = null;
            state.VisibilityCountPublication = null;
        }
        return changed;
    }

    private static void ObserveObjectSceneEvent(ObjectState state,EnbCurrentRetailRslObjectSceneEventKind kind,
        int stateId,bool hidden,int? previousStateId,uint tick,bool effectAuthored)
    {
        var activeOrder=ImmutableArray.CreateBuilder<int>();
        state.ActiveList.Visit(activeOrder.Add);
        state.ObjectSceneEvents.Add(new(state.ObjectSceneEvents.Count,kind,stateId,hidden,previousStateId)
        {
            Phase=state.Initializing?EnbCurrentRetailRslObjectSceneEventPhase.Initialization:
                effectAuthored?EnbCurrentRetailRslObjectSceneEventPhase.Controller:EnbCurrentRetailRslObjectSceneEventPhase.Packet,
            ClientTick=tick,
            PacketActionOrdinal=state.ProcessingPacketActionOrdinal,
            EffectiveActionCount=state.Actions.Count,
            ActiveStateIds=state.ActiveStateIds.ToFrozenSet(),
            ActiveListStateIds=activeOrder.ToImmutable(),
            ResourceGraph=state.Graph,
            AnimationWeights=state.AnimationEnvelopes.ToFrozenDictionary(item=>item.Key,item=>item.Value.ContributionWeight),
            AnimationObjectStateIds=state.BoundAnimationObjectStates.ToFrozenDictionary(),
            AnimationClips=state.BoundAnimations.ToFrozenDictionary(),
            AnimationFrames=state.AnimationFrames.ToFrozenDictionary(item=>item.Key,item=>item.Value.Frame),
            AnimationActivationTicks=state.AnimationFrames.ToFrozenDictionary(item=>item.Key,item=>item.Value.ActivationTick),
            AnimationTransitionFlags=state.AnimationFrames.ToFrozenDictionary(item=>item.Key,item=>item.Value.Flags),
            AnimationPreviousFrames=state.AnimationFrames.ToFrozenDictionary(item=>item.Key,item=>item.Value.PreviousFrame)
        });
        state.NotifySceneChanged();
    }

    private static ObjectState CreateState(
        IEnbRenderObject obj,
        EnbResolvedRslReference rsl,
        EnbResolvedRslLibraryResourceGraph graph,
        uint now,
        out string? initializationDiagnostic,
        Func<int, int, EnbW3dRenderHierarchyAnimation?>? animationBinder)
    {
        initializationDiagnostic = null;
        var state = new ObjectState(
            obj.RenderObjectGeneration,
            obj.RenderStateActions.Length,
            now,
            null,graph);
        state.AnimationBinder = animationBinder;
        foreach (var resource in graph.States.Where(resource => resource.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Bone))
            state.ObjectLifetimes[resource.Definition.StateId] = new DurationState(state)
            { Flags = EnbCurrentRetailRslSoundRuntime.BuildParsedFlagMask(resource.Definition.Flags) & 0x6051f };
        foreach (var resource in graph.States.Where(resource => resource.Definition.Type == EnbRslStateType.Animation))
            state.AnimationFrames[resource.Definition.StateId] = new AnimationFrameState(state)
            { Flags = EnbCurrentRetailRslSoundRuntime.BuildParsedFlagMask(resource.Definition.Flags) & 0x6051f };
        foreach (var resource in graph.States.Where(resource => resource.Definition.Type == EnbRslStateType.EffectPass))
            state.SetEffectPassFlags(resource.Definition.StateId, EnbCurrentRetailRslSoundRuntime.BuildParsedFlagMask(resource.Definition.Flags) & 0x6051f);

        foreach(var resource in graph.States.Where(resource=>resource.Definition.Type==EnbRslStateType.Link))
            state.LinkFlags[resource.Definition.StateId]=EnbCurrentRetailRslSoundRuntime.BuildParsedFlagMask(resource.Definition.Flags)&0x6051f;
        foreach(var resource in graph.States.Where(resource=>resource.Definition.Type==EnbRslStateType.Sound))
            state.SoundFlags[resource.Definition.StateId]=EnbCurrentRetailRslSoundRuntime.BuildParsedFlagMask(resource.Definition.Flags)&0x6051f;

        //0045FFB0 first copies the complete template list (0045C990), then
        //walks the template head at00460091. Factories prepend through
        //00457F60, so STARTS_ENABLED is visited in reverse section order.
        //004600AB..6012D uses generic activation with LINK budget50, before
        //CreateBaseAssetRenderObjectFromConfig applies LINKID/RSID.
        bool hasConstructorStates = graph.States.Any(resource => resource.Definition.Flags.StartsEnabled);
        if (hasConstructorStates)
        {
            state.CurrentObjectStateId = null;
            foreach (var resource in graph.States.OrderByDescending(resource => resource.Definition.SectionOrdinal))
            {
                int id = resource.Definition.StateId;
                if (!resource.Definition.Flags.StartsEnabled || id == -1) continue;
                if (state.CurrentObjectStateId == id)
                {
                    SetObjectHidden(state,id,false,now,false);
                    continue;
                }
                if (IsStateEnabled(state,resource)) continue;
                int constructorActions = 0;
                if (!ActivateChain(state, graph, id, now, ref constructorActions,
                        out initializationDiagnostic, effectAuthored: false)) break;
            }
        }

        foreach (uint active in obj.RenderStateActions.IsDefaultOrEmpty ? obj.ActiveRenderStates : [])
        {
            if (active > int.MaxValue)
                continue;
            int resolved = (int)active;
            if (!graph.TryFindState(resolved, out EnbResolvedRslStateResource activeState))
                continue;
            state.ActiveStateIds.Add(resolved);
            state.ActiveList.Enable(resolved);
            if (state.ObjectLifetimes.TryGetValue(resolved, out var initialLifetime)) initialLifetime.Flags |= 0x10;
            if (state.AnimationFrames.TryGetValue(resolved, out var initialAnimation)) initialAnimation.Flags |= 0x10;
            if (state.EffectPassFlags.TryGetValue(resolved, out uint initialPass)) state.SetEffectPassFlags(resolved, initialPass | 0x10);
            if (state.SoundFlags.TryGetValue(resolved, out uint initialSound)) state.SoundFlags[resolved] = initialSound | 0x10;
            if (activeState.Definition.Type == EnbRslStateType.Object)
            {
                ObserveObjectSceneEvent(state,EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent,resolved,false,state.CurrentObjectStateId,now,false);
                state.CurrentObjectStateId = resolved;
            }
        }

        if (obj.ActiveRenderStates.IsDefaultOrEmpty || !obj.RenderStateActions.IsDefaultOrEmpty)
        {
            foreach (EnbRslRenderStateCommand command in rsl.InitialStateCommands)
            {
                if(command.Kind==EnbRslRenderStateCommandKind.InitializeState &&
                    graph.TryFindState(command.StateId,out var initialized) &&
                    (EnbRslRenderStateSemantics.IsNoOpInitializer(initialized.Definition.Type) || initialized.Definition.Type==EnbRslStateType.Link))
                {
                    if(!InitializeState(state,command.StateId,command.ArgumentStateId,now,out initializationDiagnostic))break;
                    graph=state.Graph;
                    continue;
                }
                if (command.Kind != EnbRslRenderStateCommandKind.ActivateState)
                    continue;
                int resolved = command.StateId;
                if (state.CurrentObjectStateId == resolved ||
                    graph.TryFindState(resolved, out var selected) && IsStateEnabled(state,selected))
                {
                    if (state.CurrentObjectStateId == resolved) SetObjectHidden(state,resolved,false,now,false);
                    continue;
                }
                // Initial RSID selection uses the same source-first LINK
                // activation as later selections (retail 004604D0/00460440).
                // Merely retaining the Object skips authored idle animations,
                // e.g. NB03A's Object 0 -> Animation 1. Run once per lifetime.
                int initialActions = 0;
                if (!ActivateChain(state, graph, resolved, now, ref initialActions,
                        out initializationDiagnostic, effectAuthored: false))
                    break;
            }
        }

        //6F51D0 returns after construction/LINKID/RSID, before packet actions.
        //733883's type-2 LOD setter targets this current Object only.
        state.FactoryActionCount=state.Actions.Count;
        state.FactoryObjectStateId=state.CurrentObjectStateId;
        state.PacketActionCount=0;
        if(!obj.LegoControllerMutations.IsDefaultOrEmpty)state.FactoryLegoQuery=CaptureLegoControllerQuery(state);
        ConsumeNewPacketActions(state,obj,state.Graph,out var packetDiagnostic);
        initializationDiagnostic ??= packetDiagnostic;
        graph=state.Graph;

        foreach (int stateId in state.ActiveStateIds)
        {
            if (graph.TryFindState(stateId, out EnbResolvedRslStateResource active) &&
                active.Definition.Type == EnbRslStateType.Animation &&
                !state.AnimationEnvelopes.ContainsKey(stateId))
            {
                state.AnimationEnvelopes[stateId] = new AnimationEnvelopeState(state,
                    EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation,
                    now);
                state.InvalidateEnvelopeMembership();
                state.PreviouslyBoundAnimations.Add(stateId);
            }
        }

        state.InitialObjectSceneEventCount=state.ObjectSceneEvents.Count;
        state.Initializing=false;
        return state;
    }

    private static bool IsStateEnabled(ObjectState state,EnbResolvedRslStateResource resource)
    {
        int id=resource.Definition.StateId;
        uint flags=state.ObjectLifetimes.TryGetValue(id,out var lifetime)?lifetime.Flags:
            state.AnimationFrames.TryGetValue(id,out var frame)?frame.Flags:
            state.EffectPassFlags.TryGetValue(id,out uint pass)?pass:
            state.LinkFlags.TryGetValue(id,out uint link)?link:
            state.SoundFlags.TryGetValue(id,out uint sound)?sound:
            EnbCurrentRetailRslSoundRuntime.BuildParsedFlagMask(resource.Definition.Flags)&0x6051f;
        return (flags&0x10)!=0 || state.ActiveStateIds.Contains(id);
    }

    private static bool InitializeState(ObjectState state,int stateId,int? argumentStateId,uint now,out string? diagnostic)
    {
        diagnostic=null;
        var graph=state.Graph;
        if(!graph.TryFindState(stateId,out var initialized))return true;
        //00460880 deactivates the source and its cached LINK before
        //the class initializer, then restores the previously enabled source.
        bool wasEnabled=IsStateEnabled(state,initialized);
        int restoreActions=0;
        if(wasEnabled)
        {
            if(state.ActiveStateIds.Contains(stateId))
            {
                if(!DeactivateChain(state,graph,stateId,now,ref restoreActions,out diagnostic,effectAuthored:false))return false;
            }
            else
            {
                //A copied RS_ENABLED bit has no activated LINK cache.
                DeactivateOne(state,stateId,now,effectAuthored:false);
            }
        }
        if(initialized.Definition.Type==EnbRslStateType.Link)
        {
            int? argument=argumentStateId is int value && value!=-1?value:null;
            int? previous=state.LinkArguments.TryGetValue(stateId,out var retained)?retained:initialized.Definition.LinkStateId;
            if(argument!=previous)
            {
                //0045A1D0 writes+1C, clears+20 and00458010 resolves
                //the new cached pointer. A missing ID leaves it null.
                state.LinkArguments[stateId]=argument;
                int? target=argument is int id && graph.TryFindState(id,out _)?id:null;
                graph=state.Graph=graph.WithResolvedLinkTarget(stateId,target);
                //The outer initializer has disabled this Link. Its
                //new enabled target receives only its own+18 callback.
                if(target is int targetId && graph.TryFindState(targetId,out var targetState) && IsStateEnabled(state,targetState))
                {
                    DeactivateOne(state,targetId,now,effectAuthored:false);
                }
            }
        }
        if(wasEnabled && !ActivateChain(state,graph,stateId,now,ref restoreActions,out diagnostic,effectAuthored:false))return false;
        return true;
    }

    private static void ConsumeNewPacketActions(
        ObjectState state,
        IEnbRenderObject obj,
        EnbResolvedRslLibraryResourceGraph graph,
        out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            for (int i = state.PacketActionCount; i < obj.RenderStateActions.Length; i++)
            {
                CaptureLegoControllerQueries(state,obj,i);
                state.ProcessingPacketActionOrdinal=i;
                int firstEffectiveAction=state.Actions.Count;
                try
                {
                EnbSceneRenderStateActionSnapshot action = obj.RenderStateActions[i];
                if(action.Opcode==0x002f)
                {
                    if(action.InitializerArgument is uint argument &&
                        !InitializeState(state,unchecked((int)action.RenderStateId),unchecked((int)argument),action.ClientTime,out var failure))
                        diagnostic=failure;
                    graph=state.Graph;
                    continue;
                }
                if (action.RenderStateId > int.MaxValue)
                    continue;
                int resolved = (int)action.RenderStateId;
                if (action.Opcode == 0x0030)
                {
                    //004604D0 unhides the same current Object without enabling it.
                    //Another already-enabled state returns without touching its getter.
                    if (state.CurrentObjectStateId == resolved || graph.TryFindState(resolved,out var selectedState) && IsStateEnabled(state,selectedState))
                    {
                        if(state.CurrentObjectStateId == resolved)SetObjectHidden(state,resolved,false,action.ClientTime,false);
                        state.Actions.Add(new(action.Opcode,resolved,action.ClientTime,EffectAuthored:false));
                        continue;
                    }
                    int applied = 0;
                    if (!ActivateChain(state,graph,resolved,action.ClientTime,ref applied,out var failure,effectAuthored:false))
                        diagnostic = failure;
                }
                else
                {
                    if(action.Opcode != 0x0032)
                    {
                        if (action.Opcode == 0x0031 && !ApplyNextOnce(state, graph, resolved, out var onceFailure))
                            diagnostic = onceFailure;
                        state.Actions.Add(new(action.Opcode,resolved,action.ClientTime,EffectAuthored:false));continue;
                    }
                    //00737902→00460780 invokes the source then every cached LINK
                    //deactivation virtual, including disabled targets. It has no
                    //activation walker's51-node budget.
                    if(!TryResolveLinkChain(graph,resolved,out var chain,out diagnostic))continue;
                    foreach(int id in chain)
                    {
                        DeactivateOne(state,id,action.ClientTime,effectAuthored:false);
                    }
                }
                }
                finally
                {
                    for(int a=firstEffectiveAction;a<state.Actions.Count;a++)state.EffectiveActionPacketOrdinals[a]=i;
                }
            }
        }
        finally { state.ProcessingPacketActionOrdinal=null; }
        CaptureLegoControllerQueries(state,obj,obj.RenderStateActions.Length);
        state.PacketActionCount = obj.RenderStateActions.Length;
    }

    private static bool ApplyEffectEvent(
        ObjectState state,
        EnbResolvedRslLibraryResourceGraph graph,
        EnbCurrentRetailRenderStateEffectEvent effectEvent,
        ref int appliedActions,
        out string? diagnostic)
    {
        diagnostic = null;
        int targetStateId = effectEvent.RenderStateId;
        if (!graph.TryFindState(targetStateId, out EnbResolvedRslStateResource target))
        {
            diagnostic = $"RenderStateEffect {effectEvent.DefinitionId} targets missing RSL state {targetStateId}.";
            return false;
        }

        bool currentlyActive = IsStateEnabled(state,target);
        switch (effectEvent.ActionKind)
        {
            case EnbRenderStateEffectActionKind.Activate:
                if (currentlyActive && effectEvent.ForceReactivate)
                {
                    if (!DeactivateChain(state, graph, targetStateId, effectEvent.ActualStartClientTick, ref appliedActions, out diagnostic))
                        return false;
                }
                //006F0450 uses the ID selector004604D0, unlike Ramp's
                //inactive-state generic activation00460440. Selecting the
                //current Object only unhides it, even after forced deactivation.
                if(state.CurrentObjectStateId == targetStateId)
                {
                    if(SetObjectHidden(state,targetStateId,false,effectEvent.ActualStartClientTick,true))
                    {
                        state.Actions.Add(new(0x0030,targetStateId,effectEvent.ActualStartClientTick,EffectAuthored:true));
                        appliedActions++;
                    }
                    return true;
                }
                if(IsStateEnabled(state,target))return true;
                return ActivateChain(state, graph, targetStateId, effectEvent.ActualStartClientTick, ref appliedActions, out diagnostic);

            case EnbRenderStateEffectActionKind.Deactivate:
                return DeactivateChain(state, graph, targetStateId, effectEvent.ActualStartClientTick, ref appliedActions, out diagnostic);

            case EnbRenderStateEffectActionKind.Linger:
                if (!currentlyActive)
                {
                    diagnostic = $"RenderStateEffect {effectEvent.DefinitionId} Linger state {targetStateId} is inactive; retail does not activate it through Linger.";
                    return true;
                }
                //Object/Sound +44 resolve to004586D0 (RET4).
                if (target.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Sound or EnbRslStateType.Link or EnbRslStateType.Message or EnbRslStateType.Bone or EnbRslStateType.EffectPass)
                    return true;
                if (target.Definition.Type != EnbRslStateType.Animation)
                {
                    diagnostic = $"RenderStateEffect {effectEvent.DefinitionId} Linger targets {target.Definition.Type} state {targetStateId}; only the statically closed Animation envelope branch is executable.";
                    return false;
                }
                AnimationEnvelopeState linger = GetEnvelope(state, targetStateId, effectEvent.ActualStartClientTick);
                linger.Envelope = EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.BeginLinger(
                    linger.Envelope, effectEvent.ActionTimeMilliseconds);
                linger.LastUpdateTick = effectEvent.ActualStartClientTick;
                return true;

            case EnbRenderStateEffectActionKind.Ramp:
                if (target.Definition.Type is not (EnbRslStateType.Animation or EnbRslStateType.Object or EnbRslStateType.Sound or EnbRslStateType.Link or EnbRslStateType.Message or EnbRslStateType.Bone or EnbRslStateType.EffectPass))
                {
                    diagnostic = $"RenderStateEffect {effectEvent.DefinitionId} Ramp targets {target.Definition.Type} state {targetStateId}; only the statically closed Animation envelope branch is executable.";
                    return false;
                }
                if (currentlyActive && effectEvent.ForceReactivate)
                {
                    if (!DeactivateChain(state, graph, targetStateId, effectEvent.ActualStartClientTick, ref appliedActions, out diagnostic))
                        return false;
                    currentlyActive = false;
                }
                if (!currentlyActive)
                {
                    if (!ActivateChain(state, graph, targetStateId, effectEvent.ActualStartClientTick, ref appliedActions, out diagnostic))
                        return false;
                }
                //004605E0 activates the inactive target before +48;
                //Object/Sound +48 resolve to004586F0 (RET4).
                if (target.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Sound or EnbRslStateType.Link or EnbRslStateType.Message or EnbRslStateType.Bone or EnbRslStateType.EffectPass)
                    return true;
                AnimationEnvelopeState ramp = GetEnvelope(state, targetStateId, effectEvent.ActualStartClientTick);
                ramp.Envelope = EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.BeginRamp(
                    ramp.Envelope, effectEvent.ActionTimeMilliseconds);
                ramp.LastUpdateTick = effectEvent.ActualStartClientTick;
                return true;

            case EnbRenderStateEffectActionKind.ActivateNextStateOnce:
                if (target.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Bone or
                    EnbRslStateType.Link or EnbRslStateType.Message or EnbRslStateType.Sound or EnbRslStateType.EffectPass)
                    return true;
                if (!ApplyNextOnce(state, graph, targetStateId, out diagnostic)) return false;
                state.Actions.Add(new EnbCurrentRetailRslEffectiveAction(
                    0x0031, targetStateId, effectEvent.ActualStartClientTick, EffectAuthored: true));
                appliedActions++;
                return true;

            default:
                diagnostic = $"RenderStateEffect {effectEvent.DefinitionId} action {effectEvent.ActionKind} is unsupported.";
                return false;
        }
    }

    private static bool ActivateChain(
        ObjectState state,
        EnbResolvedRslLibraryResourceGraph graph,
        int startingStateId,
        uint clientTick,
        ref int appliedActions,
        out string? diagnostic,
        bool effectAuthored = true)
    {
        EnbCurrentRetailRslLinkActivationPlan traversal =
            EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, startingStateId);
        if (traversal.Termination is not (EnbCurrentRetailRslLinkActivationTermination.NoFurtherLink or
            EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted))
        {
            diagnostic =
                $"RSL activation from state {startingStateId} ended as {traversal.Termination} at {traversal.UnresolvedOrUnfollowedLinkStateId?.ToString() ?? "-"}.";
            return false;
        }

        EnbCurrentRetailRslClassActivationPlan classPlan =
            EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal, state.CurrentObjectStateId);
        if (!classPlan.IsExecutableWithKnownCurrentObject)
        {
            EnbCurrentRetailRslClassActivationStep? blocked = classPlan.Steps
                .FirstOrDefault(static step => !step.IsExecutableWithKnownCurrentObject);
            diagnostic = blocked is null
                ? $"RSL activation from state {startingStateId} is not executable with the current Object RenderObj."
                : $"RSL activation state {blocked.StateId} type {blocked.StateType} remains fail-closed or lacks its required current Object RenderObj.";
            return false;
        }

        foreach (EnbCurrentRetailRslLinkActivationStep step in traversal.Steps)
        {
            if (!graph.TryFindState(step.StateId, out EnbResolvedRslStateResource target))
                continue;

            //Message activation is the controller's proven no-op callback;
            //the generic walker still proceeds to its LINK target.
            if (target.Definition.Type == EnbRslStateType.Message)
                continue;

            if (target.Definition.Type == EnbRslStateType.Object)
            {
                //0045EE50 enables and unhides before owner00460940 invokes the
                //old current Object's deactivation, even when it is the same
                //state or already disabled. Its current pointer is then replaced.
                state.ActiveStateIds.Add(step.StateId);
                state.ActiveList.Enable(step.StateId);
                var objectLifetime = state.ObjectLifetimes[step.StateId];
                objectLifetime.Elapsed = 0; //0045EE50; Bone00458B20 does not reset.
                objectLifetime.Flags |= 0x10;
                SetObjectHidden(state,step.StateId,false,clientTick,effectAuthored);
                state.ActivationVirtualActionOrdinals.Add(state.Actions.Count);
                state.Actions.Add(new(0x0030,step.StateId,clientTick,effectAuthored));
                appliedActions++;
                ObserveObjectSceneEvent(state,EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent,step.StateId,false,
                    state.CurrentObjectStateId,clientTick,effectAuthored);
                if(state.CurrentObjectStateId is int previous)
                {
                    DeactivateOne(state,previous,clientTick,effectAuthored);
                    appliedActions++;
                }
                state.CurrentObjectStateId = step.StateId;
                //460940 walks the copied state list after replacing current.
                //45C990 clones the reverse template list and457F60 prepends,
                //so this traversal is authored section order. Only Bone's
                //+1C (458BC0) is nontrivial: enable/relink, unhide and attach.
                //It neither walks LINK nor resets the inherited elapsed time.
                foreach(var candidate in graph.States.OrderBy(s=>s.Definition.SectionOrdinal))
                {
                    int boneId=candidate.Definition.StateId;
                    if(candidate.Definition.Type!=EnbRslStateType.Bone || !IsStateEnabled(state,candidate))continue;
                    state.ActiveStateIds.Add(boneId);
                    state.ActiveList.Enable(boneId);
                    state.ObjectLifetimes[boneId].Flags|=0x10;
                    state.ActivationVirtualActionOrdinals.Add(state.Actions.Count);
                    state.Actions.Add(new(0x0030,boneId,clientTick,effectAuthored));
                    ObserveObjectSceneEvent(state,EnbCurrentRetailRslObjectSceneEventKind.BoneHidden,boneId,false,null,clientTick,effectAuthored);
                    appliedActions++;
                }
                continue;
            }
            else if (target.Definition.Type == EnbRslStateType.Animation &&
                     state.CurrentObjectStateId is null)
            {
                diagnostic = $"RSL Animation state {step.StateId} activation has no current Object RenderObj.";
                return false;
            }

            // Sound::Activate (0045F7D0) re-enters even when already enabled:
            // 00457E10 relinks it, 0045FB40 resets the retained handle/+38 state.
            // An authored LINK may therefore reactivate a shared Sound state.
            if (state.ActiveStateIds.Add(step.StateId) || target.Definition.Type is EnbRslStateType.Sound or EnbRslStateType.Link or EnbRslStateType.Bone or EnbRslStateType.Animation or EnbRslStateType.EffectPass)
            {
                state.ActiveList.Enable(step.StateId);
                if(state.LinkFlags.TryGetValue(step.StateId,out uint linkFlags))state.LinkFlags[step.StateId]=linkFlags|0x10;
                if(state.SoundFlags.TryGetValue(step.StateId,out uint soundFlags))
                {state.SoundFlags[step.StateId]=soundFlags|0x10;if (state.CompletedSounds.Remove(step.StateId)) state.CompletedSoundPublication = null;}
                if (state.ObjectLifetimes.TryGetValue(step.StateId, out var boneLifetime)) boneLifetime.Flags |= 0x10;
                if (state.EffectPassFlags.TryGetValue(step.StateId, out uint passFlags))
                    state.SetEffectPassFlags(step.StateId, (passFlags | 0x10) & ~0x80u);
                state.ActivationVirtualActionOrdinals.Add(state.Actions.Count);
                state.Actions.Add(new EnbCurrentRetailRslEffectiveAction(
                    0x0030, step.StateId, clientTick, EffectAuthored: effectAuthored));
                if(target.Definition.Type==EnbRslStateType.Bone)
                    ObserveObjectSceneEvent(state,EnbCurrentRetailRslObjectSceneEventKind.BoneHidden,step.StateId,false,null,clientTick,effectAuthored);
                appliedActions++;
                if (target.Definition.Type == EnbRslStateType.Animation)
                {
                    var frame = state.AnimationFrames[step.StateId];
                    frame.Flags = (frame.Flags | 0x10) & ~0x80u;
                    frame.ActivationTick = clientTick;
                    //A different current Object changes the composed HAnim
                    //lookup. Preserve the prior rate through NEXT preactivation,
                    //then await the App's new concrete binding before reusing it.
                    if (state.BoundAnimationObjectStates.TryGetValue(step.StateId, out var boundObject) &&
                        boundObject != state.CurrentObjectStateId)
                    {
                        if (state.BoundAnimations.Remove(step.StateId)) state.AnimationFramePublication = null;
                        state.BoundAnimationObjectStates.Remove(step.StateId);
                    }
                    state.PreviouslyBoundAnimations.Add(step.StateId);
                    BindAnimation(state, step.StateId);
                    var animationEnvelope = GetEnvelope(state, step.StateId, clientTick);
                    animationEnvelope.Envelope = new(EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.UnsettledValue,
                        animationEnvelope.Envelope.SlopePerMillisecond);
                    animationEnvelope.LastUpdateTick = clientTick;
                }
            }
        }

        diagnostic = null;
        return true;
    }

    private static bool DeactivateChain(
        ObjectState state,
        EnbResolvedRslLibraryResourceGraph graph,
        int startingStateId,
        uint clientTick,
        ref int appliedActions,
        out string? diagnostic,
        bool effectAuthored=true)
    {
        if (!TryResolveLinkChain(graph, startingStateId, out int[] chain, out diagnostic))
            return false;

        foreach (int stateId in chain)
        {
            DeactivateOne(state,stateId,clientTick,effectAuthored);
            appliedActions++;
        }
        return true;
    }

    private static void DeactivateOne(ObjectState state, int stateId, uint clientTick, bool effectAuthored, bool preserveSoundHandle=false)
    {
        //Sound0045F810 returns before cleanup/base unlink when ACTIVATE_NEXT
        //is set. The caller's generic LINK deactivation still continues.
        if(state.SoundFlags.TryGetValue(stateId,out uint soundFlags))
        {
            if((soundFlags&8)!=0)return;
            state.SoundFlags[stateId]=soundFlags&~0x10u;
        }
        if(state.Graph.TryFindState(stateId,out var objectState) && objectState.Definition.Type==EnbRslStateType.Object)
            SetObjectHidden(state,stateId,true,clientTick,effectAuthored);
        state.ActiveStateIds.Remove(stateId);
        state.ActiveList.Disable(stateId);
        if(state.LinkFlags.TryGetValue(stateId,out uint linkFlags))state.LinkFlags[stateId]=linkFlags&~0x10u;
        if (state.ObjectLifetimes.TryGetValue(stateId, out var lifetime)) lifetime.Flags &= ~0x10u;
        if (state.EffectPassFlags.TryGetValue(stateId, out uint passFlags)) state.SetEffectPassFlags(stateId, passFlags & ~0x10u);
        if (state.AnimationEnvelopes.TryGetValue(stateId, out var envelope))
            envelope.Envelope = new(EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.UnsettledValue,
                envelope.Envelope.SlopePerMillisecond);
        if (state.AnimationFrames.TryGetValue(stateId, out var frame))
        {
            frame.Flags &= ~0x10u;
            frame.PreviousFrame = frame.Frame;
            frame.Frame = 0;
        }
        state.RemoveAnimationEntry(stateId);
        state.Actions.Add(new EnbCurrentRetailRslEffectiveAction(
            0x0032, stateId, clientTick, effectAuthored) { PreserveSoundHandle=preserveSoundHandle });
        if(objectState?.Definition.Type==EnbRslStateType.Bone)
            ObserveObjectSceneEvent(state,EnbCurrentRetailRslObjectSceneEventKind.BoneHidden,stateId,true,null,clientTick,effectAuthored);
    }

    private static AnimationEnvelopeState GetEnvelope(ObjectState state, int stateId, uint atTick)
    {
        if (!state.AnimationEnvelopes.TryGetValue(stateId, out AnimationEnvelopeState? envelope))
        {
            envelope = new AnimationEnvelopeState(state,
                EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation,
                atTick);
            state.AnimationEnvelopes[stateId] = envelope;
            state.InvalidateEnvelopeMembership();
        }
        return envelope;
    }

    private static void BindAnimation(ObjectState state, int stateId)
    {
        if (state.BoundAnimations.ContainsKey(stateId) || state.CurrentObjectStateId is not int current ||
            state.AnimationBinder is null) return;
        if (state.AnimationBinder(stateId, current) is { } clip)
        {
            state.BoundAnimations[stateId] = clip;
            state.AnimationFramePublication = null;
            state.BoundAnimationObjectStates[stateId] = current;
        }
    }

    private static bool ApplyNextOnce(ObjectState state, EnbResolvedRslLibraryResourceGraph graph,
        int stateId, out string? diagnostic)
    {
        diagnostic = null;
        var visited = new HashSet<int>();
        int current = stateId;
        while (true)
        {
            if (!visited.Add(current))
            { diagnostic = $"Animation NEXT-once recursion cycles at {current}."; return false; }
            if (!graph.TryFindState(current, out var target))
            {
                //0045A010's failed lookup assertion continues with null +48.
                return true;
            }
            if (target.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Bone or
                EnbRslStateType.Link or EnbRslStateType.Message or EnbRslStateType.Sound or EnbRslStateType.EffectPass) return true;
            if (target.Definition.Type != EnbRslStateType.Animation)
            { diagnostic = $"NEXT-once virtual for {target.Definition.Type} remains unresolved."; return false; }
            var frame = state.AnimationFrames[current];
            if ((frame.Flags & 1) == 0) { frame.Flags |= 0x20; return true; }
            if (target.Definition.NextStateId is not int next) return true;
            current = next;
        }
    }

    private static bool TryResolveLinkChain(
        EnbResolvedRslLibraryResourceGraph graph,
        int startingStateId,
        out int[] chain,
        out string? diagnostic)
    {
        var result = new List<int>();
        var visited = new HashSet<int>();
        int current = startingStateId;
        while (true)
        {
            if (!visited.Add(current))
            {
                chain = result.ToArray();
                diagnostic = $"RSL deactivation LINK chain cycles at state {current}.";
                return false;
            }
            if (!graph.TryFindState(current, out EnbResolvedRslStateResource state))
            {
                chain = result.ToArray();
                diagnostic = $"RSL deactivation LINK chain references missing state {current}.";
                return false;
            }
            result.Add(current);
            if (state.Definition.LinkStateId is not int linked)
            {
                chain = result.ToArray();
                diagnostic = null;
                return true;
            }
            current = linked;
        }
    }

    public sealed partial class ObjectState
    {
        public ObjectState(
            ulong visualGeneration,
            int packetActionCount,
            uint lastClientTick,
            int? currentObjectStateId,EnbResolvedRslLibraryResourceGraph graph)
        {
            ActiveList=new(InvalidateRenderMembership);
            Graph=graph;
            VisualGeneration = visualGeneration;
            PacketActionCount = packetActionCount;
            LastClientTick = lastClientTick;
            CurrentObjectStateId = currentObjectStateId;
        }

        public RslCallbackCounters Counters;
        internal readonly CallbackScratch CallbackScratch=new();
        public bool IsHistoricalQuery {get;private set;}
        private EnbCurrentRetailEffectRslObjectUpdate? historicalDiagnostic;
        public EnbCurrentRetailEffectRslObjectUpdate CaptureDiagnostics()
            =>IsHistoricalQuery?historicalDiagnostic??=CaptureState(this):CaptureState(this);
        public IReadOnlyList<int> RenderStateIds=>GetRenderMembership(ActiveList.Snapshot()).Render;
        public EnbCurrentRetailRslRenderStructure RenderStructure=>GetRenderMembership(ActiveList.Snapshot()).Structure;
        public IReadOnlyList<int> ActiveEffectPassStateIds=>GetRenderMembership(ActiveList.Snapshot()).EffectPasses;
        public bool CurrentObjectHidden=>CurrentObjectStateId is int current && HiddenObjectStateIds.Contains(current);
        public bool TryGetAnimationFrame(int id,out AnimationFrameState value)
        {
            value=null!;
            return (IsHistoricalQuery||AnimationBinder is not null||BoundAnimations.ContainsKey(id))&&AnimationFrames.TryGetValue(id,out value!);
        }
        public bool TryGetAnimationWeight(int id,out float value)
        {
            foreach(var entry in GetActiveEnvelopes(ActiveList.Snapshot()))
                if(entry.Id==id){value=entry.Envelope.ContributionWeight;return true;}
            value=default;return false;
        }
        private sealed record RenderMembership(IReadOnlyList<int> Order, EnbResolvedRslLibraryResourceGraph Graph,
            int? Current, IReadOnlyList<int> Render, IReadOnlyList<int> EffectPasses, EnbCurrentRetailRslRenderStructure Structure);
        private RenderMembership? _renderMembership;
        private readonly List<WeakReference<OwnerBinding>> _renderOwners=[];
        public void BindOwner(OwnerBinding binding)=>_renderOwners.Add(new(binding));
        public void NotifySceneChanged()
        {
            for(int i=_renderOwners.Count-1;i>=0;i--)
                if(_renderOwners[i].TryGetTarget(out var owner)&&!owner.Released)owner.SceneChanged?.Invoke();
                else _renderOwners.RemoveAt(i);
        }
        private (int Id, AnimationEnvelopeState Envelope)[]? _activeEnvelopes;
        private IReadOnlyDictionary<int,float>? _weightPublication;
        public void InvalidateEnvelopeMembership()
        { _activeEnvelopes = null; _weightPublication = null; }
        public void InvalidateWeights() => _weightPublication = null;
        public (int Id, AnimationEnvelopeState Envelope)[] GetActiveEnvelopes(IReadOnlyList<int> order)
        {
            if (_activeEnvelopes is not null) return _activeEnvelopes;
            var entries = new List<(int,AnimationEnvelopeState)>();
            foreach (int id in order)
                if (AnimationEnvelopes.TryGetValue(id,out var envelope)) entries.Add((id,envelope));
            return _activeEnvelopes = entries.ToArray();
        }
        public IReadOnlyDictionary<int,float> SnapshotWeights((int Id, AnimationEnvelopeState Envelope)[] entries)
        {
            if (_weightPublication is not null) return _weightPublication;
            if (entries.Length == 0) return _weightPublication = FrozenDictionary<int,float>.Empty;
            var values = new Dictionary<int,float>(entries.Length);
            foreach (var entry in entries) values.Add(entry.Id,entry.Envelope.ContributionWeight);
            return _weightPublication = new System.Collections.ObjectModel.ReadOnlyDictionary<int,float>(values);
        }
        public void InvalidateRenderMembership()
        {
            _renderMembership=null;
            InvalidateEnvelopeMembership();
            for(int i=_renderOwners.Count-1;i>=0;i--)
                if(_renderOwners[i].TryGetTarget(out var owner)&&!owner.Released)owner.Changed?.Invoke();
                else _renderOwners.RemoveAt(i);
        }
        public (IReadOnlyList<int> Render, IReadOnlyList<int> EffectPasses, EnbCurrentRetailRslRenderStructure Structure) GetRenderMembership(IReadOnlyList<int> activeOrder)
        {
            if (_renderMembership is {} saved)
                return (saved.Render, saved.EffectPasses, saved.Structure);
            var state = this;
            var graph = Graph;
            var effectPassIds = new List<int>();
            foreach (int id in activeOrder)
            {
                if (graph.TryFindState(id, out var active) && active.Definition.Type == EnbRslStateType.EffectPass)
                    effectPassIds.Add(id);
            }
            int[] renderStateIds = state.ActiveStateIds
                .Where(id => graph.TryFindState(id, out EnbResolvedRslStateResource resolved) &&
                             resolved.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Animation)
                .ToArray();
            bool hasAnimation = renderStateIds.Any(id =>
                graph.TryFindState(id, out EnbResolvedRslStateResource resolved) &&
                resolved.Definition.Type == EnbRslStateType.Animation);
            bool hasObject = renderStateIds.Any(id =>
                graph.TryFindState(id, out EnbResolvedRslStateResource resolved) &&
                resolved.Definition.Type == EnbRslStateType.Object);
            if (!hasAnimation && !hasObject && state.CurrentObjectStateId is int retainedObjectStateId)
                renderStateIds = [retainedObjectStateId];
            if (hasAnimation)
            {
                // The retained current Object is still the Animation target, but the
                // existing renderer represents that retail ownership by rendering
                // the active Animation state(s) against the retained Object root.
                // Do not also submit the Object state as a second independent root.
                renderStateIds = renderStateIds.Where(id =>
                    !graph.TryFindState(id, out EnbResolvedRslStateResource resolved) ||
                    resolved.Definition.Type != EnbRslStateType.Object).ToArray();
            }

            var renderIds=Array.AsReadOnly(renderStateIds.OrderBy(static id => id).ToArray());
            var published = new RenderMembership(activeOrder, Graph, CurrentObjectStateId,
                renderIds, effectPassIds.AsReadOnly(),new(Graph,renderIds,activeOrder,CurrentObjectStateId));
            _renderMembership = published;
            return (published.Render, published.EffectPasses, published.Structure);
        }

        // These six histories only append within an ObjectState generation.
        // Snapshot publication never exposes mutable storage; a new event makes
        // a new snapshot while already queued consumers retain the old prefix.
        private EnbCurrentRetailRslEffectiveAction[] _snapshotActions = [];
        public IReadOnlyList<EnbCurrentRetailRslEffectiveAction> SnapshotActions
            => _snapshotActions.Length == Actions.Count ? _snapshotActions : _snapshotActions = Actions.ToArray();
        private FrozenSet<int> _snapshotActivationOrdinals = FrozenSet<int>.Empty;
        public IReadOnlySet<int> SnapshotActivationOrdinals
            => _snapshotActivationOrdinals.Count == ActivationVirtualActionOrdinals.Count ? _snapshotActivationOrdinals
                : _snapshotActivationOrdinals = ActivationVirtualActionOrdinals.ToFrozenSet();
        private EnbCurrentRetailRslSoundHandleRelease[] _snapshotSoundReleases = [];
        public IReadOnlyList<EnbCurrentRetailRslSoundHandleRelease> SnapshotSoundReleases
            => _snapshotSoundReleases.Length == SoundHandleReleases.Count ? _snapshotSoundReleases
                : _snapshotSoundReleases = SoundHandleReleases.ToArray();
        private FrozenDictionary<int,int> _snapshotPacketOrdinals = FrozenDictionary<int,int>.Empty;
        public IReadOnlyDictionary<int,int> SnapshotPacketOrdinals
            => _snapshotPacketOrdinals.Count == EffectiveActionPacketOrdinals.Count ? _snapshotPacketOrdinals
                : _snapshotPacketOrdinals = EffectiveActionPacketOrdinals.ToFrozenDictionary();
        private ImmutableArray<EnbCurrentRetailRslObjectSceneEvent> _snapshotSceneEvents = [];
        public ImmutableArray<EnbCurrentRetailRslObjectSceneEvent> SnapshotSceneEvents
            => _snapshotSceneEvents.Length == ObjectSceneEvents.Count ? _snapshotSceneEvents
                : _snapshotSceneEvents = ObjectSceneEvents.ToImmutableArray();
        private FrozenDictionary<ulong,EnbCurrentRetailEffectRslObjectUpdate> _snapshotLegoQueries
            = FrozenDictionary<ulong,EnbCurrentRetailEffectRslObjectUpdate>.Empty;
        public IReadOnlyDictionary<ulong,EnbCurrentRetailEffectRslObjectUpdate> SnapshotLegoQueries
            => _snapshotLegoQueries.Count == LegoControllerQueryUpdates.Count ? _snapshotLegoQueries
                : _snapshotLegoQueries = LegoControllerQueryUpdates.ToFrozenDictionary(p=>p.Key,p=>p.Value.CaptureDiagnostics());

        private EnbResolvedRslLibraryResourceGraph _graph=null!;
        public EnbResolvedRslLibraryResourceGraph Graph
        {get=>_graph;set{if(ReferenceEquals(_graph,value))return;_graph=value;InvalidateRenderMembership();}}
        public Dictionary<int,uint> LinkFlags {get;}=[];
        public Dictionary<int,uint> SoundFlags {get;}=[];
        public HashSet<int> CompletedSounds {get;}=[];
        public FrozenSet<int>? CompletedSoundPublication, HiddenObjectPublication;
        public FrozenDictionary<int,ulong>? VisibilityCountPublication;
        public IReadOnlySet<int> SnapshotCompletedSounds => CompletedSoundPublication ??= CompletedSounds.ToFrozenSet();
        public IReadOnlySet<int> SnapshotHiddenObjects => HiddenObjectPublication ??= HiddenObjectStateIds.ToFrozenSet();
        public IReadOnlyDictionary<int,ulong> SnapshotVisibilityCounts => VisibilityCountPublication ??= ObjectVisibilityTransitionCounts.ToFrozenDictionary();
        public List<EnbCurrentRetailRslSoundHandleRelease> SoundHandleReleases {get;}=[];
        public Dictionary<int,int?> LinkArguments {get;}=[];
        public ulong VisualGeneration { get; }
        public int PacketActionCount { get; set; }
        public int? ProcessingPacketActionOrdinal { get; set; }
        public Dictionary<int,int> EffectiveActionPacketOrdinals {get;}=[];
        public readonly Dictionary<ulong,ObjectState> LegoControllerQueryUpdates=[];
        public ObjectState? FactoryLegoQuery;
        public uint LastClientTick { get; set; }
        private int? _currentObjectStateId;
        public int? CurrentObjectStateId
        {get=>_currentObjectStateId;set{if(_currentObjectStateId==value)return;_currentObjectStateId=value;InvalidateRenderMembership();}}
        public int FactoryActionCount { get; set; }
        public int? FactoryObjectStateId { get; set; }
        public int? LastDirectStateSelection { get; set; }
        public HashSet<int> ActiveStateIds { get; } = [];
        public EnbCurrentRetailRslActiveListRuntime ActiveList { get; }
        public List<EnbCurrentRetailRslEffectiveAction> Actions { get; } = [];
        public HashSet<int> ActivationVirtualActionOrdinals { get; } = [];
        public HashSet<int> HiddenObjectStateIds { get; } = [];
        public Dictionary<int,ulong> ObjectVisibilityTransitionCounts { get; } = [];
        public List<EnbCurrentRetailRslObjectSceneEvent> ObjectSceneEvents { get; } = [];
        public int InitialObjectSceneEventCount { get; set; }
        public bool Initializing { get; set; } = true;
        public Dictionary<int, AnimationEnvelopeState> AnimationEnvelopes { get; } = [];
        public Dictionary<int, EnbCurrentRetailRslAnimationEntry> AnimationEntryFrames { get; } = [];
        // The state dictionaries are populated at construction. Their mutable
        // metadata publishes new immutable snapshots only at its actual setter.
        public IReadOnlyDictionary<int,uint>? ObjectFlagsPublication, AnimationFlagsPublication, AnimationTicksPublication;
        public IReadOnlyDictionary<int,float>? AnimationFramePublication, PreviousFramePublication, ObjectElapsedPublication;
        public IReadOnlyDictionary<int,float> SnapshotAnimationFrames => AnimationFramePublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,float>(AnimationFrames
                .Where(item => IsHistoricalQuery || AnimationBinder is not null || BoundAnimations.ContainsKey(item.Key))
                .ToDictionary(item => item.Key, item => item.Value.Frame));
        public IReadOnlyDictionary<int,float> SnapshotPreviousFrames => PreviousFramePublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,float>(AnimationFrames.ToDictionary(item => item.Key, item => item.Value.PreviousFrame));
        public IReadOnlyDictionary<int,float> SnapshotObjectElapsed => ObjectElapsedPublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,float>(ObjectLifetimes.ToDictionary(item => item.Key, item => item.Value.Elapsed));

        private IReadOnlyDictionary<int,uint>? _effectPassFlagsPublication;
        private IReadOnlyDictionary<int,EnbCurrentRetailRslAnimationEntry>? _animationEntriesPublication;
        public IReadOnlyDictionary<int,uint> SnapshotObjectFlags => ObjectFlagsPublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,uint>(ObjectLifetimes.ToDictionary(item => item.Key, item => item.Value.Flags & 0xff));
        public IReadOnlyDictionary<int,uint> SnapshotAnimationFlags => AnimationFlagsPublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,uint>(AnimationFrames.ToDictionary(item => item.Key, item => item.Value.Flags));
        public IReadOnlyDictionary<int,uint> SnapshotAnimationTicks => AnimationTicksPublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,uint>(AnimationFrames.ToDictionary(item => item.Key, item => item.Value.ActivationTick));
        public IReadOnlyDictionary<int,uint> SnapshotEffectPassFlags => _effectPassFlagsPublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,uint>(new Dictionary<int,uint>(EffectPassFlags));
        public IReadOnlyDictionary<int,EnbCurrentRetailRslAnimationEntry> SnapshotAnimationEntries => _animationEntriesPublication ??=
            new System.Collections.ObjectModel.ReadOnlyDictionary<int,EnbCurrentRetailRslAnimationEntry>(new Dictionary<int,EnbCurrentRetailRslAnimationEntry>(AnimationEntryFrames));
        public void SetAnimationEntry(int id, EnbCurrentRetailRslAnimationEntry entry)
        {
            if (AnimationEntryFrames.TryGetValue(id, out var prior) && prior.ClientTick == entry.ClientTick &&
                BitConverter.SingleToInt32Bits(prior.Frame) == BitConverter.SingleToInt32Bits(entry.Frame)) return;
            AnimationEntryFrames[id] = entry;
            _animationEntriesPublication = null;
        }
        public void RemoveAnimationEntry(int id)
        {
            if (AnimationEntryFrames.Remove(id)) _animationEntriesPublication = null;
        }
        public void SetEffectPassFlags(int id, uint flags)
        {
            if (EffectPassFlags.TryGetValue(id, out uint prior) && prior == flags) return;
            EffectPassFlags[id] = flags;
            _effectPassFlagsPublication = null;
        }
        public HashSet<int> PreviouslyBoundAnimations { get; } = [];
        public Dictionary<int, EnbW3dRenderHierarchyAnimation> BoundAnimations { get; } = [];
        public Dictionary<int, int?> BoundAnimationObjectStates { get; } = [];
        public Dictionary<int, DurationState> ObjectLifetimes { get; } = [];
        public Dictionary<int, AnimationFrameState> AnimationFrames { get; } = [];
        public Dictionary<int, uint> EffectPassFlags { get; } = [];
        private Func<int, int, EnbW3dRenderHierarchyAnimation?>? _animationBinder;
        public Func<int, int, EnbW3dRenderHierarchyAnimation?>? AnimationBinder
        {
            get => _animationBinder;
            set { if ((_animationBinder is null) != (value is null)) AnimationFramePublication = null;
                _animationBinder = value; }
        }
        public bool OwnerDeleteRequested { get; set; }
    }

    public sealed class DurationState(ObjectState owner)
    {
        private float _elapsed;
        public float Elapsed
        {
            get => _elapsed;
            set { if (BitConverter.SingleToInt32Bits(_elapsed) == BitConverter.SingleToInt32Bits(value)) return;
                _elapsed = value; owner.ObjectElapsedPublication = null; }
        }
        private uint _flags;
        public uint Flags
        {
            get => _flags;
            set { if (_flags == value) return; _flags = value; owner.ObjectFlagsPublication = null; }
        }
    }

    public sealed class AnimationFrameState(ObjectState owner)
    {
        private float _frame, _previousFrame;
        public float Frame
        {
            get => _frame;
            set { if (BitConverter.SingleToInt32Bits(_frame) == BitConverter.SingleToInt32Bits(value)) return;
                _frame = value; owner.AnimationFramePublication = null; }
        }
        public float PreviousFrame
        {
            get => _previousFrame;
            set { if (BitConverter.SingleToInt32Bits(_previousFrame) == BitConverter.SingleToInt32Bits(value)) return;
                _previousFrame = value; owner.PreviousFramePublication = null; }
        }
        private uint _flags;
        public uint Flags
        {
            get => _flags;
            set { if (_flags == value) return; _flags = value; owner.AnimationFlagsPublication = null; }
        }
        private uint _activationTick;
        public uint ActivationTick
        {
            get => _activationTick;
            set { if (_activationTick == value) return; _activationTick = value; owner.AnimationTicksPublication = null; }
        }
    }

    public sealed class AnimationEnvelopeState(
        ObjectState owner,
        EnbCurrentRetailRslAnimationWeightEnvelope envelope,
        uint lastUpdateTick)
    {
        public EnbCurrentRetailRslAnimationWeightEnvelope Envelope { get; set; } = envelope;
        public uint LastUpdateTick { get; set; } = lastUpdateTick;
        //00931350 initializes HAnimInstance+0C (Animation+38) to1.
        private float _contributionWeight = 1f;
        public float ContributionWeight
        {
            get => _contributionWeight;
            set
            {
                if (BitConverter.SingleToInt32Bits(_contributionWeight) == BitConverter.SingleToInt32Bits(value)) return;
                _contributionWeight = value;
                owner.InvalidateWeights();
            }
        }
    }
}

public readonly record struct EnbCurrentRetailRslEffectiveAction(
    ushort Opcode,
    int StateId,
    uint ClientTime,
    bool EffectAuthored)
{
    // 0045FC50 ordinary completion calls base unlink (00457E90), retaining
    // the stopped handle. Explicit Sound::Deactivate (0045F810) releases it.
    public bool PreserveSoundHandle { get; init; }
}

public enum EnbCurrentRetailRslObjectSceneEventKind { Hidden, SwitchCurrent, BoneHidden }
public enum EnbCurrentRetailRslObjectSceneEventPhase { Initialization, Packet, Controller }
public readonly record struct EnbCurrentRetailRslObjectSceneEvent(int Ordinal,
    EnbCurrentRetailRslObjectSceneEventKind Kind,int StateId,bool Hidden,int? PreviousStateId=null)
{
    public EnbCurrentRetailRslObjectSceneEventPhase Phase { get; init; }
    public uint ClientTick { get; init; }
    public int EffectiveActionCount { get; init; }
    public int? PacketActionOrdinal { get; init; }
    public IReadOnlySet<int> ActiveStateIds { get; init; } = FrozenSet<int>.Empty;
    public ImmutableArray<int> ActiveListStateIds { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public EnbResolvedRslLibraryResourceGraph? ResourceGraph { get; init; }
    public IReadOnlyDictionary<int,float> AnimationFrames { get; init; } = FrozenDictionary<int,float>.Empty;
    public IReadOnlyDictionary<int,float> AnimationWeights { get; init; } = FrozenDictionary<int,float>.Empty;
    public IReadOnlyDictionary<int,int?> AnimationObjectStateIds { get; init; } = FrozenDictionary<int,int?>.Empty;
    public IReadOnlyDictionary<int,EnbW3dRenderHierarchyAnimation> AnimationClips { get; init; } = FrozenDictionary<int,EnbW3dRenderHierarchyAnimation>.Empty;
    public IReadOnlyDictionary<int,uint> AnimationActivationTicks { get; init; } = FrozenDictionary<int,uint>.Empty;
    public IReadOnlyDictionary<int,uint> AnimationTransitionFlags { get; init; } = FrozenDictionary<int,uint>.Empty;
    public IReadOnlyDictionary<int,float> AnimationPreviousFrames { get; init; } = FrozenDictionary<int,float>.Empty;
}

public readonly record struct EnbCurrentRetailRslSoundHandleRelease(int StateId,int BeforeActionOrdinal);

public sealed record EnbCurrentRetailEffectRslObjectUpdate(
    IReadOnlyList<int> ActiveStateIds,
    IReadOnlyList<EnbCurrentRetailRslEffectiveAction> Actions,
    IReadOnlyDictionary<int, float> AnimationWeights,
    int? CurrentObjectStateId,
    EnbCurrentRetailEffectRslObjectReceipt Receipt)
{

    [System.Text.Json.Serialization.JsonIgnore]
    public EnbCurrentRetailRslRenderStructure? RenderStructure {get;init;}

    public IReadOnlyDictionary<int, EnbCurrentRetailRslAnimationEntry> AnimationEntryFrames {get;init;} = FrozenDictionary<int,EnbCurrentRetailRslAnimationEntry>.Empty;
    public IReadOnlyDictionary<int, float> AnimationFrames {get;init;} = FrozenDictionary<int, float>.Empty;
    public IReadOnlyDictionary<int, uint> AnimationActivationTicks {get;init;} = FrozenDictionary<int, uint>.Empty;
    public IReadOnlyDictionary<int, uint> AnimationTransitionFlags {get;init;} = FrozenDictionary<int, uint>.Empty;
    public IReadOnlyDictionary<int, float> AnimationPreviousFrames {get;init;} = FrozenDictionary<int, float>.Empty;
    /// <summary>Distinguishes00460440 activation virtuals from004604D0's
    ///same-current Object selection, which only unhides its existing RenderObj.
    ///Selecting another enabled state does not invoke its getter.</summary>
    public IReadOnlySet<int> ActivationVirtualActionOrdinals {get;init;} = FrozenSet<int>.Empty;
    public IReadOnlyDictionary<int,int> EffectiveActionPacketOrdinals {get;init;}=FrozenDictionary<int,int>.Empty;
    public bool CurrentObjectHidden {get;init;}
    public IReadOnlySet<int> CompletedSoundStateIds {get;init;} = FrozenSet<int>.Empty;
    public IReadOnlyList<EnbCurrentRetailRslSoundHandleRelease> SoundHandleReleases {get;init;} = [];
    public IReadOnlySet<int> HiddenObjectStateIds {get;init;} = FrozenSet<int>.Empty;
    public IReadOnlyDictionary<int,ulong> ObjectVisibilityTransitionCounts {get;init;} = FrozenDictionary<int,ulong>.Empty;
    public ImmutableArray<EnbCurrentRetailRslObjectSceneEvent> ObjectSceneEvents {get;init;} = [];
    public int InitialObjectSceneEventCount {get;init;}
    public IReadOnlyDictionary<ulong,EnbCurrentRetailEffectRslObjectUpdate> LegoControllerQueryUpdates {get;init;} = FrozenDictionary<ulong,EnbCurrentRetailEffectRslObjectUpdate>.Empty;
    public EnbCurrentRetailEffectRslObjectUpdate? FactoryLegoQuery {get;init;}
    public int FactoryActionCount {get;init;}
    public int? FactoryObjectStateId {get;init;}
    public IReadOnlyList<int> ActiveListStateIds {get;init;} = [];
    public IReadOnlyDictionary<int, float> ObjectElapsedSeconds {get;init;} = FrozenDictionary<int, float>.Empty;
    public IReadOnlyDictionary<int, uint> ObjectTransitionFlags {get;init;} = FrozenDictionary<int, uint>.Empty;
    public bool OwnerDeleteRequested {get;init;}
    [System.Text.Json.Serialization.JsonIgnore]
    public EnbResolvedRslLibraryResourceGraph? ResourceGraph {get;init;}
    public IReadOnlyList<int> ActiveEffectPassStateIds {get;init;} = [];
    public IReadOnlyDictionary<int, uint> EffectPassTransitionFlags {get;init;} = FrozenDictionary<int, uint>.Empty;
}

public readonly record struct EnbCurrentRetailRslAnimationEntry(float Frame, uint ClientTick);

public sealed record EnbCurrentRetailEffectRslObjectReceipt(
    int ActiveStates,
    int EffectiveActions,
    int EffectActionsApplied,
    int EffectEventsIgnored,
    int AnimationWeights,
    int DirectStateSelectionsApplied,
    int DirectStateSelectionsRejected,
    int FailClosedEvents,
    string? Diagnostic);
