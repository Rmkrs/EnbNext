using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class RigidOwner
    {
        public readonly Dictionary<string, RootInvocations> Roots = new(StringComparer.Ordinal);
        public readonly RootInvocations PrimaryRoot=new("root");
        public readonly Dictionary<string, EnbW3dRetainedHierarchyPose> ChildPoses = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EnbW3dMeshOccurrencePreparation> MeshDefinitions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EnbW3dRigidOccurrence> Occurrences = new(StringComparer.Ordinal);
        public readonly Dictionary<string,Dictionary<int,EnbCameraCollisionGeometrySlot>> QueryGeometry = new(StringComparer.Ordinal);
        public readonly Dictionary<LiveHlodKey,HlodLevelState> HlodLevels=[];
        public bool SceneOwned;
        public SceneOwner? SceneSource;
        public long Seen;
        public LiveObjectPresentationAudit? PresentationAudit;
        public LiveRootInstance? SimpleRoot;
        public long SimpleRootFrame = -1;
    }
    private sealed class RootInvocations(string? path=null,string? attachmentPath=null)
    {
        public readonly string? Path=path;
        public readonly string? AttachmentPath=attachmentPath;
        public long Frame=-1;
        public int Used;
        public readonly RootTaskList Slots=[];
    }
    private long _preparedTraversalBuilds, _preparedTraversalUses, _skinFrameBuilds;
    private long _ownedHlodLevelBindings;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,Dictionary<LiveHlodKey,HlodLevelState>> _physicalHlodLevels=new();
    private sealed class HlodLevelState { public int? Level; }
    private HlodLevelState? OwnedHlodLevel(LiveRootInstance instance,EnbW3dRenderHlod hlod)
    {
        if(!_retainedOrdinaryMeshes || (instance.IsRsl&&instance.PreparedOwner?.SceneSource is null))return null;
        if(PhysicalParticleFactory(instance) is {} factory)
        {
            // FILE/Lego construction owns the concrete model independently of
            // visual generations. A weak factory component preserves that
            // level across appearance replacement and releases with the model.
            if(instance.PreparedHlodLevel is {} bound && ReferenceEquals(instance.PreparedHlodFactory,factory))return bound;
            _ownedHlodLevelBindings++;
            var levels=_physicalHlodLevels.GetValue(factory,static _=>new());
            var physicalKey=RetainedHlodKey(instance,hlod.Name);
            if(!levels.TryGetValue(physicalKey,out var physicalState))levels.Add(physicalKey,physicalState=new());
            instance.PreparedHlodFactory=factory;
            return instance.PreparedHlodLevel=physicalState;
        }
        var owner=instance.PreparedOwner;
        // Query-only invocations may be constructed outside the scene walk.
        // Bind them to the same ordinary owner once, rather than consulting
        // the compatibility level table after ordinary state moved off it.
        if(owner is null && _ownedSceneFrame && TryResolveSceneRigidOwner(instance.Object,out var sceneOwner))
            instance.PreparedOwner=owner=sceneOwner;
        if(owner is not {SceneOwned:true})return null;
        if(instance.PreparedHlodLevel is {} retained && instance.PreparedHlodFactory is null)return retained;
        _ownedHlodLevelBindings++;
        var key=RetainedHlodKey(instance,hlod.Name);
        var ownedLevels=instance.IsRsl?owner.SceneSource!.RslHlodLevels:owner.HlodLevels;
        if(!ownedLevels.TryGetValue(key,out var state))ownedLevels.Add(key,state=new());
        instance.PreparedHlodFactory=null;
        return instance.PreparedHlodLevel=state;
    }
    private int RememberedHlodLevel(LiveRootInstance instance,EnbW3dRenderHlod hlod)
    {
        if(OwnedHlodLevel(instance,hlod) is {} state)return state.Level??EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod);
        return _hlodLevels.GetValueOrDefault(RetainedHlodKey(instance,hlod.Name),EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod));
    }
    private void RememberHlodLevel(LiveRootInstance instance,EnbW3dRenderHlod hlod,int level)
    {
        if(OwnedHlodLevel(instance,hlod) is {} state)state.Level=level;
        else _hlodLevels[RetainedHlodKey(instance,hlod.Name)]=level;
    }
    private long _sceneRigidExternalLookups;
    private readonly bool _traceSceneLookups=Environment.GetEnvironmentVariable("ENB_TRACE_SCENE_LOOKUPS")=="1";
    private readonly Dictionary<string,long> _sceneRigidLookupSites=[];
    private RigidOwner ResolveRigidOwner(EnbNext.Presentation.IEnbRenderObject obj,bool allowScene=true,[System.Runtime.CompilerServices.CallerMemberName] string caller="")
    {
        RigidOwner owner;
        if(allowScene && _buildingSceneOwner is {} current &&
            current.Definition.RenderIdentity==obj.RenderIdentity && current.Definition.VisualGeneration==obj.VisualGeneration)
            owner=current.Rigid;
        else if(allowScene && TryResolveSceneRigidOwner(obj,out var sceneRigid,caller))owner=sceneRigid;
        else
        {
            var key=(obj.RenderIdentity,obj.VisualGeneration);
            if(!_rigidOccurrences.TryGetValue(key,out owner!))_rigidOccurrences.Add(key,owner=new());
        }
        TouchRigidOwner(owner);return owner;
    }
    private bool TryResolveSceneRigidOwner(EnbNext.Presentation.IEnbRenderObject obj,out RigidOwner owner,[System.Runtime.CompilerServices.CallerMemberName] string caller="")
    {
        owner=null!;
        if(!_ownedSceneFrame)return false;
        _sceneRigidExternalLookups++;
        bool found=_ownedScene!.TryFind(obj.RenderIdentity,out var node)&&node.Definition.VisualGeneration==obj.VisualGeneration;
        if(_traceSceneLookups){string site=caller+(found?":ordinary":":unowned");_sceneRigidLookupSites[site]=_sceneRigidLookupSites.GetValueOrDefault(site)+1;}
        if(!found)return false;
        owner=node.State.Rigid;return true;
    }
    private void TouchRigidOwner(RigidOwner owner)
    {
        if(owner.SceneOwned || owner.Seen==_rigidFrame)return;
        owner.Seen=_rigidFrame;
    }
    private LiveRootInstance RentRootInvocation(EnbNext.Presentation.IEnbRenderObject obj,
        LiveCpuRoot root,bool isRsl,string path,Matrix4x4? world,LiveRslAnimationBlendBinding? animation,
        bool ordinaryChild,bool legoController=false,int? legoBaseAsset=null,RootInvocations? prepared=null,RigidOwner? preparedOwner=null,[System.Runtime.CompilerServices.CallerMemberName] string invocationCaller="")
    {
        if(!_retainedOrdinaryMeshes)return new(obj,root,isRsl,path,world,animation){IsOrdinaryHlodChild=ordinaryChild,IsShipLegoController=legoController,LegoControllerBaseAsset=legoBaseAsset};
        var owner=preparedOwner??(_buildingSceneOwner is {} source&&source.Definition.RenderIdentity==obj.RenderIdentity
            &&source.Definition.VisualGeneration==obj.VisualGeneration?source.Rigid:ResolveRigidOwner(obj,!isRsl,invocationCaller));
        var invocations=prepared;
        if(invocations is null&&owner.SceneOwned&&path=="root")invocations=owner.PrimaryRoot;
        if(invocations is null && !owner.Roots.TryGetValue(path,out invocations))owner.Roots.Add(path,invocations=new());
        if(invocations.Frame!=_rigidFrame){invocations.Frame=_rigidFrame;invocations.Used=0;}
        int slot=invocations.Used++;
        LiveRootInstance instance;
        if(slot==invocations.Slots.Count)
        {
            instance=new(obj,root,isRsl,path,world,animation){IsOrdinaryHlodChild=ordinaryChild,IsShipLegoController=legoController,LegoControllerBaseAsset=legoBaseAsset};
            invocations.Slots.Add(instance);
        }
        else
        {
            instance=invocations.Slots[slot];
            if(!ReferenceEquals(instance.Root,root)||instance.IsRsl!=isRsl||instance.IsOrdinaryHlodChild!=ordinaryChild||instance.IsShipLegoController!=legoController||instance.LegoControllerBaseAsset!=legoBaseAsset)
                invocations.Slots[slot]=instance=new(obj,root,isRsl,path,world,animation){IsOrdinaryHlodChild=ordinaryChild,IsShipLegoController=legoController,LegoControllerBaseAsset=legoBaseAsset};
            else {instance.RefreshSimple(obj,animation);instance.WorldTransformOverride=world;}
        }
        instance.InvocationStorage=invocations;instance.InvocationSlot=slot;
        instance.PreparedOwner=owner;
        instance.RetainedChildPoses=owner.ChildPoses;
        return instance;
    }
    private readonly Dictionary<(ulong Identity, ulong Generation), RigidOwner> _rigidOccurrences = [];
    private readonly HashSet<(ulong Identity, ulong Generation)> _rigidResidentOwners = [];
    private readonly List<(ulong Identity, ulong Generation)> _rigidRetire = [];
    private long _rigidFrame;
    private void BeginRigidOccurrences(LiveFrame frame)
    {
        _rigidFrame++;
        _rigidResidentOwners.Clear();
        if(!_ownedSceneFrame)
            foreach(var owner in SnapshotObjects(frame.Snapshot))_rigidResidentOwners.Add((owner.RenderIdentity,owner.VisualGeneration));
    }
    private void EndRigidOccurrences()
    {
        _rigidRetire.Clear();
        foreach (var owner in _rigidOccurrences)
        {
            if(owner.Value.Seen!=_rigidFrame && !(_ownedSceneFrame
                ?_ownedScene!.TryFind(owner.Key.Identity,out var resident)&&resident.Definition.VisualGeneration==owner.Key.Generation
                :_rigidResidentOwners.Contains(owner.Key)))_rigidRetire.Add(owner.Key);
            // A callback or extra view may need several mutable invocations of
            // one retained occurrence. Retire unused slots/removed child paths
            // after all consumers have finished, never while they are queued.
            if(owner.Value.Seen==_rigidFrame)CompleteRootInvocations(owner.Value);
        }
        foreach (var owner in _rigidRetire) _rigidOccurrences.Remove(owner);
        _rigidRetire.Clear();
        // Ordinary slots belong to their scene generation, including invisible
        // children. Explicit generation replacement/removal releases them.
    }
    private void CompleteRootInvocations(RigidOwner owner)
    {
        // These are temporary invocation slots, not model/scene ownership.
        // Only owners used by this frame participate in the drain.
        foreach(var pair in owner.Roots)
            if(pair.Value.Frame==_rigidFrame && pair.Value.Slots.Count>pair.Value.Used)
                pair.Value.Slots.RemoveRange(pair.Value.Used,pair.Value.Slots.Count-pair.Value.Used);
        foreach(var path in owner.Roots.Where(p=>p.Value.Frame!=_rigidFrame).Select(p=>p.Key).ToArray())owner.Roots.Remove(path);
    }
    private bool TrySnapshotRigidOccurrence(LiveRootInstance instance,
        EnbNext.Presentation.IEnbRenderObject obj, EnbW3dRenderResource resource,
        Matrix4x4 world, long time, EnbW3dLightEnvironment lights,
        out EnbW3dDrawInvocation draw, out string? diagnostic,float renderScale)
    {
        draw = null!; diagnostic = null;
        var owner=instance.PreparedOwner??=ResolveRigidOwner(obj,!instance.IsRsl);TouchRigidOwner(owner);
        var occurrence=instance.PreparedOccurrence;
        if(occurrence is null || !ReferenceEquals(occurrence.Resource,resource))
        {
            if(!owner.Occurrences.TryGetValue(instance.InstancePath,out occurrence) || !ReferenceEquals(occurrence.Resource,resource))
        {
            if (!EnbW3dRigidOccurrence.TryCreate(obj, resource, instance.InstancePath, out occurrence, out diagnostic)) return false;
            owner.Occurrences[instance.InstancePath] = occurrence;
            }
            instance.PreparedOccurrence=occurrence;
        }
        bool ready=occurrence.TrySnapshot(world, renderScale, time, lights, out draw, out diagnostic);
        instance.CurrentAppearancePreparation=ready?occurrence:null;
        return ready;
    }
    private LiveObjectPresentationAudit RentPresentationAudit(LiveRootInstance instance,bool details)
    {
        var obj=instance.Object;
        if(!_retainedOrdinaryMeshes)return new(obj,details);
        var owner=instance.PreparedOwner??=ResolveRigidOwner(obj,!instance.IsRsl);TouchRigidOwner(owner);
        if(owner.PresentationAudit is null)return owner.PresentationAudit=new(obj,details);
        owner.PresentationAudit.Reset(obj,details);
        return owner.PresentationAudit;
    }
    private Dictionary<string,EnbW3dRetainedHierarchyPose>? RetainChildPoseOwner(EnbNext.Presentation.IEnbRenderObject obj,bool allowScene=true)
    {
        if(!_retainedOrdinaryMeshes)return null;
        var owner=ResolveRigidOwner(obj,allowScene);
        return owner.ChildPoses;
    }
    private EnbW3dMeshOccurrencePreparation RetainMeshDefinitions(LiveRootInstance instance)
    {
        var owner=instance.PreparedOwner??=ResolveRigidOwner(instance.Object,!instance.IsRsl);TouchRigidOwner(owner);
        var prepared=instance.PreparedDefinitions;
        if(prepared is null)
        {
            if(!owner.MeshDefinitions.TryGetValue(instance.InstancePath,out prepared))
                owner.MeshDefinitions.Add(instance.InstancePath,prepared=new(instance.InstancePath));
            instance.PreparedDefinitions=prepared;
        }
        prepared.RigidHierarchyPose=instance.ChildPose??owner.ChildPoses.GetValueOrDefault(instance.InstancePath);
        return prepared;
    }

    private EnbCameraCollisionGeometry RetainQueryGeometry(LiveRootInstance instance, EnbW3dRenderMesh mesh, float scale, uint mask)
    {
        if (!_retainedOrdinaryMeshes) return new(mesh, scale, mask);
        var owner=instance.PreparedOwner??=ResolveRigidOwner(instance.Object,!instance.IsRsl);TouchRigidOwner(owner);
        var queries=instance.PreparedQueries;
        if(queries is null)
        {
            if(!owner.QueryGeometry.TryGetValue(instance.InstancePath,out queries))owner.QueryGeometry.Add(instance.InstancePath,queries=new());
            instance.PreparedQueries=queries;
        }
        if(!queries.TryGetValue(mesh.Ordinal,out var slot))queries.Add(mesh.Ordinal,slot=new());
        return slot.Get(mesh, scale, mask);
    }

    private LiveRootInstance RentSimpleRoot(EnbNext.Presentation.IEnbRenderObject obj,
        LiveCpuRoot root, LiveRslAnimationBlendBinding? animation)
    {
        var owner=ResolveRigidOwner(obj);
        // A second invocation in the same frame needs its own mutable scene
        // state. Never overwrite an occurrence already queued by another view.
        if(owner.SimpleRootFrame==_rigidFrame)return new(obj,root,false,animationBlend:animation){PreparedOwner=owner};
        owner.SimpleRootFrame = _rigidFrame;
        if (owner.SimpleRoot is not {} retained || !ReferenceEquals(retained.Root, root))
            owner.SimpleRoot = retained = new(obj, root, false, animationBlend: animation);
        else retained.RefreshSimple(obj, animation);
        retained.PreparedOwner=owner;
        return retained;
    }

    // 0098F1D0 changes local aggregate bounds only with membership/geometry.
    // RetainedCompositeMembership.Rebuild publishes a new QueryRoot; old queued
    // queries keep their own root, and weak ownership releases retired versions.
    private sealed class QueryBoundsState
    {
        public EnbW3dRenderResource? Resource;
        public EnbW3dRenderHlod? Hlod;
        public EnbW3dHlodBoundsOwner? Local;
        public LivePostTransformHlodBounds? Post;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LiveCpuRoot,QueryBoundsState> _queryBounds=new();

    private long _ownedHlodBoundsPreparations;
    private sealed class OwnedHlodBounds(LiveCpuRoot root,EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,QueryBoundsState state)
    {
        public readonly LiveCpuRoot Root=root;
        public readonly EnbW3dRenderResource Resource=resource;
        public readonly EnbW3dRenderHlod Hlod=hlod;
        public readonly QueryBoundsState State=state;
        public readonly EnbW3dHlodBoundsOwner Bounds=new();
        public readonly bool HasBoundingBoxOverride=EnbW3dHlodCompositeBoundsCompiler.HasBoundingBoxOverride(resource,hlod);
    }
    private OwnedHlodBounds PrepareOwnedHlodBounds(LiveRootInstance instance,LiveCpuRoot root,
        EnbW3dRenderResource resource,EnbW3dRenderHlod hlod)
    {
        if(instance.PreparedHlodBounds is {} prepared && ReferenceEquals(prepared.Root,root) &&
            ReferenceEquals(prepared.Resource,resource) && ReferenceEquals(prepared.Hlod,hlod))return prepared;
        _ownedHlodBoundsPreparations++;
        return instance.PreparedHlodBounds=new(root,resource,hlod,_queryBounds.GetValue(root,static _=>new()));
    }

    private bool TryResolveRetainedQueryBounds(QueryBoundsState state,EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,Matrix4x4 world,EnbW3dHierarchyState? post,
        out EnbW3dHlodBoundsOwner bounds,out string? diagnostic, EnbW3dHlodBoundsOwner destination)
    {
        bounds=default!;diagnostic=null;
        if(!IsFinite(world))
        {diagnostic=$"W3D HLOD '{hlod.Name}' received a non-finite scene transform.";return false;}
        if(!ReferenceEquals(state.Resource,resource)||!ReferenceEquals(state.Hlod,hlod))
        {state.Resource=resource;state.Hlod=hlod;state.Local=null;state.Post=null;}
        if(post is not null)
        {
            if(state.Post is null || !state.Post.Matches(post))
            {
                if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,Matrix4x4.Identity,[],
                    post,out var local,out diagnostic,localBoundsQuery:true))return false;
                state.Post=LivePostTransformHlodBounds.Create(post,local);
            }
            return state.Post.TryResolveWorldSphere(hlod.Name,post.RigidWorldTransform,out bounds,out diagnostic,destination);
        }
        if(state.Local is null)
        {
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,Matrix4x4.Identity,[],out var local,out diagnostic))return false;
            state.Local=local;
        }
        var cached=state.Local;
        var sphere=EnbCurrentRetailRenderBoundsStorage.Calculate(world,
            new(cached.LocalSphere.Center,cached.LocalSphere.Radius),cached.LocalBox.Center,cached.LocalBox.Extents).Sphere;
        bounds=destination.Set(cached.LocalBox,cached.LocalSphere,new(new(sphere.X,sphere.Y,sphere.Z),sphere.W));
        if(!bounds.WorldSphere.IsValid)
        {diagnostic=$"W3D HLOD '{hlod.Name}' produced invalid cached world bounds.";return false;}
        return true;
    }

}
