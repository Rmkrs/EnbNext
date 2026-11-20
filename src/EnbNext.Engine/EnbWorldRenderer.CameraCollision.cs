using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class CameraQueryScene(int index)
    {
        public readonly int Index=index;
        public bool Building;
        public ulong? ShipOwner {get;set;}
        public List<EnbCameraCollider> Colliders {get;}=[];
        public EnbRetailTerrain? Terrain {get;set;}
        public EnbW3dAxisAlignedBounds? ShipBounds {get;set;}
        public readonly List<CameraColliderOwner> Effects=[];
    }
    private CameraQueryScene? _cameraCollisionFrame;
    private readonly List<CameraQueryScene> _cameraQueryScenes=[];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbCurrentRetailSpecialMaterialDefinition,EnbCameraCollisionGeometry[]> _cameraSpecialGeometry=new();

    private class CameraColliderOwner
    {
        public readonly List<EnbCameraCollisionMesh> Meshes=[];
        public readonly List<EnbCameraCollisionBox> Boxes=[];
        private readonly List<object> groups=[];
        private readonly List<EnbCameraCollisionGeometrySlot> geometries=[];
        private int meshCount,boxCount,groupCount,boundsCount;
        private readonly List<EnbW3dHlodQueryOwner> boundsOwners=[];
        public EnbW3dHlodQueryOwner Bounds()
        {if(boundsCount==boundsOwners.Count)boundsOwners.Add(new());return boundsOwners[boundsCount++];}
        public readonly EnbCameraCollider Collider;
        public CameraColliderOwner(){Collider=new(0,default,default,Meshes){Boxes=Boxes};}
        public void Begin(){meshCount=boxCount=groupCount=boundsCount=0;}
        public object Group(){if(groupCount==groups.Count)groups.Add(new());return groups[groupCount++];}
        public void Mesh(EnbW3dRenderMesh mesh,float scale,uint mask,Matrix4x4 world)
        {
            while(geometries.Count<=meshCount)geometries.Add(new());
            Mesh(geometries[meshCount].Get(mesh,scale,mask),world);
        }
        public void Mesh(EnbCameraCollisionGeometry geometry,Matrix4x4 world,object? group=null)
        {
            EnbCameraCollisionMesh mesh;
            if(meshCount==Meshes.Count){mesh=new(geometry,world);Meshes.Add(mesh);}else mesh=Meshes[meshCount];
            mesh.Geometry=geometry;mesh.World=world;mesh.FirstHitGroup=group;mesh.TraversalOrder=meshCount+boxCount;meshCount++;
        }
        public void Box(Vector3 center,Vector3 extents,Vector3 x,Vector3 y,Vector3 z)
        {
            EnbCameraCollisionBox box;
            if(boxCount==Boxes.Count){box=new(center,extents,x,y,z);Boxes.Add(box);}else box=Boxes[boxCount];
            box.Center=center;box.Extents=extents;box.X=x;box.Y=y;box.Z=z;box.TraversalOrder=meshCount+boxCount;boxCount++;
        }
        public void Finish()
        {
            if(Meshes.Count>meshCount)Meshes.RemoveRange(meshCount,Meshes.Count-meshCount);
            if(Boxes.Count>boxCount)Boxes.RemoveRange(boxCount,Boxes.Count-boxCount);
            if(boundsOwners.Count>boundsCount)boundsOwners.RemoveRange(boundsCount,boundsOwners.Count-boundsCount);
            if(groups.Count>groupCount)groups.RemoveRange(groupCount,groups.Count-groupCount);
            if(geometries.Count>meshCount)geometries.RemoveRange(meshCount,geometries.Count-meshCount);
        }
    }

    public Func<System.Numerics.Vector3,System.Numerics.Vector3,System.Numerics.Vector3,float>? TakeoffSweep(
        ulong owner, System.Numerics.Quaternion orientation)
    {
        var frame = Volatile.Read(ref _cameraCollisionFrame);
        return frame?.ShipOwner == owner && frame.ShipBounds is {} bounds
            ? (start,end,movement) => EnbShipCameraCollision.SweepTakeoff(bounds,orientation,start,end,movement,owner,frame.Colliders,frame.Terrain)
            : null;
    }

    public EnbResolvedRenderCamera ResolveShipCameraCollision(EnbResolvedRenderCamera camera,
        System.Numerics.Vector3 shipPosition, float radius, ulong shipOwner, bool keepShipInView)
    {
        var frame=Volatile.Read(ref _cameraCollisionFrame);
        return frame?.ShipOwner==shipOwner
            ? EnbShipCameraCollision.Resolve(camera,shipPosition,radius,shipOwner,keepShipInView,frame.Colliders,frame.Terrain)
            : camera;
    }

    private void UpdateCameraQueries(RootTaskList roots, LiveFrame frame)
    {
        using var reuseCollision=EnbWorkReuseDiagnostics.Measure("collision.camera");
        ObserveReuseRoots("collision.camera.input",roots);
        CameraQueryScene? scene=null;
        foreach(var candidate in _cameraQueryScenes)
            if(!ReferenceEquals(candidate,_cameraCollisionFrame)&&!candidate.Building){scene=candidate;break;}
        if(scene is null){scene=new(_cameraQueryScenes.Count);_cameraQueryScenes.Add(scene);}
        scene.Building=true;scene.Colliders.Clear();
        try
        {
            using var children=IndexQueryOccurrences(roots);
            LiveRootInstance? ship=null;
            foreach(var instance in roots)
            {
                if(ship is null&&instance.Object.IsLocalShip)ship=instance;
                if(instance.Parent is not null||instance.Object.IsLocalShip||instance.OrbitalCamera.HasValue)continue;
                if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out _,out _))continue;
                while(instance.CameraQueries.Count<=scene.Index)instance.CameraQueries.Add(new());
                var owner=instance.CameraQueries[scene.Index];
                owner.Update(this,instance,roots,children);
                if(owner.Meshes.Count==0&&owner.Boxes.Count==0)continue;
                if(instance.CurrentRetailLocalBox is not {} bounds)continue;
                owner.Collider.Owner=instance.Object.RenderIdentity;owner.Collider.LocalBounds=bounds;
                owner.Collider.World=instance.CurrentRetailBoundsWorld??world;
                scene.Colliders.Add(owner.Collider);
            }
            AppendEffectCameraColliders(scene);
            scene.Terrain=_useMainViewEnvironment&&!frame.Snapshot.IsTransitioning&&
                frame.Snapshot.Environment is {PresentationMode:1} environment&&_terrain?.Sector==environment.SectorNum?_terrain:null;
            scene.ShipOwner=ship?.Object.RenderIdentity;scene.ShipBounds=ship?.CurrentRetailLocalBox;
            _cameraCollisionFrame=scene;
        }
        finally{scene.Building=false;}
    }

    private sealed class OrdinaryCameraQuery : CameraColliderOwner
    {
        private EnbWorldRenderer view=null!;
        private RootTaskList roots=null!;
        private QueryOccurrenceIndex childrenByParent=null!;
        public void Update(EnbWorldRenderer view,LiveRootInstance root,RootTaskList roots,QueryOccurrenceIndex children)
        {
            this.view=view;this.roots=roots;childrenByParent=children;Begin();
            try{Append(root);}
            finally{Finish();this.view=null!;this.roots=null!;childrenByParent=null!;}
        }
        private void Append(LiveRootInstance instance)
        {
            if(EnbWorkReuseDiagnostics.Active)
            {
                using var inspection=EnbWorkReuseDiagnostics.Inspect();
                EnbWorkReuseDiagnostics.Observe("collision.camera.visited",ReuseRootKey(instance),ReuseRootStructure(instance));
            }
            if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out float scale,out var hsv))return;
            var resource=view.RetainedDrawResource(instance);
            using var queryChildren=view.RetainedCollisionChildren(instance,roots,childrenByParent);
            var children=queryChildren.Children;
            if(view.RetainedDrawRoot(instance).Hlod is {} hlod)
            {
                if(!TryResolveConfiguredAttachmentHierarchy(instance.Root,out var hierarchy,out _)||hierarchy is null)return;
                var animations=instance.AnimationBlend?.ForHierarchy(hierarchy.Name)??Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
                if(!TryResolveRetainedHierarchyPose(instance,hierarchy,world,ResolvePendingHierarchyScale(instance,scale),animations,
                    instance.HierarchyPostTransforms?.Transforms,out var pose,out _))return;
                // 98E7C0/98E860: final LOD then aggregate;940830 tests collision mask, not visibility.
                if(hlod.Levels.Count>0)
                    foreach(var member in hlod.Levels[^1].Members)Member(instance,resource,scale,hsv,queryChildren,pose,member,hlod.Levels[^1].Ordinal,false,hlod.IsConvertedHierarchyModel);
                if(hlod.Aggregate is {} aggregate)
                    foreach(var member in aggregate.Members)Member(instance,resource,scale,hsv,queryChildren,pose,member,null,true,hlod.IsConvertedHierarchyModel);
                for(int i=0;i<children.Count;i++)if(!queryChildren.IsConsumed(i)&&!children[i].IsOrdinaryHlodChild)Append(children[i]);
            }
            else
            {
                Leaf(instance,resource,scale,hsv,instance.Root.RootRenderObjectName,world);
                foreach(var child in children)Append(child);
            }
        }
        private void Member(LiveRootInstance instance,EnbW3dRenderResource resource,float scale,Vector3 hsv,QueryChildrenLease children,
            EnbW3dHierarchyPoseOwner pose,EnbW3dRenderHlodMember member,int? level,bool aggregate,bool converted)
        {
            var child=children.TakeMember(member,level,aggregate,converted);
            if(child is not null){Append(child);return;}
            if(member.ReferenceIndex<pose.WorldTransforms.Length)Leaf(instance,resource,scale,hsv,member.Name,pose.WorldTransforms[(int)member.ReferenceIndex]);
        }
        private void Leaf(LiveRootInstance instance,EnbW3dRenderResource resource,float scale,Vector3 hsv,string name,Matrix4x4 rigid)
        {
            var mesh=EnbW3dRenderObjectNameSemantics.FindFirstMesh(resource.Meshes,name);
            if(mesh is null)
            {
                EnbW3dExternalRenderObject? external=null;
                for(int i=0;i<resource.ExternalRenderObjects.Count;i++)
                    if(resource.ExternalRenderObjects[i].Name.Equals(name,StringComparison.OrdinalIgnoreCase)){external=resource.ExternalRenderObjects[i];break;}
                if(external?.BoxDefinition is {} box)
                {
                    if((box.Attributes&0x80)==0)return;
                    bool oriented=box.RetailClassId==0x1b;
                    Box(oriented?Vector3.Transform(box.Center,rigid):box.Center+rigid.Translation,box.Extents,
                        oriented?new(rigid.M11,rigid.M12,rigid.M13):Vector3.UnitX,
                        oriented?new(rigid.M21,rigid.M22,rigid.M23):Vector3.UnitY,
                        oriented?new(rigid.M31,rigid.M32,rigid.M33):Vector3.UnitZ);
                    return;
                }
                var special=instance.Root.SpecialMaterial?.Definition??external?.SpecialMaterialDefinition;
                if(special is null||(special.CollisionMask&16)==0)return;
                var retained=view._cameraSpecialGeometry.GetValue(special,static definition=>
                {
                    var geometries=new List<EnbCameraCollisionGeometry>();
                    foreach(var member in definition.Members)
                        if(EnbRetailSpecialMaterialGeometry.TryCreate(member,out var geometry,out _))geometries.Add(new(geometry,definition.CollisionMask));
                    return geometries.ToArray();
                });
                var group=Group();foreach(var memberGeometry in retained)Mesh(memberGeometry,rigid,group);return;
            }
            bool loadedOnly=instance.Parent is null&&!instance.IsRsl&&instance.Root.IsDirectMeshPrototype&&
                !EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)&&!EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv);
            uint mask=loadedOnly?EnbRetailDazzleCollision.LoadedMeshMask(mesh):EnbRetailDazzleCollision.ConfiguredMeshMask(mesh);
            if((mask&16)==0)return;
            var geometry=view.RetainQueryGeometry(instance,mesh,scale,mask);
            if((geometry.Mask&16)!=0)Mesh(geometry,rigid);
        }
    }

    // Rendering may omit a hidden Bone or a child behind an invisible pivot.
    // 98E7C0/98E860 still dispatch those retained objects. Build query-only
    // occurrences from their owners; never add them to the submitted draw list.
    private readonly record struct QueryMemberKey(bool Aggregate,int? Level,uint Pivot,string Name);
    private sealed class QueryChildrenStorage
    {
        public readonly RootTaskList Children=[];
        public readonly EnbFrameGroups<QueryMemberKey,int> Members=new(
            EqualityComparer<QueryMemberKey>.Create(
                static (a,b)=>a.Aggregate==b.Aggregate&&a.Level==b.Level&&a.Pivot==b.Pivot&&StringComparer.OrdinalIgnoreCase.Equals(a.Name,b.Name),
                static key=>HashCode.Combine(key.Aggregate,key.Level,key.Pivot,StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name))));
        public bool[] Consumed=[];
        public bool Indexed;
        public void Clear(){Children.Clear();Members.Clear();Array.Clear(Consumed);Indexed=false;}
    }
    [ThreadStatic] private static Stack<QueryChildrenStorage>? _queryChildrenStorage;
    private readonly struct QueryChildrenLease(QueryChildrenStorage storage) : IDisposable
    {
        public RootTaskList Children=>storage.Children;
        public bool IsConsumed(int index)=>storage.Indexed&&storage.Consumed[index];
        public LiveRootInstance? TakeMember(EnbW3dRenderHlodMember member,int? level,bool aggregate,bool converted)
        {
            if(!storage.Indexed)
            {
                if(storage.Consumed.Length<Children.Count)storage.Consumed=new bool[Children.Count];
                for(int i=0;i<Children.Count;i++)
                {
                    var child=Children[i];
                    storage.Members.Add(new(child.IsAggregateHlodChild,converted?null:child.OrdinaryHlodLevelOrdinal,
                        child.OrdinaryHlodPivot,child.Root.RootRenderObjectName),i);
                }
                storage.Indexed=true;
            }
            if(storage.Members.TryGetValues(new(aggregate,converted?null:level,member.ReferenceIndex,member.Name),out var indices))
                foreach(int index in indices)
                    if(!storage.Consumed[index]){storage.Consumed[index]=true;return Children[index];}
            return null;
        }
        public void Dispose(){storage.Clear();(_queryChildrenStorage??=[]).Push(storage);}
    }
    private QueryChildrenLease RetainedCollisionChildren(LiveRootInstance parent,RootTaskList roots,
        QueryOccurrenceIndex? childrenByParent=null)
    {
        var storage=_queryChildrenStorage is {Count:>0}?_queryChildrenStorage.Pop():new QueryChildrenStorage();
        var children=storage.Children;
        if(childrenByParent is null)
        {foreach(var child in roots)if(ReferenceEquals(child.Parent,parent))children.Add(child);}
        else children.AddRange(childrenByParent.Children(parent));
        // A concrete leaf has no missing retained children to reconcile. Keep
        // already materialized children (including dynamic attached effects),
        // but do not resolve membership or hierarchy for this common case.
        var root=parent.Root;
        if(root.HmodelChildren.Count!=0 || root.CollectionChildren.Count!=0 ||
            root.RslBoneNode is not null || root.AggregateAttachments.Count!=0)
            AppendMissingCollisionChildren(parent,children);
        return new(storage);
    }

    private void AppendMissingCollisionChildren(LiveRootInstance parent,RootTaskList children)
    {
        bool HasStorage(int ordinal){foreach(var child in children)if(child.ParentStorageOrdinal==ordinal)return true;return false;}
        bool HasRuntime(EnbCurrentRetailRslBoneRenderObjectRuntime runtime)
        {foreach(var child in children)if(ReferenceEquals(child.RslChild,runtime))return true;return false;}
        bool HasPath(string path){foreach(var child in children)if(child.InstancePath==path)return true;return false;}
        var membership=CompositeMembership(parent);
        EnbW3dHierarchyPoseOwner? parentPose=null;
        bool parentPoseResolved=false;
        LiveRootInstance Child(LiveCpuRoot root,string path,uint pivot,bool ordinary=false,int? level=null,int? slot=null,RootInvocations? prepared=null)
        {
            EnbWorkReuseDiagnostics.Add("collision.queryOnlyWrappersBuilt");
            // Existing materialized children already carry their visibility.
            // Only a missing query-only occurrence consumes this extra pose.
            if(!parentPoseResolved)
            {
                parentPoseResolved=true;
                if(TryResolveConfiguredAttachmentHierarchy(parent.Root,out var parentHierarchy,out _)&&parentHierarchy is not null&&
                    TryResolvePrimitiveFactoryTransform(parent,out var parentWorld,out float parentScale,out _))
                {
                    var animations=parent.AnimationBlend?.ForHierarchy(parentHierarchy.Name)??Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
                    if(TryResolveRetainedHierarchyPose(parent,parentHierarchy,parentWorld,ResolvePendingHierarchyScale(parent,parentScale),
                        animations,parent.HierarchyPostTransforms?.Transforms,out var evaluated,out _))parentPose=evaluated;
                }
            }
            var obj=parent.Object;
            var child=RentRootInvocation(obj,root,parent.IsRsl,path,null,null,ordinary,
                prepared:prepared,preparedOwner:prepared is not null?parent.PreparedOwner:null);
            child.Parent=parent;
            child.Appearance=root.IndependentRslFactory&&root.ConfiguredFactoryInputs is {} inputs
                ?new(inputs.Scale,inputs.Hsv.X,inputs.Hsv.Y,inputs.Hsv.Z):parent.Appearance;
            child.IsAggregateHlodChild=!ordinary&&parent.Root.Collection is null;
            child.OrdinaryHlodPivot=pivot;child.OrdinaryHlodLevelOrdinal=level;child.ParentStorageOrdinal=slot;
            child.PivotVisible=parentPose is {} pose&&pivot<(uint)pose.Visibility.Length?pose.Visibility[(int)pivot]:root.RslBoneNode?.Runtime.AnimationVisible??true;
            return child;
        }
        for(int childIndex=0;childIndex<parent.Root.HmodelChildren.Count;childIndex++)
        {
            var child=parent.Root.HmodelChildren[childIndex];
            if(membership?.Removed.Contains(child.StorageOrdinal)==true||HasStorage(child.StorageOrdinal))continue;
            var prepared=PrepareCompositeInvocation(parent,parent.Root.CollectionChildren.Count+childIndex,false,child.Ordinal,child.Root);
            children.Add(Child(child.Root,prepared?.Path??$"{parent.InstancePath}/hmodel/{child.Ordinal}:{child.Root.RootRenderObjectName}",
                child.Pivot,!child.IsAggregate,child.LevelOrdinal,child.StorageOrdinal,prepared));
        }
        for(int i=0;i<parent.Root.CollectionChildren.Count;i++)
        {
            if(membership?.Removed.Contains(i)==true||HasStorage(i))continue;
            var root=parent.Root.CollectionChildren[i];
            var prepared=PrepareCompositeInvocation(parent,i,true,i,root);
            children.Add(Child(root,prepared?.Path??$"{parent.InstancePath}/collection/{i}:{root.RootRenderObjectName}",0,slot:i,prepared:prepared));
        }
        if(parent.Root.RslBoneNode is {} node)
            foreach(var member in node.Runtime.Aggregate)
            {
                if(member.Child is not EnbCurrentRetailRslBoneRenderObjectRuntime runtime||
                    !node.DynamicChildren.TryGetValue(runtime,out var child)||HasRuntime(runtime))continue;
                var root=child.IndependentExport(child.Exported??child.Root);
                var occurrence=Child(root,$"{parent.InstancePath}/rsl-bone/{child.StateId}",(uint)member.BoneIndex);
                occurrence.RslChild=runtime;children.Add(occurrence);
            }
        if(TryResolveConfiguredAttachmentHierarchy(parent.Root,out var hierarchy,out _)&&hierarchy is not null)
            for(int i=0;i<parent.Root.AggregateAttachments.Count;i++)
            {
                var attachment=parent.Root.AggregateAttachments[i];
                if(attachment.Root is not {} root||attachment.RslChild is {Parent:null})continue;
                var prepared=PrepareAggregateInvocation(parent,i,attachment);
                string path=prepared?.Path??(attachment.RslStateId is {} state?$"{parent.InstancePath}/rsl-bone/{state}":$"{parent.InstancePath}/{i}:{attachment.SubobjectName}");
                if(HasPath(path))continue;
                int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,attachment.BoneName);
                if(pivot<0||pivot>=hierarchy.Pivots.Count)continue;
                var occurrence=Child(root,path,(uint)pivot,prepared:prepared);occurrence.RslChild=attachment.RslChild;children.Add(occurrence);
            }
    }
}
