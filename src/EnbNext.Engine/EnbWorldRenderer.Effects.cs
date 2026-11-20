using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(uint,ulong,ulong), Task<EffectSwatch>> _effectSwatches = [];
    private readonly Dictionary<(uint, ulong, ulong), EffectPassMappers> _effectResetMappers = [];
    private readonly Dictionary<(uint, ulong, ulong), EffectPassMappers> _effectProjectedMappers = [];
    private readonly Dictionary<(EnbW3dRenderMesh, EnbW3dRenderMaterialPass, bool, bool), EnbW3dRenderMaterialPass> _effectPassBindings = [];
    private readonly Dictionary<(uint, ulong, ulong), Vector3> _effectProjectionExtents = [];
    private string _effectVisualStatus = "effect visuals idle";
    private readonly Queue<string> _effectVisualHistory = new();
    private string _effectVisualHistoryText = string.Empty;

    private void SetEffectVisualStatus(string status, uint clientTime)
    {
        if (status == _effectVisualStatus) return;
        _effectVisualStatus = status;
        if (_effectVisualHistory.Count == 16) _effectVisualHistory.Dequeue();
        _effectVisualHistory.Enqueue($"{clientTime}ms {status}");
        _effectVisualHistoryText = string.Join("\n  ", _effectVisualHistory);
    }

    private void ClearEffectVisuals()
    {
        ClearEffectObjects();
        foreach(var swatch in _effectSwatches.Values.Where(t=>t.IsCompletedSuccessfully))swatch.Result.Dispose();
        _effectSwatches.Clear(); _effectPassBindings.Clear(); _effectProjectionExtents.Clear();

        _effectResetMappers.Clear();
        _effectProjectedMappers.Clear();
        _effectVisualHistory.Clear(); _effectVisualHistoryText = string.Empty;
        _effectVisualStatus = "effect visuals idle";
    }

    private void ApplyEffectVisuals(List<EnbW3dDrawTask> packets, IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        IEnbSceneFrame snapshot, EnbRetailAssetBundle assets, IReadOnlyList<LiveRootInstance> roots, uint now, uint clientTime)
    {
        var liveNodes=nodes.Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        foreach(var key in _effectSwatches.Keys.Where(k=>!liveNodes.Contains(k)).ToArray())
        {_effectSwatches[key].Result.Dispose();_effectSwatches.Remove(key);}
        foreach (var key in _effectProjectionExtents.Keys.Where(k => !liveNodes.Contains(k)).ToArray()) _effectProjectionExtents.Remove(key);
        foreach (var key in _effectResetMappers.Keys.Where(k=>!liveNodes.Contains(k)).ToArray()) _effectResetMappers.Remove(key);
        foreach (var key in _effectProjectedMappers.Keys.Where(k=>!liveNodes.Contains(k)).ToArray()) _effectProjectedMappers.Remove(key);
        if (nodes.Count == 0) { _effectPassBindings.Clear(); SetEffectVisualStatus("effect visuals passes 0", clientTime); return; }
        int pending = 0, blocked = 0, passes = 0, recovered = 0;
        string? failure = null;
        var hidden = new HashSet<(uint,ulong)>();
        var additions = new List<EnbW3dDrawTask>();
        var usedBindings = new HashSet<(EnbW3dRenderMesh, EnbW3dRenderMaterialPass, bool, bool)>();
        var prepared = new List<(EnbCurrentRetailEffectOwner Node, EnbCurrentRetailPassEffectDefinition Definition, EffectSwatch Swatch, EnbW3dMapperRuntimeState Mapper0, EnbW3dMapperRuntimeState Mapper1, uint? Epoch)>();
        foreach (var node in nodes)
        {
            if (node.Definition.EffectClassKind != EnbEffectClassKind.PassEffect) continue;
            if (!EnbCurrentRetailPassEffectDefinition.TryRead(node, out var definition, out var error))
            { blocked++; failure ??= $"E{node.Definition.DefinitionId}: {error}"; continue; }
            var swatchKey=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
            if (!_effectSwatches.TryGetValue(swatchKey, out var load))
            {
                //6EB9B0 acquires at construction. Future linked factories must
                //not publish names or consume mapper randomness ahead of it.
                load = Task.FromResult(LoadEffectSwatch(assets, definition.Swatch, definition.HsvShift,
                    unchecked(now-(clientTime-node.ConstructionTick))));
                _effectSwatches.Add(swatchKey, load);
            }
            if (!load.IsCompleted) { pending++; continue; }
            try
            {
                var swatch = load.GetAwaiter().GetResult();
                if (swatch.Error is not null) throw new InvalidDataException(swatch.Error);
                definition.ValidateSwatch(swatch.Pass!);
                uint constructionClock=unchecked(now-(clientTime-node.ConstructionTick));
                var shared=swatch.SharedMappers??throw new InvalidDataException("PassEffect retained material mappers unavailable");
                var mapper0=shared.Stage0;var mapper1=shared.Stage1;
                uint? mapperEpoch=null;
                //006EB9B0 copies the material for either projection or ResetMappers.
                //009809E0 retains that copied parent; its own Reset is a no-op.
                if(definition.ResetMappers || definition.ProjectStage0 || definition.Stage1BoxScale is not null)
                {
                    var mapperKey=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
                    var owners=definition.ResetMappers?_effectResetMappers:_effectProjectedMappers;
                    if(!owners.TryGetValue(mapperKey,out var owned))
                    {
                        owned=new(constructionClock,[mapper0.CloneForMaterial(constructionClock),mapper1.CloneForMaterial(constructionClock)]);
                        owners.Add(mapperKey,owned);
                    }
                    mapper0=owned.States[0];mapper1=owned.States[1];mapperEpoch=owned.Start;
                    if(node.Active && !owned.Activated)
                    {
                        uint activationClock=unchecked(now-(clientTime-node.ActualStartTick));
                        if(definition.ResetMappers)
                        {
                            if(!definition.ProjectStage0)mapper0.Reset(activationClock);
                            if(definition.Stage1BoxScale is null)mapper1.Reset(activationClock);
                        }
                        owned.Activated=true;
                    }
                }
                if(node.Active)prepared.Add((node,definition,swatch,mapper0,mapper1,mapperEpoch));
            }
            catch (Exception ex) { blocked++; failure ??= $"E{node.Definition.DefinitionId}: {ex.Message}"; }
        }
        // 006EDC00 -> 006BB8E0 pushes at the front of the owner's hook list.
        // 006BB7E0 submits only that entry; 006BBA20 removal exposes its predecessor.
        // Keep every material's construction/reset state, but evaluate/draw only the winner.
        var winners=prepared.GroupBy(p=>(p.Node.BoundGameId,p.Node.BoundRenderIdentity))
            .Select(group=>group.MaxBy(p=>p.Node.ActivationSequence).Node).ToHashSet();
        foreach(var selected in prepared)
        {
            var (node,definition,swatch,mapper0,mapper1,mapperEpoch)=selected;
            string? error=null;
            try
            {
                // 006EDC00 attaches passes only to an existing render owner;
                // 006EE260 leaves a detached effect's lifetime running. There
                // is no projector transform to resolve without submitted geometry.
                var geometry = packets.Where(p => p.GameId == node.BoundGameId &&
                    (node.BoundRenderIdentity is null || p.RenderIdentity==node.BoundRenderIdentity))
                    .DistinctBy(p => ((object?)p.CurrentRetailMeshInvocation ?? p.Mesh,
                        p.RenderInstancePath, p.OrderKey.BindingOrdinal - p.Material.Source.Ordinal)).ToArray();
                if (geometry.Length == 0) continue;
                string? recovery=definition.GetRecoveryDiagnostic(swatch.Pass!);
                var owner = SceneObjects(snapshot).FirstOrDefault(o => o.GameId == node.BoundGameId &&
                    (node.BoundRenderIdentity is null || o.RenderIdentity==node.BoundRenderIdentity));
                if (owner?.Spatial?.Transform is not { } spatial ||
                    !EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial, out var ownerWorld, out error))
                    throw new InvalidDataException(error ?? "PassEffect owner transform unavailable");
                // The GPU projector consumes final world positions. Existing
                // rigid, camera-facing and deformed submission lanes therefore
                // use the same projection after their own geometry transform.
                // 006EDC00 initializes each PassEffect from its own render
                // owner. One skill graph may project onto differently sized ships.
                var identity = (node.Root.EffectId, node.Root.InstanceGeneration, node.Sequence);
                if (!_effectProjectionExtents.TryGetValue(identity, out var extent))
                {
                    // 006F3BC0 returns this controller's RenderObj. RSObject
                    // 00461800 copies its current model's local bounds; +F4
                    // (00463370 -> 00461910) returns the transformed world box.
                    // Descendants already belong to that model's composite and
                    // must not be queried again as independent effect owners.
                    var root = roots.SingleOrDefault(r => r.Object.RenderIdentity == owner.RenderIdentity && r.InstancePath == "root");
                    if (root is null) continue;
                    EnbW3dAxisAlignedBounds box;
                    if (root.CurrentRetailLocalBox is {} preparedBox)
                        box=preparedBox;
                    else
                    {
                        if (root.Root.Hlod is {} hlod)
                        {
                            if(!TryResolveHlodCompositeBounds(root,hlod,ownerWorld,
                                root.AnimationBlend?.ResolvedSamples ?? Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),out var bounds,out error))
                                throw new InvalidDataException(error);
                            box=bounds.LocalBox;
                        }
                        else if(TryComputeRootLocalBounds(root.RetainedQueryRoot ?? CreateConfiguredBoundsRoot(root.Root,1),out var local,out error))
                            box=new(local.BoxCenter,local.BoxExtent);
                        else throw new InvalidDataException(error);
                    }
                    // Configured model bounds already contain factory scale.
                    // RSObject applies its rigid controller matrix, not the
                    // renderer's additional per-mesh scale transform.
                    extent = EnbCurrentRetailRenderBoundsStorage.Calculate(ownerWorld,Vector4.Zero,box.Center,box.Extents).BoxExtent;
                    _effectProjectionExtents.Add(identity, extent);
                }
                // 006EE260 builds both projectors from unary matrix+94. Bone
                // placement changes that frame, not the owner's authored box.
                if(!_combatEffectPoses.TryGetValue(identity,out var boundWorld) || !Matrix4x4.Invert(boundWorld,out var projectorInverse))
                    throw new InvalidDataException(_combatPoseErrors.GetValueOrDefault(identity,"PassEffect retained projector pose unavailable"));
                Matrix4x4? projection = definition.ProjectStage0 ? definition.ProjectionFromWorld(projectorInverse, extent, swatch.TextureWidths[0]) : null;
                Matrix4x4? projection1 = recovery is null && definition.Stage1BoxScale is { } box1
                    ? (definition with { BoxScale=box1, ProjectionAxis=definition.Stage1ProjectionAxis, ProjectionKind=definition.Stage1ProjectionKind }).ProjectionFromWorld(projectorInverse,extent,swatch.TextureWidths[1]) : null;
                if(!winners.Contains(node))continue;
                var evaluation=projection.HasValue?mapper0.EvaluateMatrix(now):mapper0.Evaluate(now);
                var evaluation1=projection1.HasValue?mapper1.EvaluateMatrix(now):mapper1.Evaluate(now);
                foreach (var packet in geometry)
                {
                    if (!EnbCurrentRetailPassEffectDefinition.CanApplyToMesh(packet.Mesh)) continue;
                    var key = (packet.Mesh, swatch.Pass!, definition.ProjectStage0, definition.Stage1BoxScale.HasValue); usedBindings.Add(key);
                    if (!_effectPassBindings.TryGetValue(key, out var material))
                    { material = definition.BindToTargetMesh(swatch.Pass!, packet.Mesh); _effectPassBindings.Add(key, material); }
                    if(!definition.TryResolveSubmissionSort(swatch.Pass!.SingleBindingSemantics!.Value.FixedFunction.PackedRetailState,
                        packet.OrderKey.StaticSort,out var submissionSort))continue;
                    additions.Add((packet).ForkTask().SetMaterial(new(material, material.SingleBindingSemantics)).SetOrderKey(packet.OrderKey with
                        {
                            StaticSort = submissionSort,
                            MaterialPassOrdinal = 10000 + node.Definition.DefinitionId,
                            BlendClass = material.SingleBindingSemantics!.Value.FixedFunction.BlendEnabled ? EnbW3dDrawBlendClass.Blended : EnbW3dDrawBlendClass.Opaque
                        }).SetRenderInstancePath(packet.RenderInstancePath + $"/effect/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}").SetCurrentRetailEffectProjection(projection).SetCurrentRetailAdditionalMeshPassFlags(definition.HideBaseMesh ? 4u : 0u).SetCurrentRetailEffectMapper(evaluation).SetCurrentRetailSharedMaterialMappers(new(mapper0,mapper1)).SetCurrentRetailMaterialMappers(null).SetCurrentRetailEffectProjection1(projection1).SetCurrentRetailEffectMapper1(evaluation1).SetCurrentRetailEffectMapperStartMilliseconds(mapperEpoch).SetCurrentRetailStage0UvOffset(null).SetCurrentRetailPltDayNightPhase(null));
                    passes++;
                }
                if(recovery is not null){recovered++;failure ??= $"E{node.Definition.DefinitionId}: {recovery}";}
                if (definition.HideBaseMesh) hidden.Add((owner.GameId,owner.RenderIdentity));
            }
            catch (Exception ex) { blocked++; failure ??= $"E{node.Definition.DefinitionId}: {ex.Message}"; }
        }
        packets.RemoveAll(p => hidden.Contains((p.GameId,p.RenderIdentity))); packets.AddRange(additions);
        foreach (var key in _effectPassBindings.Keys.Where(k => !usedBindings.Contains(k)).ToArray()) _effectPassBindings.Remove(key);
        SetEffectVisualStatus($"effect visuals passes {passes} hidden {hidden.Count} pending {pending} blocked {blocked}" +
            (recovered==0?"":$" inactive-projector {recovered}") + (failure is null ? "" : $" · {failure}"), clientTime);
    }

    private EffectSwatch LoadEffectSwatch(EnbRetailAssetBundle assets, string name, Vector3 hsv,uint clock)
    {
        if(_shipExhaustOwner is {} primary)return primary.LoadEffectSwatch(assets,name,hsv,clock);
        var leases=new List<EnbCurrentRetailTextureRegistry.Lease>();
        try
        {
            var prototypes=EnbW3dRenderResourceCache.ForAssetNamespace(assets);
            _prototypeResources=prototypes;
            var names=prototypes.CreatePublicationContext(clock).CreateNamespace(prototypes.CapturePrototypeSources(out _));
            bool configured=EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv);
            string variant=EnbCurrentRetailPrototypeVariantName.Create(name,1,hsv);
            EnbW3dRenderMesh? mesh=null;
            string? error=null;
            bool Acquire()
            {
                foreach(string token in configured?new[]{variant,name}:new[]{name})
                {
                    bool final=configured&&token==variant;
                    LiveCpuRoot? root=null;
                    EnbW3dRenderResource? resource=null;
                    if(TryAcquireConfiguredPrototype(-1,token,-1,assets,out var existing))root=existing;
                    else
                    {
                        if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,
                            token,out var source,out error,out _,out bool missing,names))
                        {if(!missing)throw new InvalidDataException(error);continue;}
                        if(source.CurrentRetailAggregate is null && source.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh} entry &&
                            prototypes.TryGetRegisteredPrototype(source,out resource))
                            mesh=resource.Meshes[entry.Ordinal];
                        if(mesh is null || configured&&!final)
                        {
                            if(!TryGetCpuRoot(new(-1,-1,source.LogicalFileName,source.RequestedObjectName),source,assets,out var original,out error))
                                throw new InvalidDataException(error);
                            //91D4B0 constructs the named object before6EB9B0
                            //tests its class. Even a discarded emitter owns
                            //constructor property tables and consumes randomness.
                            root=HasParticleConstruction(original)?ConstructParticleFactoryTree(original):original;
                        }
                    }
                    if(root is not null)
                    {
                        if(configured&&!final)
                        {
                            if(HasConfigurableMeshTree(root))
                            {
                                var created=RetainConfiguredMeshTree(name,1,hsv,assets,root,clock);
                                root=created.CompositeRoot??root with {Resource=created.Model.Resource};
                            }
                            root=RetainConfiguredPrimitive(name,root,1,hsv,assets);
                            if(HasParticleConstruction(root))root=RetainParticleFactoryTree(name,root,1,hsv);
                        }
                        mesh=root.IsDirectMeshPrototype
                            ?root.Resource.Meshes.Single(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,root.RootRenderObjectName)):null;
                    }
                    return true;
                }
                return false;
            }
            if(name.Length!=0 && !Acquire())
            {
                //6EBB18 loads the literal token+.w3d after ordinary lookup,
                //then performs a fresh lookup (including its own lazy load).
                if(assets.W3dFiles.TryResolve(name+".w3d",name,out var file,out var fileError,out bool opened))
                {if(file.ResourceLocation.DataLength!=0)names.PublicationContext!.Publish(file);}
                else if(opened)return new(null,[],fileError);
                Acquire();
            }
            EnbW3dRenderMaterialPass pass;
            if(mesh is not null)pass=EnbCurrentRetailPassEffectDefinition.CaptureSwatchPass(mesh);
            else
            {
                var missing=prototypes.AcquireNamedTexture("missingtexture.tga");leases.Add(missing);
                var image=assets.Textures.ResolveCurrentRetailMissingTexture("missingtexture.tga",assets.Paths.ClientExecutablePath);
                pass=EnbCurrentRetailPassEffectDefinition.CreateMissingSwatchDescriptor(new(0,image.LogicalName,null)
                    {CurrentRetailTextureState=missing.State,ResolveLogicalNameAtRuntime=true});
            }
            if(pass.SingleBindingSemantics is null || pass.TextureStages.Count>2)
                throw new InvalidDataException("PassEffect requires a uniform swatch with at most two texture stages");
            //6EE337/375 and6EE5EB/605 read the retained descriptor's texture
            //width. Named failed loads use the same0094B920 fallback as meshes;
            //a retained generated image must not be replaced by a newer name.
            pass=(pass).Copy().SetTextureStages(pass.TextureStages.Select(stage=>(stage).Copy().SetTextures(stage.Textures.Select(t=>EnbCurrentRetailMeshTexturePreparation.PrepareBinding(t,assets)).ToArray()).SetCurrentRetailUniformTexture(stage.CurrentRetailUniformTexture is {} texture?EnbCurrentRetailMeshTexturePreparation.PrepareBinding(texture,assets):null)).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(triangle=>triangle with {
                    Textures=triangle.Textures.Select(t=>t is null?null:EnbCurrentRetailMeshTexturePreparation.PrepareBinding(t,assets)).ToArray()
                }).ToArray());
            var widths=pass.TextureStages.Select(stage=>
            {
                var texture=stage.Textures.Single();
                return (texture.CurrentRetailResolvedTexture?.Image ?? assets.Textures.Resolve(texture.LogicalName).Image ?? throw new InvalidDataException("PassEffect texture unavailable")).Width;
            }).ToArray();
            foreach(var texture in pass.TextureStages.Select(s=>s.CurrentRetailUniformTexture?.CurrentRetailTextureState).OfType<EnbCurrentRetailTextureObjectState>())
                if(!leases.Any(l=>ReferenceEquals(l.State,texture)) && prototypes.RetainTextureObject(texture) is {} lease)leases.Add(lease);
            EnbW3dSharedMaterialMappers? shared=null;
            if(pass.CurrentRetailUniformMaterial?.Material is {} material)
            {
                if(!prototypes.TryRetainMaterialMappers([material],clock,out var retained,out error))throw new InvalidDataException(error);
                shared=retained[material];
            }
            return new(pass, widths, null){Textures=leases,SharedMappers=shared};
        }
        catch (Exception ex) {foreach(var lease in leases)lease.Dispose();return new(null, [], ex.Message); }
    }
    private sealed record EffectSwatch(EnbW3dRenderMaterialPass? Pass, int[] TextureWidths, string? Error):IDisposable
    {
        public IReadOnlyList<EnbCurrentRetailTextureRegistry.Lease> Textures {get;init;}=[];
        public EnbW3dSharedMaterialMappers? SharedMappers {get;init;}
        public void Dispose(){foreach(var texture in Textures)texture.Dispose();}
    }
    private sealed record EffectPassMappers(uint Start, EnbW3dMapperRuntimeState[] States)
    {
        public bool Activated {get;set;}
    }
}
