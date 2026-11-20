using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    //Concrete nonhierarchical parent v+BC/v+C8 are462C30/462C90 no-ops. An INI
    //mount does not admit their independently constructed controller children.
    private static bool HasNoOpLegoMounts(LiveCpuRoot root)=>root.IsDirectMeshPrototype ||
        (root.Hlod is null && root.AttachmentHierarchyName is null &&
            root.Resource.Meshes.Any(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,root.RootRenderObjectName))) ||
        root.Sphere is not null || root.Ring is not null || root.RetainedEmitter is not null || root.Collection is not null || root.SpecialMaterial is not null ||
        root.Resource.ExternalRenderObjects.Any(e=>e.Kind is EnbW3dExternalRenderObjectKind.NullObject or
            EnbW3dExternalRenderObjectKind.Box or EnbW3dExternalRenderObjectKind.Dazzle &&
            string.Equals(e.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase));

    private sealed class LegoRslOwner(IEnbRenderObject obj, EnbResolvedRslReference reference):LegoPhysicalObject(null)
    {
        public readonly EnbRenderObjectState Object=CreateRenderInvocation(obj);
        private EnbRenderObjectState? secondaryInvocation;
        public EnbRenderObjectState Invocation(bool secondary)=>secondary?secondaryInvocation??=CreateRenderInvocation(Object):Object;
        public uint Time;
        public SceneOwner? ParentScene;
        public EnbCurrentRetailEffectRslObjectRuntime.OwnerBinding? RuntimeBinding;
        public readonly EnbResolvedRslReference Reference = reference;
        public EnbRslObjectState? Update;
        public LiveRootInstance? QueryRoot;
        public string? QueryDiagnostic;
        public readonly Dictionary<int,LegoPhysicalModel> Models=[];
    }

    private readonly Dictionary<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path),LegoRslOwner> _legoRslOwners=[];

    // Cache only structural membership. Rebuild from dictionary enumeration so
    // free-slot reuse after removal preserves the existing traversal order.
    // Model, clock, transform and query values remain live on each wrapper.
    private readonly Dictionary<ulong,KeyValuePair<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path),LegoRslOwner>[]> _legoRslByParent=[];
    private readonly Dictionary<ulong,(ulong Parent,LegoRslOwner Owner)> _legoRslByIdentity=[];
    private bool _legoRslIndexValid;

    private void InvalidateLegoRslIndex()
    {
        _legoRslIndexValid=false;
        _legoRslByParent.Clear();
        _legoRslByIdentity.Clear();
    }

    private void EnsureLegoRslIndex()
    {
        if(_legoRslIndexValid)return;
        var groups=new Dictionary<ulong,List<KeyValuePair<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path),LegoRslOwner>>>();
        foreach(var pair in _legoRslOwners)
        {
            if(!groups.TryGetValue(pair.Key.Identity,out var group))
                groups.Add(pair.Key.Identity,group=[]);
            group.Add(pair);
            _legoRslByIdentity.TryAdd(pair.Value.Object.RenderIdentity,(pair.Key.Identity,pair.Value));
        }
        foreach(var pair in groups)_legoRslByParent.Add(pair.Key,pair.Value.ToArray());
        _legoRslIndexValid=true;
    }

    private KeyValuePair<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path),LegoRslOwner>[] LegoRslOwnersFor(ulong identity)
    {
        EnsureLegoRslIndex();
        return _legoRslByParent.TryGetValue(identity,out var owners)?owners:[];
    }

    private LegoRslOwner? LegoRslOwnerFor(ulong identity)
    {
        EnsureLegoRslIndex();
        return _legoRslByIdentity.TryGetValue(identity,out var entry)?entry.Owner:null;
    }

    private LegoRslOwner ConstructLegoRslOwner(IEnbRenderObject parent,string source,int ordinal,
        ulong revision,int baseAsset,string path,EnbResolvedRslReference reference,EnbRetailAssetBundle assets,
        uint time,IReadOnlyCollection<IEnbRenderObject> objects)
    {
        var key=(parent.RenderIdentity,source,ordinal,revision,baseAsset,path);
        if(_legoRslOwners.TryGetValue(key,out var retained))return retained;
        ulong serial;uint soundIdentity;
        do {serial=(ulong)Interlocked.Increment(ref _nextEffectRslIdentity);soundIdentity=uint.MaxValue-unchecked((uint)serial);}
        while(objects.Any(o=>o.GameId==soundIdentity));
        //729770 constructs6F5358 before72B7F0 resolves the mount.941880
        //initializes the independent wrapper, including an orphan, at identity.
        var spatial=new EnbSceneSpatialSnapshot(EnbSceneSpatialProviderKind.Fixed,
            new(time,Vector3.Zero,Quaternion.Identity,Vector3.Zero,0,Vector3.Zero,Vector3.UnitX)
            {ProviderTransform=Matrix4x4.Identity},null);
        var obj=new EnbSceneObjectSnapshot((1UL<<63)|serial,0,soundIdentity,false,false,0,
            baseAsset,1,0,0,0,0,false,null,parent.Radar,spatial,null,null,
            [],[],[],[],[],[],null,[],null,0,0);
        var owner=new LegoRslOwner(obj,reference){Time=time};
        if(_ownedScene is not null&&_ownedScene.TryFind(parent.RenderIdentity,out var sourceNode))owner.ParentScene=sourceNode.State;
        owner.RuntimeBinding=_effectRslObjectRuntime.BindOwner(obj.RenderIdentity,()=>{if(owner.ParentScene is {} source)source.PhysicalSpecsDirty=true;});
        _legoRslOwners.Add(key,owner);
        InvalidateLegoRslIndex();
        PrepareRslObjectFactories(obj,reference,assets);
        owner.Update=_effectRslObjectRuntime.Advance(obj,reference,[],time,null,
            (animationId,objectId)=>ResolveRetainedRslAnimationClip(obj,reference,assets,animationId,objectId),
            advanceCallbacks:false,ownerBinding:owner.RuntimeBinding);
        if((owner.Update.Graph??reference.ResourceGraph) is {} graph)
            ReconcileRslSoundSelections(obj,owner.Update,graph,assets);
        ConsumeRslBoneActions(obj,reference,assets,owner.Update);
        owner.QueryRoot=ResolveRslSourceQueryRoot(obj,reference,owner.Update,assets,time,out owner.QueryDiagnostic);
        return owner;
    }

    private bool _ownedLegoPlanTransitioning;
    private void RemoveLegoRslOwner((ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path) key)
    {
        if(_legoRslOwners.Remove(key,out var owner)&&owner.RuntimeBinding is {} binding)_retiredRslBindings.Add(binding);
    }
    private void ClearLegoRslOwners()
    {
        foreach(var owner in _legoRslOwners.Values)if(owner.RuntimeBinding is {} binding)_retiredRslBindings.Add(binding);
        _legoRslOwners.Clear();
    }
    private void NotifyOwnedLegoMembership(ulong identity)
    {
        if(_ownedScene is not null&&_ownedScene.TryFind(identity,out var node))
        {node.State.InvalidateSceneModelBindings();node.State.LegoRslChildrenDirty=true;node.State.PhysicalSpecsDirty=true;_dirtyLegoFactoryOwners.Add(node.State);}
    }
    private void PrepareLegoRslOwners(LiveFrame frame,EnbRetailAssetBundle assets,EnbRetailBaseAssetResolver resolver,
        OwnerPublicationMap<EnbRslObjectState> updates,
        OwnerPublicationMap<EnbCurrentRetailRslEffectPassLibrary> passes,EnbRetainedMembershipSet<ulong> live,
        EnbRetainedMembershipSet<(ulong RenderIdentity,ulong VisualGeneration)> generations)
    {
        using var ownerTiming=EnbWorkReuseDiagnostics.Measure("owners.mountedRsl");
        if(_shipExhaustOwner is not null)return;
        if(_ownedSceneFrame&&frame.Snapshot.IsTransitioning&&!_ownedLegoPlanTransitioning)
        {
            ClearLegoRslOwners();InvalidateLegoRslIndex();
            foreach(var node in _ownedScene!.ActiveNodes){node.State.LegoRslChildren=null;node.State.LegoRslChildrenDirty=true;}
        }
        _ownedLegoPlanTransitioning=_ownedSceneFrame&&frame.Snapshot.IsTransitioning;
        var used=_ownedSceneFrame?null:new HashSet<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path)>();
        if(!frame.Snapshot.IsTransitioning)
        foreach(var (parent,source) in SceneOwnerObjects(frame.Snapshot))
        {
            var prepared=source?.LegoRslChildren;
            if(prepared is null||source is not {LegoRslChildrenDirty:false})
            {
            if(source is not null)source.LegoRslChildrenDirty=false;
            var retainedOwners=new List<LegoRslOwner>();
            var parentUsed=new HashSet<(ulong Identity,string Source,int Ordinal,ulong Revision,int Base,string Path)>();
            var physicallyRetained=RetainedPhysicalLegoWrappers(parent.RenderIdentity);
            var children=new List<(string Source,int Ordinal,ulong Revision,int Base,string Name)>();
            foreach(var child in parent.ShipLegos?.Attachments??[])
                if(child.Type is 1 or 2 or 3 && child.BaseAsset>0)
                    children.Add(("aux",child.Ordinal,child.MaterializationRevision,child.BaseAsset,child.BoneName));
            for(int i=0;i<parent.SubParts.Length;i++)
            {
                var child=parent.SubParts[i];
                if(child.PartValue is >0 and <=int.MaxValue)
                    children.Add(("0x00B4",i,child.MaterializationRevision,(int)child.PartValue,child.Name));
            }
            if(RetainedLegoSpecs(parent,all:true) is {} retained)
            {
                children.Clear();
                children.AddRange(retained.Select(s=>(s.Source,s.Ordinal,s.MaterializationRevision,s.BaseAsset,
                    string.Join('/',s.Path)+(s.Path.LastOrDefault()==string.Empty?"/":string.Empty))));
            }
            foreach(var (key,wrapper) in LegoRslOwnersFor(parent.RenderIdentity))
                if(physicallyRetained.Contains(wrapper) &&
                    !children.Any(c=>c.Source==key.Source && c.Ordinal==key.Ordinal && c.Revision==key.Revision && c.Base==key.Base && c.Name==key.Path))
                    children.Add((key.Source,key.Ordinal,key.Revision,key.Base,key.Path));
            foreach(var child in children)
            {
                if(!TryParseShipLegoPath(child.Name,out var path,out _))continue;
                var key=(parent.RenderIdentity,child.Source,child.Ordinal,child.Revision,child.Base,string.Join('/',path));
                if(ResolveBaseAsset(resolver,child.Base).Rsl is not {} reference)continue;
                used?.Add(key);parentUsed.Add(key);
                var owner=ConstructLegoRslOwner(parent,child.Source,child.Ordinal,child.Revision,child.Base,
                    key.Item6,reference,assets,frame.CurrentClientTime,SceneObjects(frame.Snapshot));
                retainedOwners.Add(owner);
            }
            prepared=retainedOwners.ToArray();
            if(source is not null)
            {
                source.LegoRslChildren=prepared;
                source.CallbackIdentities.Clear();source.CallbackIdentities.Add(parent.RenderIdentity);
                foreach(var childOwner in prepared)source.CallbackIdentities.Add(childOwner.Object.RenderIdentity);
                foreach(var pair in LegoRslOwnersFor(parent.RenderIdentity))
                    if(!parentUsed.Contains(pair.Key)){RemoveLegoRslOwner(pair.Key);InvalidateLegoRslIndex();}
            }
            }
            foreach(var owner in prepared)
            {
                owner.Time=frame.CurrentClientTime;
                var current=owner.Object;
                live.Add(current.RenderIdentity);
                generations.Add((current.RenderIdentity,current.RenderObjectGeneration));
                var pass=RetainRslPassOwner(current,owner.Reference,assets,prepareWholeLibrary:true);
                if(pass is not null)passes[current.RenderIdentity]=pass;
                PrepareRslObjectFactories(current,owner.Reference,assets);
                //Construction activates authored states; scene membership, not
                //controller existence, determines whether callbacks can advance.
                owner.Update=_effectRslObjectRuntime.Advance(current,owner.Reference,[],frame.CurrentClientTime,null,
                    (animationId,objectId)=>ResolveRetainedRslAnimationClip(current,owner.Reference,assets,animationId,objectId),
                    advanceCallbacks:false,ownerBinding:owner.RuntimeBinding);
                updates[current.RenderIdentity]=owner.Update;
                ConsumeRslBoneActions(current,owner.Reference,assets,owner.Update);
                SynchronizeMountedLegoScene(owner,assets);
                if((owner.Update.Graph??owner.Reference.ResourceGraph) is {} graph)
                    ReconcileRslSoundSelections(current,owner.Update,graph,assets);
                _rslEffectiveActionsByRenderIdentity[current.RenderIdentity]=owner.Update.Actions;
                _retainedEmitterRslUpdates[(current.RenderIdentity,current.RenderObjectGeneration)]=owner.Update;
                owner.QueryRoot=null;owner.QueryDiagnostic=null;
                owner.QueryRoot=ResolveRslSourceQueryRoot(current,owner.Reference,owner.Update,assets,
                    frame.CurrentClientTime,out owner.QueryDiagnostic);
            }
        }
        if(used is not null)foreach(var key in _legoRslOwners.Keys.Where(k=>!used.Contains(k)).ToArray())
        {
            RemoveLegoRslOwner(key);
            InvalidateLegoRslIndex();
        }
    }

    private LiveRootInstance? ResolveRslSourceQueryRoot(IEnbRenderObject obj,EnbResolvedRslReference reference,
        EnbRslObjectState update,EnbRetailAssetBundle assets,uint time,out string? diagnostic,
        RootTaskList? retainedQueries=null)
    {
        diagnostic=null;
        if(update.CurrentObjectStateId is not {} currentState)return null;
        //4622D0/462320 ->45EDC0 query the current model even when hidden.
        //Resolve its retained animation samples without scene callbacks.
        var queryRoots=new RootTaskList();
        var graph=update.Graph??reference.ResourceGraph;
        AddRslRoots(obj,reference,assets,queryRoots,null,[],time,update,out diagnostic,out _,currentObjectQuery:true);
        var top=queryRoots.Where(r=>r.InstancePath=="root").ToArray();
        if(top.Length==1){retainedQueries?.AddRange(queryRoots);diagnostic=null;return top[0];}
        diagnostic??="RSL source controller has no unique current model";
        return null;
    }
}
