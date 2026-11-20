using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(uint, ulong, ulong), EffectParticleInstance> _effectParticleInstances = [];

    private void ApplyEffectParticles(List<EnbW3dDrawTask> packets, HashSet<(uint, ulong, ulong)> live,
        EnbW3dLightEnvironment lights, EnbResolvedRenderCamera camera, uint now, ref int draws, ref int blocked, ref string? failure)
    {
        bool retainGeometry=RetainEffectParticleStorage;
        foreach (var (key, entry) in _effectParticleInstances.ToArray())
        {
            long age = entry.Elapsed.Observe(now);
            bool active = live.Contains(key);
            long end = entry.Aggregate?.Max(p => p.EndMilliseconds) ?? entry.Asset.EndMilliseconds;
            if (entry.AnimatedAsset is not null)
                end=active ? long.MaxValue : entry.Instance.AnimatedParticles.LastAge+(long)((double)entry.Aggregate!.Max(p=>p.Definition.LifetimeSeconds)*1000);
            var continuous=entry.Instance.ContinuousParticles;
            if (continuous is not null && !active)
                continuous.Advance(unchecked((uint)age),System.Numerics.Matrix4x4.Identity,false);
            if (age >= end || (continuous is not null && !active && continuous.Births.Count==0) ||
                (!active && continuous is null && entry.Aggregate is null && entry.Instance.Birth.BirthWorld is null))
            { entry.Instance.ParticlesComplete=true; _effectParticleInstances.Remove(key); continue; }
            try
            {
                if (entry.Aggregate is { } aggregate)
                {
                    for (int i=0; i<aggregate.Count; i++)
                    {
                        var asset=aggregate[i];
                        var retained=entry.AnimatedAsset is null ? null : entry.Instance.AnimatedParticles.Emitters[i];
                        bool line=asset.Definition.RenderMode==2;
                        var child=new EnbW3dParticleEmitterDrawInstance(asset.Definition.Name, asset.Definition.Name,
                            entry.Owner.GameId, EnbRenderPassKind.OrdinaryWorld, asset.Definition, asset.Texture,
                            (line || asset.Definition.LocalSpace) && retained is not null ? retained.CurrentWorld :
                            line || asset.Definition.LocalSpace ? entry.Instance.Poses.LatestWorld(entry.Instance.Radius) : System.Numerics.Matrix4x4.Identity, "", 0, i)
                            { RuntimeTextureName=asset.RuntimeTextureName, RetainedTexture=asset.RetainedTexture };
                        if (!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key, entry.Owner.RenderIdentity, entry.Owner.VisualGeneration,
                            child, camera, lights, unchecked((uint)entry.Node.Sequence), 0, age, out var childPacket, out _, out var childError,
                            particleSizeScale: entry.Instance.Radius,
                            birthWorld: birth => retained is null ? entry.Instance.Poses.Resolve(birth, entry.Instance.Radius) : retained.Worlds[birth],
                            emissionStopMilliseconds: active ? null : entry.Instance.Poses.LastAge,
                            retainedBirths: retained?.Births,
                            worldBirths: retained?.WorldBirths,
                            activeEmissionRun: line && active && (retained is null || retained.HasLineEndpoint) ? retained?.EmissionRun??0 : null,
                            inheritedVelocity: retained is null ? birth => entry.Instance.Poses.ResolveInheritedVelocity(birth, asset.Definition.VelocityInheritanceFactor) : null,
                            motionHistory:retained?.MotionHistory,randomHistory:entry.Replay?[i],cullToCamera:FrustumCullingEnabled,
                            frameStreams:retainGeometry?_particleStreamFrame:null,geometryOwner:retainGeometry?entry.Geometry(i):null))
                            throw new InvalidDataException(childError);
                        if (childPacket is not null)
                        {
                            packets.Add((childPacket).ForkTask().SetRenderInstancePath($"effect-particle/{key.Item1}/{key.Item2}/{key.Item3}/{i}"));
                            if(retainGeometry)_retainedEffectParticlePackets++;
                            draws++;
                        }
                    }
                    if (entry.AnimatedAsset is null) entry.Instance.Poses.RetainLatestSample();
                    continue;
                }
                if (continuous is null && !entry.Instance.Birth.Capture(unchecked((uint)age), entry.Asset.BirthMilliseconds, entry.Instance.Radius)) continue;
                var emitter = new EnbW3dParticleEmitterDrawInstance(entry.Asset.Definition.Name, entry.Asset.Definition.Name,
                    entry.Owner.GameId, EnbRenderPassKind.OrdinaryWorld, entry.Asset.Definition, entry.Asset.Texture,
                    (entry.Asset.Definition.LocalSpace || entry.Asset.Definition.RenderMode==2) && continuous is not null
                        ? continuous.CurrentWorld
                        : entry.Instance.Birth.BirthWorld ?? System.Numerics.Matrix4x4.Identity, "", 0, 0)
                        { RuntimeTextureName=entry.Asset.RuntimeTextureName, RetainedTexture=entry.Asset.RetainedTexture };
                if (!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(entry.Asset.Key, entry.Owner.RenderIdentity, entry.Owner.VisualGeneration,
                        emitter, camera, lights, unchecked((uint)entry.Node.Sequence), 0, age, out var packet, out _, out var error,
                        particleSizeScale: entry.Instance.Radius,
                        worldBirths: continuous?.Births,
                        activeEmissionRun:continuous?.IsEmitting==true?continuous.EmissionRun:null,
                        motionHistory:continuous?.MotionHistory,randomHistory:entry.Replay?[0],cullToCamera:FrustumCullingEnabled,
                        frameStreams:retainGeometry?_particleStreamFrame:null,geometryOwner:retainGeometry?entry.Geometry(0):null))
                    throw new InvalidDataException(error);
                if (packet is not null)
                {
                    packets.Add((packet).ForkTask().SetRenderInstancePath($"effect-particle/{key.Item1}/{key.Item2}/{key.Item3}"));
                    if(retainGeometry)_retainedEffectParticlePackets++;
                    draws++;
                }
            }
            catch (Exception ex) { blocked++; failure ??= $"E{entry.Node.Definition.DefinitionId}: {ex.Message}"; }
        }
    }

    private sealed record EffectParticleInstance(EnbCurrentRetailEffectOwner Node, IEnbRenderObject Owner,
        EnbCurrentRetailRObjParticleAsset Asset, EffectObjectInstance Instance,
        IReadOnlyList<EnbCurrentRetailRObjParticleAsset>? Aggregate = null,
        EnbCurrentRetailRObjEffectAsset? AnimatedAsset = null)
    {
        private EnbCurrentRetailParticleEmitterRuntime.GeometryOwner[]? _geometry;
        public EnbCurrentRetailParticleEmitterRuntime.GeometryOwner Geometry(int index)
            =>(_geometry??=(Aggregate??new[]{Asset}).Select(p=>new EnbCurrentRetailParticleEmitterRuntime.GeometryOwner(p.Definition)).ToArray())[index];
        public EnbParticleElapsedTime Elapsed { get; } = new(Node.ActualStartTick);
        public EnbRetailParticleReplayHistory[]? Replay {get;} = AnimatedAsset is null && Instance.ContinuousParticles is null
            ? (Aggregate??new[]{Asset}).Select(p=>new EnbRetailParticleReplayHistory(p.Definition)).ToArray() : null;
    }
}
