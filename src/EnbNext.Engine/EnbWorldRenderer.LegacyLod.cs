using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private bool TryConvertRegisteredLegacyLodOwner(EnbW3dRenderResource resource,
        EnbCurrentRetailW3dLegacyLodDefinition definition,EnbRetailAssetBundle assets,
        LiveCpuRootKey key,HashSet<string> stack,int depth,out EnbW3dRenderResource converted,
        out IReadOnlyList<LiveHmodelChild> retainedChildren,out string? diagnostic)
    {
        converted=resource;retainedChildren=[];diagnostic=null;
        string cycle=$"0400:{key.PrototypeRegistration}:{definition.Name}";
        if(!stack.Add(cycle)){diagnostic=$"0400 '{definition.Name}' forms a recursive prototype cycle.";return false;}
        try
        {
            if(definition.Children.Count==0)
                throw new InvalidDataException("0400's zero-child conversion indexes before its supplied root array.");
            var roots=new List<LegacyLodNode>();
            foreach(var child in definition.Children)
            {
                LiveCpuRoot root;
                //9A4EA0 acquires through manager+14 before9A4990 transfers
                //the concrete children. Generated names share that registry.
                if(TryAcquireConfiguredChild(key.BaseAsset,key.RslStateId,child.Name,assets,out root))
                {
                    roots.Add(SnapshotLegacyLodRoot(root));
                    continue;
                }
                if(string.Equals(child.Name,"NULL",StringComparison.OrdinalIgnoreCase))
                {
                    var empty=resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetExternalRenderObjects([new("NULL",EnbW3dExternalRenderObjectKind.NullObject,resource.LogicalFileName,resource.Key.ArchivePath)]);
                    root=new(false,empty,null,new(StringComparer.OrdinalIgnoreCase){"NULL"},"NULL","NULL",null,null,[]);
                }
                else
                {
                    if(!_prototypeResources.TryFindPrototypeSource(child.Name,out var source))
                    {
                        if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.MixResources,
                            child.Name,out var loaded,out diagnostic))
                            throw new InvalidDataException($"0400 '{definition.Name}' could not create child '{child.Name}': {diagnostic}");
                        source=loaded;
                        if(loaded.CurrentRetailAggregate is null &&
                            (!_prototypeResources.TryLoadFile(loaded,out _,out diagnostic,ReadMapperClock()) ||
                             !_prototypeResources.TryFindPrototypeSource(child.Name,out source)))
                            throw new InvalidDataException($"0400 '{definition.Name}' could not create child '{child.Name}': {diagnostic}");
                    }
                    var childKey=new LiveCpuRootKey(key.BaseAsset,key.RslStateId,source.LogicalFileName,child.Name,source.PrototypeRegistration);
                    if(!TryGetCpuRootCore(childKey,source,assets,stack,depth+1,out root,out diagnostic))return false;
                }
                roots.Add(SnapshotLegacyLodRoot(root));
            }
            //009A4990 reverses the temporary owner's retained roots.0098BF30
            //takes its hierarchy from the last supplied root (the first authored).
            var first=roots[0].Root;
            var hierarchy=first.Resource.Hierarchies.FirstOrDefault(value=>
                string.Equals(value.Name,first.AttachmentHierarchyName,StringComparison.OrdinalIgnoreCase))
                ??EnbW3dRenderResourceCompiler.CreateDefaultHierarchy($"<0400:{definition.Name}>");
            roots.Reverse();
            var provider=new LegacyLodTransferProvider(hierarchy);
            var levels=EnbCurrentRetailHlodLevelTransferRuntime.Transfer(roots,provider);
            foreach(var root in roots)provider.Release(root);
            var meshes=new List<EnbW3dRenderMesh>();var external=new List<EnbW3dExternalRenderObject>();
            var composites=new List<EnbW3dRenderCompositeChild>();var children=new List<LiveHmodelChild>();
            var outputLevels=new List<EnbW3dRenderHlodLevel>();int ordinal=0;
            for(int level=0;level<levels.Count;level++)
            {
                var members=new List<EnbW3dRenderHlodMember>();
                foreach(var entry in levels[level])
                {
                    var child=entry.Child.Root;string name=child.RootRenderObjectName;
                    members.Add(new((uint)entry.PivotIndex,name));
                    if(child.NamedChildBoundsRoot is not null || child.Hlod is not null || child.Collection is not null || child.Sphere is not null || child.Ring is not null || child.RetainedEmitter is not null || child.SpecialMaterial is not null)
                    {
                        if(!TryDescribeCompositeChild(child,name,out var composite,out diagnostic))return false;
                        if(!composites.Any(value=>string.Equals(value.Name,name,StringComparison.OrdinalIgnoreCase)))composites.Add(composite);
                        children.Add(new(ordinal,(uint)entry.PivotIndex,child,level));
                    }
                    else
                    {
                        foreach(var mesh in child.Resource.Meshes.Where(value=>child.AllowedRenderObjectNames?.Contains(value.Name)==true ||
                            EnbW3dRenderObjectNameSemantics.MatchesMesh(value,name)))
                            if(!meshes.Any(value=>ReferenceEquals(value,mesh)))meshes.Add(mesh);
                        foreach(var value in child.Resource.ExternalRenderObjects.Where(value=>string.Equals(value.Name,name,StringComparison.OrdinalIgnoreCase)))
                            if(!external.Any(existing=>ReferenceEquals(existing,value)))external.Add(value);
                    }
                    ordinal++;
                }
                outputLevels.Add(new(level,float.MaxValue,0x7f7fffff,members.ToArray()));
            }
            var hlod=new EnbW3dRenderHlod(definition.Name,hierarchy.Name,outputLevels.ToArray()){IsConvertedLegacyLod=true};
            converted=resource.Copy().SetMeshes(meshes.ToArray()).SetExternalRenderObjects(external.ToArray()).SetHierarchies([hierarchy]).SetHierarchyModels([]).SetHierarchicalLods([hlod]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren(composites.ToArray());
            retainedChildren=children.ToArray();return true;
        }
        catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException)
        {diagnostic=ex.Message;return false;}
        finally{stack.Remove(cycle);}
    }

    private static LegacyLodNode SnapshotLegacyLodRoot(LiveCpuRoot root)
    {
        if(!TryDescribeCompositeChild(root,root.RootRenderObjectName,out var description,out var diagnostic))
            throw new InvalidDataException(diagnostic);
        var node=new LegacyLodNode(root,description.RetailClassId);
        if(root.Collection is not null)
            foreach(var child in root.CollectionChildren)node.Children.Add((new(child,0),0));
        else if(root.Hlod is {} hlod)
        {
            int ordinal=0;
            foreach(var member in hlod.Levels.SelectMany(level=>level.Members).Concat(hlod.Aggregate?.Members??[]))
            {
                var retained=root.HmodelChildren.FirstOrDefault(child=>child.StorageOrdinal==ordinal);
                var child=retained?.Root;
                if(child is null)
                {
                    var selected=root.Resource.Copy().SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren([]);
                    child=new(root.OverbrightOff,selected,null,new(StringComparer.OrdinalIgnoreCase){member.Name},member.Name,member.Name,null,null,[]);
                }
                node.Children.Add((new(child,0),member.ReferenceIndex));ordinal++;
            }
            //009A3A50 attaches0600 children through the base object's +80
            //virtual. They are ordinary HLOD aggregate children when0098BF30
            //enumerates and transfers the owner's complete retained storage.
            foreach(var attachment in root.AggregateAttachments)
            {
                var hierarchy=root.Resource.Hierarchies.First(value=>
                    string.Equals(value.Name,root.AttachmentHierarchyName,StringComparison.OrdinalIgnoreCase));
                int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,attachment.BoneName);
                bool inlineEmitter=attachment.Exhaust is null && root.Resource.ExternalRenderObjects.Any(value=>
                    value.Kind==EnbW3dExternalRenderObjectKind.ParticleEmitter &&
                    string.Equals(value.Name,attachment.SubobjectName,StringComparison.OrdinalIgnoreCase)) &&
                    hlod.Aggregate?.Members.Any(member=>member.ReferenceIndex==(uint)pivot &&
                        string.Equals(member.Name,attachment.SubobjectName,StringComparison.OrdinalIgnoreCase))==true;
                if(inlineEmitter)continue; // metadata mirror of the existing lane
                var child=attachment.Root;
                if(child is null && attachment.Sphere is {} sphere)
                    child=CreateRetainedSphereRoot(root.Resource,attachment.SubobjectName,sphere);
                if(child is null && attachment.Exhaust is {} emitter)
                {
                    var empty=root.Resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren([]).SetExternalRenderObjects([]);
                    child=new(false,empty,null,new(StringComparer.OrdinalIgnoreCase),attachment.SubobjectName,attachment.SubobjectName,null,null,[]){RetainedEmitter=emitter};
                }
                if(child is null && string.Equals(attachment.SubobjectName,"NULL",StringComparison.OrdinalIgnoreCase))
                {
                    var empty=root.Resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren([]).SetExternalRenderObjects([new("NULL",EnbW3dExternalRenderObjectKind.NullObject,root.Resource.LogicalFileName,root.Resource.Key.ArchivePath)]);
                    child=new(false,empty,null,new(StringComparer.OrdinalIgnoreCase){"NULL"},"NULL","NULL",null,null,[]);
                }
                if(child is null)
                    throw new InvalidDataException($"0400 transfer of aggregate child '{attachment.SubobjectName}' requires its concrete retained render owner.");
                node.Children.Add((new(child,0),checked((uint)pivot)));
            }
        }
        return node;
    }

    // The immutable CPU roots stay shared; these constructor-local references
    // represent ownership transfer without mutating cached source prototypes.
    private sealed class LegacyLodNode(LiveCpuRoot root,int classId)
    {
        public readonly LiveCpuRoot Root=root;
        public readonly int ClassId=classId;
        public readonly List<(LegacyLodNode Child,uint Pivot)> Children=[];
        public int References=1;
    }
    private sealed class LegacyLodTransferProvider(EnbW3dRenderHierarchy hierarchy)
        :IEnbCurrentRetailHlodLevelTransferProvider<LegacyLodNode>
    {
        public int ClassId(LegacyLodNode child)=>child.ClassId;
        public int ChildCount(LegacyLodNode child)=>child.Children.Count;
        public LegacyLodNode RetainedChild(LegacyLodNode child,int index)
        {var result=child.Children[index].Child;Retain(result);return result;}
        public int ChildPivot(LegacyLodNode parent,LegacyLodNode child)
            =>checked((int)parent.Children.First(value=>ReferenceEquals(value.Child,child)).Pivot);
        public void RemoveChild(LegacyLodNode parent,LegacyLodNode child)
        {int index=parent.Children.FindIndex(value=>ReferenceEquals(value.Child,child));parent.Children.RemoveAt(index);Release(child);}
        public void Retain(LegacyLodNode child)=>child.References++;
        public void Release(LegacyLodNode child)
        {
            if(--child.References!=0)return;
            foreach(var entry in child.Children)Release(entry.Child);child.Children.Clear();
        }
        public void SetParentToHlod(LegacyLodNode child) { } // Published members carry their new parent through LiveCpuRoot.
        public void SetHierarchyPivotMatrix(LegacyLodNode child,int pivot)
        {
            if((uint)pivot>=(uint)hierarchy.Pivots.Count)
                throw new InvalidDataException($"0400 child '{child.Root.RootRenderObjectName}' references absent retained pivot {pivot}.");
            // The draw tree evaluates this exact retained pivot for the child.
        }
        public bool HasScene()=>false; // Construction precedes scene attachment.
        public int CurrentLevel()=>0;
        public void AttachToCurrentScene(LegacyLodNode child)=>throw new InvalidOperationException("An unplaced constructor has no scene.");
    }
}
