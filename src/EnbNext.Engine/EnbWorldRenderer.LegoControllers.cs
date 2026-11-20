using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class RetainedLegoControllers
    {
        public EnbCurrentRetailAttachedRadiusController? RootRadius;
        public ulong Sequence;
        public SceneOwner? SceneSource;
        public System.Collections.Immutable.ImmutableArray<Net7LegoControllerMutation> ConsumedMutations;
        public bool ApplyingMutations;
        public LiveShipLegoSpec[]? StoredSpecs,SelectedSpecs;
        public int? PacketBoundary;
        public ulong? Generation;
        public readonly EnbRetailLegoControllerTree<LiveShipLegoSpec> Tree=new();
        public readonly Dictionary<EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node,EnbCurrentRetailAttachedRadiusController> Radii=[];
        public string? RadiusDiagnostic;
        public IEnbRenderObject? RadiusObject;
        public EnbRetailAssetBundle? RadiusAssets;
        public EnbRetailBaseAssetResolver? RadiusResolver;
        public uint RadiusTime;
        public bool RadiusRootReplacement;
        public readonly HashSet<EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node> LiveNodes=[];
    }
    private readonly Dictionary<ulong,RetainedLegoControllers> _legoControllers=[];
    private bool _controllersOwnedScene;
    private long _legoSpecPreparations,_legoRadiusGraphBuilds,_legoControllerRetirementSweeps,_radiusGroupingWrites;

    private System.Collections.Immutable.ImmutableDictionary<ulong,float> BuildRetainedCurrentRetailDistanceRadii(
        IReadOnlyCollection<LiveRootInstance> roots,EnbClientBaseAssetMountCatalog? mounts)
    {
        if(_ownedSceneFrame&&mounts is not null&&roots is RootTaskList tasks&&tasks.IsOwnedBy(this))
            return BuildOwnedRadiusPublication(tasks,mounts,_preparingLegoRadiusQueries?RadiusPublicationPhase.Query:RadiusPublicationPhase.Final);
        var radii=System.Collections.Immutable.ImmutableDictionary.CreateBuilder<ulong,float>();
        PublishRetainedCurrentRetailDistanceRadii(roots,mounts,radii);
        return radii.ToImmutable();
    }

    // Snapshot callers retain their publication boundary; synchronous effect
    // consumers update their existing radius storage directly.
    private void PublishRetainedCurrentRetailDistanceRadii(IReadOnlyCollection<LiveRootInstance> roots,
        EnbClientBaseAssetMountCatalog? mounts,IDictionary<ulong,float> radii)
    {
        if(mounts is null)return;
        var owner=_shipExhaustOwner??this;
        VisitCurrentRetailRadiusControllers(roots,mounts,(obj,controllers)=>
        {
            if(TryResolveLegoRadius(controllers,mounts,out float radius))radii[obj.RenderIdentity]=radius;
        },obj=>
        {
            // The native controller radius changes at its notifications. Do
            // not reconstruct its entire query graph just to discard it and
            // read this retained scalar. Root admission still runs below.
            if(!owner._legoControllers.TryGetValue(obj.RenderIdentity,out var retained) ||
                !retained.Radii.TryGetValue(retained.Tree.Root,out var cached))return true;
            radii[obj.RenderIdentity]=cached.Radius;return false;
        });
    }

    private readonly RadiusQueryScratch _radiusQueryScratch=new();
    private sealed class RadiusQueryScratch : IDisposable
    {
        private bool _active;
        public readonly RootTaskList Roots=[];
        public readonly Dictionary<ulong,(LiveRootInstance Root,int Count)> Groups=[];
        public readonly List<IEnbRenderObject> Objects=[];
        public RadiusQueryScratch Rent()
        {if(_active)return new RadiusQueryScratch().Rent();_active=true;return this;}
        public void Dispose(){Roots.Clear();Groups.Clear();Objects.Clear();_active=false;}
    }

    private void VisitCurrentRetailRadiusControllers(IReadOnlyCollection<LiveRootInstance> roots,
        EnbClientBaseAssetMountCatalog mounts,Action<IEnbRenderObject,IReadOnlyDictionary<string,LegoRadiusController>> visit,
        Func<IEnbRenderObject,bool>? needsControllers=null)
    {
        var owner=_shipExhaustOwner??this;
        //Visibility suppresses drawing, not72CC10 controller/model queries.
        using var scratch=_radiusQueryScratch.Rent();
        var queryRoots=scratch.Roots;
        queryRoots.AddRange(roots);queryRoots.AddRange(_hiddenCombatSourceRoots);
        var rootGroups=scratch.Groups;
        foreach(var root in queryRoots)
            if(root.InstancePath=="root" && HasCurrentRetailShipLegoComposition(root.Object))
            {
                _radiusGroupingWrites++;
                ulong id=root.Object.RenderIdentity;
                if(rootGroups.TryGetValue(id,out var group))rootGroups[id]=(group.Root,group.Count+1);
                else rootGroups.Add(id,(root,1));
            }
        var objects=scratch.Objects;
        foreach(var group in rootGroups.Values)
            if(group.Count==1)objects.Add(group.Root.Object);
        foreach(var empty in _emptyRslSourcePoses.Values)
            if(!rootGroups.ContainsKey(empty.Object.RenderIdentity) && HasCurrentRetailShipLegoComposition(empty.Object))
                objects.Add(empty.Object);
        foreach(var obj in objects)
        {
            // A cached scalar still requires the same finite root admission,
            // but does not need a new controller/model descriptor.
            bool hasRoot=rootGroups.TryGetValue(obj.RenderIdentity,out var rootGroup);
            if(hasRoot && !TryGetLegoRadiusWorld(rootGroup.Root,out _))continue;
            if(needsControllers?.Invoke(obj)==false)continue;
            LegoRadiusController rootController;
            if(hasRoot)
            {
                if(!TryCaptureLegoRadiusController(rootGroup.Root,out rootController))continue;
            }
            else
            {
                var empty=_emptyRslSourcePoses[obj.RenderIdentity];
                rootController=new("root",obj.BaseAsset,empty.Pose.World,null,obj.Scale,true);
            }
            _legoRadiusGraphBuilds++;
            var controllers=new Dictionary<string,LegoRadiusController>(StringComparer.Ordinal){{"root",rootController}};
            var selected=RetainedLegoSpecs(obj)?.Select(s=>(obj.RenderIdentity,s.Source,s.Ordinal,s.MaterializationRevision,s.BaseAsset,s.CanonicalPath)).ToHashSet();
            if(selected is null)
            {
                selected=[];
                foreach(var spec in obj.ShipLegos?.Attachments??[])
                    if(TryParseShipLegoPath(spec.BoneName,out var path,out _))
                        selected.Add((obj.RenderIdentity,"aux",spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,string.Join('/',path)));
                for(int i=0;i<obj.SubParts.Length;i++)
                    if(TryParseShipLegoPath(obj.SubParts[i].Name,out var path,out _))
                        selected.Add((obj.RenderIdentity,"0x00B4",i,obj.SubParts[i].MaterializationRevision,unchecked((int)obj.SubParts[i].PartValue),string.Join('/',path)));
            }
            foreach(var drawn in queryRoots.Where(r=>r.Object.RenderIdentity==obj.RenderIdentity && r.IsShipLegoController))
                if(TryCaptureLegoRadiusController(drawn,out var controller))controllers.TryAdd(controller.Path,controller);
            var candidates=new List<LegoRadiusController>();
            foreach(var (key,factory) in owner._legoParticleFactories)
                if(selected.Contains(key) && factory.PhysicalFactoryOwner is {} identity && owner._legoSourceInstances.TryGetValue(identity,out var source) &&
                    TryCaptureLegoRadiusController(source,out var controller))candidates.Add(controller);
            foreach(var (key,rsl) in owner.LegoRslOwnersFor(obj.RenderIdentity))
                if(selected.Contains(key) && rsl.QueryDiagnostic is null && rsl.Update is not null &&
                    (rsl.QueryRoot is not null || rsl.Update.CurrentObjectStateId is null) &&
                    TryBuildObjectWorld(rsl.Object,out var wrapperWorld))
                    candidates.Add(new("lego/"+key.Path,key.Base,wrapperWorld,rsl.QueryRoot,1,
                        rsl.QueryRoot is null || HasNoOpLegoMounts(rsl.QueryRoot.Root),true));
            foreach(var source in candidates.OrderBy(r=>r.Path.Count(c=>c=='/')))
            {
                if(controllers.ContainsKey(source.Path))continue;
                int slash=source.Path.LastIndexOf('/');
                string parentPath=slash==4?"root":source.Path[..slash];
                if(!controllers.TryGetValue(parentPath,out var parent) ||
                    !mounts.TryResolveMount(parent.Base,source.Path[(slash+1)..],out _))continue;
                controllers.Add(source.Path,source);
            }
            visit(obj,controllers);
        }
    }

    private static IEnumerable<LiveShipLegoSpec> StoredLegoSpecs(EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node node)
    {
        if(node.Value is {} value)yield return value;
        foreach(var child in node.Children.Values.Concat(node.Pending))
            foreach(var spec in StoredLegoSpecs(child))yield return spec;
    }

    private IReadOnlyList<LiveShipLegoSpec>? RetainedLegoSpecs(IEnbRenderObject obj,bool all=false)
    {
        if(_shipExhaustOwner is {} primary)return primary.RetainedLegoSpecs(obj,all);
        if(obj.LegoControllerMutations.IsDefaultOrEmpty)return null;
        var retained=_buildingSceneOwner is {} source&&source.Definition.RenderIdentity==obj.RenderIdentity?source.LegoControllers:null;
        if(retained is null&&!_legoControllers.TryGetValue(obj.RenderIdentity,out retained))return null;
        if(!retained.ApplyingMutations)
        {
            if(all && retained.StoredSpecs is {} stored)return stored;
            if(!all && retained.SelectedSpecs is {} selected)return selected;
        }
        _legoSpecPreparations++;
        var specs=StoredLegoSpecs(retained.Tree.Root);
        // Pending duplicates keep separate factory lifetimes. Query consumers
        // select the first queued controller, as72ABB0 does, until replay.
        var result=(all?specs:specs.Where(s=>ReferenceEquals(retained.Tree.Find(s.Path)?.Value,s))).ToArray();
        // Callbacks inside Add/Remove must see that precise intermediate tree.
        // Only stable membership publishes a reusable descriptor collection.
        if(!retained.ApplyingMutations)
        {if(all)retained.StoredSpecs=result;else retained.SelectedSpecs=result;}
        return result;
    }

    private void ObserveLegoControllers(IEnbRenderObject obj,EnbRetailAssetBundle assets,EnbRetailBaseAssetResolver resolver,
        uint time,IReadOnlyCollection<IEnbRenderObject> objects,SceneOwner? owner=null)
    {
        if(obj.LegoControllerMutations.IsDefaultOrEmpty)return;
        var retained=owner?.LegoControllers;
        if(retained is null)
        {
            if(!_legoControllers.TryGetValue(obj.RenderIdentity,out retained))_legoControllers.Add(obj.RenderIdentity,retained=new());
            if(owner is not null){owner.LegoControllers=retained;retained.SceneSource=owner;}
        }
        retained.RadiusObject=obj;retained.RadiusAssets=assets;retained.RadiusResolver=resolver;retained.RadiusTime=time;
        // Pose/time remain current for callbacks. Membership and radius graph
        // construction run only after a source definition notification.
        if(owner is {LegoControllersDirty:false})return;
        EnsureLegoRadius(retained,retained.Tree.Root);
        if(retained.Tree.Attached is null)InitializeLegoControllerCallbacks(retained);
        if(retained.Generation is {} previous && previous!=obj.RenderObjectGeneration)
        {
            retained.PacketBoundary=0;
            retained.RadiusRootReplacement=true;
            try
            {
                foreach(var child in retained.Tree.Root.Children.Values)
                {
                    EnsureLegoRadius(retained,child).Changed=null;
                    ChangeLegoPhysicalMembership(obj,null,child.Value!,assets,0,true);
                    AttachLegoRadiusObserver(retained,retained.Tree.Root,child);
                }
                RefreshRetainedLegoRadius(retained,retained.Tree.Root);
            }
            finally{retained.RadiusRootReplacement=false;retained.PacketBoundary=null;}
        }
        retained.Generation=obj.RenderObjectGeneration;
        if(owner is not null)owner.LegoControllersDirty=false;
        if(retained.ConsumedMutations.Equals(obj.LegoControllerMutations))return;
        bool membershipChanged=false;
        retained.ApplyingMutations=true;
        try
        {
        foreach(var change in obj.LegoControllerMutations)
        {
            if(change.Sequence<=retained.Sequence)continue;
            membershipChanged=true;retained.StoredSpecs=null;retained.SelectedSpecs=null;
            retained.Sequence=change.Sequence;
            retained.PacketBoundary=change.RenderStateActionCount;
            if(change.Kind==Net7LegoControllerMutationKind.Remove){retained.Tree.Remove(change.BoneName);continue;}
            if(change.Kind==Net7LegoControllerMutationKind.Rebuild)retained.Tree.Remove(change.PreviousBoneName??change.BoneName);
            var existing=change.Kind==Net7LegoControllerMutationKind.ReplaceModel?retained.Tree.Find(change.BoneName):null;
            //732AF9 branches to732A3F on a missing lookup: BASE-only then
            //constructs a controller through7294D0 using the stored path/type.
            var spec=new LiveShipLegoSpec(change.Source,change.Ordinal,EnbRetailLegoControllerTree<LiveShipLegoSpec>.ParsePath(change.BoneName),
                existing?.Value?.Type??change.Type,change.BaseAsset,string.Empty){MaterializationRevision=change.MaterializationRevision};
            bool constructed=change.BaseAsset>0 && (existing is not null||change.Type is 1 or 2 or 3);
            if(constructed)
            {
                var factory=ResolveLegoBaseFactory(obj,spec,assets);
                var resolution=factory?.Resolution??ResolveBaseAsset(resolver,spec.BaseAsset);
                if(factory is not null)constructed=factory.Root is not null;
                else if(resolution.Rsl is {} reference)
                {
                    //732880's BASE replacement constructs synchronously, just
                    //as729770 does for creation. Do not collapse an intermediate
                    //RSL factory/initial activation out of a notification batch.
                    ConstructLegoRslOwner(obj,spec.Source,spec.Ordinal,spec.MaterializationRevision,
                        spec.BaseAsset,spec.CanonicalPath,reference,assets,time,objects);
                }
                else if(resolution.W3d is {} source)
                {
                    constructed=TryGetCpuRoot(new(spec.BaseAsset,-1,source.LogicalFileName,source.RequestedObjectName),source,assets,out var root,out _);
                    if(constructed)RetainLegoParticleFactory(obj,spec,root);
                }
                else constructed=false;
            }
            if(existing is not null)
            {
                if(constructed)
                {
                    //72A7C0 rebinds direct descendants to the replacement model.
                    //Its parent notification72CE40 updates the pivot only.
                    foreach(var child in existing.Children.Values)
                    {
                        ChangeLegoPhysicalMembership(obj,existing.Value,child.Value!,assets,retained.PacketBoundary,false);
                        DetachLegoRadiusObserver(retained,existing,child);
                        ChangeLegoPhysicalMembership(obj,spec,child.Value!,assets,retained.PacketBoundary,true);
                    }
                    existing.Value=spec;
                    foreach(var child in existing.Children.Values)AttachLegoRadiusObserver(retained,existing,child);
                    RefreshRetainedLegoRadius(retained,existing);
                    RefreshLegoPhysicalParentPivot(obj,retained.Tree,existing,assets,retained.PacketBoundary);
                }
                else retained.Tree.Remove(change.BoneName);
            }
            else if(constructed)retained.Tree.Add(change.BoneName,spec);
        }
        retained.ConsumedMutations=obj.LegoControllerMutations;
        }
        finally {retained.ApplyingMutations=false;}
        if(!membershipChanged)return;
        var liveNodes=retained.LiveNodes;
        liveNodes.Clear();
        Keep(retained.Tree.Root,liveNodes);
        foreach(var node in retained.Radii.Keys)
            if(!liveNodes.Contains(node))retained.Radii.Remove(node);
        liveNodes.Clear();
        static void Keep(EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node node,
            HashSet<EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node> live)
        {
            live.Add(node);
            foreach(var child in node.Children.Values)Keep(child,live);
            foreach(var child in node.Pending)Keep(child,live);
        }
    }

    private void InitializeLegoControllerCallbacks(RetainedLegoControllers retained)
    {
        retained.Tree.Attached=(parent,child)=>
        {
            ChangeLegoPhysicalMembership(retained.RadiusObject!,parent.Value,child.Value!,retained.RadiusAssets!,retained.PacketBoundary,true);
            AttachLegoRadiusObserver(retained,parent,child);
        };
        retained.Tree.Detached=(parent,child)=>
        {
            ChangeLegoPhysicalMembership(retained.RadiusObject!,parent.Value,child.Value!,retained.RadiusAssets!,retained.PacketBoundary,false);
            DetachLegoRadiusObserver(retained,parent,child);
        };
        retained.Tree.RadiusRefreshRequested=(parent,child)=>
        {
            if(child is null || retained.RadiusAssets!.ClientBaseAssetMounts?.TryResolveMount(parent.Value?.BaseAsset??retained.RadiusObject!.BaseAsset,child.Path[^1],out _)==true)
                RefreshRetainedLegoRadius(retained,parent);
        };
    }

    private void ObserveLegoControllers(IEnbSceneFrame scene,EnbRetailAssetBundle assets,EnbRetailBaseAssetResolver resolver,uint time)
    {
        using var ownerTiming=EnbWorkReuseDiagnostics.Measure("owners.controllers");
        if(_shipExhaustOwner is not null)return;
        // Normal scene removals detach these owners at mutation application.
        // Legacy/secondary modes and their transition still reconcile once.
        if(!_ownedSceneFrame || !_controllersOwnedScene)
        {
            _legoControllerRetirementSweeps++;
            var live=SceneObjects(scene).Select(o=>o.RenderIdentity).ToHashSet();
            foreach(var identity in _legoControllers.Keys.Where(k=>!live.Contains(k)).ToArray())_legoControllers.Remove(identity);
            foreach(var identity in _legoPhysicalOwners.Keys.Where(k=>!live.Contains(k)&&!_effectRslRootOwners.ContainsKey(k)).ToArray())_legoPhysicalOwners.Remove(identity);
        }
        _controllersOwnedScene=_ownedSceneFrame;
        foreach(var (obj,owner) in SceneOwnerObjects(scene))
            ObserveLegoControllers(obj,assets,resolver,time,SceneObjects(scene),owner);
    }
}
