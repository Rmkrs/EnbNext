using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LiveCpuRoot, Dictionary<int, LiveCpuRoot>> _influenceRoomRoots = new();
    private sealed record InfluenceHelperFactory(IEnbRenderObject Object,LiveCpuRoot Root,
        EnbCurrentRetailInfluenceNebulaBoundarySource Source);
    private readonly Dictionary<ulong,InfluenceHelperFactory> _influenceHelperFactories=[];

    private bool TryPrepareInfluenceHelper(IEnbRenderObject obj,LiveCpuRoot root,string sourceName,
        out InfluenceHelperFactory helper,out string? diagnostic)
    {
        helper=null!;diagnostic=null;
        if((_shipExhaustOwner??this)._influenceHelperFactories.TryGetValue(obj.RenderIdentity,out helper!))return true;
        var selected=new EnbRenderObjectState(obj);
        var appearance=root.UnconfiguredFactoryObject?FactoryAppearance.Neutral:FactoryAppearance.From(obj);
        var names=root.AuthoredHlodMemberNames??root.Hlod?.Levels.FirstOrDefault()?.Members.Select(m=>m.Name).ToArray();
        if(root.ConfiguredFactoryInputs is {} inputs)
            appearance=new(inputs.Scale,inputs.Hsv.X,inputs.Hsv.Y,inputs.Hsv.Z);
        if(root.ConfiguredFactoryInputs is null && !root.ParticleConstructionPrepared && HasConfigurableMeshTree(root))root=RetainConfiguredDirectMeshRoot(root,ref appearance);
        selected.Scale=appearance.Scale;selected.HsvH=appearance.HsvH;selected.HsvS=appearance.HsvS;selected.HsvV=appearance.HsvV;
        if(root.Hlod is not {} hlod)
        {diagnostic="type 0x0A FILE root is not a one-level HLOD boundary source";return false;}
        if(root.AggregateAttachments.Count!=0)
        {
            //7E8B43..7E8C5A clones every permanent HLOD child into its room.
            //Exhaust-shaped attachments are generic class15 children here.
            //Their factory must run before these room clones are made.
            try { root=RetainInfluenceParticleSource(selected,root); }
            catch(InvalidDataException error){diagnostic=error.Message;return false;}
            if(!TryPromoteInfluenceAttachments(root,out root,out diagnostic))return false;
            hlod=root.Hlod!;
        }
        if(!EnbCurrentRetailInfluenceNebulaRuntime.TryCreateObjectWorldTransform(obj,out var world,out diagnostic))return false;
        helper=RetainInfluenceHelper(selected,root,new(ConfiguredHierarchyResource(root),hlod,world,sourceName)
            {ModelScale=selected.Scale,AuthoredMemberNames=names,
                PendingHierarchyScale=root.ConfiguredHierarchyTranslations is null?null:1});
        return true;
    }

    private static bool TryPromoteInfluenceAttachments(LiveCpuRoot root,out LiveCpuRoot promoted,out string? diagnostic)
    {
        promoted=root;diagnostic=null;
        var hlod=root.Hlod!;
        var tree=root.Resource.Hierarchies.Single(h=>h.Name.Equals(hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
        var members=new List<EnbW3dRenderHlodMember>(hlod.Aggregate?.Members??[]);
        var children=new List<LiveHmodelChild>(root.HmodelChildren);
        var descriptions=new List<EnbW3dRenderCompositeChild>(root.Resource.CompositeRenderChildren);
        int levelCount=hlod.Levels.Sum(l=>l.Members.Count);
        foreach(var attachment in root.AggregateAttachments)
        {
            if(attachment.Root is not {} child)
            {diagnostic=$"Influence permanent child '{attachment.SubobjectName}' was not materialized.";return false;}
            uint pivot=checked((uint)EnbCurrentRetailAggregateBoneLookup.Resolve(tree,attachment.BoneName));
            string name=$"<influence-permanent:{members.Count}:{attachment.SubobjectName}>";
            if(!TryDescribeCompositeChild(child,name,out var description,out diagnostic))return false;
            children.Add(new(levelCount+members.Count,pivot,child,null,true));
            descriptions.Add(description);members.Add(new(pivot,name));
        }
        var updated=hlod with {Aggregate=new(hlod.Aggregate?.RawValue??0,members.ToArray())};
        promoted=root with {Hlod=updated,HmodelChildren=children,AggregateAttachments=[],Resource=root.Resource.Copy().SetHierarchicalLods(root.Resource.HierarchicalLods.Select(h=>ReferenceEquals(h,hlod)?updated:h).ToArray()).SetCompositeRenderChildren(descriptions)};
        return true;
    }

    private InfluenceHelperFactory RetainInfluenceHelper(IEnbRenderObject obj,LiveCpuRoot root,
        EnbCurrentRetailInfluenceNebulaBoundarySource source)
    {
        if(_shipExhaustOwner is {} primary)return primary.RetainInfluenceHelper(obj,root,source);
        if(_influenceHelperFactories.TryGetValue(obj.RenderIdentity,out var retained))return retained;
        //7338E4/7E87C0 clone the room members once. The helper's +28 points
        //to the client object, not its replaceable +AC RenderObj.737400 does
        //not recreate this helper;7E8520/744260 release it on owner removal.
        retained=new(obj,CloneInfluenceDazzleClocks(root,_dazzlePreviousFrameClock) with {PhysicalFactoryOwner=new PhysicalFactoryLifetime()},source);
        _influenceHelperFactories.Add(obj.RenderIdentity,retained);
        return retained;
    }

    //7E87C0 clones the members at helper construction, before room selection.
    //Collection990B00/990680/990740 recursively clones its children; the
    //concrete Dazzle96BF90/96BCE0 replaces F8 with the current global clock.
    //Do not carry an older source factory's clock through those child copies.
    private static LiveCpuRoot CloneInfluenceDazzleClocks(LiveCpuRoot root,uint clock)
        =>root with
        {
            DazzleFactoryCreationClock=clock,
            CollectionChildren=root.CollectionChildren.Select(c=>CloneInfluenceDazzleClocks(c,clock)).ToArray(),
            HmodelChildren=root.HmodelChildren.Select(c=>c with {Root=CloneInfluenceDazzleClocks(c.Root,clock)}).ToArray(),
            AggregateAttachments=root.AggregateAttachments.Select(a=>a.Root is {} child
                ?a with {Root=CloneInfluenceDazzleClocks(child,clock)}:a).ToArray()
        };

    private bool TryAppendInfluenceRoom(IEnbRenderObject obj, LiveCpuRoot sourceRoot,
        EnbCurrentRetailInfluenceNebulaBoundarySource source, EnbCurrentRetailInfluenceNebulaEvaluation evaluation,
        RootTaskList destination, out string? diagnostic)
    {
        diagnostic = null;
        //7E9585/7E96D0: apply the owner's matrix once, to both room and boundary.
        //Secondary views borrow the same helper's first-update transform.
        var owner=_shipExhaustOwner??this;
        if(owner._influenceStates.TryGetValue((obj.RenderIdentity,obj.RenderObjectGeneration,obj.BaseAsset),out var retained))
            source=source with {WorldTransform=retained.Source.WorldTransform};
        try { sourceRoot=owner.RetainInfluenceParticleSource(obj,sourceRoot); }
        catch(InvalidDataException error){diagnostic=error.Message;return false;}

        int? reference = evaluation.Region == EnbCurrentRetailInfluenceNebulaRegion.Outside ? 0 : evaluation.RoomReferenceIndex;
        if (reference is not int pivot) return true;
        var hierarchy = source.Resource.Hierarchies.Single(h => h.Name.Equals(source.Hlod.HierarchyName, StringComparison.OrdinalIgnoreCase));
        if (!EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(hierarchy, Matrix4x4.Identity,
                source.PendingHierarchyScale??source.ModelScale, out var transforms, out diagnostic)) return false;
        var groups = _influenceRoomRoots.GetOrCreateValue(sourceRoot);
        if (!groups.TryGetValue(pivot, out var group))
        {
            if (!TryBuildInfluenceRoom(sourceRoot, pivot, new(obj.HsvH,obj.HsvS,obj.HsvV), out group, out diagnostic)) return false;
            //7E87C0 clones these members once; composition is not construction.
            group=group with {PhysicalFactoryOwner=sourceRoot.PhysicalFactoryOwner};
            groups.Add(pivot, group);
        }
        //7E964C composes the stored pivot matrix with the spatial owner.
        var world = EnbRetailParticleHierarchyTransform.Compose(transforms[pivot], source.WorldTransform);
        var staged = new RootTaskList();
        if (!TryAppendRootInstanceTreeCore(obj, group, false, world,
                $"influence/room/{pivot}", true, null, staged, out diagnostic,appearance:new(source.ModelScale,obj.HsvH,obj.HsvS,obj.HsvV))) return false;
        staged[0].InheritedRenderObjectScale = source.ModelScale;
        if (group.AttachmentHierarchyName is {} name)
            staged[0].SetHierarchyState(name, world, source.ModelScale, System.Collections.Immutable.ImmutableDictionary<int,Matrix4x4>.Empty);
        destination.AddRange(staged);
        return true;
    }

    private static bool TryBuildInfluenceRoom(LiveCpuRoot source, int pivot, Vector3 factoryHsv, out LiveCpuRoot room, out string? diagnostic)
    {
        diagnostic = null;
        var hlod = source.Hlod!;
        var ordinary = new List<LiveCpuRoot>();
        var permanent = new List<LiveCpuRoot>();
        int ordinal = 0;
        foreach (var member in hlod.Levels[0].Members.Concat(hlod.Aggregate?.Members ?? []))
        {
            int current = ordinal++;
            if (member.ReferenceIndex != (uint)pivot) continue;
            var child = source.HmodelChildren.FirstOrDefault(c => c.Ordinal == current)?.Root;
            if (child is null)
            {
                var meshes = source.Resource.Meshes.Where(m => EnbW3dRenderObjectNameSemantics.MatchesMesh(m, member.Name)).ToArray();
                var external = source.Resource.ExternalRenderObjects.Where(e => e.Name.Equals(member.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var resource = EmptyResource(member.Name).Copy().SetMeshes(meshes).SetExternalRenderObjects(external);
                child = new(source.OverbrightOff, resource, null,
                    new HashSet<string>(meshes.Select(m=>m.Name).Concat(external.Select(e=>e.Name)), StringComparer.OrdinalIgnoreCase),
                    member.Name, member.Name, null, null, []) { IsDirectMeshPrototype=meshes.Length==1 };
            }
            //7E87C0 clones children after48B220/48D710 configured the source
            //HLOD. Moving them into a Collection does not undo that HSV.
            //Collections themselves are skipped by48D710's class dispatch.
            child=child with {InfluenceFactoryHsv=child.Collection is null?factoryHsv:Vector3.Zero};
            (current < hlod.Levels[0].Members.Count ? ordinary : permanent).Add(child);
        }
        string roomName = $"Room{pivot:00}";
        var collection = new EnbCurrentRetailW3dCollectionDefinition(0, roomName, 10,
            ordinary.Select(c=>c.RootRenderObjectName).ToArray(), [], null);
        room = new(source.OverbrightOff, EmptyResource(roomName), null, null, roomName, roomName, null, null, [])
            { Collection=collection, CollectionChildren=ordinary.ToArray() };
        if (permanent.Count == 0)
        {
            //Collection +F0/+F4 unions the cloned MeshModels after factory
            //configuration. Their draw resources keep raw vertices plus an
            //explicit scale, so those raw headers cannot be the culling input.
            if (ordinary.All(c=>c.IsDirectMeshPrototype && c.Resource.Meshes.Count==1 && c.Resource.Meshes[0].CurrentRetailLoadedGeometry is not null))
            {
                var bounds=ordinary.Select(c=>
                {
                    var model=c.Resource.Meshes[0].CurrentRetailLoadedGeometry!;
                    var box=EnbCurrentRetailRenderBoundsStorage.MeshLocalBox(model.Minimum,model.Maximum);
                    return new EnbCurrentRetailCollectionBounds(new(model.Center,model.Radius),box.Center,box.Extent);
                }).ToArray();
                room=room with {InfluenceLocalBounds=EnbCurrentRetailCollectionBounds.Calculate(()=>bounds.Length,
                    i=>bounds[i].Sphere,i=>(bounds[i].BoxCenter,bounds[i].BoxExtent))};
            }
            return true;
        }

        //7E8BE4 promotes a collection through98BF30. Class17 with at most
        //one child remains a collection child; otherwise extraction preserves
        //order (990E50 shifts the remaining entries). Permanent children follow.
        var levelChildren = ordinary.Count > 1 ? ordinary : [room];
        string treeName = roomName + "/pivot";
        var tree = EnbW3dRenderResourceCompiler.CreateDefaultHierarchy(treeName);
        var children = levelChildren.Concat(permanent).ToArray();
        var descriptions = new List<EnbW3dRenderCompositeChild>();
        var retained = new List<LiveHmodelChild>();
        var names = new List<string>();
        for (int i=0; i<children.Length; i++)
        {
            string name = roomName + "/member/" + i;
            if (!TryDescribeCompositeChild(children[i], name, out var description, out diagnostic)) return false;
            descriptions.Add(description); names.Add(name);
            retained.Add(new(i, 0, children[i], i<levelChildren.Count ? 0 : null, i>=levelChildren.Count));
        }
        var promoted = new EnbW3dRenderHlod(roomName, treeName,
            [new(0, float.MaxValue, 0x7f7fffff, names.Take(levelChildren.Count).Select(n=>new EnbW3dRenderHlodMember(0,n)).ToArray())])
            { Aggregate=new(0,names.Skip(levelChildren.Count).Select(n=>new EnbW3dRenderHlodMember(0,n)).ToArray()) };
        room = new(source.OverbrightOff, EmptyResource(roomName).Copy().SetHierarchies([tree]).SetHierarchicalLods([promoted]).SetCompositeRenderChildren(descriptions),
            promoted, null, roomName, roomName, null, treeName, []) { HmodelChildren=retained, AuthoredGlobalOwner=true };
        return true;

        EnbW3dRenderResource EmptyResource(string name) => new(
            source.Resource.Key with { ResourceName=source.Resource.Key.ResourceName+"/influence/"+pivot+"/"+name },
            source.Resource.LogicalFileName, [], [], [], [], []);
    }
}
