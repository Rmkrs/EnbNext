using System.Collections.Immutable;
using System.Numerics;

using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // The effective descriptor is compiled once by the ordinary appearance
    // path. Capture borrows it, its mapper references and its model texture
    // leases. No descriptor/model copy is performed by a camera draw.
    private readonly List<EnbCurrentRetailMeshAppearance> _shadowAppearanceModels = [];
    private readonly EnbCurrentRetailMeshBatchState _shadowMeshBatches = new();
    private readonly Dictionary<(ulong Identity, ulong Generation),
        Dictionary<LiveMeshAppearanceSlot, EnbCurrentRetailMeshAppearance>> _meshAppearanceBindings = [];
    private readonly record struct LiveMeshAppearanceSlot(EnbW3dRenderMesh Mesh, string Path, int Binding)
    {
        public bool Equals(LiveMeshAppearanceSlot other) => ReferenceEquals(Mesh, other.Mesh) && Path == other.Path && Binding == other.Binding;
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Mesh), Path, Binding);
    }
    private EnbCurrentRetailMeshBatchState CameraMeshBatches => (_sharedMaterialOwner ?? this)._shadowMeshBatches;
    private readonly HashSet<LiveSimpleTerrainShadow> _shadowTargetOwners = [];
    private readonly Dictionary<string, EnbOpenGlRenderTexture> _shadowTargetsByName = new(StringComparer.Ordinal);
    private ulong _nextShadowTargetIdentity;
    private HashSet<LiveRootInstance> _frameShadowCaptureRoots = [];
    private readonly Dictionary<LiveSimpleTerrainShadow, Dictionary<LiveGpuKey, EnbOpenGlStage0TextureMeshResource>> _shadowCaptureResources = [];

    private void ReleaseShadowCaptureResources(EnbGlApi? gl, LiveSimpleTerrainShadow source)
    {
        if (!_shadowCaptureResources.Remove(source, out var resources)) return;
        foreach (var resource in resources.Values) _texturedBackend.DestroyResource(gl, resource);
    }

    private static string DynamicShadowTextureName(LiveSimpleTerrainShadow source)
        => source.CaptureTextureName;

    private HashSet<LiveRootInstance> FindDynamicShadowRoots(LiveFrame frame,
        EnbRetailAssetBundle assets, IReadOnlyList<LiveRootInstance> roots)
    {
        var result = _frameShadowCaptureRoots = [];
        if ((DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Shadows) || !_useMainViewEnvironment || frame.Snapshot.IsTransitioning ||
            frame.Snapshot.Environment is not { PresentationMode: 1 }) return result;
        foreach (var obj in frame.Snapshot.Objects)
        {
            var rsl = ResolveBaseAsset(assets.BaseAssetResolver!, obj.BaseAsset).Rsl;
            if (rsl?.Library?.Flags is not { ShadowSource: true, SimpleShadow: false }) continue;
            if (FindCurrentShadowSourceRoot(obj, rsl, roots, out _) is not {} current) continue;
            foreach (var root in roots)
                // Actual aggregate/Lego/fixed parents, never object ID/path prefix.
                for (var parent = root; parent is not null; parent = parent.Parent)
                    if (ReferenceEquals(parent, current)) { result.Add(root); break; }
        }
        return result;
    }

    private void RetainShadowAppearanceLegacy(List<EnbW3dDrawTask> packets, int start, uint clock)
    {
        var appearanceOwner = _sharedMaterialOwner ?? this;
        // Group indices so retained packets can replace their exact slots.
        // The previous per-packet FindIndex scanned the whole appended range.
        foreach (var group in Enumerable.Range(start, packets.Count - start)
            .Where(index=>packets[index].SpecialMaterial is null)
            .GroupBy(index => (packets[index].Mesh, packets[index].OrderKey.BindingOrdinal - packets[index].Material.Source.Ordinal)))
        {
            var first = packets[group.First()];
            var mesh = first.Mesh;
            EnbCurrentRetailMeshAppearance? entry = null;
            foreach(var candidate in appearanceOwner._shadowAppearanceModels)
            {
                if(!ReferenceEquals(candidate.Mesh,mesh)||candidate.Passes.Length!=group.Count())continue;
                int ordinal=0;bool matches=true;
                foreach(int index in group)
                    if(!ReferenceEquals(candidate.Passes[ordinal++],packets[index].Material.Source)){matches=false;break;}
                if(matches){entry=candidate;break;}
            }
            if (entry is null)
            {
                var passes = group.Select(index => packets[index].Material.Source).ToArray();
                bool effectLocal=(_shipExhaustOwner??this)._effectRslMeshModels.TryGetValue(mesh,out var configured);
                if(effectLocal || _configuredDirectMeshModels.TryGetValue(mesh,out configured) ||
                    _shipExhaustOwner is {} primary && primary._configuredDirectMeshModels.TryGetValue(mesh,out configured))
                {
                    var retained=configured!.CreateAppearance(mesh,passes);
                    entry=retained with {Owner=retained.Owner.CloneRenderObject(),PrototypeOwned=!effectLocal};
                }
                else
                {
                var materials = passes.SelectMany(pass => pass.VertexMaterials.Select(b => b.Material)
                    .Concat(pass.TriangleBindings.Where(b => b.VertexMaterial.HasValue).Select(b => b.VertexMaterial!.Value.Material))
                    .Concat(pass.CurrentRetailUniformMaterial is {} uniform ? [uniform.Material] : Array.Empty<EnbW3dRenderVertexMaterial>()));
                if (!_prototypeResources.TryRetainMaterialMappers(materials, clock, out var mappers, out _)) continue;
                // Retain the actual post-mutation table, including unused entries
                // and duplicate pointers. Swatch/default descriptor references
                // do not become loaded entries merely because they are drawn.
                var loadedMaterials = first.CurrentRetailLoadedMaterials ?? mesh.CurrentRetailLoadedMaterials;
                if (!appearanceOwner._shadowMeshBatches.TryCreateModel(_prototypeResources, mesh, loadedMaterials, clock, out var owner, out _,first.CurrentRetailLoadedTextures)) continue;
                entry = new(mesh, passes, owner, mappers, passes.All(pass =>
                    mesh.MaterialPasses.Any(original => ReferenceEquals(original, pass)) || _overbrightMaterials.OwnsDescriptor(pass)));
                }
                appearanceOwner._shadowAppearanceModels.Add(entry);
            }
            var identity = (first.RenderIdentity, first.VisualGeneration);
            if (!appearanceOwner._meshAppearanceBindings.TryGetValue(identity, out var bindings))
                bindings=appearanceOwner.CreateMeshAppearanceBindings(identity);
            appearanceOwner.BindMeshAppearance(bindings,new(mesh, first.RenderInstancePath, group.Key.Item2),entry);
            var invocation = EnbCurrentRetailMeshInvocation.Create(first, entry);
            foreach (int index in group)
            {
                var packet=packets[index];
                var retained = (packet).ForkTask().SetCurrentRetailMaterialMappers(entry.Mappers).SetCurrentRetailMeshInvocation(invocation);
                packets[index] = retained;
            }
        }
    }

    private readonly HashSet<LiveSimpleTerrainShadow> _shadowRetainedSources=[];
    private readonly HashSet<(ulong Identity,ulong Generation)> _shadowLiveAppearanceOwners=[];
    private readonly EnbMeshAppearanceMembership _appearanceMembership=new();
    private void BindMeshAppearance(Dictionary<LiveMeshAppearanceSlot,EnbCurrentRetailMeshAppearance> bindings,
        LiveMeshAppearanceSlot slot,EnbCurrentRetailMeshAppearance appearance)
    {
        if (bindings.TryGetValue(slot,out var previous))
        {
            if (ReferenceEquals(previous,appearance)) return;
            _appearanceMembership.Release(previous);
        }
        _appearanceMembership.Retain(appearance);
        bindings[slot]=appearance;
    }
    private readonly HashSet<(ulong Identity,ulong Generation)> _unownedMeshAppearanceBindings=[];
    private readonly List<Dictionary<LiveMeshAppearanceSlot,EnbCurrentRetailMeshAppearance>> _retiredMeshAppearanceBindings=[];
    private Dictionary<LiveMeshAppearanceSlot,EnbCurrentRetailMeshAppearance> CreateMeshAppearanceBindings((ulong Identity,ulong Generation) identity)
    {
        var bindings=new Dictionary<LiveMeshAppearanceSlot,EnbCurrentRetailMeshAppearance>();
        _meshAppearanceBindings.Add(identity,bindings);
        // Initial binding only. Ordinary membership subsequently retires through
        // its source notification, independently of camera/LOD visibility.
        if(_ownedScene is null || !_ownedScene.TryFind(identity.Identity,out var node) ||
            node.Definition.VisualGeneration!=identity.Generation)_unownedMeshAppearanceBindings.Add(identity);
        return bindings;
    }
    private void DetachOwnedMeshAppearances(IEnbRenderObject definition)
    {
        var identity=(definition.RenderIdentity,definition.VisualGeneration);
        if(_meshAppearanceBindings.Remove(identity,out var bindings))_retiredMeshAppearanceBindings.Add(bindings);
        _unownedMeshAppearanceBindings.Remove(identity);
    }
    private void DrainDetachedMeshAppearances()
    {
        foreach(var bindings in _retiredMeshAppearanceBindings)
            foreach(var appearance in bindings.Values)_appearanceMembership.Release(appearance);
        _retiredMeshAppearanceBindings.Clear();
    }
    private void ReleaseMeshAppearanceOwner((ulong Identity,ulong Generation) identity)
    {
        _unownedMeshAppearanceBindings.Remove(identity);
        if (!_meshAppearanceBindings.Remove(identity,out var bindings)) return;
        foreach (var appearance in bindings.Values) _appearanceMembership.Release(appearance);
    }
    private void RetireMeshAppearance(EnbCurrentRetailMeshAppearance retired)
    {
        // Queued draw leases still protect the underlying model. Invalidate
        // borrowed prepared layouts at the same drain boundary as before.
        _appearanceEpoch++;
        retired.Owner.Dispose();
        int index=_shadowAppearanceModels.FindIndex(model=>ReferenceEquals(model,retired));
        if(index<0)throw new InvalidOperationException("Retired appearance has no model owner.");
        _shadowAppearanceModels.RemoveAt(index);
    }

    private void UpdateDynamicShadowCaptures(EnbGlApi gl, int framebuffer, LiveFrame frame,
        EnbRetailAssetBundle assets, IReadOnlyList<LiveRootInstance> roots,
        IReadOnlyDictionary<LiveRootInstance, IReadOnlyList<EnbW3dDrawTask>> appearance,
        EnbRetailTerrainLightState? light, IReadOnlyDictionary<string, EnbTextureImage> runtimeTextures,
        EnbCurrentRetailEnvironmentAtlasSet? atlas, EnbRenderTextureRuntimeSettings settings,
        EnbRenderTextureBackendCapabilities capabilities, IReadOnlySet<ulong> radarDetachedIdentities)
    {
        UpdateSimpleTerrainShadowSources(frame, assets, roots, light, radarDetachedIdentities);
        var mappings = EnbOpenGlShadowTargetFormats.ResolveCoreMappings(gl.ContextInfo.Version);
        foreach (var source in _simpleTerrainShadowRegistry.SnapshotTerrainSources())
        {
            if (!source.Dynamic) continue;
            if (!source.TextureInitialized)
            {
                if (mappings is null) { source.Diagnostic = "Capture format mapping unavailable for this GL profile."; continue; }
                source.TextureInitialized = true;
                var texture = EnbCurrentRetailShadowTargetAllocation.CreateSource<EnbOpenGlRenderTexture, EnbTextureImage>(
                    mappings.Keys.ToHashSet(), format =>
                    {
                        var mapping = mappings[format];
                        return EnbOpenGlRenderTexture.TryCreate(gl, 64, mapping.Internal, mapping.Pixel, 0x2601, out var target, out var error)
                            ? target : null;
                    }, () => assets.Textures.Resolve("shadow.tga").Image);
                source.Target = texture.CaptureTarget;
                source.HasBlob = texture.Blob is not null;
                if (source.Target is not null)
                {
                    source.CaptureTextureName = $"enbnext-shadow-{++_nextShadowTargetIdentity}";
                    source.Target.Clear(gl, 1, 1, 1, 1);
                    _shadowTargetOwners.Add(source);
                    _shadowTargetsByName.Add(DynamicShadowTextureName(source), source.Target);
                }
            }
            // 0050FED8 gates the terrain manager's0075F8B0 capture/application
            // call, not the independent RSL projection update above.
            if (light is not { Enabled: true } || source.Target is not {} capture || source.Source.Projection is not {} projection ||
                !projection.TryCreateCaptureCamera(64, out var camera)) continue;
            var obj = frame.Snapshot.Objects.FirstOrDefault(o => o.RenderIdentity == source.Identity && o.RenderObjectGeneration == source.Generation);
            if (obj is null) continue;
            var rsl = ResolveBaseAsset(assets.BaseAssetResolver!, obj.BaseAsset).Rsl;
            if (rsl is null || FindCurrentShadowSourceRoot(obj, rsl, roots, out _) is not {} current) continue;
            var originals = appearance.Where(pair =>
            {
                for (var parent = pair.Key; parent is not null; parent = parent.Parent)
                    if (ReferenceEquals(parent, current)) return true;
                return false;
            }).SelectMany(pair => pair.Value).ToArray();
            var packets = originals.Select(p => (p).ForkTask().SetLightEnvironment(CaptureLighting(assets).CaptureEnvironment)).ToArray();
            CaptureDynamicShadow(gl, capture, camera, packets, assets, runtimeTextures, atlas, settings, capabilities, ReadMapperClock(), source);
        }
        // Terrain holds target references until an enabled manager update, even
        // if the source left the scene. Destroy only after both owners release.
        var retainedSources=_shadowRetainedSources;
        retainedSources.Clear();
        foreach(var source in _simpleTerrainShadows.Values)retainedSources.Add(source);
        foreach(var source in _terrainSimpleShadowSources)retainedSources.Add(source);
        foreach (var retired in _shadowTargetOwners.Where(s => !retainedSources.Contains(s)).ToArray())
        {
            ReleaseShadowCaptureResources(gl, retired);
            _shadowTargetsByName.Remove(DynamicShadowTextureName(retired));
            retired.Target!.Destroy(gl);
            retired.Target = null;
            _shadowTargetOwners.Remove(retired);
        }
        if (_sharedMaterialOwner is null)
        {
            // The proxy borrows the target RenderObj (0047FF70/004801C3).
            // A camera cull or LOD change does not release its MeshModel.
            // Keep each instantiated mesh slot until replacement or owner
            // generation retirement; the scanner can first encounter a slot.
            var live=_shadowLiveAppearanceOwners;
            live.Clear();
            if(!_ownedSceneFrame)foreach(var obj in frame.Snapshot.Objects)live.Add((obj.RenderIdentity,obj.VisualGeneration));
            // Sector-authored orbitals are retained roots outside the network snapshot.
            foreach(var root in roots)
                if(root.PreparedOwner is not {SceneOwned:true})live.Add((root.Object.RenderIdentity,root.Object.VisualGeneration));
            foreach (var identity in _unownedMeshAppearanceBindings.ToArray())
                if(!live.Contains(identity))ReleaseMeshAppearanceOwner(identity);
            DrainDetachedMeshAppearances();
            // Unchanged membership needs no model scan or structural hashing.
            if (_appearanceMembership.HasPendingRetirements)
                _appearanceMembership.Drain(RetireMeshAppearance);
        }
        _shadowRetainedSources.Clear();
        _shadowLiveAppearanceOwners.Clear();
        gl.BindFramebuffer(0x8D40, framebuffer);
    }

    private void CaptureDynamicShadow(EnbGlApi gl, EnbOpenGlRenderTexture target,
        EnbResolvedRenderCamera camera, IReadOnlyList<EnbW3dDrawTask> packets,
        EnbRetailAssetBundle assets, IReadOnlyDictionary<string, EnbTextureImage> runtimeTextures,
        EnbCurrentRetailEnvironmentAtlasSet? atlas, EnbRenderTextureRuntimeSettings settings,
        EnbRenderTextureBackendCapabilities capabilities, uint clock, LiveSimpleTerrainShadow source)
    {
        var gpuResources = new List<EnbOpenGlStage0TextureMeshResource>();
        var transientResources = new List<EnbOpenGlStage0TextureMeshResource>();
        if (!_shadowCaptureResources.TryGetValue(source, out var retained))
            _shadowCaptureResources.Add(source, retained = []);
        var used = new HashSet<LiveGpuKey>();
        try
        {
            if (!EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.OrdinaryWorld, packets, out var order, out var diagnostic))
            { source.Diagnostic = diagnostic; return; }
            // The capture is another camera draw of the retained invocation.
            // It uses the same descriptor batches and RSL context admission as
            // the main view; only its light environment/camera differ.
            if (order.Packets.Any(packet => packet.SpecialMaterial is null && packet.CurrentRetailMeshInvocation is null))
            { source.Diagnostic = "Capture appearance owner unavailable."; return; }
            var draws = CompileMeshDraws(order.Packets,
                texture => runtimeTextures.TryGetValue(texture.LogicalName, out var image)
                    ? image : assets.Textures.Resolve(texture.LogicalName), clock, _=>camera);
            foreach (var draw in draws)
            {
                if (draw.Submission is not {} indexed)
                { source.Diagnostic = draw.Diagnostic; return; }
                var packet = indexed.Packet;
                var key = new LiveGpuKey(_assetGeneration, packet.OrderKey.ResourceKey, packet.OrderKey.MeshOrdinal,
                    packet.OrderKey.MaterialPassOrdinal, packet.RenderIdentity, packet.VisualGeneration,
                    packet.OrderKey.BindingOrdinal, packet.RenderInstancePath, LiveLaneKind.Textured,
                    settings.TextureReduction, settings.TextureFilter, atlas?.ReflectionTexture,
                    packet.CurrentRetailEffectMapperStartMilliseconds, draw.BatchIdentity);
                // Capture is another draw of the retained native mesh owner.
                // Keep immutable uploads, not the previous camera/light uniforms.
                // Deformed geometry retains the existing per-frame upload path.
                bool reusable = packet.DeformedGeometry is null;
                EnbOpenGlStage0TextureMeshResource? gpu = null;
                if (reusable && retained.TryGetValue(key, out var previous))
                {
                    var prior = previous.LanePlan.Submission.Packet;
                    if (prior.CurrentRetailColorizationRevision == packet.CurrentRetailColorizationRevision &&
                        prior.CurrentRetailNameDecalRevision == packet.CurrentRetailNameDecalRevision &&
                        prior.CurrentRetailDecalRevision == packet.CurrentRetailDecalRevision &&
                        EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(previous.LanePlan, indexed, camera, out var refreshed, out _))
                        gpu = previous with { LanePlan = refreshed };
                    else
                    {
                        _texturedBackend.DestroyResource(gl, previous);
                        retained.Remove(key);
                    }
                }
                if (gpu is null)
                {
                    if (!EnbW3dProgrammableTextureLaneCompiler.TryCompile(indexed, camera, settings, capabilities,
                        texture => ResolveTextureImage(texture, assets.Textures, runtimeTextures, atlas), out var lane, out diagnostic) ||
                        !_texturedBackend.TryCreateResource(gl, lane, clock, out gpu, out diagnostic))
                    { source.Diagnostic = diagnostic; return; }
                }
                if (reusable) { retained[key] = gpu; used.Add(key); }
                else transientResources.Add(gpu);
                gpuResources.Add(gpu);
            }
            target.Clear(gl, 1, 1, 1, 1);
            bool specialFlushed=false;
            void FlushSpecial()
            {
                specialFlushed=true;
                var receipt=FlushSpecialMaterials(gl,target.Framebuffer,64,64,order.Packets,_=>camera,
                    order.Packets.First().LightEnvironment,_specialSceneAmbient,assets,settings,capabilities,viewportInset:1,retailCapturePixelCenters:true);
                if(receipt.Diagnostics.Count!=0)throw new InvalidDataException(string.Join(";",receipt.Diagnostics));
            }
            foreach (var gpu in gpuResources)
            {
                var packet=gpu.LanePlan.Submission.Packet;
                if(!specialFlushed && packet.OrderKey.StaticSort.RuntimeLevel!=0)FlushSpecial();
                _specialDeviceLights=packet.LightEnvironment;
                if(gpu.LanePlan.RunStates.LastOrDefault(r=>r.Image0 is not null) is {} samplerRun)_specialMaterialBackend.Stage0Sampler=samplerRun.Sampler;
                var receipt = _texturedBackend.Submit(gl, target.Framebuffer, 64, 64, gpu, clock, new(false, 0, 0, 1),
                    EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(false), rendererSupportsFog: false,
                    clearColorAndDepth: false, viewportInset: 1, retailCapturePixelCenters: true);
                if (receipt.GlErrorCode != 0) { source.Diagnostic = receipt.ToString(); return; }
            }
            if(!specialFlushed && order.Packets.Length!=0)FlushSpecial();
            source.Diagnostic = null;
        }
        finally
        {
            foreach (var gpu in transientResources) _texturedBackend.DestroyResource(gl, gpu);
            foreach (var stale in retained.Keys.Where(key => !used.Contains(key)).ToArray())
            { _texturedBackend.DestroyResource(gl, retained[stale]); retained.Remove(stale); }
        }
    }

    private void DestroyDynamicShadows(EnbGlApi? gl)
    {
        foreach (var source in _shadowCaptureResources.Keys.ToArray()) ReleaseShadowCaptureResources(gl, source);
        foreach (var source in _shadowTargetOwners) { source.Target?.Destroy(gl); source.Target = null; source.TextureInitialized = false; }
        _shadowTargetOwners.Clear();
        _shadowTargetsByName.Clear();
        foreach (var source in _simpleTerrainShadows.Values.Concat(_terrainSimpleShadowSources))
            if (source.Dynamic) source.TextureInitialized = false;
        _frameShadowCaptureRoots.Clear();
        _shadowMeshBatches.ResetBuffers();
    }

    private void ClearMeshAppearances()
    {
        _appearanceEpoch++;
        foreach (var model in _shadowAppearanceModels) model.Owner.Dispose();
        _shadowAppearanceModels.Clear();
        _meshAppearanceBindings.Clear();
        _unownedMeshAppearanceBindings.Clear();_retiredMeshAppearanceBindings.Clear();
        _appearanceMembership.Clear();
        _shadowMeshBatches.ResetBuffers();
    }
}
