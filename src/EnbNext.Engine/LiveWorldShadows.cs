using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

using EnbNext.Rendering.OpenGL;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class LiveSimpleTerrainShadow(ulong identity, ulong generation)
    {
        public ulong Identity { get; } = identity;
        public ulong Generation { get; } = generation;
        public EnbCurrentRetailShadowSourceProjection Source { get; } = new();
        public bool Dynamic { get; set; }
        public bool SceneAttached { get; set; }
        public bool TextureInitialized { get; set; }
        public EnbOpenGlRenderTexture? Target { get; set; }
        public bool HasBlob { get; set; }
        public string CaptureTextureName { get; set; } = "";
        public string? Diagnostic { get; set; }
    }

    private readonly Dictionary<(ulong Identity, ulong Generation), LiveSimpleTerrainShadow> _simpleTerrainShadows = [];
    private readonly EnbCurrentRetailShadowSourceRegistry<LiveSimpleTerrainShadow> _simpleTerrainShadowRegistry = new();
    private IReadOnlyList<LiveSimpleTerrainShadow> _terrainSimpleShadowSources = [];
    private (uint Sector, long Generation)? _simpleTerrainShadowScene;
    private Vector3 _terrainProjectorDirection = Vector3.UnitZ;

    private void UpdateSimpleTerrainShadowSources(LiveFrame frame, EnbRetailAssetBundle assets,
        IReadOnlyList<LiveRootInstance> roots, EnbRetailTerrainLightState? light, IReadOnlySet<ulong> radarDetachedIdentities)
    {
        var scene = _useMainViewEnvironment && !frame.Snapshot.IsTransitioning &&
            frame.Snapshot.Environment is { PresentationMode: 1 } environment
            ? ((uint Sector, long Generation)?)(environment.SectorNum, _assetGeneration) : null;
        if (scene != _simpleTerrainShadowScene)
        {
            _simpleTerrainShadows.Clear();
            _simpleTerrainShadowRegistry.Clear();
            _terrainSimpleShadowSources = [];
            _terrainProjectorDirection = Vector3.UnitZ;
            _simpleTerrainShadowScene = scene;
        }
        if (scene is null) return;
        // 0050FED0..0050FF35: disabled lighting retains the manager direction.
        if (light is { Enabled: true }) _terrainProjectorDirection = -light.Direction;
        var present = frame.Snapshot.Objects.Select(obj=>(obj.RenderIdentity,obj.RenderObjectGeneration)).ToHashSet();
        foreach (var obj in frame.Snapshot.Objects)
        {
            var key = (obj.RenderIdentity, obj.RenderObjectGeneration);
            if (obj.Spatial is null || obj.ObjectType is 0x08 or 0x0A ||
                radarDetachedIdentities.Contains(obj.RenderIdentity))
            {
                //495160 removes manager membership, not wrapper+C8 ownership.
                if(_simpleTerrainShadows.TryGetValue(key,out var detached) && detached.SceneAttached)
                {
                    _simpleTerrainShadowRegistry.Remove(detached);
                    detached.SceneAttached=false;
                }
                continue;
            }
            var resolved = ResolveBaseAsset(assets.BaseAssetResolver!, obj.BaseAsset);
            var rsl = resolved.Rsl;
            if (rsl?.Library?.Flags is not { ShadowSource: true } flags) continue;
            //495430 retains wrapper+C8; attachment notifications do not replace it.
            if (!_simpleTerrainShadows.TryGetValue(key, out var source))
            {
                source = new(obj.RenderIdentity, obj.RenderObjectGeneration);
                source.Dynamic = !flags.SimpleShadow;
                source.HasBlob = flags.SimpleShadow;
                _simpleTerrainShadows.Add(key, source);
            }
            if(!source.SceneAttached)
            {
                _simpleTerrainShadowRegistry.Register(source, flags.HintStationary);
                source.SceneAttached=true;
            }
            source.Source.NotifySpaceObjectUpdate(obj.ObjectType, hasRenderObject:true);
            var instance = FindCurrentShadowSourceRoot(obj, rsl, roots, out var state);
            // A retained RSL wrapper can temporarily have no current child.
            // Preserve its projector and pending invalidation across that state.
            if (instance?.CurrentRetailLocalBox is not {} bounds || instance.CurrentRetailBoundsWorld is not {} world ||
                state is null) continue;
            source.Source.Update(bounds, world, _terrainProjectorDirection, 64, state.Definition.Flags.InstanceData);
        }
        foreach (var key in _simpleTerrainShadows.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            _simpleTerrainShadowRegistry.Remove(_simpleTerrainShadows[key]);
            _simpleTerrainShadows.Remove(key);
        }
        // 0050FED8 gates scene+68, not the RSL transform/update above.
        // 0075F8B0 rebuilds terrain membership only on that call. Terrain owns
        // projector references (0047F0E0), even after their source leaves the
        // manager, until the next enabled update or terrain destruction.
        if (light is { Enabled: true })
            _terrainSimpleShadowSources = _simpleTerrainShadowRegistry.SnapshotTerrainSources();
    }

    // 0045DBB0 follows the outer RSL's current state+B4, then that state's
    // RenderObj accessor+20. Other active states and visible-root order do not
    // select this child; neither does outer scene/first-person visibility.
    private LiveRootInstance? FindCurrentShadowSourceRoot(IEnbRenderObject obj,
        EnbResolvedRslReference rsl, IReadOnlyList<LiveRootInstance> roots,
        out EnbResolvedRslStateResource? state)
    {
        state = null;
        if (!_frameRslObjectUpdates.TryGetValue(obj.RenderIdentity, out var update) ||
            update.CurrentObjectStateId is not int stateId || rsl.ResourceGraph is not {} graph ||
            !graph.TryFindState(stateId, out var currentState)) return null;
        state = currentState;
        if (!_rslObjectPrototypes.TryGetValue(currentState,out var prototype) ||
            prototype.Root is null) return null;
        //45DBB0 follows the live copied state, whose +20 getter returns its
        //retained factory object. The template pointer is not that instance.
        var currentRoot=ResolveEffectRslRoot(obj,stateId,prototype.Root);
        return roots.FirstOrDefault(root => root.IsRsl && root.InstancePath == "root" &&
            root.Object.RenderIdentity == obj.RenderIdentity && root.Object.VisualGeneration == obj.VisualGeneration &&
            ReferenceEquals(root.Root,currentRoot));
    }

    private void AppendSimpleTerrainShadowPackets(LiveFrame frame, EnbRetailAssetBundle assets,
        List<EnbW3dDrawTask> packets)
    {
        if (_terrain is not {} terrain) return;
        foreach (var source in _terrainSimpleShadowSources)
        {
            if (source.Source.Projection is not {} projection || source.Dynamic && source.Target is null && !source.HasBlob) continue;
            // The GPU applies state47 before logarithmic depth encoding, so477E20 keeps its
            // original receiver vertices rather than taking the unsupported +3 branch.
            var mesh = EnbRetailTerrainProjectorGeometry.BuildMesh(terrain, projection, true,
                new(0,source.Target is null ? "shadow.tga" : DynamicShadowTextureName(source),null) { ResolveLogicalNameAtRuntime=true },
                new(1,"MultProjectorGradient.tga",null) { ResolveLogicalNameAtRuntime=true }, .75f);
            var pass = mesh.MaterialPasses[0];
            var order = new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld, EnbW3dDrawBlendClass.Blended,
                EnbW3dStaticSortSemantics.FromAuthoredValue(0),
                new("scene-simple-shadow",assets.Paths.RootDirectory,0,0),0,0,source.Identity,0);
            packets.Add(new(order,source.Identity,source.Generation,0,Matrix4x4.Identity,
                new(mesh.BoundingSphereCenter,mesh.BoundingRadius),mesh,
                new EnbW3dDeformedGeometry(mesh.Positions.ToImmutableArray(),mesh.Normals.ToImmutableArray()),
                new(pass,pass.SingleBindingSemantics!.Value),EnbW3dLightEnvironment.BlackNoLights,
                frame.CurrentClientTime,null,0,"scene simple shadow")
            {
                CurrentRetailEffectProjection=projection.TextureFromWorld,
                CurrentRetailEffectProjection1=projection.GradientFromWorld,
                CurrentRetailDepthBias=mesh.CurrentRetailDepthBias,
                RenderInstancePath=source.Target is null ? "scene-simple-shadow" : "scene-dynamic-shadow",
            });
        }
    }
}
