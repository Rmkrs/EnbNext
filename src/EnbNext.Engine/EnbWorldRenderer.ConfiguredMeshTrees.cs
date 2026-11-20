using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private static bool HasConfigurableMeshTree(LiveCpuRoot root)=>root.IsDirectMeshPrototype ||
        root.Hlod is not null || root.Collection is not null;

    private ConfiguredDirectMeshVariant RetainConfiguredMeshTree(string name,float scale,Vector3 hsv,
        EnbRetailAssetBundle assets,LiveCpuRoot source,uint? constructionClock=null)
    {
        string key=EnbCurrentRetailPrototypeVariantName.Create(name,scale,hsv);
        if(_configuredDirectMeshVariants.TryGetValue(key,out var cached))return cached;
        float effectiveScale=EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)?scale:1;
        var effectiveHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv)?hsv:Vector3.Zero;
        var primitiveConfigurationOwner=new object();
        var originals=new List<EnbW3dRenderMesh>();
        var inputs=new List<EnbW3dRenderMesh>();
        var outsideCollections=new List<bool>();
        //48BE60 follows HLOD +60/+64 recursively. Flatten only the model
        //operations in retained order. Collection blocks this early copy walk;
        //its mesh copies occur later in93EDD0's scale dispatch. Preserve both
        //the render hierarchy and each concrete child occurrence.
        int Add(EnbW3dRenderMesh mesh,bool outsideCollection)
        {
            int index=inputs.Count;originals.Add(mesh);
            outsideCollections.Add(outsideCollection);
            inputs.Add((mesh).Copy().SetContainerName($"<factory-input:{index}>"));return index;
        }
        Func<IReadOnlyList<EnbW3dRenderMesh>,LiveCpuRoot> Prepare(LiveCpuRoot root,bool outsideCollection=true)
        {
            if(root.IsDirectMeshPrototype)
            {
                var indices=root.Resource.Meshes.Where(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,root.RootRenderObjectName)).Select(m=>Add(m,outsideCollection)).ToArray();
                return meshes=>root with {Resource=root.Resource.Copy().SetMeshes(indices.Select(i=>meshes[i]).ToArray()),
                    RootRenderObjectName=indices.Length==1?EnbW3dRenderObjectNameSemantics.RegisteredMeshName(meshes[indices[0]]):root.RootRenderObjectName};
            }
            if(root.Collection is not null)
            {
                var collectionChildren=root.CollectionChildren.Select(child=>Prepare(child,false)).ToArray();
                return meshes=>root with {CollectionChildren=collectionChildren.Select(child=>child(meshes)).ToArray()};
            }
            if(root.Hlod is not {} hlod)return _=>root;
            int ordinal=0;
            var inline=new List<int>();
            var memberIndices=new Dictionary<int,int>();
            var children=new Dictionary<int,Func<IReadOnlyList<EnbW3dRenderMesh>,LiveCpuRoot>>();
            foreach(var member in hlod.Levels.SelectMany(l=>l.Members).Concat(hlod.Aggregate?.Members??[]))
            {
                //98C7A0 skips failed acquisitions;98EF60/48BE60 walk the
                //compacted retained array, not the original HMODEL file slots.
                var child=root.HmodelChildren.FirstOrDefault(c=>c.StorageOrdinal==ordinal);
                if(child is not null)children.Add(ordinal,Prepare(child.Root,outsideCollection));
                else
                {
                    var mesh=root.Resource.Meshes.SingleOrDefault(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,member.Name));
                    if(mesh is not null){int index=Add(mesh,outsideCollection);inline.Add(index);memberIndices.Add(ordinal,index);}
                }
                ordinal++;
            }
            return meshes=>
            {
                int slot=0;
                EnbW3dRenderHlodMember Rewrite(EnbW3dRenderHlodMember member)=>memberIndices.TryGetValue(slot++,out int index)
                    ? member with {Name=EnbW3dRenderObjectNameSemantics.RegisteredMeshName(meshes[index])}:member;
                var selected=hlod with {Levels=hlod.Levels.Select(l=>l with {Members=l.Members.Select(Rewrite).ToArray()}).ToArray(),
                    Aggregate=hlod.Aggregate is {} aggregate?aggregate with {Members=aggregate.Members.Select(Rewrite).ToArray()}:null};
                //Keep semantic child names separately from per-occurrence model keys.
                //7E8A5A queries the cloned child name when selecting boundaries.
                return root with {Hlod=selected,AuthoredHlodMemberNames=root.AuthoredHlodMemberNames??hlod.Levels.FirstOrDefault()?.Members.Select(m=>m.Name).ToArray()??[],Resource=root.Resource.Copy().SetMeshes(inline.Select(i=>meshes[i]).ToArray()).SetHierarchicalLods([selected]).SetHierarchyModels([]),
                    HmodelChildren=root.HmodelChildren.Select(c=>children.TryGetValue(c.StorageOrdinal,out var project)?c with {Root=project(meshes)}:c).ToArray()};
            };
        }
        var project=Prepare(source);
        var sequence=new EnbW3dRenderHlod("<factory-sequence>","",[new(0,0,0,inputs.Select(m=>new EnbW3dRenderHlodMember(0,EnbW3dRenderObjectNameSemantics.RegisteredMeshName(m))).ToArray())]);
        var flat=source.Resource.Copy().SetMeshes(inputs).SetHierarchicalLods([sequence]).SetHierarchyModels([]);
        uint clock=constructionClock??ReadMapperClock();
        using var acquired=new EnbCurrentRetailRObjMeshMappers(flat,clock,CameraMeshBatches,_prototypeResources,originals.Cast<EnbW3dRenderMesh?>().ToArray(),_configuredDirectMeshModels);
        using var configured=outsideCollections.Any(value=>!value)
            ?acquired.ConfigureCollectionTree(clock,assets.Textures,effectiveHsv,effectiveScale,outsideCollections)
            :acquired.Configure(clock,assets.Textures,effectiveHsv,effectiveScale,compositeOccurrences:true);
        //98EF60 and991220 forward scale to HLOD/Collection children;
        //48D710 deliberately does not forward HSV through Collection.
        bool HasDazzles(LiveCpuRoot node)=>node.Resource.ExternalRenderObjects.Any(e=>e.DazzleDefinition is not null) ||
            node.CollectionChildren.Any(HasDazzles)||node.HmodelChildren.Any(child=>HasDazzles(child.Root));
        LiveCpuRoot ConfigurePrimitives(LiveCpuRoot node,Vector3 shift)
        {
            //98EF60 scales each child's hierarchy storage in sequence; its
            //animation header accumulates separately from the pivot stores.
            node=node with {HierarchyConfigurationOwner=primitiveConfigurationOwner};
            if(node.Resource.ExternalRenderObjects.Any(e=>e.DazzleDefinition is not null))
                node=node with {Resource=node.Resource.Copy().SetExternalRenderObjects(node.Resource.ExternalRenderObjects.Select(e=>
                    e.DazzleDefinition is not null?e with {DazzleInstanceScale=e.DazzleInstanceScale*effectiveScale,HasConfiguredDazzleScale=true}:e).ToArray()),
                    PrimitiveConfigurationOwner=primitiveConfigurationOwner,NamedChildBoundsRoot=IsDirectDazzle(node)?null:node.NamedChildBoundsRoot};
            if(node.Hlod is not null)
                node=node with {ConfiguredHierarchyTranslations=(node.ConfiguredHierarchyTranslations??node.Resource.Hierarchies)
                    .Select(tree=>tree.ScaleStoredTranslations(effectiveScale)).ToArray()};
            if(node.Sphere is not null || node.Ring is not null)return node with
            {
                Sphere=node.Sphere is {} sphere?(sphere with {Asset=sphere.Asset.Scale(effectiveScale)}).ApplyHsv(assets,shift):null,
                Ring=node.Ring is {} ring?(ring with {Asset=ring.Asset.ScaleRadiusMultipliers(effectiveScale)}).ApplyHsv(assets,shift):null,
                PrimitiveConfigurationOwner=primitiveConfigurationOwner
            };
            if(node.Collection is not null)return node with
            {CollectionChildren=node.CollectionChildren.Select(child=>ConfigurePrimitives(child,Vector3.Zero)).ToArray()};
            if(node.Hlod is {} parentHlod)
            {
                var children=node.HmodelChildren.Select(child=>child with {Root=ConfigurePrimitives(child.Root,shift)}).ToArray();
                var descriptions=node.Resource.CompositeRenderChildren.ToDictionary(child=>child.Name,StringComparer.OrdinalIgnoreCase);
                var members=parentHlod.Levels.SelectMany(level=>level.Members).Concat(parentHlod.Aggregate?.Members??[]).ToArray();
                foreach(var child in children.Where(child=>HasDazzles(child.Root)))
                {
                    string memberName=members[child.StorageOrdinal].Name;
                    if(!TryDescribeCompositeChild(child.Root,memberName,out var description,out var error))throw new InvalidDataException(error);
                    descriptions[memberName]=description;
                }
                return node with {HmodelChildren=children,Resource=node.Resource.Copy().SetCompositeRenderChildren(descriptions.Values.ToArray())};
            }
            return node;
        }
        var root=ConfigurePrimitives(project(configured.Resource.Meshes),effectiveHsv) with {PrimitiveConfigurationOwner=primitiveConfigurationOwner};
        var model=configured.RetainRenderObjects(root.Resource.Copy().SetMeshes(configured.Resource.Meshes));
        var result=new ConfiguredDirectMeshVariant(model,(source.ConfiguredFactoryInputs?.Scale??1)*effectiveScale,effectiveHsv,root)
            {UnconfiguredCompositeRoot=source};
        RetainConfiguredEffectAssembly(key,source,root,model,result.Scale,result.Hsv);
        foreach(var mesh in model.Resource.Meshes)_configuredDirectMeshModels.Add(mesh,model);
        _configuredDirectMeshVariants.Add(key,result);
        _prototypeResources.RegisterConfiguredPrototypeName(key);
        return result;
    }
}
