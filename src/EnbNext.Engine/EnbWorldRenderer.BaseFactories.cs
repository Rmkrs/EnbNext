using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    internal Action? CollectNativeEventsDuringLoading { get; set; }
    private sealed record OrdinaryBaseFactory(EnbBaseAssetResolution Resolution,LiveCpuRoot? Root,string? Diagnostic);
    private readonly record struct OrdinaryBaseFactoryKey(ulong Identity,ulong Revision,int Base,int Scale,int Hue,int Saturation,int Value)
    {
        public static OrdinaryBaseFactoryKey From(IEnbRenderObject obj)=>new(obj.RenderIdentity,obj.BaseAssetMaterializationRevision,
            obj.BaseAsset,BitConverter.SingleToInt32Bits(obj.Scale),BitConverter.SingleToInt32Bits(obj.HsvH),
            BitConverter.SingleToInt32Bits(obj.HsvS),BitConverter.SingleToInt32Bits(obj.HsvV));
    }
    private readonly Dictionary<OrdinaryBaseFactoryKey,OrdinaryBaseFactory> _ordinaryBaseFactories=[];
    private readonly Dictionary<(ulong Identity,ulong BaseRevision,int Slot,ulong Revision),LiveCpuRoot> _fixedShipParticleFactories=[];
    private readonly Dictionary<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path),LiveCpuRoot> _legoParticleFactories=[];
    private readonly Dictionary<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path),OrdinaryBaseFactory> _legoBaseFactories=[];
    private sealed class LegoFactoryBinding((ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path) key)
    {
        public readonly (ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path) Key=key;
        public bool Valid=true,Resolved;
        public SceneOwner? Source;
        public IEnbRenderObject? CompatibilityObject;
        public OrdinaryBaseFactory? Factory;
        public LiveCpuRoot? Particle;
        public object? PhysicalLifetime;
    }
    private readonly Dictionary<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path),LegoFactoryBinding> _legoFactoryBindings=[];
    private LegoFactoryBinding BindLegoFactory(IEnbRenderObject obj,LiveShipLegoSpec spec)
    {
        if(spec.FactoryBinding is {Valid:true} bound){if(bound.Source is null)bound.CompatibilityObject=obj;return bound;}
        var key=(obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath);
        if(!_legoFactoryBindings.TryGetValue(key,out bound))
        {
            _legoFactoryBindings.Add(key,bound=new(key));
            if(_ownedScene is not null&&_ownedScene.TryFind(obj.RenderIdentity,out var node))
            {bound.Source=node.State;node.State.LegoFactories.Add(bound);_dirtyLegoFactoryOwners.Add(node.State);}
            else {bound.CompatibilityObject=obj;_compatLegoFactoryBindings.Add(bound);}
        }
        return spec.FactoryBinding=bound;
    }
    private void ClearLegoFactoryBindings()
    {
        foreach(var binding in _legoFactoryBindings.Values){DetachLegoPhysicalLifetime(binding);binding.Valid=false;}
        _legoFactoryBindings.Clear();_compatLegoFactoryBindings.Clear();_dirtyLegoFactoryOwners.Clear();
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)node.State.LegoFactories.Clear();
    }
    // Query membership outlives scene attachment.98E0F0 clears the parent
    // pointer without resetting the child's retained world matrix.
    private readonly Dictionary<object,LiveRootInstance> _legoSourceInstances=[];

    private OrdinaryBaseFactory? ResolveLegoBaseFactory(IEnbRenderObject obj,LiveShipLegoSpec spec,EnbRetailAssetBundle assets)
    {
        if(_shipExhaustOwner is {} primary)return primary.ResolveLegoBaseFactory(obj,spec,assets);
        var binding=BindLegoFactory(obj,spec);
        if(binding.Resolved)return binding.Factory;
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(spec.BaseAsset);
        if(definition.Kind is not (EnbBaseAssetDefinitionKind.FileName or EnbBaseAssetDefinitionKind.NullObject) && !EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(definition)){binding.Resolved=true;return null;}
        var key=binding.Key;
        if(_legoBaseFactories.TryGetValue(key,out var retained)){binding.Resolved=true;return binding.Factory=retained;}
        //729770 calls6F5150/6F51D0 with neutral HSV before72B7F0 mount lookup.
        //FILE children therefore use the same registered NAME/lazy FILE/MISSING
        //selection as ordinary BASE objects, and retain that factory result.
        try
        {
            var request=new EnbRenderObjectState(obj) {BaseAsset=spec.BaseAsset,Scale=EnbCurrentRetailShipLegoRuntime.LegoControllerCreationScale,
                HsvH=0,HsvS=0,HsvV=0,WorldCreationOrdinal=null};
            if(TryCreateConfiguredBaseFactory(request,assets,definition,out var configured))
            {
                retained=configured with {Root=configured.Root is {} generated?RetainLegoParticleFactory(obj,spec,generated):null};
                _legoBaseFactories.Add(key,retained);
                binding.Resolved=true;return binding.Factory=retained;
            }
            EnbCurrentRetailRObjEffectAsset.FactoryPreparation? selected=null;
            EnbResolvedW3dResource? source;
            if(definition.Kind==EnbBaseAssetDefinitionKind.NullObject ||
                definition.Kind==EnbBaseAssetDefinitionKind.FileName && definition.Name is {} name &&
                string.Equals(name,"NULL",StringComparison.OrdinalIgnoreCase))
            {
                //6F5358 constructs941330("NULL");729770 retains the nonzero
                //RenderObj even though it draws nothing. Use the same concrete
                //NULL representation as the built-in prototype, without FILE IO.
                //91D4E2 also selects that prototype before FILE for NAME=NULL.
                if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                    assets.FileResources,assets.RetailMixResources,"NULL",out source,out var why))
                    throw new InvalidDataException(why);
            }
            else
            {
                selected=EnbCurrentRetailRObjEffectAsset.SelectOrdinaryBaseFactory(assets,spec.BaseAsset,
                    CreateLiveWorldPublicationContext(ReadMapperClock()),_prototypeResources.CapturePrototypeSources(out _));
                source=selected.Source;
            }
            if(source is null)
                retained=new(new(spec.BaseAsset,EnbBaseAssetResolutionStatus.NullObject,definition),null,null);
            else
            {
                var resolution=new EnbBaseAssetResolution(spec.BaseAsset,EnbBaseAssetResolutionStatus.W3d,definition,source);
                bool ready=TryGetCpuRoot(new(spec.BaseAsset,-1,source.LogicalFileName,source.RequestedObjectName),source,assets,out var root,out var diagnostic);
                if(ready)root=RetainLegoParticleFactory(obj,spec,root with {UnconfiguredFactoryObject=selected?.IsMissingFactoryObject??false});
                retained=new(resolution,ready?root:null,diagnostic);
            }
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException)
        {
            retained=new(new(spec.BaseAsset,EnbBaseAssetResolutionStatus.UnsupportedDefinition,definition,Diagnostic:error.Message),null,error.Message);
        }
        _legoBaseFactories.Add(key,retained);
        binding.Resolved=true;return binding.Factory=retained;
    }

    private static float InitialBaseLodBias(IEnbRenderObject obj)
        //733873..733883 applies this only on CreateObject, not737400 base replacement.
        =>obj.WorldCreationOrdinal.HasValue && obj.ObjectType==2 && obj.BaseAssetMaterializationRevision==0 ? .1f : 1f;

    private static LiveCpuRoot SetFactoryLodBias(LiveCpuRoot root,float bias)
    {
        //98D2F0 visits aggregate members, never ordinary HLOD levels.
        //97D5B0 forwards to its retained buffer. Sphere90AF30/Ring910850 clamp
        //their own bias; ordinary Mesh/Collection leaves do not forward it.
        if(root.RetainedEmitter is not null)return root with {LodBias=bias};
        if(root.Sphere is not null || root.Ring is not null)return root with {LodBias=bias>0?bias:0};
        if(root.Hlod is null && !root.Resource.HierarchyModels.Any(m=>
            string.Equals(m.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase)))return root;
        bias=bias>0?bias:0;
        return root with {LodBias=bias,
            HmodelChildren=root.HmodelChildren.Select(c=>c.IsAggregate?c with {Root=SetFactoryLodBias(c.Root,bias)}:c).ToArray(),
            AggregateAttachments=root.AggregateAttachments.Select(a=>a.Root is {} child?a with {Root=SetFactoryLodBias(child,bias)}:a).ToArray()};
    }

    private readonly HashSet<OrdinaryBaseFactoryKey> _liveOrdinaryBaseFactoryKeys=[];

    private void PruneOrdinaryBaseFactories(IEnbSceneFrame snapshot)
    {
        var live=_liveOrdinaryBaseFactoryKeys;
        live.Clear();
        foreach(var obj in SceneObjects(snapshot))live.Add(OrdinaryBaseFactoryKey.From(obj));
        // Dictionary removal preserves enumeration on the target .NET runtime.
        foreach(var key in _ordinaryBaseFactories.Keys)
            if(!live.Contains(key))_ordinaryBaseFactories.Remove(key);
        live.Clear();
    }

    private LiveCpuRoot RetainLegoParticleFactory(IEnbRenderObject obj,LiveShipLegoSpec spec,LiveCpuRoot source)
    {
        if(_shipExhaustOwner is {} primary)return primary.RetainLegoParticleFactory(obj,spec,source);
        var binding=BindLegoFactory(obj,spec);
        if(binding.Particle is {} particle){BindLegoPhysicalLifetime(binding);return particle;}
        var key=binding.Key;
        if(_legoParticleFactories.TryGetValue(key,out var retained)){binding.Particle=retained;BindLegoPhysicalLifetime(binding);return retained;}
        //729770 constructs at controller scale1/neutral HSV before72B7F0
        //looks for a parent/mount.72A7C0 preserves children on hull replacement.
        retained=(HasParticleConstruction(source)
            ? RetainParticleFactoryTreeCore(source.FactoryLookupName??source.RootRenderObjectName,
                source,1,System.Numerics.Vector3.Zero,true)
            : source) with {PhysicalFactoryOwner=new PhysicalFactoryLifetime(),DazzleFactoryCreationClock=_dazzlePreviousFrameClock};
        _legoParticleFactories.Add(key,retained);
        binding.Particle=retained;BindLegoPhysicalLifetime(binding);return retained;
    }

    private LiveCpuRoot ApplyLegoInheritedLodBias(IEnbRenderObject obj,LiveShipLegoSpec spec,LiveCpuRoot root,float bias)
    {
        if(_shipExhaustOwner is {} primary)return primary.ApplyLegoInheritedLodBias(obj,spec,root,bias);
        if(root.LodBias==bias)return root;
        //98E4B0 invokes the child's concrete v+12C before parenting it.
        //Keep the changed factory state after removal;98E0F0 does not reset it.
        var updated=SetFactoryLodBias(root,bias);
        var binding=BindLegoFactory(obj,spec);var key=binding.Key;
        _legoParticleFactories[key]=binding.Particle=updated;
        if(_legoBaseFactories.TryGetValue(key,out var factory))_legoBaseFactories[key]=binding.Factory=factory with {Root=updated};
        return updated;
    }

    private void PruneLegoParticleFactories(IEnbSceneFrame snapshot)
    {
        using var ownerTiming=EnbWorkReuseDiagnostics.Measure("owners.factoryRetirement");
        if(_ownedSceneFrame)
        {
            foreach(var source in _dirtyLegoFactoryOwners)PruneLegoFactoryOwner(source.Definition,source.LegoFactories);
            _dirtyLegoFactoryOwners.Clear();
            // Generated/legacy factories do not establish ordinary node ownership.
            foreach(var group in _compatLegoFactoryBindings.GroupBy(binding=>binding.Key.Identity).ToArray())
                if(group.First().CompatibilityObject is {} obj)PruneLegoFactoryOwner(obj,group.ToList());
            return;
        }
        var live=new HashSet<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path)>();
        foreach(var obj in SceneObjects(snapshot))
        {
            if(RetainedLegoSpecs(obj,all:true) is {} retained)
            {
                foreach(var spec in retained)live.Add((obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath));
                continue;
            }
            foreach(var child in obj.ShipLegos?.Attachments??[])
                if(TryParseShipLegoPath(child.BoneName,out var path,out _))
                    live.Add((obj.RenderIdentity,"aux",child.Ordinal,child.MaterializationRevision,child.BaseAsset,string.Join('/',path)));
            for(int i=0;i<obj.SubParts.Length;i++)
            {
                var child=obj.SubParts[i];
                if(TryParseShipLegoPath(child.Name,out var path,out _))
                    live.Add((obj.RenderIdentity,"0x00B4",i,child.MaterializationRevision,unchecked((int)child.PartValue),string.Join('/',path)));
            }
        }
        foreach(var (identity,physical) in _legoPhysicalOwners)
        {
            var visited=new HashSet<LegoPhysicalObject>();
            void Keep(LegoPhysicalObject model)
            {
                if(!visited.Add(model))return;
                if(model.Spec is {} spec)live.Add((identity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath));
                if(model is LegoPhysicalModel concrete)foreach(var child in concrete.Children)Keep(child);
                else if(model is LegoRslOwner wrapper)foreach(var retained in wrapper.Models.Values)Keep(retained);
            }
            foreach(var model in physical.RootModels.Values)Keep(model);
            foreach(var (key,wrapper) in LegoRslOwnersFor(identity))
                if(live.Contains(key))Keep(wrapper);
            //Orphan controllers retain their physical descendants too.
            foreach(var model in physical.ChildModels.Values)
                if(model.Spec is {} spec && live.Contains((identity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath)))Keep(model);
            foreach(var key in physical.ChildModels.Keys.Where(k=>!visited.Contains(physical.ChildModels[k])).ToArray())physical.ChildModels.Remove(key);
        }
        foreach(var key in _legoFactoryBindings.Keys.Where(k=>!live.Contains(k)).ToArray())
            if(_legoFactoryBindings.TryGetValue(key,out var binding))
            {RemoveLegoFactoryBinding(binding);binding.Source?.LegoFactories.Remove(binding);}
        foreach(var key in _legoParticleFactories.Keys.Where(k=>!live.Contains(k)).ToArray())_legoParticleFactories.Remove(key);
        foreach(var key in _legoBaseFactories.Keys.Where(k=>!live.Contains(k)).ToArray())_legoBaseFactories.Remove(key);
        var sourceOwners=(_shipExhaustOwner??this)._legoBaseFactories.Values
            .Select(f=>f.Root?.PhysicalFactoryOwner).Where(o=>o is not null).ToHashSet();
        foreach(var key in _legoSourceInstances.Keys.Where(k=>!sourceOwners.Contains(k)).ToArray())_legoSourceInstances.Remove(key);
    }

    private LiveCpuRoot RetainFixedShipParticleFactory(IEnbRenderObject obj,int slot,string name,LiveCpuRoot source)
    {
        if(_shipExhaustOwner is {} primary)return primary.RetainFixedShipParticleFactory(obj,slot,name,source);
        if(!HasParticleConstruction(source))return source;
        //6FCB70 callers:6F33F0 base replacement,732790 Attachments notification,
        //6FEF70 SubParts. A controller-scale/composition change is not a factory.
        var key=(obj.RenderIdentity,obj.BaseAssetMaterializationRevision,slot,obj.FixedShipVisualRevision);
        if(_fixedShipParticleFactories.TryGetValue(key,out var retained))return retained;
        retained=RetainParticleFactoryTreeCore(name,source,1,System.Numerics.Vector3.Zero,true);
        _fixedShipParticleFactories.Add(key,retained);
        return retained;
    }

    private OrdinaryBaseFactory? ResolveSceneBaseFactory(IEnbRenderObject obj,EnbRetailAssetBundle assets)
    {
        if(_shipExhaustOwner is {} primary)return primary.ResolveSharedSceneFactory(obj,assets);
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(obj.BaseAsset);
        if(definition.Kind is not (EnbBaseAssetDefinitionKind.FileName or EnbBaseAssetDefinitionKind.NullObject) && !EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(definition))return null;
        var key=OrdinaryBaseFactoryKey.From(obj);
        if(_ordinaryBaseFactories.TryGetValue(key,out var retained))return retained;
        retained=CreateOrdinaryBaseFactory(obj,assets,definition);
        _ordinaryBaseFactories.Add(key,retained);
        return retained;
    }

    private OrdinaryBaseFactory CreateOrdinaryBaseFactory(IEnbRenderObject obj,EnbRetailAssetBundle assets,EnbBaseAssetDefinition definition, EnbCurrentRetailRObjEffectAsset.FactoryPreparation? retainedSelection = null)
    {
        CollectNativeEventsDuringLoading?.Invoke();
        OrdinaryBaseFactory retained;
        //733816/6F33F0: construct once, retain until replacement, independent of later publication.
        try
        {
            if(definition.Kind==EnbBaseAssetDefinitionKind.NullObject)
                return CreateOrdinaryNullBaseFactory(obj,assets,definition);
            if(retainedSelection is null && TryCreateConfiguredBaseFactory(obj,assets,definition,out var configured))return configured;
            var selected=retainedSelection ?? EnbCurrentRetailRObjEffectAsset.SelectOrdinaryBaseFactory(assets,obj.BaseAsset,
                CreateLiveWorldPublicationContext(ReadMapperClock()),_prototypeResources.CapturePrototypeSources(out _),
                obj.Scale,new(obj.HsvH,obj.HsvS,obj.HsvV));
            if(selected.Source is not {} source)
            {
                retained=selected.Build().IsNullObject ? CreateOrdinaryNullBaseFactory(obj,assets,definition)
                    :new(new(obj.BaseAsset,EnbBaseAssetResolutionStatus.NullObject,definition),null,null);
                if(retained.Root is {} nullRoot && definition.Kind==EnbBaseAssetDefinitionKind.FileName && definition.Name is {} nullName)
                    retained=retained with {Root=RetainConfiguredPrimitive(nullName,nullRoot,obj.Scale,new(obj.HsvH,obj.HsvS,obj.HsvV),assets)};
            }
            else
            {
                var resolution=new EnbBaseAssetResolution(obj.BaseAsset,EnbBaseAssetResolutionStatus.W3d,definition,source);
                bool ready=TryGetCpuRoot(new(obj.BaseAsset,-1,source.LogicalFileName,source.RequestedObjectName),source,assets,out var root,out var diagnostic);
                if(ready)
                {
                    root=root with {UnconfiguredFactoryObject=selected.IsMissingFactoryObject};
                    if(selected.IsPreconfiguredFactoryObject)
                    {
                        root=root with {ConfiguredFactoryInputs=(1,System.Numerics.Vector3.Zero)};
                        if(root.IsDirectMeshPrototype)
                            foreach(var mesh in root.Resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,root.RootRenderObjectName)))
                                PostprocessOrdinaryNamedMesh(mesh);
                    }
                    if(!selected.IsMissingFactoryObject && !selected.IsPreconfiguredFactoryObject)
                    {
                        root=RetainConfiguredPrimitive(source.RequestedObjectName,root,obj.Scale,new(obj.HsvH,obj.HsvS,obj.HsvV),assets);
                        //6F51D0 ->48B220 configures/publishes mesh, HLOD and Collection before returning.
                        //Particle-bearing trees retain their constructor path below.
                        if(root.Collection is not null || HasConfigurableMeshTree(root)&&!HasParticleConstruction(root))
                        {
                            var collectionObject=FactoryAppearance.From(obj);
                            root=RetainConfiguredDirectMeshRoot(root,ref collectionObject);
                        }
                    }
                    if(HasParticleConstruction(root))
                    {
                        //6F51D0 creates the whole tree through48B220 before
                        //scene admission. Generic particle members, including
                        //hidden levels, own constructor tables at this point.
                        var factoryObject=selected.IsMissingFactoryObject||selected.IsPreconfiguredFactoryObject?FactoryAppearance.Neutral:FactoryAppearance.From(obj);
                        float rawScale=factoryObject.Scale;
                        var rawHsv=new System.Numerics.Vector3(factoryObject.HsvH,factoryObject.HsvS,factoryObject.HsvV);
                        if(HasConfigurableMeshTree(root))root=RetainConfiguredDirectMeshRoot(root,ref factoryObject);
                        root=RetainParticleFactoryTreeCore(source.RequestedObjectName,root,rawScale,rawHsv,obj.ObjectType is 0 or 1);
                    }
                }
                retained=new(resolution,ready?SetFactoryLodBias(root,InitialBaseLodBias(obj)) with {PhysicalFactoryOwner=new PhysicalFactoryLifetime(),DazzleFactoryCreationClock=_dazzlePreviousFrameClock}:null,diagnostic);
            }
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException)
        {
            //Unsupported decoding is not native factory failure. Keep it local;
            //only the proven factory failure path above may select MISSING.
            retained=new(new(obj.BaseAsset,EnbBaseAssetResolutionStatus.UnsupportedDefinition,definition,Diagnostic:error.Message),null,error.Message);
        }
        return retained;
    }

    private OrdinaryBaseFactory CreateOrdinaryNullBaseFactory(IEnbRenderObject obj,EnbRetailAssetBundle assets,
        EnbBaseAssetDefinition definition)
    {
        //6F5358 constructs941330;6F33F0 retains it and notifies the bone
        //controller even though it contributes no draw packets. Keep its pose
        //and child map through the existing concrete NULL prototype path.
        if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,
            assets.RetailMixResources,"NULL",out var source,out var why))throw new InvalidDataException(why);
        bool ready=TryGetCpuRoot(new(obj.BaseAsset,-1,source.LogicalFileName,source.RequestedObjectName),
            source,assets,out var root,out var diagnostic);
        return new(new(obj.BaseAsset,EnbBaseAssetResolutionStatus.W3d,definition,source),
            ready?root with {PhysicalFactoryOwner=new PhysicalFactoryLifetime(),DazzleFactoryCreationClock=_dazzlePreviousFrameClock}:null,diagnostic);
    }

    //Specialized consumers retain their visibility and buffer owners while
    //sharing construction tables with the generic factory namespace.
    private static bool HasSpecializedExhaustFactory(LiveCpuRoot root)=>
        root.CollectionChildren.Any(HasSpecializedExhaustFactory) ||
        root.HmodelChildren.Any(c=>HasSpecializedExhaustFactory(c.Root)) ||
        root.AggregateAttachments.Any(a=>a.Exhaust is not null || a.Root is {} child && HasSpecializedExhaustFactory(child));
}
