using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(int, Vector3), Task<EnbCurrentRetailRObjEffectAsset>> _effectObjectAssets = [];
    private sealed record EffectObjectModelAcquisition(EnbCurrentRetailRObjMeshMappers Models,string MeshRegistrations):IDisposable
    {
        public (float Scale,Vector3 Hsv)? FactoryInputs {get;init;}
        public IReadOnlyList<EnbCurrentRetailRObjMeshMappers> Children {get;init;}=[];
        public void Dispose(){Models.Dispose();foreach(var child in Children)child.Dispose();}
    }
    private readonly Dictionary<Task<EnbCurrentRetailRObjEffectAsset>,EffectObjectModelAcquisition> _effectObjectModelAcquisitions=[];
    private readonly Dictionary<Task<EnbCurrentRetailRObjEffectAsset>,Dictionary<(string,long),EffectObjectModelAcquisition>> _effectObjectAttachmentAcquisitions=[];
    private readonly Dictionary<Task<EnbCurrentRetailRObjEffectAsset>,EnbCurrentRetailRObjEffectAsset.FactoryPreparation> _effectObjectPreparationSources=[];
    private readonly Dictionary<(uint, ulong, ulong), EffectObjectInstance> _effectObjectInstances = [];
    private readonly HashSet<(uint, ulong)> _effectObjectPrewarmed = [];
    private readonly HashSet<(uint,ulong,ulong)> _effectObjectPublishedSources=[];
    private readonly Dictionary<(int, float, Vector3),uint> _effectObjectSharedMapperStarts = [];
    private readonly Dictionary<(int, float, Vector3, string), EnbCurrentRetailRObjMeshMappers> _effectObjectMeshPrototypes = [];
    private readonly Dictionary<(uint, ulong, ulong), EffectObjectMeshOwners> _effectObjectMeshOwners = [];
    private sealed record EffectConfiguredMeshGroup(IReadOnlyList<EnbCurrentRetailRObjMeshMappers> Models,float Scale,Vector3 Hsv);
    private readonly Dictionary<(string Name,EnbCurrentRetailRObjEffectAsset? Unnamed),EffectConfiguredMeshGroup> _effectConfiguredMeshGroups=[];
    private string _effectObjectVisualStatus = "effect objects idle";

    private void ClearEffectObjects()
    {
        _detailCompositeParticles.Clear();_detailRetainedEmitterOwners.Clear();_detailShipExhaustInstances.Clear();
        _detailParticleJetInstances.Clear();_detailAttachmentParticles.Clear();_detailTunnelParticles.Clear();_sharedDetailOwners.Clear();
        _hlodDetailOwners.Clear();_hlodCandidateRoots.Clear();_hlodCandidateDecisions.Clear();
        _particleJets.Clear();_particleJetCallbacks.Clear();_jetBufferCollision.Clear(); _particleJetFactories.Clear();
        _lineAssets.Clear();
        _effectLineLevels.Clear();_effectLineTiling.Clear();_preparedEffectLines.Clear();_effectLinePreparation=default;
        _effectObjectLineLevels.Clear();_effectObjectLineCandidates.Clear();_preparedObjectLineKeys.Clear();
        _lineBindings.Clear();
        _requiredEffectObjectAssets.Clear();
        ClearWarpPlumes();
        ClearWarpTunnels();
        foreach(var instance in _effectObjectInstances.Values)instance.Dispose();
        _effectObjectAssets.Clear(); _effectObjectInstances.Clear(); _effectObjectPrewarmed.Clear();
        foreach(var acquired in _effectObjectModelAcquisitions.Values)acquired.Dispose();
        _effectObjectModelAcquisitions.Clear();
        foreach(var children in _effectObjectAttachmentAcquisitions.Values)
            foreach(var acquired in children.Values)acquired.Dispose();
        _effectObjectAttachmentAcquisitions.Clear();
        _effectObjectPreparationSources.Clear();
        _effectObjectAssetRegistrations.Clear();_effectObjectInstanceAssets.Clear();
        _effectObjectAssetNamespaceVersions.Clear();
        _effectObjectPublishedSources.Clear();
        _effectObjectSharedMapperStarts.Clear();
        foreach (var owner in _effectObjectMeshOwners.Values) owner.Dispose();
        _effectObjectMeshOwners.Clear();
        foreach (var prototype in _effectObjectMeshPrototypes.Values) prototype.Dispose();
        _effectObjectMeshPrototypes.Clear();
        foreach(var group in _effectConfiguredMeshGroups.Values)
            foreach(var model in group.Models)model.Dispose();
        _effectConfiguredMeshGroups.Clear();
        _effectRslScheduler.ClearSourceBindings();
        _lastEffectObjectRadii.Clear();
        _effectSequences.Clear();
        foreach(var owner in _effectRslOwners.Values)ReleaseEffectRslModels(owner);
        _effectRslOwners.Clear();
        _effectRingOwners.Clear(); _ringPreviousTime=null;
        _effectSphereOwners.Clear();
        _combatEffectBindings.Clear(); _combatEffectPoses.Clear();
        _combatFrontFramePoses.Clear();
        _combatSeekEndpoints.Clear(); _combatFixedBindings.Clear();
        _combatChildSeekRenders.Clear();
        _combatLookTargets.Clear(); _combatPoseErrors.Clear();
        _effectParticleInstances.Clear();
        ClearCompositeCallbacks();_effectCompositeParticles.Clear();_effectCompositeBounds.Clear();_compositeRemovalSerial=0; _compositeBufferCollision.Clear();
        _effectStandaloneStreakInstances.Clear();
        _bloodOwners.Clear();
        _bloodAttachments.Clear();
        _bloodSounds.Clear();
        _shipDamageStates.Clear();_shipDamageChildren.Clear();_shipDamageAttachments.Clear();
        ClearProjectedDecals();
        _effectStreakInstances.Clear(); _effectHlodLevels.Clear(); _effectHlodSelections.Clear(); _effectHlodErrors.Clear();_effectHlodSceneAssets.Clear();_effectRenderSceneChanges.Clear();_effectCulledRoots.Clear();
        _effectObjectVisualStatus = "effect objects idle";
    }

    private readonly record struct EffectObjectDrawState(int Draws,int Pending,int Blocked,int Recovered,string? Failure);

    private EffectObjectDrawState PrepareEffectObjects(List<EnbW3dDrawTask> packets, IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        IEnbSceneFrame snapshot, EnbRetailAssetBundle assets, IReadOnlyDictionary<ulong, float> sourceRadii,
        EnbW3dLightEnvironment lights, uint clientTime, uint mapperTime, EnbResolvedRenderCamera camera,float depthMaximum)
    {
        _effectRenderSceneChanges.Clear();
        var live = nodes.Select(n => (n.Root.EffectId, n.Root.InstanceGeneration, n.Sequence)).ToHashSet();
        foreach(var key in _effectCompositeBounds.Keys.Where(key=>!live.Contains(key)).ToArray())_effectCompositeBounds.Remove(key);
        foreach(var key in _effectObjectInstanceAssets.Keys.Where(key=>!live.Contains(key)).ToArray())_effectObjectInstanceAssets.Remove(key);
        var retainedPreparations=_effectObjectAssets.Values.Concat(_effectObjectInstanceAssets.Values).ToHashSet();
        foreach(var task in _effectObjectPreparationSources.Keys.Where(task=>!retainedPreparations.Contains(task)).ToArray())
            _effectObjectPreparationSources.Remove(task);
        foreach(var task in _effectObjectModelAcquisitions.Keys.Where(task=>task.IsFaulted || task.IsCanceled || !retainedPreparations.Contains(task)).ToArray())
        { _effectObjectModelAcquisitions[task].Dispose();_effectObjectModelAcquisitions.Remove(task); }
        foreach(var task in _effectObjectAttachmentAcquisitions.Keys.Where(task=>task.IsFaulted || task.IsCanceled || !retainedPreparations.Contains(task)).ToArray())
        {
            foreach(var acquired in _effectObjectAttachmentAcquisitions[task].Values)acquired.Dispose();
            _effectObjectAttachmentAcquisitions.Remove(task);
        }
        _effectObjectPublishedSources.RemoveWhere(key=>!live.Contains(key));
        foreach (var key in _effectObjectInstances.Keys.Where(k => !live.Contains(k)).ToArray())
        { _effectObjectInstances[key].Dispose(); _effectObjectInstances.Remove(key); }
        foreach (var key in _effectObjectMeshOwners.Keys.Where(k => !live.Contains(k)).ToArray())
        { _effectObjectMeshOwners[key].Dispose(); _effectObjectMeshOwners.Remove(key); }
        var roots = nodes.Select(n => (n.Root.EffectId, n.Root.InstanceGeneration)).ToHashSet();
        _effectObjectPrewarmed.RemoveWhere(k => !roots.Contains(k));
        int draws = 0, pending = 0, blocked = 0, recovered = 0; string? failure = null;
        foreach (var node in nodes)
        {
            if (node.Active && node.Definition.EffectClassKind == EnbEffectClassKind.ParticleJetEffect)
            {
                continue;
            }
            if (_effectObjectPrewarmed.Add((node.Root.EffectId, node.Root.InstanceGeneration)) && assets.PresentationCatalog is { } catalog)
                foreach (var next in catalog.ResolveEffectGraph(node.Root.DefinitionId).Definitions)
                {
                    if (EnbCurrentRetailRObjEffectDefinition.TryReadConstruction(node, out var future, out _,definitionOverride:next)) LoadEffectObject(assets, future.BaseAssetId, future.HsvShift,prewarm:true);
                }
            if (node.Definition.EffectClassKind is not (EnbEffectClassKind.RObjEffect or EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect or EnbEffectClassKind.RObjLookAtEffect)) continue;
            if (!node.TryGetRObjDefinition( out var definition, out var error))
            { blocked++; failure ??= $"E{node.Definition.DefinitionId}: {error}"; continue; }
            float requestedScale=definition.AssetScale;
            var load = LoadEffectObject(assets, definition.BaseAssetId, definition.HsvShift,node:node,currentClientTime:clientTime);
            if (load is null) { blocked++; failure ??= "RObjEffect asset cache limit"; continue; }
            Action? finishParticleConstruction=null;
            try
            {
                if(load.IsCompletedSuccessfully)
                {
                    var prepared=load.Result;
                    if(prepared.RslOwner is not null)continue;
                    definition=prepared.ResolveFactoryDefinition(definition);
                    var acquisitionKey=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
                    if(prepared.PrototypeLoadSources.Count>0 && !_effectObjectPublishedSources.Contains(acquisitionKey))
                    {
                        if(!_prototypeResources.TryFindPrototypeSource(prepared.PrototypeLoadSources[0].RequestedObjectName,out _))
                        {
                            uint clock=unchecked(mapperTime-(clientTime-node.ConstructionTick));
                            foreach(var source in prepared.PrototypeLoadSources)
                                if(!_prototypeResources.TryLoadFile(source,out _,out var why,clock))throw new InvalidDataException(why);
                        }
                        _effectObjectPublishedSources.Add(acquisitionKey);
                    }
                    if(prepared.IsNullObject)continue;
                }
                EffectObjectMeshOwners? meshOwners = null;
                if (load.IsCompletedSuccessfully)
                    meshOwners = RetainEffectObjectMeshOwners(node, definition, load.Result, assets, clientTime, mapperTime);
                if(meshOwners?.FactoryInputs is {} initialInputs)
                    definition=definition with {AssetScale=initialInputs.Scale,HsvShift=initialInputs.Hsv};
                if (!node.Active) continue;
                var owner = ResolveCombatResourceOwner(node, SceneObjects(snapshot));
                if (owner?.Spatial?.Transform is not { } spatial ||
                    !EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial, out var ownerWorld, out error))
                {
                    var missingKey=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
                    throw new InvalidDataException($"{error ?? "RObjEffect owner unavailable"}; root E{node.Root.DefinitionId} " +
                        $"0x{node.Root.EffectId:X}/g{node.Root.InstanceGeneration}/n{node.Sequence} {node.LinkKind} parent {node.ParentSequence}; " +
                        $"bound 0x{node.BoundGameId:X8}, source 0x{node.Root.SourceGameId:X8}, target 0x{node.Root.TargetGameId:X8}; " +
                        $"age {unchecked(clientTime-node.ActualStartTick)}ms; bone '{node.Root.BoneName}'; " +
                        $"pose {_combatPoseErrors.GetValueOrDefault(missingKey,_combatEffectPoses.ContainsKey(missingKey)?"ready":"not prepared")}; " +
                        $"seek receipt {_combatSeekEndpoints.ContainsKey(missingKey)}; " +
                        $"published endpoints {node.Root.PublishedEndpoints is not null}; geometry {node.Root.GeometryKind}; " +
                        $"packet endpoint receipt {node.Root.EndpointReceipt?.ToString() ?? "unobserved"}; endpoint shorts {node.Root.EndpointShortA}/{node.Root.EndpointShortB}");
                }
                var key = (node.Root.EffectId, node.Root.InstanceGeneration, node.Sequence);
                ownerWorld=ResolveCombatEffectPose(node,ownerWorld);
                // This render object belongs to the effect instance, not the
                // owner's current mesh. A model change must not discard birth
                // histories or reapply the source-setter radius multiplier.
                if (!_effectObjectInstances.TryGetValue(key, out var instance) || instance.OwnerIdentity != owner.RenderIdentity)
                {
                    if (definition.ScaleToRadius && !IsMatrixBoundEffect(node) && !IsCoordinateBoundEffect(node) && !sourceRadii.ContainsKey(owner.RenderIdentity))
                        throw new InvalidDataException("RObjEffect composed source radius unavailable");
                    float radius = ResolveCombatRenderScale(node,definition,sourceRadii.GetValueOrDefault(owner.RenderIdentity));
                    instance?.Dispose();
                    instance = new(owner.RenderIdentity, owner.VisualGeneration, radius, unchecked(mapperTime - (clientTime - node.ActualStartTick)))
                        {MissingFactoryScaleResolved=load.IsCompletedSuccessfully && load.Result.IgnoresRequestedFactoryInputs,
                         SourceRadiusScale=definition.ScaleToRadius && !IsMatrixBoundEffect(node) && !IsCoordinateBoundEffect(node)
                            ?sourceRadii[owner.RenderIdentity]:1};
                    _effectParticleInstances.Remove(key);
                    _effectStreakInstances.Remove(key);
                    _effectStandaloneStreakInstances.Remove(key);
                    _effectObjectInstances[key] = instance;
                }
                uint age = unchecked(clientTime - node.ActualStartTick);
                ownerWorld=PlaceCombatEffect(node,definition,ownerWorld,camera,sourceRadii.GetValueOrDefault(owner.RenderIdentity));
                if (!load.IsCompleted) { instance.Birth.Record(age, ownerWorld); instance.Poses.Record(age, ownerWorld); pending++; continue; }
                var asset = load.GetAwaiter().GetResult();
                definition=asset.ResolveFactoryDefinition(definition);
                if(asset.IgnoresRequestedFactoryInputs && !instance.MissingFactoryScaleResolved)
                {
                    instance=instance with {Radius=instance.Radius/requestedScale,MissingFactoryScaleResolved=true};
                    _effectObjectInstances[key]=instance;
                }
                meshOwners ??= RetainEffectObjectMeshOwners(node, definition, asset, assets, clientTime, mapperTime);
                if(meshOwners.FactoryInputs is {} retainedInputs)
                {
                    definition=definition with {AssetScale=retainedInputs.Scale,HsvShift=retainedInputs.Hsv};
                    instance=instance with {Radius=retainedInputs.Scale*instance.SourceRadiusScale};
                    _effectObjectInstances[key]=instance;
                }
                asset=ResolveCompositeAsset(node,asset,out var queryMembership);
                if(!instance.DiscardedParticlesConstructed)
                    finishParticleConstruction=()=>
                    {
                        instance.DiscardedParticlesConstructed=true;
                        foreach(var discarded in asset.DiscardedParticleConstructions)_=new EnbRetailParticleRandomOwner(discarded);
                    };
                // A no-draw placeholder may finish loading after the earlier
                // fast-path check. Never send its absent resource to the GPU.
                if(asset.IsNullObject)continue;
                _effectSequences.TryGetValue(EffectHlodKey.For(node,owner),out var sequence);
                if(asset.Sequence is not null && sequence is null){pending++;continue;}
                if(asset.Resource is not null || asset.ObjectLine is not null)
                    UpdateEffectDazzleCollision(PrepareEffectQueryOwner(node,asset,meshOwners,queryMembership),ownerWorld,instance.Radius,
                        node.ActualStartTick,clientTime,sequence?.Current,camera,
                        definition.AssetScale!=1 || definition.HsvShift!=Vector3.Zero
                            ?instance.SourceRadiusScale:null,hierarchySourceScale:instance.SourceRadiusScale);
                if(_effectCulledRoots.Contains(EffectHlodKey.For(node,owner)))continue;
                if(asset.SpecialMaterial is {} special)
                {
                    AppendSpecialEffect(packets,instance,-1,special,asset.Resource!.Key,ownerWorld,lights,clientTime,owner,
                        $"effect-object/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}",ref draws);
                    continue;
                }
                if((asset.Mixed?.RecoveryDiagnostic ?? asset.ObjectLine?.Line.RecoveryDiagnostic) is {} recovery)
                { recovered++; failure ??= $"E{node.Definition.DefinitionId}: {recovery}"; }

                if (_effectHlodErrors.TryGetValue(EffectHlodKey.For(node,owner),out var costError))
                    throw new InvalidDataException(costError);
                var selection = asset.Selection;
                void AdvanceMixedParticles(EnbCurrentRetailMixedEffectAsset mixed)
                {
                    instance.Poses.Record(age, ownerWorld);
                    instance.Streaks ??= new(mixed, instance.Radius, unchecked((uint)node.Sequence));
                    instance.Streaks.Advance(mixed, sequence?.Poses??instance.Poses, instance.Radius,sequence?.Observations.Samples);
                    sequence?.Observations.RetainLatestSample();
                    if(sequence is not null)instance.Poses.RetainLatestSample();
                    _effectStreakInstances.TryAdd(key, new(node, owner, mixed, instance));
                }
                if(asset.ForceSubObjectLod && asset.UsesDetailBudget)
                {
                    if(!_effectHlodSelections.TryGetValue(EffectHlodKey.For(node,owner),out var parentSelection))
                    {pending++;continue;}
                    selection=parentSelection;
                }
                if(asset.UsesDetailBudget && _effectHlodSelections.TryGetValue(EffectHlodKey.For(node,owner),out var compositeSelection))selection=compositeSelection;
                else if(asset.UsesDetailBudget && _effectHlodLevels.TryGetValue(EffectHlodKey.For(node,owner),out int retainedLevel))
                    selection=new(asset.Resource!.HierarchicalLods.Single().Name,retainedLevel);
                PrepareEffectCompositeParticles(node,owner,asset,-1,ownerWorld,instance.Radius,selection,clientTime,sequence?.Current,hierarchySourceScale:instance.SourceRadiusScale);
                if(asset.ModelAttachments is {Count:>0} models)
                {
                    if(asset.Resource!.Meshes.Count==0 && asset.AggregateParticles is {} companionParticles)
                    {
                        instance.Poses.Record(age,ownerWorld);
                        instance.AnimatedParticles.Advance(asset,sequence?.Poses??instance.Poses,instance.Radius,sequence?.Observations.Samples);
                        _effectParticleInstances.TryAdd(key,new(node,owner,companionParticles[0],instance,companionParticles,asset));
                    }
                    for(int modelIndex=0;modelIndex<models.Count;modelIndex++)
                    {
                        var model=models[modelIndex];
                        var pose=model.Pose(asset,Matrix4x4.CreateScale(instance.Radius)*ownerWorld,node.ActualStartTick,clientTime,observed:sequence?.Current);
                        var childSelection=model.Asset.Selection;
                        var childKey=EffectHlodKey.For(node,owner) with {Child=modelIndex};
                        int ParentLevel(int index)
                        {
                            var parent=index<0?asset:models[index].Asset;
                            var selected=index<0?selection:_effectHlodSelections.GetValueOrDefault(EffectHlodKey.For(node,owner) with {Child=index});
                            return selected?.LevelOrdinal??EffectSceneLevel(EffectHlodKey.For(node,owner) with {Child=index},parent);
                        }
                        bool sceneAttached=model.IsSelected(models,ParentLevel);
                        pose.Visible &= sceneAttached;
                        if(!model.PreparesDetail && model.Asset.Resource?.HierarchicalLods.SingleOrDefault() is {} ordinaryHlod)
                            childSelection=new(ordinaryHlod.Name,EffectSceneLevel(childKey,model.Asset));
                        if(model.PreparesDetail && model.Asset.UsesDetailBudget && _effectHlodLevels.TryGetValue(childKey,out int retainedChildLevel))
                            childSelection=new(model.Asset.Resource!.HierarchicalLods.Single().Name,retainedChildLevel);
                        if(model.PreparesDetail && model.Asset.UsesDetailBudget && pose.Visible)
                        {
                            if(!_effectHlodSelections.TryGetValue(childKey,out var chosenChild))
                            {pending++;continue;}
                            childSelection=chosenChild;
                        }
                        var immediateParent=model.ParentModelIndex<0?asset:models[model.ParentModelIndex].Asset;
                        var immediateSelection=model.ParentModelIndex<0?selection:
                            _effectHlodSelections.GetValueOrDefault(EffectHlodKey.For(node,owner) with {Child=model.ParentModelIndex});
                        if(model.LevelOrdinal is null && immediateParent.ForceSubObjectLod && model.Asset.Resource is {HierarchicalLods.Count:1})
                        {
                            //0098DDC7..DDE5 overrides aggregate children after
                            //budgeting, before their Render/visibility gate.
                            var childHlod=model.Asset.Resource!.HierarchicalLods.Single();
                            int requested=immediateSelection?.LevelOrdinal??EffectSceneLevel(EffectHlodKey.For(node,owner) with {Child=model.ParentModelIndex},immediateParent);
                            int forced=Math.Min(Math.Max(requested,0),childHlod.Levels.Count-1); //0098ED30
                            var forcedSelection=new EnbW3dHlodSelection(childHlod.Name,forced);
                            childSelection=forcedSelection;
                            if(model.Asset.UsesDetailBudget)
                            {
                                QueueEffectRenderLevel(childKey,forcedSelection);
                            }
                        }
                        var emitterPose=model.Pose(asset,ownerWorld,node.ActualStartTick,clientTime,instance.Radius,sequence?.Current);
                        PrepareEffectCompositeParticles(node,owner,model.Asset,modelIndex,emitterPose.World,instance.Radius,childSelection,clientTime,parentSceneAttached:sceneAttached);
                        if(!pose.Visible || model.Asset.IsNullObject)continue;
                        if(model.Asset.SpecialMaterial is {} childSpecial)
                        {
                            AppendSpecialEffect(packets,instance,modelIndex,childSpecial,model.Asset.Resource!.Key,emitterPose.World,lights,clientTime,owner,
                                $"effect-object/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}/model/{modelIndex}",ref draws);
                            continue;
                        }
                        var modelMappers = meshOwners!.Children[modelIndex];
                        var skinParentPose=model.Asset.Resource is {HierarchicalLods.Count:0,HierarchyModels.Count:0,Meshes.Count:1} directResource &&
                            directResource.Meshes[0].BoneIndices.Count!=0
                            ? model.ParentPose(asset,Matrix4x4.CreateScale(instance.Radius)*ownerWorld,node.ActualStartTick,clientTime,observed:sequence?.Current):null;
                        EnbW3dDrawInvocation childList;
                        bool childBuilt = _retainedOrdinaryMeshes
                            ? EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,modelMappers.Resource,pose.World,clientTime,
                                EnbRenderPassKind.OrdinaryWorld,childSelection,lights,[],null,meshOwners.PreparationForChild(modelIndex),
                                out childList,out error,skinParentPose,new(emitterPose.World,instance.Radius))
                            : EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,modelMappers.Resource,pose.World,clientTime,
                                EnbRenderPassKind.OrdinaryWorld,childSelection,lights,[],out childList,out error,new(emitterPose.World,instance.Radius),skinParentPose);
                        if(!childBuilt)throw new InvalidDataException(error);
                        if(childList.Dazzles.Count!=0)
                        {
                            var rigidPose=model.Pose(asset,ownerWorld,node.ActualStartTick,clientTime,instance.Radius,sequence?.Current);
                            foreach(var dazzle in childList.Dazzles)
                            {
                                var dazzleKey=(modelIndex,dazzle.HlodName,dazzle.LevelOrdinal,dazzle.MemberOrdinal);
                                if(!meshOwners.Dazzles.TryGetValue(dazzleKey,out var history))
                                    meshOwners.Dazzles.Add(dazzleKey,history=new(meshOwners.ConstructionClock));
                                _frameDazzles.Add((model.Asset.PlaceDazzle(dazzle,rigidPose.World,instance.Radius,node.ActualStartTick,clientTime,hierarchySourceScale:instance.SourceRadiusScale) with {History=history},1,camera,0,depthMaximum));
                                draws++;
                            }
                        }
                        var mapperKey=(definition.BaseAssetId,definition.AssetScale,definition.HsvShift);
                        _effectObjectSharedMapperStarts.TryAdd(mapperKey,instance.MapperStart);
                        int modelPacketStart = packets.Count;
                        foreach(var authored in childList.Packets)
                        {
                            packets.Add((_overbrightMaterials.Apply(authored,asset.OverbrightOff,out _)).ForkTask().SetRenderInstancePath($"effect-object/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}/model/{modelIndex}").SetCurrentRetailMeshModelScale(instance.Radius).SetCurrentRetailMaterialMappers(modelMappers.Mappers).SetCurrentRetailEffectMapperStartMilliseconds(definition.ResetMappers ? instance.MapperStart : _effectObjectSharedMapperStarts[mapperKey]));draws++;
                        }
                        RetainEffectMeshInvocations(packets, modelPacketStart, modelMappers);
                    }
                    if(asset.Resource is null || asset.Resource.Meshes.Count==0 && !asset.Resource.ExternalRenderObjects.Any(e=>e.SpecialMaterialDefinition is not null))
                    {
                        int? childPrimitiveLevel=asset.ForceSubObjectLod?selection?.LevelOrdinal:null;
                        if(asset.Spheres is not null && !ApplyEffectSpheres(packets,node,owner,clientTime,camera,ref draws,childPrimitiveLevel))
                        {pending++;continue;}
                        if(asset.Rings is not null && !ApplyEffectRings(packets,node,owner,clientTime,camera,ref draws,childPrimitiveLevel))
                        {pending++;continue;}
                        if(asset.Mixed is {} childParticles)AdvanceMixedParticles(childParticles);
                        sequence?.Observations.RetainLatestSample();
                        sequence?.Poses.RetainLatestSample();
                        continue;
                    }
                }
                if(asset.ObjectLine is {} line)
                {
                    var lineKey=EffectHlodKey.For(node,owner);
                    if(!_preparedObjectLineKeys.Contains(lineKey)){pending++;continue;}
                    var mapperKey=(definition.BaseAssetId,definition.AssetScale,definition.HsvShift);
                    _effectObjectSharedMapperStarts.TryAdd(mapperKey,instance.MapperStart);
                    uint lineAge=unchecked(mapperTime-(definition.ResetMappers?instance.MapperStart:_effectObjectSharedMapperStarts[mapperKey]));
                    if(line.Build(owner.RenderIdentity,owner.VisualGeneration,owner.GameId,ownerWorld,instance.Radius,camera,lineAge,_effectObjectLineLevels[lineKey]) is {} linePacket)
                    {
                        packets.Add((_overbrightMaterials.Apply(linePacket,asset.OverbrightOff,out _)).ForkTask().SetRenderInstancePath($"effect-object/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}"));
                        draws++;
                    }
                    continue;
                }
                //Standalone class15 owns the same independent scene buffer as
                //an embedded emitter (97DEF0/95E990), including bounds/LOD and
                //same-clock queued births. Keep the separately admitted wide
                //finite-lifetime policy out of this ordinary uint-clock owner.
                var standaloneParticle=asset.Resource is null ? asset.Particle ??
                    (asset.AggregateParticles is {Count:1} one ? one[0] : null) : null;
                if(standaloneParticle is null && asset.StandaloneStreak is {} streakAsset)
                    standaloneParticle=new(streakAsset.Key,streakAsset.Texture,streakAsset.Definition,streakAsset.PivotIndex)
                        {RuntimeTextureName=streakAsset.RuntimeTextureName,RetainedTexture=streakAsset.RetainedTexture};
                if(standaloneParticle is {} direct && direct.Definition.RenderMode <= 4 &&
                    EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(direct.Definition.LifetimeSeconds)<=uint.MaxValue)
                {
                    PrepareStandaloneEffectParticle(node,owner,direct,instance,ownerWorld,clientTime);
                    continue;
                }
                if(asset.StandaloneStreak is {} standalone)
                {
                    if(instance.StandaloneStreak is null)
                    {
                        instance.StandaloneStreak=new(standalone.Definition,instance.Radius,unchecked((uint)node.Sequence));
                        foreach(var pose in instance.Poses.Samples)
                            instance.StandaloneStreak.Advance(pose.Age,Matrix4x4.CreateFromQuaternion(pose.Rotation)*Matrix4x4.CreateTranslation(pose.Position));
                        instance.Poses.RetainLatestSample();
                    }
                    instance.StandaloneStreak.Advance(age,ownerWorld);
                    _effectStandaloneStreakInstances.TryAdd(key,new(node,owner,standalone,instance));
                    RetainEffectEmitterCollision(instance,ownerWorld,camera,clientTime,standalone.Definition.MaximumEmissions,instance.StandaloneStreak.EmittedParticleCount);
                    continue;
                }
                int? forcedPrimitiveLevel=asset.ForceSubObjectLod ? selection?.LevelOrdinal : null;
                if(asset.Spheres is not null && !ApplyEffectSpheres(packets,node,owner,clientTime,camera,ref draws,forcedPrimitiveLevel))
                {pending++;continue;}
                if(asset.Rings is not null && !ApplyEffectRings(packets,node,owner,clientTime,camera,ref draws,forcedPrimitiveLevel))
                {pending++;continue;}
                if((asset.Rings is not null || asset.Spheres is not null) && asset.Mixed is null &&
                    (asset.Resource is null || asset.Resource.Meshes.Count==0 && !asset.Resource.ExternalRenderObjects.Any(e=>e.SpecialMaterialDefinition is not null)))
                {
                    sequence?.Observations.RetainLatestSample();
                    sequence?.Poses.RetainLatestSample();
                    continue;
                }
                if (asset.Mixed is { } mixed)
                {
                    AdvanceMixedParticles(mixed);
                    if(mixed.Resource.Meshes.Count==0 && !mixed.Resource.ExternalRenderObjects.Any(e=>e.DazzleDefinition is not null || e.SpecialMaterialDefinition is not null)) continue;
                }
                if(asset.UsesDetailBudget)
                {
                    var hlodKey = EffectHlodKey.For(node, owner);
                    if (_effectHlodErrors.TryGetValue(hlodKey, out var why)) throw new InvalidDataException(why);
                    // A worker may finish between allocation and this draw.
                    // Wait one frame rather than bypassing the shared allocator.
                    if (!_effectHlodSelections.TryGetValue(hlodKey, out var chosen)) { pending++; continue; }
                    selection = chosen;
                }
                if (asset.AggregateParticles is { } particles)
                {
                    if(instance.ParticlesComplete && asset.Resource is null)continue;
                    if (asset.Resource is null && age >= particles.Max(p => p.EndMilliseconds)) continue;
                    // World-space finite bursts need poses only through their
                    // final birth. Local buffers still follow the current matrix
                    // after emission ends (0097DD90), until removal freezes it.
                    if(sequence is null && (asset.Resource is not null || particles.Any(p=>p.Definition.LocalSpace || p.Definition.MaximumEmissions==0 ||
                        instance.Poses.LastAge<=p.BirthMilliseconds*((long)p.Definition.MaximumEmissions+p.Definition.RuntimeBurstSize-1)/p.Definition.RuntimeBurstSize)))
                        instance.Poses.Record(age, ownerWorld);
                    if (asset.Resource is not null) instance.AnimatedParticles.Advance(asset,sequence?.Poses??instance.Poses,instance.Radius,sequence?.Observations.Samples);
                    if(!_effectParticleInstances.ContainsKey(key))_effectParticleInstances.Add(key, new(node, owner, particles[0], instance, particles,asset.Resource is null ? null : asset));
                    if(_effectParticleInstances[key].Replay is {} finite)
                        for(int i=0;i<particles.Count;i++)finite[i].ObserveFinite(age,b=>instance.Poses.Resolve(b,instance.Radius),
                            b=>instance.Poses.ResolveInheritedVelocity(b,particles[i].Definition.VelocityInheritanceFactor));
                    if(asset.Resource is null && particles.Count==1)
                        RetainEffectEmitterCollision(instance,ownerWorld,camera,clientTime,particles[0].Definition.MaximumEmissions,_effectParticleInstances[key].Replay?[0].EmittedCount??0);
                    if(asset.Resource is null || asset.Resource.Meshes.Count==0 && !asset.Resource.ExternalRenderObjects.Any(e=>e.SpecialMaterialDefinition is not null))
                    {
                        sequence?.Observations.RetainLatestSample();
                        sequence?.Poses.RetainLatestSample();
                        continue;
                    }
                }
                if (asset.Particle is { } particle)
                {
                    if(instance.ParticlesComplete)continue;
                    if (age >= particle.EndMilliseconds) continue;
                    if (particle.Definition.MaximumEmissions == 0)
                    {
                        if (instance.ContinuousParticles is null)
                        {
                            instance.ContinuousParticles = new(particle.Definition,allowInheritedMotion:true,nativeBirthScale:instance.Radius);
                            foreach(var pose in instance.Poses.Samples)
                                instance.ContinuousParticles.Advance(pose.Age,Matrix4x4.CreateFromQuaternion(pose.Rotation)*Matrix4x4.CreateTranslation(pose.Position),true);
                        }
                        instance.ContinuousParticles.Advance(age, ownerWorld, true);
                    }
                    else instance.Birth.Record(age, ownerWorld);
                    if(!_effectParticleInstances.ContainsKey(key))_effectParticleInstances.Add(key, new(node, owner, particle, instance));
                    if(_effectParticleInstances[key].Replay is {} flash && instance.Birth.Capture(age,particle.BirthMilliseconds,instance.Radius))
                        flash[0].ObserveFinite(age,_=>instance.Birth.BirthWorld!.Value);
                    RetainEffectEmitterCollision(instance,ownerWorld,camera,clientTime,particle.Definition.MaximumEmissions,
                        instance.ContinuousParticles?.EmittedCount??_effectParticleInstances[key].Replay?[0].EmittedCount??0);
                    continue;
                }
                if (!node.TryGetRObjDefinition( out _, out error)) throw new InvalidDataException(error);
                var world = Matrix4x4.CreateScale(instance.Radius) * ownerWorld;
                _effectObjectSharedMapperStarts.TryAdd((definition.BaseAssetId, definition.AssetScale, definition.HsvShift),instance.MapperStart);
                var sample=asset.Sample(node.ActualStartTick,clientTime,sequence?.Current);
                EnbW3dDrawInvocation list;
                bool built;
                if(asset.HasConfiguredHierarchy)
                {
                    var resource=meshOwners!.Root!.Resource;
                    var post=new EnbW3dHierarchyState(resource.HierarchicalLods.Single().HierarchyName,ownerWorld,instance.Radius,new Dictionary<int,Matrix4x4>())
                        {PendingHierarchyScale=instance.SourceRadiusScale};
                    built=_retainedOrdinaryMeshes
                        ? EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,resource,world,clientTime,
                            EnbRenderPassKind.OrdinaryWorld,selection,lights,sample,post,meshOwners.RootPreparation,
                            out list,out error,specialTransform:new(ownerWorld,instance.Radius))
                        : EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,resource,world,clientTime,
                            EnbRenderPassKind.OrdinaryWorld,selection,lights,
                            sample,post,out list,out error,new(ownerWorld,instance.Radius));
                }
                else built=_retainedOrdinaryMeshes
                    ? EnbW3dDrawPacketCompiler.TryBuildPrepared(owner,meshOwners!.Root!.Resource,world,clientTime,
                        EnbRenderPassKind.OrdinaryWorld,selection,lights,sample,null,meshOwners.RootPreparation,
                        out list,out error,specialTransform:new(ownerWorld,instance.Radius))
                    : EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,meshOwners!.Root!.Resource,world,clientTime,
                        EnbRenderPassKind.OrdinaryWorld,selection,lights,sample,out list,out error,new(ownerWorld,instance.Radius));
                if(!built)throw new InvalidDataException(error);
                foreach(var dazzle in list.Dazzles)
                {
                    var dazzleKey=(-1,dazzle.HlodName,dazzle.LevelOrdinal,dazzle.MemberOrdinal);
                    if(!meshOwners.Dazzles.TryGetValue(dazzleKey,out var history))
                        meshOwners.Dazzles.Add(dazzleKey,history=new(meshOwners.ConstructionClock));
                    _frameDazzles.Add((asset.PlaceDazzle(dazzle,ownerWorld,instance.Radius,node.ActualStartTick,clientTime,sequence?.Current,instance.SourceRadiusScale) with {History=history},1,camera,0,depthMaximum));
                    draws++;
                }
                int rootPacketStart = packets.Count;
                foreach (var authored in list.Packets)
                {
                    var packet = (_overbrightMaterials.Apply(authored, asset.OverbrightOff, out _)).ForkTask().SetRenderInstancePath($"effect-object/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}").SetCurrentRetailMeshModelScale(instance.Radius).SetCurrentRetailMaterialMappers(meshOwners.Root.Mappers).SetCurrentRetailEffectMapperStartMilliseconds(definition.ResetMappers ? instance.MapperStart : _effectObjectSharedMapperStarts[(definition.BaseAssetId, definition.AssetScale, definition.HsvShift)]);
                    packets.Add(packet); draws++;
                }
                RetainEffectMeshInvocations(packets, rootPacketStart, meshOwners.Root);
                if(asset.Mixed is null && sequence is not null)
                {
                    sequence.Observations.RetainLatestSample();
                    sequence.Poses.RetainLatestSample();
                }
            }
            catch (Exception ex) { blocked++; failure ??= $"E{node.Definition.DefinitionId}: {ex.Message}"; }
            finally
            {
                try{finishParticleConstruction?.Invoke();}
                catch(InvalidDataException ex){blocked++;failure??=ex.Message;}
            }
        }
        return new(draws,pending,blocked,recovered,failure);
    }

    private void DrawPreparedEffectObjects(List<EnbW3dDrawTask> packets,IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        IEnbSceneFrame snapshot,EnbW3dLightEnvironment lights,EnbResolvedRenderCamera camera,uint clientTime,
        EffectObjectDrawState prepared)
    {
        var sceneChanges=ApplyEffectRenderSceneChanges();
        var (draws,pending,blocked,recovered,failure)=prepared;
        blocked+=sceneChanges.Blocked;failure??=sceneChanges.Failure;
        var live=nodes.Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        ApplyEffectCompositeParticles(packets,nodes,snapshot,lights,camera,clientTime,ref draws,ref blocked,ref failure);
        ApplyEffectParticles(packets, live, lights, camera, clientTime, ref draws, ref blocked, ref failure);
        ApplyStandaloneEffectStreaks(packets,nodes.Where(n=>n.Active).Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet(),camera,clientTime,ref draws,ref blocked,ref failure);
        ApplyEffectStreaks(packets, nodes.Where(n => n.Active).Select(n => (n.Root.EffectId, n.Root.InstanceGeneration, n.Sequence)).ToHashSet(), camera, lights, clientTime, ref draws, ref blocked, ref failure);
        _effectObjectVisualStatus = $"effect objects draws {draws} pending {pending} blocked {blocked}" +
            (recovered == 0 ? "" : $" recovered {recovered}") + (failure is null ? "" : $" · {failure}");
        if (_effectStreakInstances.Count != 0)
            _effectObjectVisualStatus += $" · mixed {_effectStreakInstances.Count} streak particles {_effectStreakInstances.Values.Sum(e => e.Instance.Streaks!.Buffers.Sum(b => b.RetainedParticleCount))} HLOD [{string.Join(',', _effectHlodSelections.Values.Select(s => s.LevelOrdinal))}]";
    }

    private static bool IsConfiguredDirectEffectMesh(EnbW3dRenderResource resource,EnbCurrentRetailRObjEffectDefinition definition)
        => (definition.AssetScale!=1 || definition.HsvShift!=Vector3.Zero) && resource.Meshes.Count==1 &&
            resource.HierarchicalLods.Count==0 && resource.HierarchyModels.Count==0 && resource.SpecialMaterials.Count==0;

    private EffectObjectMeshOwners RetainEffectObjectMeshOwners(EnbCurrentRetailEffectOwner node,
        EnbCurrentRetailRObjEffectDefinition definition, EnbCurrentRetailRObjEffectAsset asset, EnbRetailAssetBundle assets, uint clientTime, uint mapperTime)
    {
        var key = (node.Root.EffectId, node.Root.InstanceGeneration, node.Sequence);
        if (!_effectObjectMeshOwners.TryGetValue(key, out var owners))
        {
            uint clock = unchecked(mapperTime - (clientTime - node.ConstructionTick));
            (float Scale,Vector3 Hsv)? retainedFactoryInputs=null;
            EnbCurrentRetailRObjMeshMappers Retain(EnbCurrentRetailRObjEffectAsset modelAsset, int child, string? prototypeName = null,bool sourceOnly=false)
            {
                if(_effectObjectInstanceAssets.TryGetValue(key,out var configuredTask) &&
                    _effectObjectModelAcquisitions.TryGetValue(configuredTask,out var configuredAcquisition) &&
                    configuredAcquisition.FactoryInputs is {} configuredInputs &&
                    (child==-1 || child<configuredAcquisition.Children.Count))
                {
                    retainedFactoryInputs=configuredInputs;
                    var model=child==-1?configuredAcquisition.Models:configuredAcquisition.Children[child];
                    return definition.ResetMappers?model.Localize(clock):model;
                }
                bool inlineComposite=child==-1 && modelAsset.ModelAttachments.Count==0 && modelAsset.Resource is {} inlineResource &&
                    inlineResource.HierarchicalLods.Count==1 && inlineResource.Meshes.Count!=0 &&
                    inlineResource.HierarchicalLods[0].Levels.SelectMany(l=>l.Members).Concat(inlineResource.HierarchicalLods[0].Aggregate?.Members??[])
                        .All(member=>inlineResource.Meshes.Any(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,member.Name)));
                string? directName=child==-1 && modelAsset.Resource is {} directResource && (IsConfiguredDirectEffectMesh(directResource,definition)||inlineComposite)
                    ?modelAsset.FactoryObjectName:null;
                var rawInputs=definition.FactoryInputs??(definition.AssetScale,definition.HsvShift);
                EnbCurrentRetailRObjMeshMappers FinishDirect(ConfiguredDirectMeshVariant variant)
                {
                    retainedFactoryInputs=(variant.Scale,variant.Hsv);
                    return definition.ResetMappers?variant.Model.Localize(clock):variant.Model;
                }
                if(directName is not null && _configuredDirectMeshVariants.TryGetValue(
                    EnbCurrentRetailPrototypeVariantName.Create(directName,rawInputs.Item1,rawInputs.Item2),out var existingDirect))
                    return FinishDirect(existingDirect);
                if(modelAsset.LazyPrototypeSource is {} lazySource &&
                    !_prototypeResources.TryFindPrototypeSource(lazySource.RequestedObjectName,out _))
                {
                    //0045EF80/0091D4B0 loads the whole lazy Object file, then
                    //retries the named prototype. Later consumers share those
                    //published models/mappers rather than a private effect copy.
                    if(!_prototypeResources.TryLoadFile(lazySource,out _,out var loadError,clock))
                        throw new InvalidDataException(loadError);
                }
                //009A3A50 creates repeated named children through0091D4B0;
                //their RenderObj clones share the prototype's mesh models.
                //0048B220 localizes each occurrence when configuring a parent.
                bool configured = definition.AssetScale != 1 || definition.HsvShift != Vector3.Zero;
                string registration=modelAsset.FactoryPrototypeRegistration==0?"":"/registration/"+modelAsset.FactoryPrototypeRegistration.ToString(System.Globalization.CultureInfo.InvariantCulture);
                EffectObjectModelAcquisition? acquired=null;
                if(_effectObjectInstanceAssets.TryGetValue(key,out var preparation))
                {
                    if(child==-1)_effectObjectModelAcquisitions.TryGetValue(preparation,out acquired);
                    else if(prototypeName is not null && _effectObjectAttachmentAcquisitions.TryGetValue(preparation,out var children))
                        children.TryGetValue((prototypeName.ToLowerInvariant(),modelAsset.FactoryPrototypeRegistration),out acquired);
                }
                //0098C420/0098C7A0 resolve children for each new parent.
                //The parent registration alone does not identify its models.
                if(acquired is not null)registration+="/meshes/"+acquired.MeshRegistrations;
                else if(modelAsset.RegisteredBaseMeshSources.Count!=0)
                    registration+="/meshes/"+string.Join(',',modelAsset.RegisteredBaseMeshSources.Select(s=>s.PrototypeRegistration.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                string occurrence=child.ToString(System.Globalization.CultureInfo.InvariantCulture)+registration;
                var sourceKey = prototypeName is not null
                    ? (0, 1f, Vector3.Zero, EnbCurrentRetailPrototypeVariantName.Create(prototypeName!, 1, Vector3.Zero)+registration)
                    : (definition.BaseAssetId, 1f, Vector3.Zero, occurrence);
                if (!_effectObjectMeshPrototypes.TryGetValue(sourceKey, out var prototype))
                {
                    var prepared=modelAsset.UnconfiguredMeshResource ?? modelAsset.Resource!;
                    if(acquired is not null)
                    {
                        prepared=prepared.Copy().SetMeshes(acquired.Models.Resource.Meshes);
                        prototype=acquired.Models.RetainRenderObjects(prepared);
                    }
                    else
                    {
                    var registeredMeshes=new List<EnbW3dRenderMesh?>();
                    foreach(var mesh in prepared.Meshes)
                    {
                        string name=EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh);
                        if(modelAsset.RegisteredBaseMeshSources.Count!=0)
                        {
                            var captured=modelAsset.RegisteredBaseMeshSources.FirstOrDefault(s=>s.RequestedObjectName.Equals(name,StringComparison.OrdinalIgnoreCase));
                            registeredMeshes.Add(captured?.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh} capturedEntry &&
                                _prototypeResources.TryGetRegisteredPrototype(captured,out var capturedResource)?capturedResource.Meshes[capturedEntry.Ordinal]:null);
                            continue;
                        }
                        if(!_prototypeResources.TryFindPrototypeSource(name,out var source) ||
                            EnbW3dRenderResourceKey.From(source.ResourceLocation)!=(modelAsset.RegisteredBaseMeshSourceKey??prepared.Key) ||
                            source.RegisteredPrototype is not {Kind:EnbW3dPrototypeKind.Mesh} entry ||
                            !_prototypeResources.TryGetRegisteredPrototype(source,out var published))registeredMeshes.Add(null);
                        else registeredMeshes.Add(published.Meshes[entry.Ordinal]);
                    }
                    if(registeredMeshes.Any(mesh=>mesh is not null))
                    {
                        //0091D4B0/0093ED00 retain the already registered source.
                        //Each named mesh has its own prototype/model lifetime;
                        //an absent sibling must not discard a valid match.
                        //Adopt only proven same-file mesh entries here; the
                        //remaining RObj factory lookup/preload path is separate.
                        prepared=EnbCurrentRetailMeshTexturePreparation.Prepare(prepared.Copy().SetMeshes(prepared.Meshes.Select((mesh,index)=>registeredMeshes[index]??mesh).ToArray()),assets);
                        prototype=new(prepared,clock,CameraMeshBatches,_prototypeResources,registeredMeshes);
                    }
                    else prototype = new(prepared, clock, CameraMeshBatches);
                    }
                    _effectObjectMeshPrototypes.Add(sourceKey, prototype);
                }
                if(sourceOnly)return prototype;
                if (configured)
                {
                    if(directName is not null)
                        return FinishDirect(RetainConfiguredDirectMeshVariant(directName,rawInputs.Item1,rawInputs.Item2,assets,clock,
                            ()=>prototype.RetainRenderObjects(prototype.Resource),composite:inlineComposite));
                    var prototypeKey = (definition.BaseAssetId, definition.AssetScale, definition.HsvShift,
                        occurrence);
                    if (!_effectObjectMeshPrototypes.TryGetValue(prototypeKey, out var variant))
                    {
                        bool directMesh=IsConfiguredDirectEffectMesh(prototype.Resource,definition);
                        bool composite=prototype.Resource.HierarchicalLods.Count!=0 || prototype.Resource.HierarchyModels.Count!=0;
                        variant = prototype.Configure(clock, assets.Textures, definition.HsvShift,
                            directMesh||composite?definition.AssetScale:null,composite);
                        _effectObjectMeshPrototypes.Add(prototypeKey, variant);
                    }
                    prototype = variant;
                }
                return definition.ResetMappers ? prototype.Localize(clock) : prototype;
            }
            owners = new(definition.ResetMappers) {ConstructionClock=clock};
            try
            {
                if(asset.Resource is not null && asset.ModelAttachments.Count!=0 &&
                    (definition.AssetScale!=1 || definition.HsvShift!=Vector3.Zero))
                {
                    var raw=definition.FactoryInputs??(definition.AssetScale,definition.HsvShift);
                    var groupKey=(EnbCurrentRetailPrototypeVariantName.Create(asset.FactoryObjectName??"",raw.Item1,raw.Item2),
                        asset.FactoryObjectName is null?asset:null);
                    if(!_effectConfiguredMeshGroups.TryGetValue(groupKey,out var group))
                    {
                        var sources=new List<EnbCurrentRetailRObjMeshMappers>{Retain(asset,-1,sourceOnly:true)};
                        for(int i=0;i<asset.ModelAttachments.Count;i++)
                            sources.Add(Retain(asset.ModelAttachments[i].Asset,i,asset.ModelAttachments[i].Name,sourceOnly:true));
                        group=new(EnbCurrentRetailRObjMeshMappers.ConfigureCompositeGroup(sources,clock,assets.Textures,
                            definition.HsvShift,definition.AssetScale),definition.AssetScale,definition.HsvShift);
                        _effectConfiguredMeshGroups.Add(groupKey,group);
                    }
                    //006EAFA0/006EA830 localize only after the entire configured
                    //factory has completed. Child copies must not interleave.
                    owners.Root=definition.ResetMappers?group.Models[0].Localize(clock):group.Models[0];
                    foreach(var model in group.Models.Skip(1))owners.Children.Add(definition.ResetMappers?model.Localize(clock):model);
                    retainedFactoryInputs=(group.Scale,group.Hsv);
                }
                else
                {
                    if (asset.Resource is not null) owners.Root = Retain(asset, -1);
                    for (int i = 0; i < asset.ModelAttachments.Count; i++)
                        owners.Children.Add(Retain(asset.ModelAttachments[i].Asset, i, asset.ModelAttachments[i].Name));
                }
                owners.FactoryInputs=retainedFactoryInputs;
                _effectObjectMeshOwners.Add(key, owners);
            }
            catch { owners.Dispose(); throw; }
        }
        if (node.Active && !owners.Activated)
        {
            if (definition.ResetMappers)
            {
                uint clock = unchecked(mapperTime - (clientTime - node.ActualStartTick));
                owners.Root?.Activate(clock);
                foreach (var child in owners.Children) child.Activate(clock);
            }
            owners.Activated = true;
        }
        return owners;
    }

    private static void RetainEffectMeshInvocations(List<EnbW3dDrawTask> packets, int start,
        EnbCurrentRetailRObjMeshMappers owner)
    {
        //Each MeshClass occurrence reaches0093F390, regardless of whether its
        //RenderObj was created by the ordinary scene or an RObj effect factory.
        var source = packets.GetRange(start, packets.Count - start);
        foreach (var group in source.Where(p=>p.SpecialMaterial is null).GroupBy(p => (p.Mesh, p.OrderKey.BindingOrdinal - p.Material.Source.Ordinal)))
        {
            var values = group.ToArray();
            var appearance = owner.CreateAppearance(values[0].Mesh, values.Select(p => p.Material.Source).ToArray());
            var invocation = EnbCurrentRetailMeshInvocation.Create(values[0], appearance);
            foreach (var packet in values)
            {
                int index = packets.FindIndex(start, p => ReferenceEquals(p, packet));
                packets[index] = (packet).ForkTask().SetCurrentRetailMeshInvocation(invocation);
            }
        }
    }

    private sealed class EffectObjectMeshOwners(bool local) : IDisposable
    {
        // Effect instances own their definitions independently of the shared
        // material/mapper prototype. Pose and visibility remain invocation data.
        public EnbW3dMeshOccurrencePreparation RootPreparation { get; } = new();
        private readonly Dictionary<int,EnbW3dMeshOccurrencePreparation> _childPreparations = [];
        public EnbW3dMeshOccurrencePreparation PreparationForChild(int index)
        {
            if (!_childPreparations.TryGetValue(index, out var prepared))
                _childPreparations.Add(index, prepared = new());
            return prepared;
        }

        public (float Scale,Vector3 Hsv)? FactoryInputs;
        public EnbCurrentRetailRObjMeshMappers? Root;
        public List<EnbCurrentRetailRObjMeshMappers> Children { get; } = [];
        public uint ConstructionClock;
        public Dictionary<(int Child,string Hlod,int Level,int Member),EnbRetailDazzleHistoryState> Dazzles { get; }=[];
        public bool Activated;
        public readonly EffectQueryOwner Query = new();
        public void Dispose()
        {
            if (!local) return;
            Root?.Dispose();
            foreach (var child in Children) child.Dispose();
        }
    }

    private sealed record EffectObjectInstance(ulong OwnerIdentity, ulong OwnerGeneration, float Radius, uint MapperStart) : IDisposable
    {
        //006EF160 supplies this factor separately, after factory geometry scale.
        public float SourceRadiusScale {get;init;}=1;
        //006EF040 releases the retained RenderObj; A09390 releases class24
        //delegates/resources. Inactive effects retain them until destruction.
        public Dictionary<int,EnbRetailSpecialMaterialInstance> SpecialMaterials {get;}=[];
        public void Dispose()
        {
            foreach(var material in SpecialMaterials.Values)material.Dispose();
            SpecialMaterials.Clear();
        }
        public bool MissingFactoryScaleResolved {get;init;}
        public bool DiscardedParticlesConstructed {get;set;}
        public bool ParticlesComplete { get; set; }
        public uint? EmitterRemovalClock { get; set; }
        public EnbCurrentRetailParticleBirthRuntime Birth { get; } = new();
        public EnbCurrentRetailParticlePoseHistory Poses { get; } = new();
        public EnbCurrentRetailAnimatedParticleRuntime AnimatedParticles { get; } = new();
        public EnbCurrentRetailMixedParticleRuntime? Streaks { get; set; }
        public EnbCurrentRetailStreakBufferRuntime? StandaloneStreak { get; set; }
        public EnbCurrentRetailShipParticleRuntime? ContinuousParticles { get; set; }
    }
}
