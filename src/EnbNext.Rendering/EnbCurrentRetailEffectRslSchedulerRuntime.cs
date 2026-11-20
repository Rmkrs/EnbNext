using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Persistent current-retail effect-tree scheduler for the RenderStateEffect
/// boundary. Effect definitions are instantiated once per packet-materialized
/// effect generation, survive beyond the immutable snapshot that created them,
/// and emit one-shot RSL mutations at the same lifecycle point as the retail
/// effect start callback.
/// </summary>
public sealed class EnbCurrentRetailEffectRslSchedulerRuntime
{
    private readonly Dictionary<EnbCurrentRetailEffectInstanceKey, RootSlot> _roots = [];
    private readonly UpdateScratch _updateScratch=new();
    private sealed class UpdateScratch : IDisposable
    {
        private bool _active;
        public readonly Dictionary<uint,IEnbRenderObject> World=[];
        public readonly HashSet<EnbCurrentRetailEffectInstanceKey> Present=[];
        public UpdateScratch Rent()
        {
            if(_active)return new UpdateScratch().Rent();
            _active=true;return this;
        }
        public void Dispose(){World.Clear();Present.Clear();_active=false;}
    }
    private ulong _nextEventSequence;
    private ulong _nextNodeSequence;
    private ulong _nextActivationSequence;
    private readonly Queue<string> _lifecycleHistory = new();
    private string? _lifecycleHistoryText;

    // Bounded, presentation-only evidence: survives effect expiry and allocates
    // formatted history only when a lifecycle edge changes, not every frame.
    public string LifecycleHistory => _lifecycleHistoryText ??= string.Join("\n  ", _lifecycleHistory);

    public int TrackedRootCount => _roots.Count;

    public void Clear()
    {
        ClearAttachmentBindings();
        ClearSourceBindings();
        _roots.Clear();
        _nextEventSequence = 0;
        _nextNodeSequence = 0;
        _nextActivationSequence = 0;
        _lifecycleHistory.Clear();
        _lifecycleHistoryText = null;
    }

    public void ClearSourceBindings()
    {
        foreach(var slot in _roots.Values)if(slot.Node is {} node)Clear(node);
        static void Clear(EnbCurrentRetailEffectOwner node)
        {
            node.ClearSource();
            if(node.NextChild is {} next)Clear(next);
            if(node.StartChild is {} start)Clear(start);
        }
    }

    public void ClearAttachmentBindings()
    {
        foreach(var slot in _roots.Values)if(slot.Node is {} node)Clear(node);
        static void Clear(EnbCurrentRetailEffectOwner node)
        {
            node.ClearAttachment();
            if(node.NextChild is {} next)Clear(next);
            if(node.StartChild is {} start)Clear(start);
        }
    }

    public EnbCurrentRetailEffectRslSchedulerUpdate Update(
        IReadOnlyList<EnbSceneEffectSnapshot> snapshotEffects,
        EnbRetailPresentationCatalog catalog,
        uint currentClientTickMilliseconds,
        IReadOnlySet<uint>? worldObjectIds = null,
        IReadOnlyList<IEnbRenderObject>? worldObjects = null,
        Func<EnbEffectDefinition,EnbSceneEffectSnapshot,string?>? lineFactoryRejection = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotEffects);
        ArgumentNullException.ThrowIfNull(catalog);
        using var scratch=_updateScratch.Rent();
        Dictionary<uint,IEnbRenderObject>? objectLifetimes=worldObjects is null?null:scratch.World;
        if(worldObjects is not null)
            foreach(var obj in worldObjects)objectLifetimes!.TryAdd(obj.GameId,obj);
        bool observesWorld=worldObjectIds is not null || objectLifetimes is not null;

        var events = new List<EnbCurrentRetailRenderStateEffectEvent>();
        var sounds = new List<EnbCurrentRetailEffectSoundNode>();
        var present = scratch.Present;
        int createdRoots = 0;
        int rejectedRoots = 0;
        int resolvedDurationLinkedRoots = 0;
        int rejectedDurationLinkedRoots = 0;
        int removalMarks = 0;
        int startedNodes = 0;
        int completedNodes = 0;
        int createdStartLinks = 0;
        int createdNextLinks = 0;

        foreach (EnbSceneEffectSnapshot snapshot in snapshotEffects)
        {
            var key = new EnbCurrentRetailEffectInstanceKey(snapshot.EffectId, snapshot.InstanceGeneration);
            present.Add(key);
            if (_roots.TryGetValue(key, out var existingRoot))
            {
                existingRoot.Snapshot = snapshot;
                continue;
            }

            Remember($"{currentClientTickMilliseconds}ms receive 0x{snapshot.EffectId:X}/g{snapshot.InstanceGeneration} E{snapshot.DefinitionId} {snapshot.GeometryKind} source 0x{snapshot.SourceGameId:X} target 0x{snapshot.TargetGameId:X} stamp {snapshot.Timestamp} wire-duration {snapshot.DurationMilliseconds}ms");

            EnbEffectDefinition? definition = catalog.FindEffect(snapshot.DefinitionId);
            if (definition is null || definition.EffectClassKind == EnbEffectClassKind.Unknown)
            {
                Remember($"{currentClientTickMilliseconds}ms effect definition {(definition is null || string.IsNullOrWhiteSpace(definition.EffectClass) ? "omitted" : "rejected")} E{snapshot.DefinitionId}: {(definition is null ? "missing definition" : "unknown or empty effect class")}");
                _roots[key] = RootSlot.Rejected(key);
                rejectedRoots++;
                continue;
            }

            if (RejectLineFactory(definition, catalog, currentClientTickMilliseconds,snapshot,lineFactoryRejection))
            {
                _roots[key] = RootSlot.Rejected(key);
                rejectedRoots++;
                continue;
            }

            // Resolved 000C stationary targets arrive as ObjectToPoint. The
            // remaining family needs a packet-time effect pose receipt; its
            // target ID must never be interpreted as a world-object binding.
            if (snapshot.GeometryKind == EnbSceneEffectGeometryKind.ObjectToEffect)
            {
                Remember($"{currentClientTickMilliseconds}ms effect target unresolved 0x{snapshot.EffectId:X}/g{snapshot.InstanceGeneration}: packet-time effect position unavailable");
                _roots[key] = RootSlot.Rejected(key);
                rejectedRoots++;
                continue;
            }

            // 00734F09..5E registers a binary instance even when unary +10
            // is absent. Its endpoints remain at constructor coordinates.
            bool unbound = snapshot.GeometryKind == EnbSceneEffectGeometryKind.Object &&
                definition.EffectClassKind is EnbEffectClassKind.LineEffect or EnbEffectClassKind.RObjLookAtEffect;

            uint baseStartTick = snapshot.Timestamp;
            // Current binary setters 006E80D0 / 006E81B0 mark state 2 on
            // failed lookup; 006E6AD0 skips activation and link creation.
            // StraightSeek table B003EC +28/+34 uses these same setters; its
            // bone slot +2C and LookAt's delegate through 006E8170 to +28.
            // Only packet-time gameplay evidence may terminate this tree.
            bool rejectMissingEndpoint = snapshot.GeometryKind == EnbSceneEffectGeometryKind.ObjectToObject &&
                snapshot.EndpointShortB == 0 &&
                (definition.EffectClassKind is (EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect) ||
                    definition.EffectClassKind is (EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect) &&
                    string.IsNullOrEmpty(snapshot.BoneName)) ||
                snapshot.GeometryKind is EnbSceneEffectGeometryKind.ObjectToObject or EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked &&
                definition.EffectClassKind == EnbEffectClassKind.RObjLookAtEffect ||
                // Line table00AFFF4C +28/+34 wraps the same binary lookup
                // setters at006E5860/006E58E0. 000B/000E call both before
                // registration; state2 must not bind objects arriving later.
                snapshot.GeometryKind is EnbSceneEffectGeometryKind.ObjectToObject or EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked &&
                definition.EffectClassKind == EnbEffectClassKind.LineEffect;
            // 000D source setters00735C26/3C share the same failure state.
            // Its point setter006E54A0 only writes coordinates, not state.
            bool missingPointLineSource = snapshot.GeometryKind == EnbSceneEffectGeometryKind.ObjectToPoint &&
                definition.EffectClassKind == EnbEffectClassKind.LineEffect &&
                snapshot.EndpointReceipt is { SourceCreationOrdinal: null };
            // Attach table00AFFE88 +28 -> 006E7770 and RObj's006EF160
            // delegate have the same state2 initial-failure cleanup.
            // Pass00B00184 and Sound00B000F0 also use006E7770 directly.
            // Opcode0009 calls the setter at00734F04.
            bool missingUnarySource = snapshot.GeometryKind == EnbSceneEffectGeometryKind.Object &&
                definition.EffectClassKind is EnbEffectClassKind.AttachEffect or EnbEffectClassKind.RObjEffect or
                    EnbEffectClassKind.PassEffect or EnbEffectClassKind.SoundEffect &&
                snapshot.EndpointReceipt is { SourceCreationOrdinal: null };
            if (missingUnarySource || missingPointLineSource || rejectMissingEndpoint &&
                snapshot.EndpointReceipt is { } endpoints &&
                (endpoints.SourceCreationOrdinal is null || endpoints.TargetCreationOrdinal is null))
            {
                _roots[key] = new RootSlot(key, null, null) { Completed = true, Snapshot = snapshot };
                Remember($"{currentClientTickMilliseconds}ms initial endpoint absent 0x{snapshot.EffectId:X}/g{snapshot.InstanceGeneration}; completed without links");
                continue;
            }

            int? durationOverrideMilliseconds = snapshot.DurationMilliseconds == 0
                ? null
                : snapshot.DurationMilliseconds;
            float factorySpeedup = snapshot.EffectScalar > 0f ? snapshot.EffectScalar : 1f;
            EnbCurrentRetailDurationLinkedEffectTimingResolution? durationLinkedResolution = null;

            // 0x000E +0x30 is a signed effect-definition selector, not a duration.
            // For non-negative selectors current retail runs the linked definitions
            // as a pre-construction timing program. The selected definitions are not
            // instantiated; their StartDelay/StartLink/NextLink program supplies an
            // extra root start offset and the terminal definition supplies duration.
            if (snapshot.GeometryKind == EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked &&
                snapshot.DurationLink >= 0)
            {
                EnbCurrentRetailDurationLinkedEffectTimingResolution resolution =
                    EnbCurrentRetailDurationLinkedEffectTimingRuntime.Resolve(
                        catalog, snapshot.DurationLink, snapshot.EffectScalar);
                durationLinkedResolution = resolution;
                if (!resolution.Success)
                {
                    _roots[key] = RootSlot.Rejected(key, resolution);
                    rejectedRoots++;
                    rejectedDurationLinkedRoots++;
                    continue;
                }

                baseStartTick = unchecked(snapshot.Timestamp +
                    (uint)resolution.AdditionalStartOffsetMilliseconds);
                durationOverrideMilliseconds = resolution.DurationMilliseconds;
                resolvedDurationLinkedRoots++;
            }

            EffectBindingContext binding = unbound ? new(0,0,true,null,UnboundEndpoints:true) : EffectBindingContext.FromSnapshot(snapshot);
            EnbCurrentRetailEffectOwner node = CreateNode(
                definition,
                catalog,
                binding,
                baseStartTick: baseStartTick,
                durationOverrideMilliseconds: durationOverrideMilliseconds,
                rootKey: key,
                rootEffectId: snapshot.EffectId,
                rootGeneration: snapshot.InstanceGeneration,
                linkKind: EnbCurrentRetailEffectLinkKind.Root,
                linkToTarget: false,
                factorySpeedup: factorySpeedup, constructionTick: currentClientTickMilliseconds);
            _roots[key] = new RootSlot(key, node, durationLinkedResolution) { Snapshot = snapshot };
            createdRoots++;
        }

        foreach ((EnbCurrentRetailEffectInstanceKey key, RootSlot slot) in _roots.ToArray())
        {
            bool isPresent = present.Contains(key);
            bool sourcePresent=false,targetPresent=false;
            if(observesWorld && slot.Node?.HasUnboundEndpoints!=true && slot.Snapshot is {} observed)
            {
                sourcePresent=IsOriginalEndpointPresent(observed.SourceGameId,observed.EndpointReceipt?.SourceCreationOrdinal,
                    observed.InitialSourceObject ?? observed.PublishedEndpoints?.Source,ref slot.SourceRenderIdentity,objectLifetimes,worldObjectIds);
                targetPresent=IsOriginalEndpointPresent(observed.TargetGameId,observed.EndpointReceipt?.TargetCreationOrdinal,
                    observed.PublishedEndpoints?.Target,ref slot.TargetRenderIdentity,objectLifetimes,worldObjectIds);
                slot.SourceWasPresent|=sourcePresent || slot.SourceRenderIdentity.HasValue || objectLifetimes is not null && observed.EndpointReceipt?.SourceCreationOrdinal is not null;
                slot.TargetWasPresent|=targetPresent || slot.TargetRenderIdentity.HasValue || objectLifetimes is not null && observed.EndpointReceipt?.TargetCreationOrdinal is not null;
            }
            // Endpoint observers belong to every effect instance, including an
            // infinite Start/Next child below an already-ended finite root.
            if(observesWorld && !slot.Completed && slot.Node is {HasUnboundEndpoints:false} binaryRoot &&
                slot.Snapshot is {} binarySnapshot &&
                (binarySnapshot.IsObjectToObject || binarySnapshot.GeometryKind is EnbSceneEffectGeometryKind.Object or EnbSceneEffectGeometryKind.ObjectToPoint))
            {
                bool sourceLost=!slot.ChildSourceLossObserved && (slot.SourceWasPresent || binarySnapshot.InitialSourceObject is not null) && !sourcePresent;
                bool targetLost=!slot.ChildTargetLossObserved && slot.TargetWasPresent && !targetPresent;
                slot.ChildSourceLossObserved|=sourceLost;slot.ChildTargetLossObserved|=targetLost;
                slot.SourceWasPresent|=sourcePresent;slot.TargetWasPresent|=targetPresent;
                if(binaryRoot.NextChild is {} next)removalMarks+=MarkVisualChildrenAfterEndpointLoss(next,currentClientTickMilliseconds,sourceLost,targetLost);
                if(binaryRoot.StartChild is {} start)removalMarks+=MarkVisualChildrenAfterEndpointLoss(start,currentClientTickMilliseconds,sourceLost,targetLost);
            }
            // Retail observer 006E7ED0 retires bound effects on source
            // destruction when infinite or opted in by their authored definition.
            // Use world membership, never visibility or availability of a mesh.
            if (observesWorld && !slot.Completed && !slot.RemovalRequested &&
                slot.Node is {HasUnboundEndpoints:false} line &&
                (line.Definition.EffectClassKind is EnbEffectClassKind.LineEffect or EnbEffectClassKind.RObjLookAtEffect or EnbEffectClassKind.ParticleJetEffect or EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SpiralSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect ||
                 slot.Snapshot?.GeometryKind == EnbSceneEffectGeometryKind.Object) &&
                slot.Snapshot is { } snapshot)
            {
                bool sourceLost = (slot.SourceWasPresent || snapshot.InitialSourceObject is not null) && !sourcePresent;
                bool targetLost = slot.TargetWasPresent && !targetPresent;
                slot.SourceWasPresent |= sourcePresent;
                slot.TargetWasPresent |= targetPresent;
                bool infinite = line.FactoryInfiniteDuration;
                if ((sourceLost && (infinite || EnbRetailIniDocument.ReadBool(line.Definition.Properties, "GoAwayWhenSourceDoes"))) ||
                    (snapshot.IsObjectToObject && targetLost && (snapshot.EndWithTarget || infinite || EnbRetailIniDocument.ReadBool(line.Definition.Properties, "GoAwayWhenTargetDoes"))))
                {
                    slot.RemovalRequested = true;
                    MarkEffectTreeForRemoval(line, currentClientTickMilliseconds);
                    Remember($"{currentClientTickMilliseconds}ms endpoint lost 0x{key.EffectId:X}/g{key.InstanceGeneration}");
                    removalMarks++;
                }
            }
            if (!isPresent && !slot.RemovalRequested)
            {
                slot.RemovalRequested = true;
                Remember($"{currentClientTickMilliseconds}ms absent 0x{key.EffectId:X}/g{key.InstanceGeneration}");
                if (slot.Node is not null && !slot.Completed)
                {
                    MarkEffectTreeForRemoval(slot.Node, currentClientTickMilliseconds);
                    removalMarks++;
                }
            }

            if (slot.Node is null || slot.Completed)
            {
                if (!isPresent)
                    _roots.Remove(key);
                continue;
            }

            NodeUpdateCounters counters = default;
            int firstEvent = events.Count;
            var activatedSounds = new List<EnbCurrentRetailEffectOwner>();
            bool alive = UpdateNode(
                slot.Node,
                catalog,
                currentClientTickMilliseconds,
                slot.Snapshot!,lineFactoryRejection,
                events,
                activatedSounds,
                ref counters);
            // 006E6B52 starts SFX before the same-update duration check.
            // Cancellation is flag 0x4 (006E6E91), not SFX suppression 0x8.
            foreach (var sound in activatedSounds.Where(n => n.Status == EffectNodeStatus.Ended))
                sounds.Add(new(sound.Sequence,sound.Definition,slot.Snapshot!,sound.Binding.BoundGameId,
                    sound.LinkKind,true,sound.ActualStartTick)
                { EffectOwner=sound,EndedAtActivation=true,SourceRenderIdentity=slot.SourceRenderIdentity,TargetRenderIdentity=slot.TargetRenderIdentity });
            // Retail 006F03C0 invokes the controller on the retained +80
            // object pointer. A recycled network ID is not that object.
            for (int i = firstEvent; i < events.Count; i++)
            {
                bool source = events[i].BoundGameId == slot.Snapshot!.SourceGameId;
                events[i] = events[i] with
                {
                    BoundRenderIdentity = source ? slot.SourceRenderIdentity : slot.TargetRenderIdentity,
                    BoundCreationOrdinal = source ? slot.Snapshot.EndpointReceipt?.SourceCreationOrdinal
                        : slot.Snapshot.EndpointReceipt?.TargetCreationOrdinal,
                };
            }
            startedNodes += counters.Started;
            completedNodes += counters.Completed;
            createdStartLinks += counters.StartLinks;
            createdNextLinks += counters.NextLinks;

            if (!alive)
            {
                slot.Completed = true;
                if (!isPresent)
                    _roots.Remove(key);
            }
        }

        int scheduledNodes = 0;
        int activeNodes = 0;
        int endedWaitingForChildren = 0;
        int retiringRoots = 0;
        int completedTombstones = 0;
        int trackedDurationLinkedRoots = 0;
        string? durationLinkedExample = null;
        foreach (RootSlot slot in _roots.Values)
        {
            if (slot.DurationLinkedResolution is { Success: true } linked)
            {
                trackedDurationLinkedRoots++;
                durationLinkedExample ??= FormatDurationLinkedExample(slot.Key, linked);
            }
            if (slot.RemovalRequested)
                retiringRoots++;
            if (slot.Completed)
            {
                completedTombstones++;
                continue;
            }
            if (slot.Node is not null)
                CountNodeStates(slot.Node, ref scheduledNodes, ref activeNodes, ref endedWaitingForChildren);
        }

        var visuals = new List<EnbCurrentRetailEffectOwner>();
        foreach (var slot in _roots.Values)
            if (!slot.Completed && slot.Node is not null && slot.Snapshot is not null)
            {
                int firstSound=sounds.Count;
                CollectSounds(slot.Node,slot.Snapshot,sounds);
                for(int i=firstSound;i<sounds.Count;i++)sounds[i]=sounds[i] with
                    {SourceRenderIdentity=slot.SourceRenderIdentity,TargetRenderIdentity=slot.TargetRenderIdentity};
                CollectVisuals(slot.Node,slot.Snapshot,visuals,sourceIdentity:slot.SourceRenderIdentity,targetIdentity:slot.TargetRenderIdentity);
            }
        return new EnbCurrentRetailEffectRslSchedulerUpdate(
            events.ToArray(),
            new EnbCurrentRetailEffectRslSchedulerReceipt(
                SnapshotRoots: snapshotEffects.Count,
                TrackedRoots: _roots.Count,
                CreatedRoots: createdRoots,
                RejectedRoots: rejectedRoots,
                ResolvedDurationLinkedRoots: resolvedDurationLinkedRoots,
                RejectedDurationLinkedRoots: rejectedDurationLinkedRoots,
                TrackedDurationLinkedRoots: trackedDurationLinkedRoots,
                DurationLinkedExample: durationLinkedExample,
                RetiringRoots: retiringRoots,
                CompletedTombstones: completedTombstones,
                ScheduledNodes: scheduledNodes,
                ActiveNodes: activeNodes,
                EndedWaitingForChildren: endedWaitingForChildren,
                StartedNodes: startedNodes,
                CompletedNodes: completedNodes,
                CreatedStartLinks: createdStartLinks,
                CreatedNextLinks: createdNextLinks,
                RemovalMarks: removalMarks)) { SoundNodes = sounds.ToArray(), VisualNodes = visuals.ToArray() };
    }

    private static void CollectVisuals(EnbCurrentRetailEffectOwner node,EnbSceneEffectSnapshot root,List<EnbCurrentRetailEffectOwner> visuals,
        ulong? parentSequence=null,ulong? sourceIdentity=null,ulong? targetIdentity=null)
    {
        // Linked sounds need the unary matrix for their own position getter
        // (006E9360), as well as for forwarding to children (006E78A0).
        bool attachment=node.Definition.EffectClassKind==EnbEffectClassKind.AttachEffect;
        bool soundParent=node.Definition.EffectClassKind==EnbEffectClassKind.SoundEffect &&
            ((root.IsObjectToObject || root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint) && parentSequence.HasValue ||
             root.GeometryKind==EnbSceneEffectGeometryKind.Object);
        // Include particle jets so presentation can render or report their active stage.
        if (node.Status != EffectNodeStatus.Ended && (attachment || soundParent || node.Definition.EffectClassKind is EnbEffectClassKind.PassEffect or EnbEffectClassKind.RObjEffect or EnbEffectClassKind.WarpEffect or EnbEffectClassKind.LineEffect or EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect or EnbEffectClassKind.RObjLookAtEffect or EnbEffectClassKind.ParticleJetEffect))
        {
            uint? bound=node.HasUnboundEndpoints?null:node.Definition.EffectClassKind==EnbEffectClassKind.RObjLookAtEffect && !EnbRetailIniDocument.ReadBool(node.Definition.Properties,"SourceBased") ? root.TargetGameId : node.Binding.BoundGameId;
            bool? targetBinding=node.Definition.EffectClassKind is EnbEffectClassKind.RObjEffect or EnbEffectClassKind.PassEffect or EnbEffectClassKind.SoundEffect ? node.Binding.BoundToTarget : null;
            node.ObserveRoot(root);node.BoundGameId=bound;node.ParentSequence=parentSequence;
            node.BoundToTarget=targetBinding;
            node.BoundRenderIdentity=targetBinding==true?targetIdentity:bound==root.SourceGameId?sourceIdentity:bound==root.TargetGameId?targetIdentity:null;
            visuals.Add(node);
        }
        if (node.NextChild is not null) CollectVisuals(node.NextChild,root,visuals,node.Sequence,sourceIdentity,targetIdentity);
        if (node.StartChild is not null) CollectVisuals(node.StartChild,root,visuals,node.Sequence,sourceIdentity,targetIdentity);
    }

    private static void CollectSounds(EnbCurrentRetailEffectOwner node,EnbSceneEffectSnapshot root,List<EnbCurrentRetailEffectSoundNode> sounds)
    {
        if (node.Status != EffectNodeStatus.Ended && !string.IsNullOrWhiteSpace(node.Definition.Sfx))
            sounds.Add(new(node.Sequence,node.Definition,root,node.Binding.BoundGameId,
                node.LinkKind,node.Status == EffectNodeStatus.Active,node.ActualStartTick){EffectOwner=node});
        if (node.NextChild is not null) CollectSounds(node.NextChild,root,sounds);
        if (node.StartChild is not null) CollectSounds(node.StartChild,root,sounds);
    }

    private bool UpdateNode(
        EnbCurrentRetailEffectOwner node,
        EnbRetailPresentationCatalog catalog,
        uint now,
        EnbSceneEffectSnapshot snapshot,
        Func<EnbEffectDefinition,EnbSceneEffectSnapshot,string?>? lineFactoryRejection,
        List<EnbCurrentRetailRenderStateEffectEvent> events,
        List<EnbCurrentRetailEffectOwner> activatedSounds,
        ref NodeUpdateCounters counters)
    {
        if (node.Status == EffectNodeStatus.Scheduled && HasReached(now, node.ScheduledStartTick))
        {
            node.ActualStartTick = now;
            node.ActivationSequence = checked(++_nextActivationSequence);
            node.ReferenceStartTick = now;
            node.Status = EffectNodeStatus.Active;
            counters.Started++;
            if (!string.IsNullOrWhiteSpace(node.Definition.Sfx) &&
                !(EnbRetailIniDocument.TryReadFloat(node.Definition.Properties,"TriggerSFXRadius",out float radius) && radius>=0))
                activatedSounds.Add(node);
            RememberNode(node, now, "start");

            if (node.Definition.EffectClassKind == EnbEffectClassKind.RenderStateEffect &&
                node.Definition.RenderStateId is int renderStateId && renderStateId >= 0 &&
                node.Definition.RenderStateAction != EnbRenderStateEffectActionKind.Unknown &&
                node.Binding.BoundGameId is uint boundGameId && boundGameId != 0)
            {
                events.Add(new EnbCurrentRetailRenderStateEffectEvent(
                    Sequence: checked(++_nextEventSequence),
                    RootEffectId: node.RootEffectId,
                    RootInstanceGeneration: node.RootGeneration,
                    DefinitionId: node.Definition.DefinitionId,
                    BoundGameId: boundGameId,
                    SourceGameId: node.Binding.SourceGameId,
                    TargetGameId: node.Binding.TargetGameId,
                    RenderStateId: renderStateId,
                    ActionKind: node.Definition.RenderStateAction,
                    ActionTimeMilliseconds: node.Definition.RenderStateActionTimeMilliseconds ?? 1000,
                    ForceReactivate: node.Definition.RenderStateForceReactivate,
                    ActualStartClientTick: now,
                    LinkKind: node.LinkKind));
            }

            if (node.Definition.StartLinkId is int startLinkId && startLinkId >= 0)
            {
                EnbEffectDefinition? childDefinition = catalog.FindEffect(startLinkId);
                if (childDefinition is null || childDefinition.EffectClassKind == EnbEffectClassKind.Unknown)
                    Remember($"{now}ms StartLink {(childDefinition is null || string.IsNullOrWhiteSpace(childDefinition.EffectClass) ? "omitted" : "rejected")} E{node.Definition.DefinitionId}->E{startLinkId}: {(childDefinition is null ? "missing definition" : "unknown or empty effect class")}");
                if (childDefinition is not null && childDefinition.EffectClassKind != EnbEffectClassKind.Unknown &&
                    !RejectLineFactory(childDefinition, catalog, now,snapshot,lineFactoryRejection))
                {
                    uint forwardedDuration = 0;
                    if (node.Definition.PassDurationToStartLink)
                    {
                        forwardedDuration = node.DurationMilliseconds;
                        if (!node.Definition.OffsetDurationPassedToStartLink)
                        {
                            forwardedDuration = node.StartLinkDelayMilliseconds < forwardedDuration
                                ? forwardedDuration - node.StartLinkDelayMilliseconds
                                : 0;
                        }
                    }

                    uint childBaseStart = unchecked(now + node.StartLinkDelayMilliseconds);
                    EffectBindingContext childBinding = node.Binding.ForUnaryLink(
                        node.Definition.UnaryStartLinkToTarget,node.Definition.EffectClassKind);
                    node.StartChild = CreateNode(
                        childDefinition,
                        catalog,
                        childBinding,
                        childBaseStart,
                        node.Definition.PassDurationToStartLink ? unchecked((int)forwardedDuration) : null,
                        node.RootKey,
                        node.RootEffectId,
                        node.RootGeneration,
                        EnbCurrentRetailEffectLinkKind.StartLink,
                        node.Definition.UnaryStartLinkToTarget, factorySpeedup:node.FactorySpeedup, constructionTick: now);
                    if (node.MarkedForRemoval)
                        MarkEffectTreeForRemoval(node.StartChild, now);
                    counters.StartLinks++;
                }
            }
        }

        if (node.Status == EffectNodeStatus.Active)
        {
            uint elapsed = unchecked(now - node.ActualStartTick);
            if (node.DurationMilliseconds != uint.MaxValue && HasReached(elapsed, node.DurationMilliseconds))
            {
                node.Status = EffectNodeStatus.Ended;
                node.ClearAttachment();
                node.ClearSource();
                counters.Completed++;
                RememberNode(node, now, "end");

                if (node.Definition.NextLinkId is int nextLinkId && nextLinkId >= 0)
                {
                    EnbEffectDefinition? childDefinition = catalog.FindEffect(nextLinkId);
                    if (childDefinition is null || childDefinition.EffectClassKind == EnbEffectClassKind.Unknown)
                        Remember($"{now}ms NextLink {(childDefinition is null || string.IsNullOrWhiteSpace(childDefinition.EffectClass) ? "omitted" : "rejected")} E{node.Definition.DefinitionId}->E{nextLinkId}: {(childDefinition is null ? "missing definition" : "unknown or empty effect class")}");
                    if (childDefinition is not null && childDefinition.EffectClassKind != EnbEffectClassKind.Unknown &&
                        !RejectLineFactory(childDefinition, catalog, now,snapshot,lineFactoryRejection))
                    {
                        EffectBindingContext childBinding = node.Binding.ForUnaryLink(
                            node.Definition.UnaryNextLinkToTarget,node.Definition.EffectClassKind);
                        node.NextChild = CreateNode(
                            childDefinition,
                            catalog,
                            childBinding,
                            now,
                            null,
                            node.RootKey,
                            node.RootEffectId,
                            node.RootGeneration,
                            EnbCurrentRetailEffectLinkKind.NextLink,
                            node.Definition.UnaryNextLinkToTarget, factorySpeedup:node.FactorySpeedup, constructionTick: now);
                        // Retail link-binding virtual +24 (006E78A0 unary,
                        // 006E8B60 binary) propagates +7C bit4 to NEW children.
                        // Do not suppress links: their callbacks and authored
                        // cycle tails still run, but they cannot restart a full
                        // uncancelled chain or leave an infinite Cloak pass alive.
                        if (node.MarkedForRemoval)
                            MarkEffectTreeForRemoval(node.NextChild, now);
                        counters.NextLinks++;
                    }
                }
            }
        }

        // Retail updates the NextLink child first (+0x60), then StartLink (+0x5C).
        if (node.NextChild is not null &&
            !UpdateNode(node.NextChild, catalog, now,snapshot,lineFactoryRejection, events, activatedSounds, ref counters))
        {
            node.NextChild = null;
        }
        if (node.StartChild is not null &&
            !UpdateNode(node.StartChild, catalog, now,snapshot,lineFactoryRejection, events, activatedSounds, ref counters))
        {
            node.StartChild = null;
        }

        return node.Status != EffectNodeStatus.Ended || node.NextChild is not null || node.StartChild is not null;
    }

    private bool RejectLineFactory(EnbEffectDefinition definition, EnbRetailPresentationCatalog catalog, uint now,
        EnbSceneEffectSnapshot snapshot,Func<EnbEffectDefinition,EnbSceneEffectSnapshot,string?>? construct)
    {
        //6E9490 only installs SFX/start/next links after the class factory
        //succeeds. The renderer-owned BASE namespace must construct first,
        //including when StartDelay has not elapsed or this is a linked child.
        if(definition.EffectClassKind==EnbEffectClassKind.LineEffect && construct?.Invoke(definition,snapshot) is {} failure)
        {
            Remember($"{now}ms factory {(failure == "LineEffect BASE is not class 1C" ? "omitted" : "rejected")} E{definition.DefinitionId}: {failure}");
            return true;
        }
        // 006EACD3..D9 requires SegLine class 0x1C; NullObject returns 0x16
        // at 009414B0. Failed construction never installs start/next links.
        if (definition.EffectClassKind != EnbEffectClassKind.LineEffect ||
            definition.BaseAssetId is not int id || catalog.ResolveBaseAsset(id).Kind != EnbBaseAssetDefinitionKind.NullObject)
            return false;
        Remember($"{now}ms factory omitted E{definition.DefinitionId}: LineEffect BASE{id} is NullObject (class 0x16, requires 0x1C)");
        return true;
    }

    private EnbCurrentRetailEffectOwner CreateNode(
        EnbEffectDefinition definition,
        EnbRetailPresentationCatalog catalog,
        EffectBindingContext binding,
        uint baseStartTick,
        int? durationOverrideMilliseconds,
        EnbCurrentRetailEffectInstanceKey rootKey,
        uint rootEffectId,
        ulong rootGeneration,
        EnbCurrentRetailEffectLinkKind linkKind,
        bool linkToTarget,
        float factorySpeedup = 1f, uint constructionTick = 0)
    {
        _ = catalog;
        _ = linkToTarget;
        float rootStartDelaySeconds = MathF.Max(definition.StartDelay ?? 0f, 0f);
        uint startDelay = ToRetailMilliseconds(rootStartDelaySeconds);
        uint scheduled = unchecked(baseStartTick + startDelay);
        bool line=definition.EffectClassKind==EnbEffectClassKind.LineEffect;
        float reciprocalSpeedup=1f/factorySpeedup;
        uint duration = ResolveFactoryDuration(definition, durationOverrideMilliseconds);
        //006E97BF truncates authored milliseconds before scaling Line duration.
        if(line && duration!=uint.MaxValue && durationOverrideMilliseconds.GetValueOrDefault()==0)
            duration=unchecked((uint)(long)(duration*(double)reciprocalSpeedup));
        bool factoryInfinite=duration==uint.MaxValue;
        //006F07F0/0B10/0FD0/1760 pass1 to the binary base for zero/-1.
        //The factory still forces endpoint-removal flags from its original -1.
        if(duration is 0 or uint.MaxValue && definition.EffectClassKind is
            (EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SpiralSeekEffect or
             EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect))duration=1;
        //006E9A01 scales Line cycle seconds before millisecond truncation.
        uint cycle = ToRetailMilliseconds((definition.CycleTime ?? 0f)*(line?(double)reciprocalSpeedup:1d));
        //006E6710 stores the next complete cycle, with native DWORD overflow.
        if(cycle!=0 && duration!=uint.MaxValue)
        {
            uint whole=(duration/cycle)*cycle;
            duration=duration==0?cycle:duration>whole?unchecked(whole+cycle):whole;
        }
        uint startLinkDelay = ToRetailMilliseconds(definition.StartLinkDelay ?? 0f);
        //006E9DDB scales the already truncated DWORD only for LineEffect.
        if(line)startLinkDelay=unchecked((uint)(long)(startLinkDelay*(double)reciprocalSpeedup));
        return new EnbCurrentRetailEffectOwner(
            rootKey,
            rootEffectId,
            rootGeneration,
            definition,
            binding,
            scheduled,
            duration,
            cycle,
            startLinkDelay,
            linkKind) { Sequence = checked(++_nextNodeSequence), ConstructionTick=constructionTick,FactoryInfiniteDuration=factoryInfinite,FactorySpeedup=factorySpeedup };
    }

    internal static uint ResolveSeekConstructorDuration(EnbEffectDefinition definition, int? durationOverrideMilliseconds)
    {
        uint duration=ResolveFactoryDuration(definition,durationOverrideMilliseconds);
        //Seek subclass normalizes before006E6710 rounds to a full cycle.
        if(duration is 0 or uint.MaxValue)duration=1;
        uint cycle=ToRetailMilliseconds(definition.CycleTime??0f);
        if(cycle!=0)
        {
            uint whole=(duration/cycle)*cycle;
            duration=duration>whole?unchecked(whole+cycle):whole;
        }
        return duration;
    }

    private static uint ResolveFactoryDuration(EnbEffectDefinition definition, int? durationOverrideMilliseconds)
    {
        //006E7017 compares/subtracts unsigned duration, including FFFFFFFF.
        //006E9732 preserves every nonzero factory DWORD; zero selects the
        //child's authored duration, even when a parent explicitly forwarded it.
        if (durationOverrideMilliseconds is int durationOverride && durationOverride!=0)
            return unchecked((uint)durationOverride);
        if (definition.InfiniteDuration)
            return uint.MaxValue;
        float authored = definition.DurationValue ?? 0f;
        if (!float.IsFinite(authored))
            throw new InvalidDataException($"Effect Duration '{authored}' is not finite.");
        if (authored <= 0f)
            authored = 1.0f;
        return ToRetailMilliseconds(authored);
    }

    private static uint ToRetailMilliseconds(double seconds)
    {
        double milliseconds = (double)seconds * 1000.0;
        if (!double.IsFinite(milliseconds) || milliseconds > uint.MaxValue || milliseconds < 0)
            throw new InvalidDataException($"Effect time '{seconds}' is outside the current-retail DWORD millisecond range.");
        return unchecked((uint)(long)milliseconds);
    }

    private static bool HasReached(uint current, uint target)
        => unchecked(current - target) <= 0x7fffffffu;

    private void RememberNode(EnbCurrentRetailEffectOwner node, uint now, string edge)
        => Remember($"{now}ms {edge} 0x{node.RootEffectId:X}/g{node.RootGeneration}/n{node.Sequence} E{node.Definition.DefinitionId} {node.LinkKind} duration {node.DurationMilliseconds}ms cancel {node.MarkedForRemoval}");

    private void Remember(string entry)
    {
        if (_lifecycleHistory.Count == 64) _lifecycleHistory.Dequeue();
        _lifecycleHistory.Enqueue(entry);
        _lifecycleHistoryText = null;
    }

    private static void MarkEffectTreeForRemoval(EnbCurrentRetailEffectOwner node, uint now)
    {
        uint elapsed = unchecked(now - node.ReferenceStartTick);
        if (elapsed < 0x80000000u)
        {
            if (node.CycleMilliseconds == 0 || elapsed == uint.MaxValue)
            {
                node.DurationMilliseconds = elapsed;
            }
            else if (elapsed == 0)
            {
                node.DurationMilliseconds = node.CycleMilliseconds;
            }
            else
            {
                uint whole = (elapsed / node.CycleMilliseconds) * node.CycleMilliseconds;
                node.DurationMilliseconds = whole < elapsed
                    ? unchecked(whole + node.CycleMilliseconds)
                    : whole;
            }
        }
        else
        {
            node.DurationMilliseconds = node.CycleMilliseconds == 0 ? 0u : node.CycleMilliseconds;
        }

        node.MarkedForRemoval = true;
        if (node.StartChild is not null)
            MarkEffectTreeForRemoval(node.StartChild, now);
        if (node.NextChild is not null)
            MarkEffectTreeForRemoval(node.NextChild, now);
    }

    private static bool IsOriginalEndpointPresent(uint gameId,ulong? creationOrdinal,IEnbRenderObject? published,
        ref ulong? identity,IReadOnlyDictionary<uint,IEnbRenderObject>? objects,IReadOnlySet<uint>? ids)
    {
        if(objects is null)return ids?.Contains(gameId)==true;
        // Presentation identities track Net7 object lifetimes, not mesh/visual
        // generations. A replacement with the same GameID is not this observer's
        // original object (006F310E -> observer destruction -> 006E7690/006E7ED0).
        identity ??= published?.RenderIdentity;
        if(!objects.TryGetValue(gameId,out var current) ||
            creationOrdinal is {} ordinal && current.WorldCreationOrdinal!=ordinal ||
            identity is {} retained && current.RenderIdentity!=retained)return false;
        identity ??= current.RenderIdentity;
        return true;
    }

    private static int MarkVisualChildrenAfterEndpointLoss(EnbCurrentRetailEffectOwner node,uint now,bool sourceLost,bool targetLost)
    {
        // 006E7ED0 clears the endpoint pointer and, when its removal flag is
        // set, applies the same cycle-aware removal to that instance's subtree.
        // ParticleJet table00B00138+4 ->00412760 ->006E7ED0 shares this
        // observer; its renderer separately retains already emitted tails.
        bool binary=node.Definition.EffectClassKind is EnbEffectClassKind.LineEffect or EnbEffectClassKind.RObjLookAtEffect or EnbEffectClassKind.ParticleJetEffect or EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SpiralSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect;
        bool infinite=node.FactoryInfiniteDuration;
        // Unary 006E7690/flag100 observes the selected binding, which may be
        // the binary root's target. Factory 006E9FAB forces this flag for an
        // infinite duration, otherwise GoAwayWhenSourceDoes controls it.
        // SoundEffect table00B000F0+4 also dispatches to unary006E7690.
        bool unaryLost=node.Definition.EffectClassKind is (EnbEffectClassKind.RObjEffect or EnbEffectClassKind.PassEffect or EnbEffectClassKind.SoundEffect) &&
            (node.Binding.BoundGameId==node.Binding.SourceGameId && sourceLost ||
             node.Binding.BoundGameId==node.Binding.TargetGameId && targetLost);
        bool remove=binary && (sourceLost && (infinite || node.Definition.GoAwayWhenSourceDoes) ||
                targetLost && (infinite || EnbRetailIniDocument.ReadBool(node.Definition.Properties,"GoAwayWhenTargetDoes"))) ||
            unaryLost && (infinite || node.Definition.GoAwayWhenSourceDoes);
        if(remove && !node.MarkedForRemoval && node.Status!=EffectNodeStatus.Ended)
        {
            MarkEffectTreeForRemoval(node,now);
            return 1;
        }
        return (node.NextChild is {} next?MarkVisualChildrenAfterEndpointLoss(next,now,sourceLost,targetLost):0)+
            (node.StartChild is {} start?MarkVisualChildrenAfterEndpointLoss(start,now,sourceLost,targetLost):0);
    }

    private static string FormatDurationLinkedExample(
        EnbCurrentRetailEffectInstanceKey key,
        EnbCurrentRetailDurationLinkedEffectTimingResolution resolution)
    {
        string path = string.Join(">", resolution.TraversedDefinitionIds);
        string fallback = resolution.UsedRetailFallbackDuration ? " fallback1s" : string.Empty;
        return $"0x{key.EffectId:X8}/g{key.InstanceGeneration}:{path} off {resolution.AdditionalStartOffsetMilliseconds}ms dur {resolution.DurationMilliseconds}ms invspd {resolution.ReciprocalSpeedup:0.###}{fallback}";
    }

    private static void CountNodeStates(
        EnbCurrentRetailEffectOwner node,
        ref int scheduled,
        ref int active,
        ref int endedWaitingForChildren)
    {
        switch (node.Status)
        {
            case EffectNodeStatus.Scheduled:
                scheduled++;
                break;
            case EffectNodeStatus.Active:
                active++;
                break;
            case EffectNodeStatus.Ended:
                endedWaitingForChildren++;
                break;
        }
        if (node.NextChild is not null)
            CountNodeStates(node.NextChild, ref scheduled, ref active, ref endedWaitingForChildren);
        if (node.StartChild is not null)
            CountNodeStates(node.StartChild, ref scheduled, ref active, ref endedWaitingForChildren);
    }

    private sealed class RootSlot
    {
        public RootSlot(
            EnbCurrentRetailEffectInstanceKey key,
            EnbCurrentRetailEffectOwner? node,
            EnbCurrentRetailDurationLinkedEffectTimingResolution? durationLinkedResolution = null)
        {
            Key = key;
            Node = node;
            DurationLinkedResolution = durationLinkedResolution;
        }

        public EnbCurrentRetailEffectInstanceKey Key { get; }
        public EnbSceneEffectSnapshot? Snapshot { get; set; }
        public EnbCurrentRetailEffectOwner? Node { get; }
        public EnbCurrentRetailDurationLinkedEffectTimingResolution? DurationLinkedResolution { get; }
        public bool RemovalRequested { get; set; }
        public bool Completed { get; set; }
        public bool SourceWasPresent { get; set; }
        public bool TargetWasPresent { get; set; }
        public bool ChildSourceLossObserved { get; set; }
        public bool ChildTargetLossObserved { get; set; }
        public ulong? SourceRenderIdentity;
        public ulong? TargetRenderIdentity;

        public static RootSlot Rejected(
            EnbCurrentRetailEffectInstanceKey key,
            EnbCurrentRetailDurationLinkedEffectTimingResolution? durationLinkedResolution = null)
            => new(key, null, durationLinkedResolution) { Completed = true };
    }







    private struct NodeUpdateCounters
    {
        public int Started;
        public int Completed;
        public int StartLinks;
        public int NextLinks;
    }
}

public readonly record struct EnbCurrentRetailEffectInstanceKey(
    uint EffectId,
    ulong InstanceGeneration);

public enum EnbCurrentRetailEffectLinkKind
{
    Root,
    StartLink,
    NextLink,
}

public sealed record EnbCurrentRetailRenderStateEffectEvent(
    ulong Sequence,
    uint RootEffectId,
    ulong RootInstanceGeneration,
    int DefinitionId,
    uint BoundGameId,
    uint SourceGameId,
    uint TargetGameId,
    int RenderStateId,
    EnbRenderStateEffectActionKind ActionKind,
    int ActionTimeMilliseconds,
    bool ForceReactivate,
    uint ActualStartClientTick,
    EnbCurrentRetailEffectLinkKind LinkKind)
{
    public ulong? BoundRenderIdentity { get; init; }
    public ulong? BoundCreationOrdinal { get; init; }
}

public sealed record EnbCurrentRetailEffectRslSchedulerUpdate(
    IReadOnlyList<EnbCurrentRetailRenderStateEffectEvent> RenderStateEvents,
    EnbCurrentRetailEffectRslSchedulerReceipt Receipt)
{
    public IReadOnlyList<EnbCurrentRetailEffectSoundNode> SoundNodes { get; init; } = [];
    public IReadOnlyList<EnbCurrentRetailEffectOwner> VisualNodes { get; init; } = [];
}



public sealed record EnbCurrentRetailEffectSoundNode(ulong Sequence,EnbEffectDefinition Definition,
    EnbSceneEffectSnapshot Root,uint? BoundGameId,EnbCurrentRetailEffectLinkKind LinkKind,
    bool Active,uint ActualStartTick)
{
    public EnbCurrentRetailEffectOwner? EffectOwner {get;init;}
    public bool EndedAtActivation { get; init; }
    public ulong? SourceRenderIdentity { get; init; }
    public ulong? TargetRenderIdentity { get; init; }
    public bool ClientWarp { get; init; }
    public bool ClientAnimation { get; init; }
    public bool ClientDamage { get; init; }
    //43E530 flags2 plays/releases a voice at a captured position. Unlike an
    //ended Effect owner, release does not stop an authored continuous voice.
    public System.Numerics.Vector3? ReleasedWorldPosition { get; init; }
    public bool Doppler { get; init; }
    public float RadiusMultiplier { get; init; } = 1;
}

public sealed record EnbCurrentRetailEffectRslSchedulerReceipt(
    int SnapshotRoots,
    int TrackedRoots,
    int CreatedRoots,
    int RejectedRoots,
    int ResolvedDurationLinkedRoots,
    int RejectedDurationLinkedRoots,
    int TrackedDurationLinkedRoots,
    string? DurationLinkedExample,
    int RetiringRoots,
    int CompletedTombstones,
    int ScheduledNodes,
    int ActiveNodes,
    int EndedWaitingForChildren,
    int StartedNodes,
    int CompletedNodes,
    int CreatedStartLinks,
    int CreatedNextLinks,
    int RemovalMarks);

public sealed partial class EnbCurrentRetailEffectOwner
    {
        internal EnbCurrentRetailEffectOwner(
            EnbCurrentRetailEffectInstanceKey rootKey,
            uint rootEffectId,
            ulong rootGeneration,
            EnbEffectDefinition definition,
            EffectBindingContext binding,
            uint scheduledStartTick,
            uint durationMilliseconds,
            uint cycleMilliseconds,
            uint startLinkDelayMilliseconds,
            EnbCurrentRetailEffectLinkKind linkKind)
        {
            RootKey = rootKey;
            RootEffectId = rootEffectId;
            RootGeneration = rootGeneration;
            Definition = definition;
            Binding = binding;
            ScheduledStartTick = scheduledStartTick;
            ReferenceStartTick = scheduledStartTick;
            InitialDurationMilliseconds = DurationMilliseconds = durationMilliseconds;
            CycleMilliseconds = cycleMilliseconds;
            StartLinkDelayMilliseconds = startLinkDelayMilliseconds;
            LinkKind = linkKind;
        }

        public EnbCurrentRetailEffectInstanceKey RootKey { get; }
        public ulong Sequence { get; set; }
        public uint ConstructionTick { get; set; }
        public uint RootEffectId { get; }
        public ulong RootGeneration { get; }
        private EnbEffectDefinition definition=null!;
        public EnbEffectDefinition Definition {get=>definition;set {if(ReferenceEquals(definition,value))return;definition=value;InvalidateConstruction();}}
        internal EffectBindingContext Binding { get; }
        public uint ScheduledStartTick { get; }
        public uint ReferenceStartTick { get; set; }
        public uint ActualStartTick { get; set; }
        public ulong ActivationSequence { get; set; }
        public uint? InitialDurationMilliseconds { get; set; }
        public bool FactoryInfiniteDuration { get; set; }
        public float FactorySpeedup { get; set; } = 1f;
        public uint DurationMilliseconds { get; set; }
        public uint CycleMilliseconds { get; }
        public uint StartLinkDelayMilliseconds { get; }
        private EnbCurrentRetailEffectLinkKind linkKind;
        public EnbCurrentRetailEffectLinkKind LinkKind {get=>linkKind;set {if(linkKind==value)return;linkKind=value;InvalidateConstruction();}}
        internal EffectNodeStatus Status { get; set; }
        public bool MarkedForRemoval { get; set; }
        public EnbCurrentRetailEffectOwner? StartChild { get; set; }
        public EnbCurrentRetailEffectOwner? NextChild { get; set; }

        public EnbCurrentRetailEffectOwner(ulong Sequence,EnbEffectDefinition Definition,
            EnbSceneEffectSnapshot Root,uint? BoundGameId,bool Active,uint ActualStartTick)
        {
            this.Sequence=Sequence;this.Definition=Definition;this.Root=Root;this.BoundGameId=BoundGameId;
            this.Active=Active;this.ActualStartTick=ActualStartTick;DurationMilliseconds=uint.MaxValue;
            RootKey=new(Root.EffectId,Root.InstanceGeneration);RootEffectId=Root.EffectId;RootGeneration=Root.InstanceGeneration;
        }
        public EnbCurrentRetailAttachOwner? Attachment {get;private set;}
        public void UpdateAttachment(IReadOnlyList<IEnbRenderObject> objects)
        {
            if(Definition.EffectClassKind==EnbEffectClassKind.AttachEffect&&LinkKind==EnbCurrentRetailEffectLinkKind.Root&&Root.GeometryKind==EnbSceneEffectGeometryKind.Object)
                (Attachment??=new(this)).Update(this,objects);
        }
        public void ClearAttachment(){Attachment?.Dispose();Attachment=null;}
        private EnbSceneEffectSnapshot root=null!;
        public EnbSceneEffectSnapshot Root {get=>root;set {if(ReferenceEquals(root,value))return;root=value;InvalidateConstruction();}}
        // RegisterEffect creates a new generation for new construction inputs.
        // Subsequent network samples supply live pose observations only.
        internal void ObserveRoot(EnbSceneEffectSnapshot value)=>root=value;
        private uint? boundGameId;
        public uint? BoundGameId {get=>boundGameId;set {if(boundGameId==value)return;boundGameId=value;InvalidateConstruction();}}
        public bool Active {get=>Status==EffectNodeStatus.Active;set=>Status=value?EffectNodeStatus.Active:EffectNodeStatus.Scheduled;}
        public ulong? ParentSequence {get;set;}
        public ulong? BoundRenderIdentity {get;set;}
        private bool? boundToTarget;
        public bool? BoundToTarget {get=>boundToTarget;set {if(boundToTarget==value)return;boundToTarget=value;InvalidateConstruction();}}
        public bool UsesTargetBinding=>BoundToTarget??(BoundGameId==Root.TargetGameId&&BoundGameId!=Root.SourceGameId);
        public string? UnaryBoneName=>UsesTargetBinding?null:Root.BoneName;
        public bool HasUnboundEndpoints=>Binding.UnboundEndpoints;
        // Resource identity only, like an ordinary point effect. Never inserted
        // into the world or used as a gameplay endpoint (006E7D70).
        public EnbSceneObjectSnapshot CreateUnboundResourceOwner()
            =>new(ulong.MaxValue-Sequence,1,0,false,false,0,Definition.BaseAssetId??0,1,0,0,0,0,false,null,
                new(false,0,null),new(EnbSceneSpatialProviderKind.Fixed,
                    new(ActualStartTick,System.Numerics.Vector3.Zero,System.Numerics.Quaternion.Identity,
                        System.Numerics.Vector3.Zero,0,System.Numerics.Vector3.Zero,System.Numerics.Vector3.UnitX),null),
                null,null,[],[],[],[],[],[],null,[],null,0,0);
        public uint TravelDurationMilliseconds=>InitialDurationMilliseconds??DurationMilliseconds;
}
internal readonly record struct EffectBindingContext(
        uint SourceGameId,
        uint TargetGameId,
        bool IsBinary,
        uint? BoundGameId,
        bool BoundToTarget=false,
        bool UnboundEndpoints=false)
    {
        public static EffectBindingContext FromSnapshot(EnbSceneEffectSnapshot snapshot)
        {
            bool binary = snapshot.GeometryKind is
                EnbSceneEffectGeometryKind.ObjectToObject or
                EnbSceneEffectGeometryKind.ObjectToEffect or
                EnbSceneEffectGeometryKind.ObjectToPoint or
                EnbSceneEffectGeometryKind.ObjectToObjectDurationLinked;
            uint? bound = snapshot.SourceGameId == 0 ? null : snapshot.SourceGameId;
            return new EffectBindingContext(snapshot.SourceGameId, snapshot.TargetGameId, binary, bound);
        }

        public EffectBindingContext ForUnaryLink(bool toTarget,EnbEffectClassKind parentClass)
        {
            // 006E8B60 selects an endpoint for a binary parent. Unary006E78A0
            // forwards its own selected object; it cannot reselect the packet's
            // source when a target-bound child's Next/Start link is constructed.
            bool binaryParent=parentClass is EnbEffectClassKind.LineEffect or EnbEffectClassKind.RObjLookAtEffect or
                EnbEffectClassKind.ParticleJetEffect or EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SpiralSeekEffect or
                EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect;
            uint selected = binaryParent && IsBinary ? (toTarget?TargetGameId:SourceGameId) : BoundGameId.GetValueOrDefault();
            return this with { BoundGameId = selected == 0 ? null : selected,
                BoundToTarget=binaryParent && IsBinary?toTarget:BoundToTarget };
        }
    }
internal enum EffectNodeStatus
    {
        Scheduled,
        Active,
        Ended,
    }
