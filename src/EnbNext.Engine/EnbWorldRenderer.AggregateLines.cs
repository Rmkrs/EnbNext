using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<LiveParticleEmitterKey, AggregateLineInstance> _aggregateLines = [];
    private readonly HashSet<LiveParticleEmitterKey> _aggregateLineBindings = [];
    private readonly List<string> _aggregateLineErrors = [];
    private long _aggregateLineTime;
    private long _aggregateLinePreparations;
    private sealed record AggregateLineMember(int Ordinal,EnbW3dRenderHlodMember Member,EnbW3dExternalRenderObject External);
    private sealed record AggregateLinePreparation(EnbW3dRenderResource Resource,EnbW3dRenderHlod Hlod,
        bool UnitScale,EnbW3dRenderHierarchy? Hierarchy,AggregateLineMember[] Members);

    private AggregateLinePreparation PrepareAggregateLines(LiveRootInstance root,EnbW3dRenderResource resource,EnbW3dRenderHlod hlod)
    {
        bool unitScale=root.RenderScale==1;
        if(root.AggregateLines is {} prior && ReferenceEquals(prior.Resource,resource) &&
            ReferenceEquals(prior.Hlod,hlod) && prior.UnitScale==unitScale)return prior;
        var hierarchy=resource.Hierarchies.FirstOrDefault(h=>h.Name.Equals(hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
        var members=new List<AggregateLineMember>();
        if(hierarchy is not null && hlod.Aggregate is {} aggregate)
            for(int ordinal=0;ordinal<aggregate.Members.Count;ordinal++)
            {
                var member=aggregate.Members[ordinal];
                if(member.ReferenceIndex>int.MaxValue)continue;
                foreach(var external in resource.ExternalRenderObjects)
                {
                    if(!external.Name.Equals(member.Name,StringComparison.OrdinalIgnoreCase) ||
                        external.ParticleEmitterDefinition is not {} definition || !external.HasReadyParticleTexture)continue;
                    if(definition.RenderMode!=2 && !(unitScale && definition.RenderMode is 0 or 1 &&
                        !definition.LocalSpace && definition.MaximumEmissions==0 && definition.InitialEmissionTimeSeconds==0))continue;
                    members.Add(new(ordinal,member,external));break;
                }
            }
        _aggregateLinePreparations++;
        return root.AggregateLines=new(resource,hlod,unitScale,hierarchy,members.ToArray());
    }

    // Negative ordinals identify the permanent aggregate list, independently of
    // the selected level's member count. Ordinary level emitters keep their path.
    private LiveParticleEmitterKey AggregateLineKey(LiveRootInstance root, string resource, int pivot, int ordinal) =>
        new((_shipExhaustOwner ?? this)._assetGeneration, root.Object.RenderIdentity, root.Object.VisualGeneration,
            root.Root.Resource.Key, root.InstancePath, resource, pivot, -1 - ordinal);

    private void UpdateAggregateLines(long now, RootTaskList roots)
    {
        if (_shipExhaustOwner is not null) return;
        if (now < _aggregateLineTime) _aggregateLines.Clear();
        _aggregateLineTime = now;
        _aggregateLineBindings.Clear();
        _aggregateLineErrors.Clear();
        foreach (var root in roots)
        {
            if (root.Object.ObjectType is 0 or 1 || root.OrbitalCamera.HasValue ||
                root.HierarchyPostTransforms is not null || root.Root.Hlod is not { Aggregate: { } aggregate } hlod)
                continue;
            var resource = ConfiguredHierarchyResource(root.Root,root.AnimationBlend?.DrawResource);
            var prepared=PrepareAggregateLines(root,resource,hlod);
            var hierarchy=prepared.Hierarchy;
            if(hierarchy is null)continue;
            foreach(var preparedMember in prepared.Members)
            {
                int ordinal=preparedMember.Ordinal;
                var member=preparedMember.Member;
                var external=preparedMember.External;
                int pivot = (int)member.ReferenceIndex;
                var key = AggregateLineKey(root, external.ResourceName, pivot, ordinal);
                if (!_aggregateLines.TryGetValue(key, out var instance))
                {
                    EnbCurrentRetailShipParticleRuntime buffer;
                    try { buffer = new(external.ParticleEmitterDefinition!, allowInheritedMotion: true); }
                    catch (InvalidDataException) { continue; } // Keep the existing explicit draw rejection.
                    instance = new(buffer);
                    _aggregateLines.Add(key, instance);
                }
                _aggregateLineBindings.Add(key);
                try
                {
                    Matrix4x4 world;
                    if (root.WorldTransformOverride is { } attached) world = attached;
                    else if (!TryBuildObjectWorld(root.Object, out world)) throw new InvalidDataException("invalid owner transform");
                    var samples = root.AnimationBlend?.ForHierarchy(hierarchy.Name) ?? [];
                    var post=ConfiguredHierarchyPostTransforms(root);
                    EnbW3dHierarchyPoseOwner pose;
                    string? error;
                    bool ready=TryResolveRetainedHierarchyPose(root,hierarchy,post?.RigidWorldTransform??world,
                        post?.HierarchyTranslationScale,samples,post?.Transforms,out pose,out error);
                    if(!ready)throw new InvalidDataException(error);
                    if (pivot >= pose.WorldTransforms.Length) throw new InvalidDataException("aggregate pivot outside hierarchy");
                    instance.Buffer.Advance(now, pose.WorldTransforms[pivot], pose.Visibility[pivot]);
                    instance.Emitter = new(member.Name, external.ResourceName, root.Object.GameId, EnbRenderPassKind.OrdinaryWorld,
                        external.ParticleEmitterDefinition!, external.ParticleTextureLocation!, instance.Buffer.CurrentWorld,
                        hierarchy.Name, pivot, -1 - ordinal)
                    { RetainedTexture = external.ParticleRetainedTexture,
                      RuntimeTextureName = external.ParticleRetainedTexture?.LogicalName };
                }
                catch (InvalidDataException e)
                {
                    _aggregateLineErrors.Add($"Aggregate line '{member.Name}': {e.Message}");
                    instance.Buffer.Advance(now, instance.Buffer.CurrentWorld, false);
                }
            }
        }
        foreach (var (key, instance) in _aggregateLines.ToArray())
        {
            if (_aggregateLineBindings.Contains(key)) continue;
            // 0097D820 -> 00960CF0 detaches the emitter; 0095C310 waits for
            // the independent buffer to empty. Draw retained tails below.
            instance.Buffer.Advance(now, instance.Buffer.CurrentWorld, false);
            if (instance.Buffer.Births.Count == 0) _aggregateLines.Remove(key);
        }
    }

    private bool IsRetainedAggregateLine(LiveRootInstance root, EnbW3dHlodSelection? selection,
        EnbW3dParticleEmitterDrawInstance emitter)
    {
        if (selection is not { } selected || root.Root.Hlod is not { } hlod) return false;
        var level = hlod.Levels.FirstOrDefault(l => l.Ordinal == selected.LevelOrdinal);
        if (level is null || emitter.BindingOrdinal < level.Members.Count) return false;
        var key = AggregateLineKey(root, emitter.EmitterResourceName, emitter.PivotIndex, emitter.BindingOrdinal - level.Members.Count);
        return (_shipExhaustOwner ?? this)._aggregateLineBindings.Contains(key);
    }

    private void DrawAggregateLines(List<EnbW3dDrawTask> packets, LiveFrame frame, EnbW3dLightEnvironment lighting,
        ref int emitters, ref int particles, ref int rejected, ref string? failure)
    {
        var source = _shipExhaustOwner ?? this;
        rejected += source._aggregateLineErrors.Count;
        failure ??= source._aggregateLineErrors.FirstOrDefault();
        foreach (var (key, instance) in source._aggregateLines)
        {
            if (instance.Emitter is not { } emitter || (_shipExhaustOwner is not null &&
                !SceneObjects(frame.Snapshot).Any(o => o.RenderIdentity == key.RenderIdentity && o.VisualGeneration == key.VisualGeneration))) continue;
            emitters++;
            if (!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(key.ResourceKey, key.RenderIdentity, key.VisualGeneration,
                emitter, frame.Camera, lighting, ComputeStableParticleEmitterSalt(key.InstancePath), 0, frame.CurrentClientTime,
                out var packet, out var receipt, out var error, worldBirths: instance.Buffer.Births,
                activeEmissionRun: instance.Buffer.IsEmitting ? instance.Buffer.EmissionRun : null,
                motionHistory:instance.Buffer.MotionHistory,cullToCamera:FrustumCullingEnabled))
            { rejected++; failure ??= error; continue; }
            particles += receipt.LiveParticleCount;
            if (packet is not null && (!FrustumCullingEnabled || EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(frame.Camera, packet.Bounds)))
                packets.Add((packet).ForkTask().SetRenderInstancePath($"aggregate-line/{key.InstancePath}/{key.BindingOrdinal}"));
        }
    }

    private sealed class AggregateLineInstance(EnbCurrentRetailShipParticleRuntime buffer)
    {
        public EnbCurrentRetailShipParticleRuntime Buffer { get; } = buffer;
        public EnbW3dParticleEmitterDrawInstance? Emitter { get; set; }
    }
}
