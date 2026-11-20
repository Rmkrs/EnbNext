using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(uint, ulong, ulong), EffectStreakInstance> _effectStreakInstances = [];
    private readonly Dictionary<EffectHlodKey, int> _effectHlodLevels = [];
    private readonly Dictionary<EffectHlodKey, EnbW3dHlodSelection> _effectHlodSelections = [];
    private readonly Dictionary<EffectHlodKey, string> _effectHlodErrors = [];
    private readonly Dictionary<EffectHlodKey,int> _effectObjectLineLevels=[];
    private readonly List<(EffectHlodKey Key,EnbRenderDetailOwner Candidate)> _effectObjectLineCandidates=[];
    private readonly HashSet<EffectHlodKey> _preparedObjectLineKeys=[];

    private readonly HashSet<(int,Vector3)> _requiredEffectObjectAssets=[];
    private readonly Dictionary<(int,Vector3),string> _effectObjectAssetRegistrations=[];
    private readonly Dictionary<(int,Vector3),long> _effectObjectAssetNamespaceVersions=[];
    private readonly Dictionary<(uint,ulong,ulong),Task<EnbCurrentRetailRObjEffectAsset>> _effectObjectInstanceAssets=[];
    private Task<EnbCurrentRetailRObjEffectAsset>? LoadEffectObject(EnbRetailAssetBundle assets, int id, Vector3 hsv,bool prewarm=false,
        EnbCurrentRetailEffectOwner? node=null,uint? currentClientTime=null)
    {
        var instanceKey=node is null?default:(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
        if(node is not null && _effectObjectInstanceAssets.TryGetValue(instanceKey,out var retained))return retained;
        var factoryInputs=node is not null && node.TryGetRObjDefinition(out var factoryDefinition,out _)
            ?factoryDefinition.FactoryInputs??(factoryDefinition.AssetScale,factoryDefinition.HsvShift):(1f,hsv);
        if(!prewarm)
        {
            Task<EnbCurrentRetailRObjEffectAsset>? configuredPrimitive=null;
            try
            {
                if(TryAcquireConfiguredEffectMesh(assets,id,factoryInputs,out var meshAsset,out var meshOwner))
                {
                    configuredPrimitive=Task.FromResult(meshAsset);
                    _effectObjectModelAcquisitions.Add(configuredPrimitive,meshOwner);
                }
                else if(TryAcquireConfiguredEffectPrimitive(assets,id,factoryInputs,out var primitive))
                    configuredPrimitive=Task.FromResult(primitive);
            }
            catch(Exception ex){configuredPrimitive=Task.FromException<EnbCurrentRetailRObjEffectAsset>(ex);}
            if(configuredPrimitive is not null)
            {
                if(node is not null)_effectObjectInstanceAssets.Add(instanceKey,configuredPrimitive);
                return configuredPrimitive;
            }
        }
        var assetKey=(id,hsv);
        var registeredPrototypes=_prototypeResources.CapturePrototypeSources(out long namespaceVersion);
        uint publicationClock=ReadMapperClock();
        if(node is not null && currentClientTime is {} publicationNow)
            publicationClock=unchecked(publicationClock-(publicationNow-node.ConstructionTick));
        var publicationContext=prewarm?null:CreateLiveWorldPublicationContext(publicationClock);
        EnbResolvedW3dResource? registered=null;
        var baseDefinition=assets.PresentationCatalog?.ResolveBaseAsset(id);
        if(baseDefinition is {Kind:EnbBaseAssetDefinitionKind.FileName,Name:{} name} &&
            _prototypeResources.TryFindPrototypeSource(name,out var source))registered=source;
        EnbResolvedW3dResource? registeredMissing=null;
        if(baseDefinition is {Kind:EnbBaseAssetDefinitionKind.FileName or EnbBaseAssetDefinitionKind.Missing} &&
            _prototypeResources.TryFindPrototypeSource("missing",out var missingSource))registeredMissing=missingSource;
        //The absent/-1 selector takes the ordinary "missing" acquisition
        //directly. Use the same selector as worker preparation, so its named
        //children are retained before a later frame can remove prototypes.
        var registeredAcquisition=registered??(baseDefinition is not null &&
            EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(baseDefinition)?registeredMissing:null);
        Dictionary<string,EnbResolvedW3dResource>? rslSources=null;
        if(baseDefinition is {Kind:EnbBaseAssetDefinitionKind.Rsl,RslId:{} rslId} &&
            assets.RenderStates?.TryGetLibrary(rslId,out var library)==true)
            foreach(var state in library.States.Where(s=>s.Type==EnbRslStateType.Object && s.Object is not null))
                if(_prototypeResources.TryFindPrototypeSource(state.Object!,out var rslSource))
                    (rslSources??=new(StringComparer.OrdinalIgnoreCase))[state.Object!]=rslSource;
        string registration=rslSources is null
            ? (registered?.PrototypeRegistration??0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "rsl/"+string.Join(',',rslSources.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase)
                .Select(p=>p.Value.PrototypeRegistration.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if(registeredMissing is not null)registration+="/missing/"+registeredMissing.PrototypeRegistration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(EnbCurrentRetailPrototypeVariantName.ChangesScale(factoryInputs.Item1)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(factoryInputs.Item2))
            registration+="/factory/"+EnbCurrentRetailPrototypeVariantName.Create(baseDefinition?.Name??"",factoryInputs.Item1,factoryInputs.Item2);
        //An unregistered root may be an aggregate whose base already exists
        //under an unrelated filename. Do not reuse preparation across changes
        //to that namespace; live instances above retain their original task.
        bool usesNamespace=EnbCurrentRetailPrototypeVariantName.ChangesScale(factoryInputs.Item1) ||
            EnbCurrentRetailPrototypeVariantName.ChangesHsv(factoryInputs.Item2) || (registered is null && baseDefinition?.Kind is EnbBaseAssetDefinitionKind.FileName or EnbBaseAssetDefinitionKind.Rsl) ||
            registered?.AggregatePrototype is not null ||
            registeredAcquisition?.RegisteredPrototype?.Kind is EnbW3dPrototypeKind.HierarchicalLod or EnbW3dPrototypeKind.HierarchyModel;
        Task<EnbCurrentRetailRObjEffectAsset> Acquire(Task<EnbCurrentRetailRObjEffectAsset> preparation)
        {
            try
            {
                var selected=_effectObjectPreparationSources.GetValueOrDefault(preparation);
                if(!prewarm && node is not null && selected?.PublicationsApplied==true)
                    _effectObjectPublishedSources.Add(instanceKey);
                //Speculative asset prewarm is not a native RenderObj
                //construction. Pin models and run child factories only when
                //the effect is actually acquired on the render thread.
                if(!prewarm)RetainRegisteredEffectPreparation(preparation,selected?.Source??registeredAcquisition,assets,selected?.ModelChildren??registeredPrototypes);
            }
            catch(Exception ex) {preparation=Task.FromException<EnbCurrentRetailRObjEffectAsset>(ex);}
            if(node is null)return preparation;
            //Preserve the existing per-effect failure boundary when retaining
            //an optional model fails; the render consumer reports faulted work.
            _effectObjectInstanceAssets.Add(instanceKey,preparation);
            return preparation;
        }
        if (_effectObjectAssets.TryGetValue(assetKey, out var existing) &&
            (prewarm || _effectObjectPreparationSources.GetValueOrDefault(existing)?.PublicationsApplied==true) &&
            _effectObjectAssetRegistrations.GetValueOrDefault(assetKey,"0")==registration &&
            (!usesNamespace || !_effectObjectAssetNamespaceVersions.TryGetValue(assetKey,out long preparedVersion) || preparedVersion==namespaceVersion))
        {
            return Acquire(existing);
        }
        if (prewarm && _effectObjectAssets.Count >= 32) return null;
        _effectObjectAssets.Remove(assetKey);
        EnbEffectAssetCacheAdmission.TrimForAdmission(_effectObjectAssets,_requiredEffectObjectAssets,32);
        foreach(var stale in _effectObjectAssetRegistrations.Keys.Where(key=>!_effectObjectAssets.ContainsKey(key)).ToArray())
            _effectObjectAssetRegistrations.Remove(stale);
        foreach(var stale in _effectObjectAssetNamespaceVersions.Keys.Where(key=>!_effectObjectAssets.ContainsKey(key)).ToArray())
            _effectObjectAssetNamespaceVersions.Remove(stale);
        //Capture NAME lookup on the render thread. The immutable source is
        //safe for worker preparation; existing effect clones pin their task
        //when the registry later publishes or replaces a named prototype.
        Task<EnbCurrentRetailRObjEffectAsset> task;
        try
        {
            var selected=EnbCurrentRetailRObjEffectAsset.PrepareOwnedFactory(assets,id,hsv,registered,registeredPrototypes,registeredMissing,publicationContext,
                prewarm?null:PostprocessEffectNamedMesh,factoryInputs);
            if(!prewarm&&TryPublishEffectPrimitive(assets,id,selected,factoryInputs,out var primitive))task=Task.FromResult(primitive);
            else if(!prewarm&&TryPublishEffectMesh(assets,id,selected,factoryInputs,out var mesh,out var meshOwner))
            {
                task=Task.FromResult(mesh);
                _effectObjectModelAcquisitions.Add(task,meshOwner);
            }
            else task=LoadVisualAsset(selected.Build);
            if(selected.Source is not null || selected.PrototypePublications.Count!=0)_effectObjectPreparationSources.Add(task,selected);
        }
        catch(Exception ex) {task=Task.FromException<EnbCurrentRetailRObjEffectAsset>(ex);}
        _effectObjectAssets.Add(assetKey, task);
        _effectObjectAssetRegistrations[assetKey]=registration;
        _effectObjectAssetNamespaceVersions[assetKey]=namespaceVersion;
        return Acquire(task);
    }

    private void RetainRegisteredEffectPreparation(Task<EnbCurrentRetailRObjEffectAsset> task,
        EnbResolvedW3dResource? source, EnbRetailAssetBundle assets,IReadOnlyDictionary<string,EnbResolvedW3dResource> registeredPrototypes)
    {
        if(source is null || task.IsFaulted)return;
        if(!_effectObjectModelAcquisitions.ContainsKey(task) && AcquireEffectPreparationModels(source,assets) is {} root)
            _effectObjectModelAcquisitions.Add(task,root);
        if(_effectObjectAttachmentAcquisitions.ContainsKey(task) || source.CurrentRetailAggregate is not {} aggregate)return;
        var children=new Dictionary<(string,long),EffectObjectModelAcquisition>();
        _effectObjectAttachmentAcquisitions.Add(task,children);
        //009A3A50 acquires each authored attachment independently through
        //0091D4B0. Pin only selected named model children; repeated
        //occurrences share models and localize their own appearance later.
        foreach(var attachment in EnbCurrentRetailRObjEffectAsset.EnumerateRetainedModelChildren(source,registeredPrototypes))
        {
            var child=attachment.Source;
            if(
                (child.CurrentRetailAggregate?.BaseRegisteredPrototype??child.RegisteredPrototype)?.Kind is EnbW3dPrototypeKind.HierarchyModel or EnbW3dPrototypeKind.HierarchicalLod or EnbW3dPrototypeKind.Mesh)
            {
                var key=(attachment.Name.ToLowerInvariant(),child.PrototypeRegistration);
                if(!children.ContainsKey(key) && AcquireEffectPreparationModels(child,assets) is {} acquired)children.Add(key,acquired);
            }
        }
    }

    private void PostprocessEffectNamedMesh(EnbResolvedW3dResource source)
    {
        if(source.CurrentRetailAggregate is {} aggregate)
            source=source with {CurrentRetailAggregate=null,RequestedObjectName=aggregate.BaseRequestedObjectName,
                LogicalFileName=aggregate.BaseLogicalFileName,ResourceLocation=aggregate.BaseResourceLocation,
                LooseFilePath=aggregate.BaseLooseFilePath,RegisteredPrototype=aggregate.BaseRegisteredPrototype,
                PrototypeRegistration=aggregate.BasePrototypeRegistration};
        if(!_prototypeResources.TryGetRegisteredPrototype(source,out var resource) &&
            !EnbW3dRenderResourceCompiler.TryCompile(source,out resource,out var error))throw new InvalidDataException(error);
        if(source.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh} entry)
            PostprocessOrdinaryNamedMesh(resource.Meshes[entry.Ordinal]);
        else foreach(var mesh in resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,source.RequestedObjectName)))
            PostprocessOrdinaryNamedMesh(mesh);
    }

    private EffectObjectModelAcquisition? AcquireEffectPreparationModels(EnbResolvedW3dResource source,EnbRetailAssetBundle assets)
    {
        if(source.CurrentRetailAggregate is {} aggregate)
            source=source with {CurrentRetailAggregate=null,RequestedObjectName=aggregate.BaseRequestedObjectName,
                LogicalFileName=aggregate.BaseLogicalFileName,ResourceLocation=aggregate.BaseResourceLocation,
                LooseFilePath=aggregate.BaseLooseFilePath,RegisteredPrototype=aggregate.BaseRegisteredPrototype,
                PrototypeRegistration=aggregate.BasePrototypeRegistration};
        if(!_prototypeResources.TryGetRegisteredPrototype(source,out var resource) &&
            !EnbW3dRenderResourceCompiler.TryCompile(source,out resource,out var error))throw new InvalidDataException(error);
        var selected=source.RegisteredPrototype;
        if(selected is null)
            foreach(var candidate in source.Model.PrototypeEntries)
                if(EnbW3dRenderResourceCache.PrototypeName(resource,candidate).Equals(source.RequestedObjectName,StringComparison.OrdinalIgnoreCase))
                {selected=candidate;break;}
        if(selected is not {} entry)return null;
        EnbW3dRenderMesh[] meshes;
        IEnumerable<string> childNames=[];
        switch(entry.Kind)
        {
            case EnbW3dPrototypeKind.Mesh:
                meshes=[resource.Meshes[entry.Ordinal]];
                break;
            case EnbW3dPrototypeKind.HierarchicalLod:
                var hlod=resource.HierarchicalLods[entry.Ordinal];
                childNames=hlod.Levels.SelectMany(level=>level.Members).Concat(hlod.Aggregate?.Members??[]).Select(member=>member.Name);
                meshes=resource.Meshes.Where(m=>hlod.Levels.SelectMany(l=>l.Members).Concat(hlod.Aggregate?.Members??[])
                    .Any(n=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,n.Name))).ToArray();break;
            case EnbW3dPrototypeKind.HierarchyModel:
                var model=resource.HierarchyModels[entry.Ordinal];
                childNames=model.Nodes.Select(node=>node.RenderObjectName);
                meshes=resource.Meshes.Where(m=>model.Nodes.Any(n=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,n.RenderObjectName))).ToArray();break;
            default:return null;
        }
        var registeredMeshes=new List<EnbW3dRenderMesh?>();
        var preparedMeshes=new List<EnbW3dRenderMesh>();
        var meshRegistrations=new List<string>();
        //98C420/98C7A0 construct children in retained member order through
        //48B0F0. Geometry generation consumes the construction CRT stream,
        //so it must precede the descriptor-order material projection below.
        foreach(string name in childNames)
            if(_prototypeResources.TryFindPrototypeSource(name,out var namedChild) &&
                namedChild.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh} namedEntry &&
                _prototypeResources.TryGetRegisteredPrototype(namedChild,out var namedResource))
                PostprocessOrdinaryNamedMesh(namedResource.Meshes[namedEntry.Ordinal]);
        //Registered Mesh children need not have a chunk in the parent's file.
        //0091D370 retains the first publication of a qualified name. Match
        //worker preparation's table before retaining pending model owners.
        var meshNames=meshes.Select(EnbW3dRenderObjectNameSemantics.RegisteredMeshName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach(string name in childNames)
            if(!meshNames.Contains(name,StringComparer.OrdinalIgnoreCase) &&
                _prototypeResources.TryFindPrototypeSource(name,out var childSource) && childSource.RegisteredPrototype?.Kind==EnbW3dPrototypeKind.Mesh)
                meshNames.Add(name);
        foreach(string name in meshNames)
        {
            if(!_prototypeResources.TryFindPrototypeSource(name,out var meshSource) ||
                meshSource.RegisteredPrototype is not {Kind:EnbW3dPrototypeKind.Mesh} meshEntry ||
                !_prototypeResources.TryGetRegisteredPrototype(meshSource,out var published))
            {
                //0098C8DD..0098C9A0 skips only the failed connection. Keep
                //each successful sibling acquisition independently. The local
                //unregistered mesh retains its existing preparation path.
                preparedMeshes.Add(meshes.First(m=>EnbW3dRenderObjectNameSemantics.RegisteredMeshName(m).Equals(name,StringComparison.OrdinalIgnoreCase)));
                registeredMeshes.Add(null);
                meshRegistrations.Add("0");
                continue;
            }
            registeredMeshes.Add(published.Meshes[meshEntry.Ordinal]);
            preparedMeshes.Add(published.Meshes[meshEntry.Ordinal]);
            meshRegistrations.Add(meshSource.PrototypeRegistration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if(!registeredMeshes.Any(mesh=>mesh is not null))return null;
        //0091D4B0 acquires the RenderObj before returning it to the caller.
        //Pin only this selected root's models, before a later frame can remove
        //their named prototypes while immutable asset preparation is pending.
        var prepared=EnbCurrentRetailMeshTexturePreparation.Prepare(resource.Copy().SetMeshes(preparedMeshes),assets);
        return new(new(prepared,ReadMapperClock(),CameraMeshBatches,_prototypeResources,registeredMeshes),string.Join(',',meshRegistrations));
    }

    private List<(EffectHlodKey Key, EnbHlodDetailOwner Candidate, float FixedCost)> PrepareEffectHlods(
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes, IEnbSceneFrame snapshot, EnbRetailAssetBundle assets,
        IReadOnlyDictionary<ulong, float> radii, EnbResolvedRenderCamera camera, uint currentClientTime, out float fixedEffectCost)
    {
        fixedEffectCost=0;
        _effectObjectLineCandidates.Clear();_preparedObjectLineKeys.Clear();
        _requiredEffectObjectAssets.Clear();
        foreach(var node in nodes)
            if(node.TryGetRObjDefinition(out var needed,out _))
                _requiredEffectObjectAssets.Add((needed.BaseAssetId,needed.HsvShift));
        _effectHlodSelections.Clear(); _effectHlodErrors.Clear();_effectCulledRoots.Clear();
        for(int sourceIndex=0;sourceIndex<nodes.Count;sourceIndex++)nodes[sourceIndex].UpdateSource(SceneObjects(snapshot));
        var radiusOwners = SceneObjects(snapshot).Concat(nodes.Select(n=>n.Root.LastSourceObject).OfType<IEnbRenderObject>())
            .Concat(_combatLookTargets.Values.Select(t=>t.Object).OfType<IEnbRenderObject>()).ToArray();
        var radiusIds = radiusOwners.Select(o=>o.RenderIdentity).ToHashSet();
        foreach(var id in _lastEffectObjectRadii.Keys.Where(id=>!radiusIds.Contains(id)).ToArray()) _lastEffectObjectRadii.Remove(id);
        foreach(var owner in radiusOwners)
            if(!_lastEffectObjectRadii.ContainsKey(owner.RenderIdentity) && !HasCurrentRetailShipLegoComposition(owner) &&
                assets.ClientBaseAssets?.TryResolve(owner.BaseAsset,out var geometry)==true)
            {
                float radius=(geometry.Radius+geometry.RadiusAdjust)*owner.Scale;
                if(float.IsFinite(radius)) _lastEffectObjectRadii[owner.RenderIdentity]=radius<0?0:radius;
            }
        var result = new List<(EffectHlodKey, EnbHlodDetailOwner, float)>();
        void AddCandidate(EffectHlodKey key,EnbHlodDetailOwner candidate,float cost)
        {
            //98E9A0 applies the minimum-level setter before96F0C0.
            SetEffectHlodSceneLevel(key,candidate.CurrentLevelOrdinal);
            result.Add((key,candidate,cost));
        }
        var used = new HashSet<EffectHlodKey>();
        foreach (var node in nodes.Where(n => n.Active))
        {
            if (!node.TryGetRObjDefinition( out var definition, out _)) continue;
            var load = LoadEffectObject(assets, definition.BaseAssetId, definition.HsvShift,node:node,currentClientTime:currentClientTime);
            if (load?.IsCompletedSuccessfully != true) continue;
            var retainedAsset=ResolveCompositeAsset(node,load.Result,out var boundsOwner);
            if (retainedAsset.RslOwner is not null) continue;
            float requestedScale=definition.AssetScale;
            definition=retainedAsset.ResolveFactoryDefinition(definition);
            if(_effectObjectModelAcquisitions.TryGetValue(load,out var configuredModel) && configuredModel.FactoryInputs is {} retainedInputs)
                definition=definition with {AssetScale=retainedInputs.Scale,HsvShift=retainedInputs.Hsv};
            var owner = ResolveCombatResourceOwner(node, SceneObjects(snapshot));
            if (owner?.Spatial?.Transform is not { } spatial) continue;
            var key = EffectHlodKey.For(node, owner); used.Add(key);
            try
            {
                if (!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial, out var rigid, out var error))
                    throw new InvalidDataException(error);
                // The object setter applies radius once (006EF160). Budgeting
                // must use the live effect instance's scale after its owner is
                // removed or changes model, just as draw compilation does.
                float radius = _effectObjectInstances.TryGetValue((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence),out var instance) &&
                    instance.OwnerIdentity==owner.RenderIdentity ? instance.Radius /
                        (retainedAsset.IgnoresRequestedFactoryInputs && !instance.MissingFactoryScaleResolved?requestedScale:1) :
                    ResolveCombatRenderScale(node,definition,radii.GetValueOrDefault(owner.RenderIdentity));
                float hierarchySourceScale=instance is not null && instance.OwnerIdentity==owner.RenderIdentity
                    ?instance.SourceRadiusScale:ResolveCombatRenderScale(node,definition with {AssetScale=1},radii.GetValueOrDefault(owner.RenderIdentity));
                rigid=ResolveCombatEffectPose(node,rigid);
                rigid=PlaceCombatEffect(node,definition,rigid,camera,radii.GetValueOrDefault(owner.RenderIdentity));
                //006EF250 publishes the current effect transform before scene
                //bounds/LOD traversal. Refresh existing standalone buffers here;
                //construction and callback attachment still occur in Apply.
                if(_shipExhaustOwner is null && instance is not null &&
                    _effectCompositeParticles.TryGetValue((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence,-2),out var standalone) &&
                    ReferenceEquals(standalone.Standalone,instance))
                {
                    var particle=standalone.Particles[0];particle.World=rigid;
                    particle.State?.UpdateTransform(rigid);
                }
                if(retainedAsset.ObjectLine is not null)
                {
                    _preparedObjectLineKeys.Add(key);
                    _effectObjectLineLevels.TryAdd(key,EnbSegLineDetailBudgetSemantics.CurrentRetailInitialLevelOrdinal);
                }
                if(!AdmitEffectSimpleRoot(key,retainedAsset,configuredModel?.Models.Resource??retainedAsset.Resource,
                    rigid,radius,hierarchySourceScale,configuredModel?.FactoryInputs is not null,camera))continue;
                if(retainedAsset.ObjectLine is {} objectLine)
                {
                    if(!EnbSegLineDetailBudgetSemantics.TryComputeProjectedDetail(camera,objectLine.Bounds(rigid,radius),out float detail))
                        throw new InvalidDataException("Object line projected detail unavailable");
                    _effectObjectLineLevels.TryAdd(key,EnbSegLineDetailBudgetSemantics.CurrentRetailInitialLevelOrdinal);
                    uint pointCount=(uint)objectLine.Points.Count;
                    if(objectLine.Line.Subdivisions==0)fixedEffectCost+=EnbSegLineDetailBudgetSemantics.FixedRenderCost(pointCount);
                    else _effectObjectLineCandidates.Add((key,EnbSegLineDetailBudgetSemantics.CreateCandidate($"object-line/{key}",pointCount,
                        (uint)objectLine.Line.Subdivisions,_effectObjectLineLevels[key],detail)));
                    _preparedObjectLineKeys.Add(key);
                    continue;
                }
                if(retainedAsset.IsNullObject)
                {
                    //941330/9415E0 create visible no-draw objects (flags601).
                    //Their +104/942020 ->+114/942040 substitutes1e-6 for
                    //the zero integer cost. Absence of geometry is not absence
                    //of a scene member. RSL-owned roots are budgeted separately.
                    fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback;
                    continue;
                }
                if(retainedAsset.StandaloneBox is {} box)
                {
                    //00998860 returns12 through inherited +114/00942040,
                    //even when009989D0's normal display mask suppresses draw.
                    fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(box.IntegerRenderCost);
                    continue;
                }
                if(retainedAsset.SpecialMaterial is {} special)
                {
                    fixedEffectCost+=SpecialEffectFixedCost(special);
                    continue;
                }
                if(retainedAsset.StandaloneDazzle is not null)
                {
                    //Class32 inherits942020/942040; +20/462950 returns0.
                    fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback;
                    continue;
                }
                var sequence=PrepareEffectSequence(key,retainedAsset,unchecked(currentClientTime-node.ActualStartTick),rigid);
                if(instance is not null || !definition.ScaleToRadius || IsMatrixBoundEffect(node) || IsCoordinateBoundEffect(node) || radii.ContainsKey(owner.RenderIdentity))
                    PrepareEffectCompositeScene(node,owner,retainedAsset,rigid,radius,currentClientTime,sequence?.Current,hierarchySourceScale);
                //An offscreen root retains its children and their callbacks,
                //including ordinary levels selected on earlier visible frames.
                for(int child=0;child<retainedAsset.ModelAttachments.Count;child++)used.Add(key with {Child=child});
                if(!AdmitEffectComposite(key,retainedAsset,rigid,radius,hierarchySourceScale,camera,
                    node.ActualStartTick,currentClientTime,sequence?.Current,boundsOwner.Admission))continue;
                fixedEffectCost+=AdditionalEffectMeshCost(retainedAsset.Resource,retainedAsset.Mixed is null && retainedAsset.AggregateParticles is null);
                if(retainedAsset.ModelAttachments is {Count:>0} models)
                {
                    if(retainedAsset.Resource is {HierarchicalLods.Count:1} parentResource)
                    {
                        if(retainedAsset.UsesDetailBudget)
                            AddCandidate(key,retainedAsset.Candidate(Matrix4x4.CreateScale(radius)*rigid,radius,camera,
                                EffectSceneLevel(key,retainedAsset),node.ActualStartTick,currentClientTime,sequence?.Current,rigid,hierarchySourceScale,boundsOwner.Candidate(-1)),0);
                        else
                        {
                            if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(parentResource,parentResource.HierarchicalLods.Single(),_=>true,out var parentCosts,out var parentError))
                                throw new InvalidDataException(parentError);
                            fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(parentCosts[0]);
                        }
                    }
                    fixedEffectCost+=(retainedAsset.AggregateParticles?.Count??0)*EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback;
                    fixedEffectCost+=retainedAsset.Mixed?.EmitterFixedCost??0;
                    for(int modelIndex=0;modelIndex<models.Count;modelIndex++)
                    {
                        var model=models[modelIndex];
                        var childKey=key with {Child=modelIndex};used.Add(childKey);
                        if(!model.PreparesDetail)continue;
                        var pose=model.Pose(retainedAsset,Matrix4x4.CreateScale(radius)*rigid,node.ActualStartTick,currentClientTime,observed:sequence?.Current);
                        if(pose.Visible)
                        {
                            fixedEffectCost+=AdditionalEffectMeshCost(model.Asset.Resource,model.Asset.Mixed is null && model.Asset.AggregateParticles is null);
                            if(model.Asset.SpecialMaterial is {} childSpecial)
                                fixedEffectCost+=SpecialEffectFixedCost(childSpecial);
                            else if(model.Asset.IsNullObject || model.Asset.StandaloneDazzle is not null)
                                fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback;
                            else if(model.Asset.StandaloneBox is {} childBox)
                                fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(childBox.IntegerRenderCost);
                            else if(model.Asset.UsesDetailBudget)
                                AddCandidate(childKey,model.Asset.Candidate(pose.World,radius,camera,EffectSceneLevel(childKey,model.Asset),node.ActualStartTick,currentClientTime,queryOwner:boundsOwner.Candidate(modelIndex)),0);
                            else
                            {
                                //Concrete Mesh +104 uses its own fixed cost;
                                //it has no hierarchy or mutable LOD level.
                                fixedEffectCost+=FixedEffectMeshRootCost(model.Asset.Resource!);
                            }
                        }
                    }
                    continue;
                }
                if(!retainedAsset.UsesDetailBudget)
                {
                    if(retainedAsset.Mixed is {} mixed)
                    {
                        fixedEffectCost+=FixedEffectMeshRootCost(mixed.Resource)+mixed.EmitterFixedCost;
                    }
                    else if(retainedAsset.Rings is not null && retainedAsset.Resource is {Meshes.Count:>0} ringMesh)
                    {
                        if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(ringMesh,ringMesh.HierarchicalLods.Single(),_=>true,out var costs,out var costError))
                            throw new InvalidDataException(costError);
                        fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(costs[0]);
                    }
                    else if(retainedAsset.AggregateParticles is {} particles && retainedAsset.Resource is {} attachedMesh)
                    {
                        // Static HMODEL meshes use the existing fixed mesh draw
                        // path, with no HLOD candidate. Do not manufacture LODs.
                        if(attachedMesh.HierarchicalLods.Count==0)
                        {
                            fixedEffectCost+=FixedEffectMeshRootCost(attachedMesh)+particles.Count*EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback;
                            continue;
                        }
                        if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(attachedMesh,attachedMesh.HierarchicalLods.Single(),_=>true,out var costs,out var costError))
                            throw new InvalidDataException(costError);
                        fixedEffectCost+=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(costs[0])+particles.Count*EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback;
                    }
                    else if(retainedAsset.Resource is {} ordinary && (ordinary.Meshes.Count>0 || ordinary.HierarchicalLods.Count==1 || ordinary.HierarchyModels.Count==1))
                    {
                        // RSL wrapper00461090 forwards +104;0098E9A0's
                        // single-level branch adds +114 to the fixed budget.
                        fixedEffectCost+=FixedEffectMeshRootCost(ordinary);
                    }
                    continue;
                }
                var world = Matrix4x4.CreateScale(radius) * rigid;
                AddCandidate(key,retainedAsset.Candidate(world,radius,camera,EffectSceneLevel(key,retainedAsset),node.ActualStartTick,currentClientTime,sequence?.Current,rigid,hierarchySourceScale,boundsOwner.Candidate(-1)),retainedAsset.Mixed?.EmitterFixedCost??0);
            }
            catch (Exception ex) { _effectHlodErrors[key] = ex.Message; }
        }
        foreach (var stale in _effectHlodLevels.Keys.Where(k => !used.Contains(k)).ToArray()) _effectHlodLevels.Remove(stale);
        foreach(var stale in _effectHlodSceneAssets.Keys.Where(k=>!used.Contains(k)).ToArray())_effectHlodSceneAssets.Remove(stale);
        foreach(var stale in _effectObjectLineLevels.Keys.Where(k=>!_preparedObjectLineKeys.Contains(k)).ToArray())_effectObjectLineLevels.Remove(stale);
        foreach(var stale in _effectSequences.Keys.Where(k=>!used.Contains(k)).ToArray())_effectSequences.Remove(stale);
        return result;
    }

    //Class24 +104/942020 -> +114/942040 -> +20/A09400 sums each
    //retained member's polygon count. It has no HLOD allocation state.
    private static float SpecialEffectFixedCost(EnbCurrentRetailSpecialMaterialDefinition special)=>
        EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(special.Members.Aggregate(0,(sum,m)=>unchecked(sum+(int)m.TriangleCount)));

    private static float AdditionalEffectMeshCost(EnbW3dRenderResource? resource,bool includeEmitters=false)
    {
        if(resource?.HierarchicalLods.SingleOrDefault() is not {Aggregate.Members.Count:>0} hlod)return 0;
        //0098E9A0 visits additional children through +104 independently of
        //the parent level budget. Box, NULL and dazzle inherit00942020 too;
        //scene-owned emitters retain this zero-cost contribution separately
        //from their independently allocated particle buffers.
        if(!EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(resource,hlod,
            member=>resource.Meshes.Any(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,member.Name)) ||
                member.Name.Equals("NULL",StringComparison.OrdinalIgnoreCase) ||
                resource.ExternalRenderObjects.Any(child=>child.Name.Equals(member.Name,StringComparison.OrdinalIgnoreCase) &&
                    (child.BoxDefinition is not null || child.DazzleDefinition is not null || child.SpecialMaterialDefinition is not null || child.Kind==EnbW3dExternalRenderObjectKind.NullObject || includeEmitters && child.ParticleEmitterDefinition is not null)),out var cost,out var error))
            throw new InvalidDataException(error);
        return cost;
    }

    private static float FixedEffectMeshRootCost(EnbW3dRenderResource resource)
    {
        //0098C7A0 converts all successfully bound HMODEL nodes to one HLOD
        //level. Count occurrences, including repeated references to one mesh.
        var level=resource.HierarchicalLods.SingleOrDefault();
        if(level is null && resource.HierarchyModels.SingleOrDefault() is {} model)
            level=EnbW3dHmodelConversion.Create(model,_=>true);
        int cost;
        if(level is not null)
        {
            if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,level,_=>true,out var costs,out var error))
                throw new InvalidDataException(error);
            cost=costs.Single();
        }
        else
        {
            //MeshClass00942020 ->00942040 ->0093F370 uses triangles*passes.
            var mesh=resource.Meshes.Single();
            if(!EnbW3dHlodBudgetCompiler.TryResolveNamedRenderObjectCost(resource,EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh),out cost,out var error))
                throw new InvalidDataException(error);
        }
        return EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(cost);
    }

    private void ApplyEffectStreaks(List<EnbW3dDrawTask> packets, HashSet<(uint, ulong, ulong)> live,
        EnbResolvedRenderCamera camera, EnbW3dLightEnvironment lights, uint now, ref int draws, ref int blocked, ref string? failure)
    {
        foreach (var (key, entry) in _effectStreakInstances.ToArray())
        {
            uint age = unchecked(now - entry.Node.ActualStartTick);
            var state = entry.Instance.Streaks!;
            if (!live.Contains(key) && age - state.LastAge >= (uint)((double)entry.Asset.MaximumParticleLifetimeSeconds * 1000))
            { _effectStreakInstances.Remove(key); continue; }
            try
            {
                for (int i = 0; i < entry.Asset.Streaks.Count; i++)
                {
                    var child = entry.Asset.Streaks[i];
                    if (!EnbCurrentRetailStreakRuntime.TryBuildPacket(child.Key, entry.Owner.RenderIdentity, entry.Owner.VisualGeneration,
                        entry.Owner.GameId, child.Definition.Name, i, child.Definition, state.Buffers[i].Sample(age), camera, age, out var packet, out var error,child.Texture,child.RuntimeTextureName,child.RetainedTexture))
                        throw new InvalidDataException(error);
                    if (packet is null) continue;
                    packets.Add((packet).ForkTask().SetRenderInstancePath($"effect-streak/{key.Item1}/{key.Item2}/{key.Item3}/{i}"));
                    draws++;
                }
                for (int i = 0; i < entry.Asset.Sprites.Count; i++)
                {
                    var child = entry.Asset.Sprites[i];
                    var sprite = state.Sprites[i];
                    var retained=state.AnimatedSprites?.Emitters[i] ?? sprite.RetainedLine;
                    bool line=child.Definition.RenderMode==2 && (retained is not null || sprite.Continuous is not null);
                    bool active = live.Contains(key);
                    if (!active) sprite.Continuous?.Advance(age, Matrix4x4.Identity, false);
                    var instance = new EnbW3dParticleEmitterDrawInstance(child.Definition.Name, child.Definition.Name,
                        entry.Owner.GameId, EnbRenderPassKind.OrdinaryWorld, child.Definition, child.Texture,
                        line || child.Definition.LocalSpace ? retained?.CurrentWorld ?? sprite.CurrentWorld : Matrix4x4.Identity, "", child.PivotIndex, i)
                        { RuntimeTextureName=child.RuntimeTextureName, RetainedTexture=child.RetainedTexture };
                    if (!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(child.Key, entry.Owner.RenderIdentity, entry.Owner.VisualGeneration,
                        instance, camera, lights, unchecked((uint)entry.Node.Sequence), 0, age,
                        out var packet, out _, out var error, particleSizeScale:entry.Instance.Radius,
                        birthWorld: retained is not null ? birth=>retained.Worlds[birth] : sprite.Continuous is null ? birth => sprite.Poses.Resolve(birth,entry.Instance.Radius) : null,
                        emissionStopMilliseconds: active ? null : sprite.Poses.LastAge,
                        retainedBirths:retained?.Births,
                        worldBirths: retained is not null ? retained.WorldBirths : sprite.Continuous?.Births,
                        activeEmissionRun:line && active ?
                            retained is {HasLineEndpoint:true} ? retained.EmissionRun :
                            sprite.Continuous is {IsEmitting:true} continuous ? continuous.EmissionRun : null : null,
                        inheritedVelocity: retained is null && sprite.Continuous is null ? birth => sprite.Poses.ResolveInheritedVelocity(birth,child.Definition.VelocityInheritanceFactor) : null,
                        motionHistory:retained?.MotionHistory ?? sprite.Continuous?.MotionHistory,randomHistory:sprite.Replay,cullToCamera:FrustumCullingEnabled))
                        throw new InvalidDataException(error);
                    sprite.Poses.RetainLatestSample();
                    if (packet is null) continue;
                    packets.Add((packet).ForkTask().SetRenderInstancePath($"effect-sprite/{key.Item1}/{key.Item2}/{key.Item3}/{i}"));
                    draws++;
                }
            }
            catch (Exception ex) { blocked++; failure ??= $"E{entry.Node.Definition.DefinitionId}: {ex.Message}"; }
        }
    }

    // Effect-owned clocks, sequence observations and detail history survive a
    // change to the bound owner's mesh; RenderIdentity is its lifetime token.
    private readonly record struct EffectHlodKey(uint Effect, ulong Generation, ulong Sequence, ulong Owner,int Child=-1)
    {
        public static EffectHlodKey For(EnbCurrentRetailEffectOwner node, IEnbRenderObject owner)
            => new(node.Root.EffectId, node.Root.InstanceGeneration, node.Sequence, owner.RenderIdentity);
    }
    private sealed record EffectStreakInstance(EnbCurrentRetailEffectOwner Node, IEnbRenderObject Owner,
        EnbCurrentRetailMixedEffectAsset Asset, EffectObjectInstance Instance);
}
