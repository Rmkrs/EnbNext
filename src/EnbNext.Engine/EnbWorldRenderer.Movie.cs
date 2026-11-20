using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private ulong _moviePreloadObjectSequence;
    internal EnbCurrentRetailRObjEffectAsset.FactoryPreparation? AcquireMovieRetainedObjectEffect(
        EnbEffectDefinition effect,float scale,Vector3 hsv)
    {
        var assets=_retailAssetsAccessor()??throw new InvalidDataException("Movie assets unavailable");
        EnsureSwatchCatalog(assets);
        //48B220 queries retained configured RenderObjs before FILE retry.
        //A generated object has no decoded source in the FILE snapshot.
        if(effect.BaseAssetId is not int id)return null;
        if(TryAcquireConfiguredEffectPrimitive(assets,id,(scale,hsv),out var asset))return new(null,()=>asset);
        if(TryAcquireConfiguredEffectMesh(assets,id,(scale,hsv),out asset,out var acquisition))
        {
            //The namespace retains the configured models. Release this temporary
            //acquisition; the preload below borrows those same model owners.
            acquisition.Dispose();
            return new(null,()=>asset);
        }
        //The native constructor runs the configured factory before asking
        //for effect draw data. A cold converted0400 parent can only be
        //represented after its named children have been acquired/transferred.
        var selected=EnbCurrentRetailRObjEffectAsset.PrepareOwnedFactory(assets,id,
            EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv)?hsv:Vector3.Zero,
            registeredPrototypes:_prototypeResources.CapturePrototypeSources(out _),factoryInputs:(scale,hsv));
        if(TryPublishEffectMesh(assets,id,selected,(scale,hsv),out asset,out acquisition))
        {
            acquisition.Dispose();
            return new(null,()=>asset);
        }
        return selected;
    }

    internal void PreloadMovieObjectEffect(EnbEffectDefinition effect,EnbCurrentRetailRObjEffectAsset asset,float scale,Vector3 hsv)
    {
        var assets=_retailAssetsAccessor()??throw new InvalidDataException("Movie assets unavailable");
        EnsureSwatchCatalog(assets);
        if(asset.RslOwner is {} reference)
        {
            ConstructTransientRslObjectEffect(effect,reference,scale,hsv,assets);
            return;
        }
        if(effect.BaseAssetId is int id &&
            assets.PresentationCatalog!.ResolveBaseAsset(id) is {Kind:EnbBaseAssetDefinitionKind.FileName} baseDefinition)
        {
            //6EA830/6EAC50 call6F51D0 before localization/class rejection.
            //98C420/98C7A0 construct all emitter children, including hidden
            //levels. Decoded composite data alone has no constructor tables.
            //48B220 also registers configured sphere/ring variants before
            //8070C0 discards the preload; there need not be any mesh/emitter.
            var request=new EnbSceneObjectSnapshot(0,0,0,false,false,byte.MaxValue,id,scale,hsv.X,hsv.Y,hsv.Z,
                0,false,null,null!,null,null,null,[],[],[],[],[],[],null,[],null,0,0);
            if(!(_shipExhaustOwner??this)._effectFactoryRoots.TryGetValue(asset,out var factoryRoot))
            {
                var factory=CreateOrdinaryBaseFactory(request,assets,baseDefinition);
                factoryRoot=factory.Root??throw new InvalidDataException(factory.Diagnostic??"Movie object factory unavailable");
            }
            if(effect.EffectClassKind==EnbEffectClassKind.LineEffect && asset.ObjectLine is null)
                throw new InvalidDataException("Movie LineEffect BASE is not class 1C");
            //The FILE factory is unconditional in6EA830, including a cold
            //mesh-only parent whose generated named children cannot appear in
            //the decoded FILE snapshot. Localize its actual acquired models.
            if(effect.EffectClassKind!=EnbEffectClassKind.LineEffect &&
                TryAcquireConfiguredEffectMesh(assets,id,(scale,hsv),out _,out var meshOwner,factoryRoot))
            {
                using(meshOwner)
                    if(EnbRetailIniDocument.ReadBool(effect.Properties,"ResetMappers"))
                    {
                        using var localized=meshOwner.Models.Localize(ReadMapperClock());
                        foreach(var child in meshOwner.Children){using var localChild=child.Localize(ReadMapperClock());}
                    }
                return;
            }
            if(asset.Resource is null && asset.ModelAttachments.Count==0)return;
        }
        //6EAC50 has no ResetMappers localization. Its class1C path is separate.
        if(effect.EffectClassKind==EnbEffectClassKind.LineEffect)
        {
            if(asset.ObjectLine is null)throw new InvalidDataException("Movie LineEffect BASE is not class 1C");
            return;
        }
        if(asset.Resource is null && asset.ModelAttachments.Count==0)return;
        uint clock=ReadMapperClock();
        if(effect.BaseAssetId is int assemblyId && assets.PresentationCatalog!.ResolveBaseAsset(assemblyId).Name is {} assemblyName &&
            _configuredEffectAssemblies.TryGetValue(EnbCurrentRetailPrototypeVariantName.Create(assemblyName,scale,hsv),out var assembly))
        {
            //6EA830 localizes each retained child only after48B220 has
            //completed configuration and generated-name publication.
            if(EnbRetailIniDocument.ReadBool(effect.Properties,"ResetMappers"))
                foreach(var model in assembly.Models){using var localized=model.Localize(clock);}
            return;
        }
        if(asset.IsPreconfiguredFactoryObject && asset.Resource is {} retained &&
            retained.Meshes.Count!=0 && retained.Meshes.All(_configuredDirectMeshModels.ContainsKey))
        {
            //496AE0 must clone the retained configured material/mappers, not
            //reconstruct them from FILE or an unowned prepared descriptor.
            using var models=new EnbCurrentRetailRObjMeshMappers(retained,clock,CameraMeshBatches,
                _prototypeResources,retained.Meshes.Cast<EnbW3dRenderMesh?>().ToArray(),_configuredDirectMeshModels);
            if(EnbRetailIniDocument.ReadBool(effect.Properties,"ResetMappers"))
            {
                using var localized=models.Localize(clock);
            }
            return;
        }
        var root=new EnbSceneEffectSnapshot(uint.MaxValue,0,0,0,effect.DefinitionId,0,0,"",0,false,1,Vector3.Zero,
            0,0,false,Vector3.Zero,1,0,Vector3.Zero,0,Vector3.Zero,0,0,null);
        var node=new EnbCurrentRetailEffectOwner(++_moviePreloadObjectSequence,effect,root,null,false,clock){ConstructionTick=clock};
        var definition=asset.ResolveFactoryDefinition(new(effect.BaseAssetId??-1,false,
            ResetMappers:EnbRetailIniDocument.ReadBool(effect.Properties,"ResetMappers"),
            AssetScale:EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)?scale:1,
            HsvShift:EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv)?hsv:Vector3.Zero)
            {FactoryInputs=(scale,hsv)});
        var key=(root.EffectId,root.InstanceGeneration,node.Sequence);
        try
        {
            //8070C0 discards after the class constructor.496AE0 localizes
            //loaded model/material owners here; activation resets are later.
            _=RetainEffectObjectMeshOwners(node,definition,asset,assets,clock,clock);
        }
        finally
        {
            if(_effectObjectMeshOwners.Remove(key,out var owners))owners.Dispose();
        }
    }
    private void ConstructTransientRslObjectEffect(EnbEffectDefinition effect,EnbResolvedRslReference reference,
        float scale,Vector3 hsv,EnbRetailAssetBundle assets)
    {
        //6F541B ->45FFB0/45C990 constructs the complete state list before
        //STARTS_ENABLED/RSID chooses the current object.8070C0 then discards.
        ulong identity=(1UL<<63)|(ulong)Interlocked.Increment(ref _nextEffectRslIdentity);
        var obj=new EnbSceneObjectSnapshot(identity,0,0,false,false,0,effect.BaseAssetId!.Value,
            scale,hsv.X,hsv.Y,hsv.Z,0,false,null,null!,null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        var owner=new EffectRslOwner(new EnbRenderObjectState(obj),reference);
        uint clock=ReadMapperClock();
        try
        {
            PrepareRslObjectFactories(obj,reference,assets,(scale,hsv));
            if(effect.EffectClassKind==EnbEffectClassKind.LineEffect)
                throw new InvalidDataException("Movie LineEffect BASE is an RSL, not class 1C");
            //Use a private initialization observer: the discarded wrapper must
            //not enter live callbacks, sound admission or the scene owner map.
            var runtime=new EnbCurrentRetailEffectRslObjectRuntime();
            var initial=runtime.Update(obj,reference,[],clock,null,
                (animationId,objectId)=>ResolveRetainedRslAnimationClip(obj,reference,assets,animationId,objectId),advanceCallbacks:false);
            if(initial.Receipt.FailClosedEvents!=0)throw new InvalidDataException(initial.Receipt.Diagnostic);
            if(EnbRetailIniDocument.ReadBool(effect.Properties,"ResetMappers") && initial.CurrentObjectStateId is int stateId &&
                reference.ResourceGraph!.TryFindState(stateId,out var state))
            {
                if(!TryMaterializeRslObjectRoot(obj.BaseAsset,stateId,state,assets,out var root,out var error))
                    throw new InvalidDataException(error);
                owner.LocalizedRoot=LocalizeEffectRslChildren(ResolveConfiguredRslRoot(obj,stateId,root),owner,clock,skipRootMesh:true);
            }
        }
        finally
        {
            ReleaseEffectRslModels(owner,removeOwner:false);
            foreach(var key in _rslConfiguredRoots.Keys.Where(k=>k.Identity==identity).ToArray())_rslConfiguredRoots.Remove(key);
            _rslFactoryPrepared.Remove((identity,obj.RenderObjectGeneration));
            _compatRslPassOwners.Remove((identity,obj.RenderObjectGeneration));
            if(_rslPassOwners.Remove((identity,obj.RenderObjectGeneration),out var passes))passes.Library.Dispose();
        }
    }

    internal void PreloadMovieParticleJetEffect(EnbEffectDefinition effect,float incomingScale,Vector3 incomingHsv)
    {
        var assets=_retailAssetsAccessor()??throw new InvalidDataException("Movie assets unavailable");
        if(effect.BaseAssetId is not int id || !EnbCurrentRetailEffectFactoryHsv.TryRead(effect.Properties,incomingHsv,out var hsv,out _))
            throw new InvalidDataException("Movie ParticleJet base asset/HSV unavailable");
        //8070C0 ->6EC950/6ECC50 retains a constructed emitter before the
        //preload discard.6ED130's distance retiming belongs to activation.
        var factory=CreateParticleJetFactory(assets,id,EnbCurrentRetailParticleJetScale.Read(effect,incomingScale),hsv);
        if(factory.Asset is null)throw new InvalidDataException(factory.Diagnostic);
    }

    internal void PreloadMoviePassEffect(EnbEffectDefinition effect,Vector3 incomingHsv)
    {
        var assets=_retailAssetsAccessor()??throw new InvalidDataException("Movie assets unavailable");
        if(!EnbCurrentRetailPassEffectDefinition.TryReadFactory(effect,incomingHsv,out var definition,out var error))
            throw new InvalidDataException(error);
        uint clock=ReadMapperClock();
        using var swatch=LoadEffectSwatch(assets,definition.Swatch,definition.HsvShift,clock);
        if(swatch.Error is not null)throw new InvalidDataException(swatch.Error);
        definition.ValidateSwatch(swatch.Pass!);
        //8070C0 constructs before its preload branch.006EB9B0 copies the
        //material for projection/ResetMappers; Mode10 copy consumes a fresh
        //random triple. No activation/reset, owner binding or frame update
        //occurs for the discarded preload object.
        if(definition.ResetMappers || definition.ProjectStage0 || definition.Stage1BoxScale is not null)
        {
            var shared=swatch.SharedMappers??throw new InvalidDataException("PassEffect retained material mappers unavailable");
            _=shared.Stage0.CloneForMaterial(clock);
            _=shared.Stage1.CloneForMaterial(clock);
        }
    }

    private void InitializeMovieSceneRoot(LiveRootInstance instance)
    {
        //8043EF +194(1), then8043FE/+128 and804408/+120(count-1).
        //965F40 bypasses the frustum;98E9A0 raises the selected level only
        //to its minimum, preserving this initial state for96F180's allocator.
        instance.MovieForceVisible=true;
        if(instance.Root.Hlod is not {} hlod)return;
        int level;
        if(OwnedHlodLevel(instance,hlod) is {} state)level=state.Level??=hlod.Levels.Count-1;
        else
        {
            var key=RetainedHlodKey(instance,hlod.Name);
            if(!_hlodLevels.TryGetValue(key,out level))_hlodLevels.Add(key,level=hlod.Levels.Count-1);
        }
        instance.HlodSelection=new(hlod.Name,level);
    }

    private sealed class MovieModelFactory(LiveCpuRoot root,IReadOnlyList<IDisposable> owners,
        Action<EnbRetailMovieModel,bool> updateChildren,Action releaseChildren):IDisposable,IEnbRetailMovieChildFactory
    {
        private bool _disposed;
        public LiveCpuRoot Root {get;}=root;
        public void UpdateChildren(EnbRetailMovieModel model,bool engineTrailsChanged)=>updateChildren(model,engineTrailsChanged);
        public void Dispose()
        {
            if(_disposed)return;
            _disposed=true;
            releaseChildren();
            foreach(var owner in owners)owner.Dispose();
        }
    }

    internal IDisposable CreateMovieModelFactory(EnbRetailMovieModel model)
    {
        var assets=_retailAssetsAccessor()??throw new InvalidDataException("Movie assets unavailable");
        EnsureSwatchCatalog(assets);
        var source=model.Source;
        int selector=unchecked(int.MinValue+(int)model.Identity);
        if(!TryGetCpuRoot(new(selector,-1,source.LogicalFileName,source.RequestedObjectName),source,assets,out var root,out var error))
            throw new InvalidDataException(error);
        //5B8850 acquires through48B0F0, including its ordinary mesh
        //postprocess. Retain the completed tree before the next command.
        if(root.IsDirectMeshPrototype)
            foreach(var mesh in root.Resource.Meshes)PostprocessOrdinaryNamedMesh(mesh);
        var owners=new List<IDisposable>();
        LiveCpuRoot OwnSpecials(LiveCpuRoot node)
        {
            //The render cache can be cleared before the first movie frame.
            //The command's retained clone owns its texture references itself.
            var special=node.SpecialMaterial?.Clone();
            if(special is not null)owners.Add(special);
            return node with {SpecialMaterial=special,CollectionChildren=node.CollectionChildren.Select(OwnSpecials).ToArray(),
                HmodelChildren=node.HmodelChildren.Select(c=>c with {Root=OwnSpecials(c.Root)}).ToArray(),
                AggregateAttachments=node.AggregateAttachments.Select(c=>c.Root is {} child?c with {Root=OwnSpecials(child)}:c).ToArray()};
        }
        void Retain(LiveCpuRoot node)
        {
            if(node.Resource.Meshes.Count!=0)
                owners.Add(new EnbCurrentRetailRObjMeshMappers(node.Resource,ReadMapperClock(),CameraMeshBatches,_prototypeResources,
                    node.Resource.Meshes.Cast<EnbW3dRenderMesh?>().ToArray(),_configuredDirectMeshModels));
            foreach(var child in node.CollectionChildren)Retain(child);
            foreach(var child in node.HmodelChildren)Retain(child.Root);
            foreach(var child in node.AggregateAttachments)if(child.Root is {} retained)Retain(retained);
        }
        try
        {
            root=OwnSpecials(root);
            if(HasParticleConstruction(root))root=ConstructParticleFactoryTree(root);
            Retain(root);return new MovieModelFactory(root,owners,PrepareMovieChildren,()=>
            {
                foreach(var key in _movieChildren.Keys.Where(k=>k.Owner==model.Identity).ToArray())_movieChildren.Remove(key);
                _movieAttachments.RemoveAll(a=>a.Parent.Object.RenderIdentity==model.Identity);
            });
        }
        catch{foreach(var retained in owners)retained.Dispose();throw;}
    }

    private EnbRetailMovieFrame? _renderingMovie;
    internal (int Polygons, int Vertices) MovieRenderStatistics { get; private set; }
    internal string? LastRenderFailure { get; private set; }
    private readonly Dictionary<(ulong Owner, string Path), MovieChild> _movieChildren = [];
    private readonly List<(LiveRootInstance Parent,LiveRootInstance Child,int Bone)> _movieAttachments=[];
    private readonly EnbCurrentRetailRslHAnimRegistryRuntime _movieChildAnimations = new();
    private sealed class MovieChild
    {
        public LiveCpuRoot? Root;
        public string? Diagnostic;
        public EnbW3dRenderHierarchyAnimation? Animation;
        public readonly EnbCurrentRetailHierarchyAnimationState Playback = new();
    }

    private void PrepareMovieChildren(EnbRetailMovieModel model,bool engineTrailsChanged)
    {
        if(model.RenderObjectOwner is not MovieModelFactory factory)return;
        var assets=_retailAssetsAccessor()??throw new InvalidDataException("Movie child assets unavailable");
        if(engineTrailsChanged)
        {
            //810750 retries the four names only when the bone has no child.
            //A partially successful group must not retry its missing siblings.
            var emptyGroups=_movieChildren.Where(entry=>entry.Key.Owner==model.Identity&&entry.Key.Path.Contains("/movie/engine/",StringComparison.Ordinal))
                .GroupBy(entry=>entry.Key.Path[..entry.Key.Path.LastIndexOf('/')])
                .Where(group=>group.All(entry=>entry.Value.Root is null)).SelectMany(group=>group.Select(entry=>entry.Key)).ToArray();
            foreach(var key in emptyGroups)_movieChildren.Remove(key);
        }
        var roots=new RootTaskList();
        //808F00/808FD0/809890 call the child factories during the command,
        //even when several mutations precede the next rendered frame.
        if(!TryAppendRootInstanceTree(model.Snapshot(ReadMapperClock()),factory.Root,false,roots,null,out _))return;
        var visual=new EnbRetailMovieVisual(model.Source,model.Resource,model.Animation,model.Frame,model.Hidden,model.EngineTrails)
            {IsShip=model.IsShip,EngineTrailsTouched=model.EngineTrailsTouched,Damage=model.Damage,RenderObjectOwner=model.RenderObjectOwner};
        AppendMovieChildren(new Dictionary<ulong,EnbRetailMovieVisual>{{model.Identity,visual}},assets,roots,constructOnly:true);
        //98E4B0 updates parent bounds immediately, even before a scene frame.
        PrepareAttachmentComposites(roots);
    }

    private void AppendMovieChildren(IReadOnlyDictionary<ulong,EnbRetailMovieVisual> visuals, EnbRetailAssetBundle assets,
        RootTaskList roots,bool constructOnly=false)
        =>AppendShipChildren(visuals.ToDictionary(e=>e.Key,e=>new ShipChildState(e.Value.EngineTrailsTouched,
            e.Value.EngineTrails,e.Value.Damage,e.Value.Damage.Length==4?e.Value.Damage[0]:default)),
            assets,roots,_movieChildren,_movieAttachments,"movie",constructOnly);

    private sealed record ShipChildState(bool EngineTrailsTouched,bool EngineTrails,
        ImmutableArray<EnbRetailMovieDamage> Damage,EnbRetailMovieDamage Overall);

    private readonly bool _ownerLocalShipChildTasks=Environment.GetEnvironmentVariable("ENB_EXPERIMENT_OWNER_LOCAL_SHIP_CHILDREN")!="0";
    private void AppendShipChildren(IReadOnlyDictionary<ulong,ShipChildState> visuals,EnbRetailAssetBundle assets,
        RootTaskList roots,Dictionary<(ulong Owner,string Path),MovieChild> ownedChildren,
        List<(LiveRootInstance Parent,LiveRootInstance Child,int Bone)> attachments,string scope,bool constructOnly=false,bool borrowed=false,
        IReadOnlyList<LiveRootInstance>? selectedRoots=null)
    {
        attachments.Clear();
        var live = new HashSet<(ulong Owner, string Path)>();
        // These are ordered per-view tasks, not scene membership. Follow the
        // existing parent pointers; each mutation appends its new tasks below.
        using var childTasks=_ownerLocalShipChildTasks?IndexQueryOccurrences(roots):null;
        IEnumerable<LiveRootInstance> Children(LiveRootInstance parent)=>childTasks is null
            ?roots.Where(r=>ReferenceEquals(r.Parent,parent))
            :childTasks.Children(parent).Where(roots.Contains);
        foreach(var root in _ownerLocalShipChildTasks && selectedRoots is not null
            ?selectedRoots:roots.Where(r=>r.Parent is null).ToArray())Visit(root);
        //810750/810CF0 recurse after mutating the parent's child storage.
        //Walk that resulting storage, including children just constructed.
        void Visit(LiveRootInstance parent)
        {
            if(!roots.Contains(parent))return;
            Prepare(parent);
            foreach(var child in Children(parent).ToArray())Visit(child);
        }
        void Prepare(LiveRootInstance parent)
        {
            if (!visuals.TryGetValue(parent.Object.RenderIdentity, out var visual) ||
                !TryResolveConfiguredAttachmentHierarchy(parent.Root, out var hierarchy, out _) || hierarchy is null) return;
            if (!(parent.WorldTransformOverride is { } world) && !TryBuildObjectWorld(parent.Object, out world)) return;
            EnbW3dHierarchyPoseOwner pose;
            var post=ConfiguredHierarchyPostTransforms(parent);
            bool ready=TryResolveRetainedHierarchyPose(parent,hierarchy,post?.RigidWorldTransform??world,
                post?.HierarchyTranslationScale,parent.AnimationBlend?.ResolvedSamples??[],post?.Transforms,out pose,out _);
            if(!ready)return;
            for (int pivot = 0; pivot < hierarchy.Pivots.Count; pivot++)
            {
                string bone = hierarchy.Pivots[pivot].Name;
                bool engine = EnbCurrentRetailShipExhaustVisibilityRuntime.IsEngineBoneName(bone);
                bool exhaust = bone.StartsWith("exhaust", StringComparison.OrdinalIgnoreCase);
                var children = !_ownerLocalShipChildTasks || visual.EngineTrailsTouched && (engine || exhaust)
                    ? Children(parent).Where(child => child.OrdinaryHlodPivot == pivot).ToArray() : [];
                if (visual.EngineTrailsTouched && (engine || exhaust))
                {
                    foreach (var child in children) child.MovieHidden = !visual.EngineTrails;
                    //98E290 includes inline ordinary and permanent members,
                    //not only children that need separate staged root owners.
                    var retainedHlod=RetainedDrawRoot(parent).Hlod;
                    bool authoredChild=retainedHlod is not null && retainedHlod.Levels.SelectMany(l=>l.Members)
                        .Concat(retainedHlod.Aggregate?.Members??[]).Any(m=>m.ReferenceIndex==(uint)pivot);
                    if (engine && children.Length == 0 && !authoredChild && !parent.Root.AggregateAttachments.Any(a => a.BoneName.Equals(bone, StringComparison.OrdinalIgnoreCase)))
                        for (int n = 1; n <= 4; n++) Add($"p-eng{n}", $"engine/{pivot}/{n}", !visual.EngineTrails);
                }
                bool part = bone.Length == 7 && bone.StartsWith("part0q", StringComparison.OrdinalIgnoreCase) && bone[6] is >= '0' and <= '3';
                // 00811074/00811088: case-insensitive prefixes "tractor" (00BA778C) and "~weap_" (00BA7784, bytes
                // 7E 77 65 61 70 5F), i.e. the ~WEAP_nn weapon mount pivots, not "_weap_".
                bool tractor = bone.StartsWith("tractor", StringComparison.OrdinalIgnoreCase) || bone.StartsWith("~weap_", StringComparison.OrdinalIgnoreCase);
                if (!part && !tractor) continue;
                int quadrant = part ? bone[6] - '0' : 0;
                if (visual.Damage.Length != 4) continue;
                var damage=part?visual.Damage[quadrant]:visual.Overall;
                if(damage.Level<=0)continue;
                //808FD0 ->810CF0 removes concrete children during the command,
                //including inline meshes and inactive ordinary levels. A later
                //frame borrows that membership instead of merely hiding roots.
                //The factory survives first-frame asset-cache initialization;
                //reproject its irreversible authored-slot removals after a reset.
                //Already removed slots do not repeat destruction or construction.
                RemoveAuthoredDamageChildren(parent,roots,ReadMapperClock(),true,(uint)pivot,
                    _ownerLocalShipChildTasks?Children(parent):null);
                string damagePrefix=parent.InstancePath+$"/{scope}/damage/{pivot}/";
                string currentDamagePath=damagePrefix+damage.Generation;
                foreach(var entry in ownedChildren.Where(e=>e.Key.Owner==parent.Object.RenderIdentity&&
                    e.Key.Path.StartsWith(damagePrefix,StringComparison.Ordinal)&&e.Key.Path!=currentDamagePath&&
                    e.Key.Path.AsSpan(damagePrefix.Length).IndexOf('/')<0).ToArray())
                {
                    //810CF0 removes matching concrete names now, not at the
                    //next draw. A nonmatching child survives later commands.
                    //Scene-owned particle buffers retire before their next
                    //callback through PrepareRetainedEmitters, and drain.
                    if(entry.Value.Root is not {} prior||IsDamageChildName(prior.RuntimeRenderObjectName))
                        foreach(var key in ownedChildren.Keys.Where(k=>k.Owner==entry.Key.Owner&&
                            (k.Path==entry.Key.Path||k.Path.StartsWith(entry.Key.Path+"/",StringComparison.Ordinal))).ToArray())
                            ownedChildren.Remove(key);
                    else Stage(entry.Key.Path,entry.Value,false);
                }
                string? name = damage.Level switch
                {
                    2 => quadrant % 2 == 0 ? "d4m02_01" : "d4m03_01",
                    3 => quadrant % 2 == 0 ? "d4m_elec02" : "d4m_elec01",
                    4 => quadrant % 2 == 0 ? "d4m_fire02" : "d4m_fire01",
                    _ => null
                };
                if (tractor && damage.Level > 1) name = "tractor"; // Resolve its random branch once per mutation below.
                if (name is not null) Add(name, $"damage/{pivot}/{damage.Generation}", false, damage.Level);

                void Add(string name, string suffix, bool hidden, int damageLevel = 0)
                {
                    string path = parent.InstancePath + "/"+scope+"/" + suffix;
                    var key = (parent.Object.RenderIdentity, path); live.Add(key);
                    if (!ownedChildren.TryGetValue(key, out var child))
                    {
                        if(borrowed)return;
                        child = new();
                        string? animationName = null;
                        if (name == "tractor")
                        {
                            bool animated = damageLevel == 4 || damageLevel == 3 && EnbRetailMovieRandom.Damage.Next(0, 1) == 0;
                            name = damageLevel == 2 ? "d4m_smoke01" : damageLevel == 3 && animated ? "d4m01_02" : "d4m01_01";
                            if (animated) animationName = damageLevel == 3 ? "dam01_02a.dam01_02a-1" : "dam01_01a.dam01_01a-1";
                        }
                        if (EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources, assets.MixResources,
                            name, out var source, out _, out var publications, _prototypeResources.CapturePrototypeSources(out _)))
                        {
                            foreach (var publication in publications) _prototypeResources.TryLoadFile(publication, out _, out _, ReadMapperClock());
                            if (_prototypeResources.TryFindPrototypeSource(name, out var published)) source = published;
                            if (TryGetCpuRoot(new(parent.Object.BaseAsset, -1, source.LogicalFileName, name), source, assets, out var resolved, out _))
                            {
                                try {child.Root=SetFactoryLodBias(HasParticleConstruction(resolved)?ConstructParticleFactoryTree(resolved):resolved,parent.Root.LodBias);}
                                catch(Exception error) when(error is InvalidDataException or NotSupportedException)
                                {child.Diagnostic=error.Message;}
                                var file = new EnbResolvedRslFileDependency(source.LogicalFileName, EnbRslFileDependencyStatus.ResolvedW3d,
                                    source.ResourceLocation, source.Model, source.Textures, LooseFilePath: source.LooseFilePath) { PrototypeFile = source.PrototypeFile };
                                if (animationName is not null && _movieChildAnimations.TryResolve(resolved.Resource, file, assets.Paths,
                                    assets.MixResources, assets.RetailMixResources, animationName, out var animation, out _))
                                {
                                    child.Animation = animation.Animation;
                                    child.Playback.Mode = 2; child.Playback.Playback = 1;
                                    child.Playback.PreviousTime = ReadMapperClock();
                                    child.Playback.CurrentFrame = EnbRetailMovieRandom.Damage.Next(0, (int)animation.Animation.NumFrames - 1);
                                }
                            }
                        }
                        ownedChildren[key] = child;
                    }
                    Stage(path,child,hidden);
                }
                void Stage(string path,MovieChild child,bool hidden)
                {
                    live.Add((parent.Object.RenderIdentity,path));
                    if (child.Root is not { } root) return; // Missing optional child does not abort its parent movie.
                    LiveRslAnimationBlendBinding? blend = null;
                    if (child.Animation is { } clip)
                    {
                        if(!constructOnly)
                        {
                            child.Playback.CurrentFrame = (float)child.Playback.Evaluate(ReadMapperClock(), () => clip.PlaybackFrameRate, () => (int)clip.NumFrames);
                            child.Playback.PreviousTime = ReadMapperClock();
                        }
                        blend = CreateRslAnimationBlend(root, [new(0, clip.Name, "", 0, clip, new(clip.Name, child.Playback.CurrentFrame), root.Resource)]);
                    }
                    int first = roots.Count;
                    //810750/810CF0 attach through HLOD+84, not an independent
                    //scene root. Retain the pivot for pose, visibility and +104.
                    bool appended=TryAppendRootInstanceTreePrepared(parent.Object, root, false, pose.WorldTransforms[pivot], path, true,
                        blend, roots, out _, parentInstance: parent,ordinaryHlodPivot:(uint)pivot,isAggregateHlodChild:true);
                    if(childTasks is not null)for(int i=first;i<roots.Count;i++)childTasks.Add(roots[i]);
                    if(!appended)return;
                    attachments.Add((parent,roots[first],pivot));
                    for (int i = first; i < roots.Count; i++) roots[i].MovieHidden = hidden;
                }
            }
        }
        if(!constructOnly&&!borrowed)foreach (var key in ownedChildren.Keys.Where(key => !live.Contains(key)).ToArray()) ownedChildren.Remove(key);
    }

    private static bool DescendsFrom(LiveRootInstance instance, LiveRootInstance ancestor)
    {
        for (var current = instance; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static bool MovieAncestorHidden(LiveRootInstance instance)
    {
        for (var current = instance; current is not null; current = current.Parent)
            if (current.MovieHidden) return true;
        return false;
    }
    public void SetMovieFrame(EnbRetailMovieFrame movie, EnbBackdropFrame? backdrop, long token)
    {
        var previous=Interlocked.Exchange(ref _pendingFrame, new LiveFrame(movie.Snapshot, movie.Camera, false, movie.Snapshot.ClientTime,
            Stopwatch.GetTimestamp(), backdrop, token, ImmutableDictionary<LivePlanetGpuIdentity, float>.Empty,
            ImmutableDictionary<LivePlanetGpuIdentity, uint>.Empty) { Movie = movie });
        if(previous?.Snapshot is EnbSceneFrame owner)owner.Release();
        RequestFrame();
    }

    private static LiveRslAnimationBlendBinding? MovieAnimation(EnbRetailMovieFrame? movie, ulong id, LiveCpuRoot root)
    {
        if (movie is null || !movie.Visuals.TryGetValue(id, out var visual) || visual.Animation is not { } animation) return null;
        return CreateRslAnimationBlend(root, [new(0, animation.Name, visual.Source.LogicalFileName, 0,
            animation, new(animation.Name, visual.Frame), visual.Resource)]);
    }
}
