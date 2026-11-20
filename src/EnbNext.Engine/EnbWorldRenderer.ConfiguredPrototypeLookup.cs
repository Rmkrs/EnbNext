using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;
using System.Numerics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    //A prepared effect can carry an unchanged concrete factory without a
    //generated namespace entry. Movie localization must consume that same
    //construction, not rerun its child factories from the exported resource.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbCurrentRetailRObjEffectAsset,LiveCpuRoot> _effectFactoryRoots=new();
    private static bool IsEffectNoScaleLeaf(LiveCpuRoot root)=>IsNoOpConfiguredLeaf(root);
    private static bool ContainsConvertedLegacy(LiveCpuRoot root)=>root.Hlod?.IsConvertedLegacyLod==true ||
        root.HmodelChildren.Any(child=>ContainsConvertedLegacy(child.Root));
    private static bool HasLevelOwnedModelTree(LiveCpuRoot root)=>root.HmodelChildren.Any(child=>
        !child.IsAggregate && child.Root.Hlod is not null || HasLevelOwnedModelTree(child.Root));
    private bool TryPublishEffectMesh(EnbRetailAssetBundle assets,int id,
        EnbCurrentRetailRObjEffectAsset.FactoryPreparation selected,(float Scale,Vector3 Hsv) inputs,
        out EnbCurrentRetailRObjEffectAsset asset,out EffectObjectModelAcquisition owner)
    {
        if(_shipExhaustOwner is {} primary)return primary.TryPublishEffectMesh(assets,id,selected,inputs,out asset,out owner);
        asset=null!;owner=null!;
        if(selected.IsMissingFactoryObject||selected.IsPreconfiguredFactoryObject || selected.Source is not {} source ||
            (source.CurrentRetailAggregate?.BaseRegisteredPrototype??source.RegisteredPrototype) is not {Kind:EnbW3dPrototypeKind.Mesh or EnbW3dPrototypeKind.HierarchicalLod or EnbW3dPrototypeKind.HierarchyModel or EnbW3dPrototypeKind.LegacyLod} prototype)return false;
        bool configured=EnbCurrentRetailPrototypeVariantName.ChangesScale(inputs.Scale)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(inputs.Hsv);
        //6EA830 calls6F51D0/48B220 before constructing the effect. Publish
        //the configured clone now, not when its first draw retains models.
        //PrepareOwnedFactory has published the source and postprocessed its
        //named meshes. CPU projection only observes those existing AAB trees;
        //PostprocessFactoryTree is a no-op once that tree exists. Emitter
        //tables must be constructed before configuration, then cloned.
        if(!TryGetCpuRoot(new(id,-1,source.LogicalFileName,source.RequestedObjectName),source,assets,out var root,out _))return false;
        bool nestedLegacy=root.HmodelChildren.Any(child=>ContainsConvertedLegacy(child.Root));
        bool retainedAssembly=nestedLegacy||HasLevelOwnedModelTree(root);
        if(!configured && prototype.Kind!=EnbW3dPrototypeKind.LegacyLod &&
            !retainedAssembly && !root.HmodelChildren.Any(child=>child.Root.NamedChildBoundsRoot is not null))return false;
        if((!configured&&!retainedAssembly || !CanProjectConfiguredEffectAssembly(root)) && !root.IsDirectMeshPrototype && (root.Hlod is null || root.HmodelChildren.Any(c=>c.Root.RetainedEmitter is null&&!c.Root.IsDirectMeshPrototype&&!IsDirectDazzle(c.Root)&&!IsEffectNoScaleLeaf(c.Root))))return false;
        if(root.AggregateAttachments.Count!=0 || root.Resource.ParticleEmitters.Count!=0)return false;
        if(HasParticleConstruction(root))root=ConstructParticleFactoryTree(root);
        if(!configured && retainedAssembly && CanProjectConfiguredEffectAssembly(root))
            return RetainUnconfiguredEffectAssembly(source.RequestedObjectName,root,out asset,out owner);
        //48B220's unchanged branch does not copy/configure the models or
        //publish a generated name. Still consume the constructor's retained
        //named children, including9A4990's converted owner.
        if(!configured)return TryAcquireConfiguredEffectMesh(assets,id,inputs,out asset,out owner,root);
        var variant=RetainConfiguredMeshTree(source.RequestedObjectName,inputs.Scale,inputs.Hsv,assets,root);
        root=(variant.CompositeRoot??root) with {ConfiguredFactoryInputs=(variant.Scale,variant.Hsv)};
        if(HasParticleConstruction(root))RetainParticleFactoryTree(source.RequestedObjectName,root,inputs.Scale,inputs.Hsv);
        return TryAcquireConfiguredEffectMesh(assets,id,inputs,out asset,out owner);
    }

    //6EA830 ->6F51D0 ->48B220 performs named configured acquisition before
    //the authored FILE retry. Preserve the retained model and mapper owners.
    private bool TryAcquireConfiguredEffectMesh(EnbRetailAssetBundle assets,int id,
        (float Scale,Vector3 Hsv) inputs,out EnbCurrentRetailRObjEffectAsset asset,out EffectObjectModelAcquisition owner,LiveCpuRoot? constructedRoot=null)
    {
        if(_shipExhaustOwner is {} primary)return primary.TryAcquireConfiguredEffectMesh(assets,id,inputs,out asset,out owner,constructedRoot);
        asset=null!;owner=null!;
        if(assets.PresentationCatalog?.ResolveBaseAsset(id) is not {Kind:EnbBaseAssetDefinitionKind.FileName,Name:{} name})return false;
        bool configured=EnbCurrentRetailPrototypeVariantName.ChangesScale(inputs.Scale)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(inputs.Hsv);
        string variantName=EnbCurrentRetailPrototypeVariantName.Create(name,inputs.Scale,inputs.Hsv);
        if(TryAcquireConfiguredEffectAssembly(variantName,out asset,out owner))return true;
        bool final=configured&&HasConfiguredPrototype(variantName);
        var root=constructedRoot;
        if(root is null)
        {
            if(!final&&!HasConfiguredPrototype(name))return false;
            if(!TryAcquireConfiguredPrototype(id,final?variantName:name,-1,assets,out root))return false;
        }
        if(!final && configured && CanProjectConfiguredEffectAssembly(root))
        {
            var assembly=RetainConfiguredMeshTree(name,inputs.Scale,inputs.Hsv,assets,root);
            root=(assembly.CompositeRoot??root) with {ConfiguredFactoryInputs=(assembly.Scale,assembly.Hsv)};
            if(HasParticleConstruction(root))_=RetainParticleFactoryTree(name,root,inputs.Scale,inputs.Hsv);
            return TryAcquireConfiguredEffectAssembly(variantName,out asset,out owner);
        }
        //Movie ResetMappers must localize the same concrete neutral tree
        //acquired by6EA830, including nested9A4990 conversions.
        if(!configured && CanProjectConfiguredEffectAssembly(root) &&
            (root.HmodelChildren.Any(child=>ContainsConvertedLegacy(child.Root))||HasLevelOwnedModelTree(root)))
            return RetainUnconfiguredEffectAssembly(name,root,out asset,out owner);
        //The mesh effect consumer represents direct meshes and HLODs with
        //retained concrete mesh/emitter/dazzle and bounds-only leaf children.
        if(!root.IsDirectMeshPrototype && (root.Hlod is null || root.HmodelChildren.Any(c=>c.Root.RetainedEmitter is null&&!c.Root.IsDirectMeshPrototype&&!IsDirectDazzle(c.Root)&&!IsEffectNoScaleLeaf(c.Root))))return false;
        if(root.AggregateAttachments.Count!=0 || root.RetainedEmitter is not null || root.Resource.ParticleEmitters.Count!=0)return false;
        if(!final&&configured)
        {
            var variant=RetainConfiguredMeshTree(name,inputs.Scale,inputs.Hsv,assets,root);
            root=(variant.CompositeRoot??root) with {Resource=variant.CompositeRoot?.Resource??variant.Model.Resource,
                ConfiguredFactoryInputs=(variant.Scale,variant.Hsv)};
            if(HasParticleConstruction(root))root=RetainParticleFactoryTree(name,root,inputs.Scale,inputs.Hsv);
        }
        var resource=ConfiguredHierarchyResource(root);
        var registeredModels=resource.Meshes.Cast<EnbW3dRenderMesh?>().ToList();
        if(root.Hlod is {} dazzleHlod)
        {
            //98C420 retains each named child. Export its configured instance
            //under the parent's member name for draw/bounds/cost consumers.
            var members=dazzleHlod.Levels.SelectMany(level=>level.Members).Concat(dazzleHlod.Aggregate?.Members??[]).ToArray();
            var external=resource.ExternalRenderObjects.ToDictionary(value=>value.Name,StringComparer.OrdinalIgnoreCase);
            foreach(var child in root.HmodelChildren.Where(child=>IsDirectDazzle(child.Root)||IsEffectNoScaleLeaf(child.Root)))
            {
                string memberName=members[child.StorageOrdinal].Name;
                external[memberName]=child.Root.SpecialMaterial is {} special
                    ? new(memberName,EnbW3dExternalRenderObjectKind.SpecialMaterial,child.Root.Resource.LogicalFileName,child.Root.Resource.Key.ArchivePath){SpecialMaterialDefinition=special.Definition}
                    : child.Root.Resource.ExternalRenderObjects.Single(value=>value.DazzleDefinition is not null || value.BoxDefinition is not null || value.Kind==EnbW3dExternalRenderObjectKind.NullObject) with {Name=memberName};
            }
            //The effect draw compiler consumes exported class24 leaves
            //inline; do not leave an ordinary-tree descriptor that suppresses
            //that draw in favor of a separately staged child.
            resource=resource.Copy().SetExternalRenderObjects(external.Values.ToArray()).SetCompositeRenderChildren(resource.CompositeRenderChildren
                .Where(child=>!external.TryGetValue(child.Name,out var leaf)||leaf.SpecialMaterialDefinition is null).ToArray());
            //48BE60 localizes every retained Mesh child before98EF60.
            //Export those exact per-occurrence models, not another FILE copy.
            //Use unique export aliases even when an unchanged parent shares
            //one native model across occurrences. Otherwise each member matches
            //every exported copy and draws duplicate geometry. Keep the actual
            //model reference separate from that alias and its bounds descriptor.
            var meshChildren=root.HmodelChildren.Where(child=>child.Root.IsDirectMeshPrototype).ToArray();
            if(meshChildren.Length!=0)
            {
                var meshes=resource.Meshes.ToList();
                var replacements=new Dictionary<int,string>();
                var descriptors=resource.CompositeRenderChildren.ToList();
                foreach(var child in meshChildren)
                {
                    var mesh=child.Root.Resource.Meshes.Single(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,child.Root.RootRenderObjectName));
                    var projected=(mesh).Copy().SetContainerName($"<effect-child:{child.StorageOrdinal}>");
                    string modelName=EnbW3dRenderObjectNameSemantics.RegisteredMeshName(projected);
                    string originalName=members[child.StorageOrdinal].Name;
                    var descriptor=resource.CompositeRenderChildren.Single(c=>c.Name.Equals(originalName,StringComparison.OrdinalIgnoreCase));
                    descriptors.RemoveAll(c=>c.Name.Equals(originalName,StringComparison.OrdinalIgnoreCase));
                    descriptors.Add(descriptor with {Name=modelName});
                    //GPU positions carry the child's prior configuration;
                    //the common effect matrix carries only the outer factory
                    //scale. Its retained native model is already fully scaled.
                    var prior=child.Root.NamedChildBoundsRoot?.Resource.Meshes.SingleOrDefault();
                    var geometry=prior?.CurrentRetailLoadedGeometry;
                    meshes.Add(geometry is null?projected:(projected).Copy().SetPositions(geometry.Positions.ToArray()).SetBoundingBoxMin(geometry.Minimum).SetBoundingBoxMax(geometry.Maximum).SetBoundingSphereCenter(geometry.Center).SetBoundingRadius(geometry.Radius));
                    registeredModels.Add(mesh);replacements.Add(child.StorageOrdinal,modelName);
                }
                int ordinal=0;
                EnbW3dRenderHlodMember Rewrite(EnbW3dRenderHlodMember member)=>replacements.TryGetValue(ordinal++,out var name)?member with {Name=name}:member;
                var exported=dazzleHlod with {Levels=dazzleHlod.Levels.Select(level=>level with {Members=level.Members.Select(Rewrite).ToArray()}).ToArray(),
                    Aggregate=dazzleHlod.Aggregate is {} aggregate?aggregate with {Members=aggregate.Members.Select(Rewrite).ToArray()}:null};
                resource=resource.Copy().SetMeshes(meshes).SetCompositeRenderChildren(descriptors).SetHierarchicalLods([exported]);
            }
        }
        if(root.IsDirectMeshPrototype)resource=resource.Copy().SetMeshes(resource.Meshes.Where(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,root.RootRenderObjectName)).ToArray()).SetHierarchicalLods([]).SetHierarchyModels([]);
        foreach(var mesh in resource.Meshes)PostprocessOrdinaryNamedMesh(mesh);
        var models=new EnbCurrentRetailRObjMeshMappers(resource,ReadMapperClock(),CameraMeshBatches,_prototypeResources,
            root.IsDirectMeshPrototype?resource.Meshes.Cast<EnbW3dRenderMesh?>().ToArray():registeredModels,_configuredDirectMeshModels);
        owner=new(models,final?variantName:name){FactoryInputs=root.ConfiguredFactoryInputs??(1,Vector3.Zero)};
        var emitters=new List<EnbRObjCompositeEmitter>();
        if(root.Hlod is {} particleHlod)
        {
            var members=particleHlod.Levels.SelectMany(l=>l.Members.Select((m,i)=>(Member:m,Level:l.Ordinal,Ordinal:i)))
                .Concat((particleHlod.Aggregate?.Members??[]).Select((m,i)=>(Member:m,Level:-1,Ordinal:i))).ToArray();
            foreach(var child in root.HmodelChildren.Where(child=>child.Root.RetainedEmitter is not null))
            {
                var emitter=child.Root.RetainedEmitter!;
                var member=members[particleHlod.IsConvertedHierarchyModel?child.StorageOrdinal:child.Ordinal];
                var particle=new EnbCurrentRetailRObjParticleAsset(emitter.Key,emitter.Texture,emitter.Definition,checked((int)child.Pivot))
                    {RuntimeTextureName=emitter.RuntimeTextureName,RetainedTexture=emitter.RetainedTexture};
                //97DCA0/97D620 clone existing property tables; creating the
                //effect's scene owner must not rerun the emitter constructor.
                emitters.Add(new(particle,member.Level,member.Ordinal){FactoryRandomOwner=child.Root.FactoryParticleRandom});
            }
        }
        asset=new(resource,root.Hlod is {} hlod?new(hlod.Name,0):null,root.OverbrightOff,null,null)
            {FactoryObjectName=final?variantName:name,IsPreconfiguredFactoryObject=true,CompositeEmitters=emitters,
                ForceSubObjectLod=root.ForceSubObjectLod,HasConfiguredHierarchy=root.ConfiguredHierarchyTranslations is not null};
        _effectFactoryRoots.Add(asset,root);
        return true;
    }

    private bool TryAcquireConfiguredChild(int baseAsset,int state,string name,EnbRetailAssetBundle assets,out LiveCpuRoot root)
    {
        if(_shipExhaustOwner is {} primary)return primary.TryAcquireConfiguredChild(baseAsset,state,name,assets,out root);
        if(!TryAcquireConfiguredPrototype(baseAsset,name,state,assets,out root))return false;
        //990450/98C420/9A3A50 acquire an ordinary named RenderObj. Its
        //previous configuration remains in the child before parent+164 runs.
        root=root with {NamedChildFactoryScale=root.ConfiguredFactoryInputs?.Scale??1,
            NamedChildBoundsRoot=CreateConfiguredBoundsRoot(root,root.ConfiguredFactoryInputs?.Scale??1)};
        void Postprocess(LiveCpuRoot node)
        {
            if(node.IsDirectMeshPrototype)
                foreach(var mesh in node.Resource.Meshes.Where(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,node.RootRenderObjectName)))PostprocessOrdinaryNamedMesh(mesh);
            else if(node.Hlod is not null)
            {
                foreach(var mesh in node.Resource.Meshes)PostprocessOrdinaryNamedMesh(mesh);
                foreach(var child in node.HmodelChildren)Postprocess(child.Root);
            }
            foreach(var child in node.CollectionChildren)Postprocess(child);
            foreach(var child in node.AggregateAttachments)if(child.Root is {} attached)Postprocess(attached);
        }
        Postprocess(root);
        return true;
    }

    //940CE0/940D10 query the retained MeshModel, not the FILE header.
    //Capture that configured child before a containing factory applies more
    //scale. The ordinary scene still carries its outer scale separately.
    private static LiveCpuRoot CreateConfiguredBoundsRoot(LiveCpuRoot root,float hierarchyScale)
    {
        LiveCpuRoot Child(LiveCpuRoot child)=>CreateConfiguredBoundsRoot(child,hierarchyScale*(child.NamedChildFactoryScale??1));
        var collections=root.CollectionChildren.Select(Child).ToArray();
        var children=root.HmodelChildren.Select(child=>child with {Root=Child(child.Root)}).ToArray();
        var resource=root.Resource.Copy().SetMeshes(root.Resource.Meshes.Select(mesh=>mesh.CurrentRetailLoadedGeometry is {} geometry?(mesh).Copy().SetBoundingBoxMin(geometry.Minimum).SetBoundingBoxMax(geometry.Maximum).SetBoundingSphereCenter(geometry.Center).SetBoundingRadius(geometry.Radius):mesh).ToArray()).SetHierarchies(root.ConfiguredHierarchyTranslations??root.Resource.Hierarchies
                .Select(tree=>tree.ScaleStoredTranslations(hierarchyScale)).ToArray());
        if(root.Hlod is {} hlod)
        {
            var members=hlod.Levels.SelectMany(level=>level.Members).Concat(hlod.Aggregate?.Members??[]).ToArray();
            var descriptions=resource.CompositeRenderChildren.ToDictionary(child=>child.Name,StringComparer.OrdinalIgnoreCase);
            foreach(var child in children)
            {
                string name=members[child.StorageOrdinal].Name;
                if(!TryDescribeCompositeChild(child.Root,name,out var description,out var error))throw new InvalidDataException(error);
                descriptions[name]=description;
            }
            resource=resource.Copy().SetCompositeRenderChildren(descriptions.Values.ToArray());
        }
        return root with {Resource=resource,CollectionChildren=collections,HmodelChildren=children,
            NamedChildBoundsRoot=null,NamedChildFactoryScale=null,ConfiguredHierarchyTranslations=null,HierarchyConfigurationOwner=null};
    }

    private bool HasConfiguredPrototype(string name)=>
        _configuredParticleVariants.ContainsKey(name)||_configuredDirectMeshRoots.ContainsKey(name)||
        _configuredDirectMeshVariants.ContainsKey(name)||_configuredPrimitiveVariants.ContainsKey(name);

    //6F51D0 calls manager+78 before loading the authored FILE. A generated
    //prototype is a retained RenderObj, not another decoded file descriptor.
    private bool TryCreateConfiguredBaseFactory(IEnbRenderObject obj,EnbRetailAssetBundle assets,
        EnbBaseAssetDefinition definition,out OrdinaryBaseFactory factory)
    {
        if(_shipExhaustOwner is {} primary)return primary.TryCreateConfiguredBaseFactory(obj,assets,definition,out factory);
        factory=null!;
        if(definition.Kind!=EnbBaseAssetDefinitionKind.FileName || definition.Name is not {} name)return false;
        var hsv=new Vector3(obj.HsvH,obj.HsvS,obj.HsvV);
        bool configured=EnbCurrentRetailPrototypeVariantName.ChangesScale(obj.Scale)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv);
        string variant=EnbCurrentRetailPrototypeVariantName.Create(name,obj.Scale,hsv);
        if(!HasConfiguredPrototype(name) && !(configured&&HasConfiguredPrototype(variant)))return false;
        LiveCpuRoot root=null!;
        bool final=false;
        if(configured)
        {
            final=TryAcquireRslNamedRoot(obj.BaseAsset,-1,variant,assets,out root,out var error,out bool missing);
            if(!final&&!missing)throw new InvalidDataException(error);
        }
        if(!final && !TryAcquireRslNamedRoot(obj.BaseAsset,-1,name,assets,out root,out var failure,out _))
            throw new InvalidDataException(failure);
        if(final)
        {
            if(root.ConfiguredFactoryInputs is null)root=root with {ConfiguredFactoryInputs=(1,Vector3.Zero)};
            if(root.IsDirectMeshPrototype)
                foreach(var mesh in root.Resource.Meshes.Where(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,root.RootRenderObjectName)))
                    PostprocessOrdinaryNamedMesh(mesh);
        }
        else if(configured && HasConfigurableMeshTree(root))
        {
            var requested=FactoryAppearance.From(obj);
            root=RetainConfiguredDirectMeshRoot(root,ref requested);
        }
        if(!final)root=RetainConfiguredPrimitive(name,root,obj.Scale,hsv,assets);
        if(HasParticleConstruction(root))root=RetainParticleFactoryTreeCore(final?variant:name,root,
            final?1:obj.Scale,final?Vector3.Zero:hsv,obj.ObjectType is 0 or 1);
        factory=new(new(obj.BaseAsset,EnbBaseAssetResolutionStatus.W3d,definition),
            SetFactoryLodBias(root,InitialBaseLodBias(obj)) with {PhysicalFactoryOwner=new PhysicalFactoryLifetime(),DazzleFactoryCreationClock=_dazzlePreviousFrameClock},null);
        return true;
    }

    //48B220 registers the generated token through91ECA0.91D4B0 can then
    //create it by that name, including an ordinary (unconfigured) RSL request.
    //Keep model ownership separate from name removal: existing objects retain
    //the model after91EDE0 releases its prototype.
    private bool TryAcquireConfiguredPrototype(int baseAsset,string name,int state,EnbRetailAssetBundle assets,out LiveCpuRoot root)
    {
        if(_shipExhaustOwner is {} primary)return primary.TryAcquireConfiguredPrototype(baseAsset,name,state,assets,out root);
        if(_configuredPrimitiveVariants.TryGetValue(name,out var primitive))root=primitive;
        else if(_configuredParticleVariants.TryGetValue(name,out var particle))root=particle;
        else if(_configuredDirectMeshRoots.TryGetValue(name,out var meshRoot))
            root=meshRoot.Root with {ConfiguredFactoryInputs=(meshRoot.Scale,meshRoot.Hsv)};
        else if(_configuredDirectMeshVariants.TryGetValue(name,out var variant))
        {
            if(variant.CompositeRoot is {} composite)root=composite;
            else
            {
                var resource=variant.Model.Resource;
                var hlod=resource.HierarchicalLods.SingleOrDefault();
                string runtimeName=hlod?.Name??EnbW3dRenderObjectNameSemantics.RegisteredMeshName(resource.Meshes.Single());
                var hierarchy=hlod is null?null:resource.Hierarchies.FirstOrDefault(h=>h.Name.Equals(hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
                var allowed=hlod is null?resource.Meshes.Select(mesh=>mesh.Name).ToHashSet(StringComparer.OrdinalIgnoreCase):null;
                root=new(false,resource,hlod,allowed,runtimeName,runtimeName,hierarchy?.RuntimeName,hlod?.HierarchyName,[])
                    {IsDirectMeshPrototype=hlod is null};
            }
            root=root with {ConfiguredFactoryInputs=(variant.Scale,variant.Hsv)};
        }
        else {root=default!;return false;}
        root=CloneConfiguredParticleTree(root) with {FactoryLookupName=name,RslFactoryStateId=state,
            PhysicalFactoryOwner=new PhysicalFactoryLifetime(),PrimitiveFactoryIdentity=new object(),DazzleFactoryCreationClock=_dazzlePreviousFrameClock,
            OverbrightOff=assets.PresentationCatalog?.ResolveBaseAsset(baseAsset).OverbrightOff==true};
        return true;
    }

    //48B050 delegates to RenderObj::Clone.97DCA0/97D620 copy an emitter's
    //buffer/property tables; they do not replay its authored constructor.
    private static LiveCpuRoot CloneConfiguredParticleTree(LiveCpuRoot root)=>root with
    {
        FactoryParticleRandom=root.FactoryParticleRandom?.Clone(),
        CollectionChildren=root.CollectionChildren.Select(CloneConfiguredParticleTree).ToArray(),
        HmodelChildren=root.HmodelChildren.Select(c=>c with {Root=CloneConfiguredParticleTree(c.Root)}).ToArray(),
        AggregateAttachments=root.AggregateAttachments.Select(a=>a.Root is {} child
            ?a with {Root=CloneConfiguredParticleTree(child)}:a).ToArray()
    };
}
