using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private bool _isolatedLegoNotification;
    private EnbCurrentRetailAttachedRadiusController EnsureLegoRadius(RetainedLegoControllers retained,
        EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node node)
    {
        if(retained.Radii.TryGetValue(node,out var radius))return radius;
        radius=new();retained.Radii.Add(node,radius);
        if(ReferenceEquals(node,retained.Tree.Root))retained.RootRadius=radius;
        radius.Refresh(retained.RadiusAssets!.ClientBaseAssetMounts?.ResolveControllerRadius(
            node.Value?.BaseAsset??retained.RadiusObject!.BaseAsset),false,Vector3.Zero,[]);
        return radius;
    }

    private bool HasLegoRadiusModel(RetainedLegoControllers retained,LiveShipLegoSpec? spec)
    {
        var obj=retained.RadiusObject!;var assets=retained.RadiusAssets!;
        if(spec is null)
        {
            var factory=ResolveSceneBaseFactory(obj,assets);
            return factory?.Root is not null || (factory?.Resolution??ResolveBaseAsset(retained.RadiusResolver!,obj.BaseAsset)).Rsl is not null;
        }
        return ResolveLegoBaseFactory(obj,spec,assets)?.Root is not null ||
            _legoRslOwners.ContainsKey((obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath));
    }

    private void AttachLegoRadiusObserver(RetainedLegoControllers retained,
        EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node parent,EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node child)
    {
        var radius=EnsureLegoRadius(retained,child);
        if(retained.RadiusAssets!.ClientBaseAssetMounts?.TryResolveMount(parent.Value?.BaseAsset??retained.RadiusObject!.BaseAsset,child.Path[^1],out _)==true &&
            HasLegoRadiusModel(retained,parent.Value) && HasLegoRadiusModel(retained,child.Value))
            radius.Changed=()=>RefreshRetainedLegoRadius(retained,parent);
    }

    private void DetachLegoRadiusObserver(RetainedLegoControllers retained,
        EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node parent,EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node child)
    {
        if(retained.RadiusAssets!.ClientBaseAssetMounts?.TryResolveMount(parent.Value?.BaseAsset??retained.RadiusObject!.BaseAsset,child.Path[^1],out _)==true &&
            HasLegoRadiusModel(retained,parent.Value))EnsureLegoRadius(retained,child).Changed=null;
    }

    private void RefreshRetainedLegoRadius(RetainedLegoControllers retained,
        EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node node)
    {
        var radius=EnsureLegoRadius(retained,node);
        var mounts=retained.RadiusAssets!.ClientBaseAssetMounts;
        if(mounts is null){radius.Refresh(null,false,Vector3.Zero,[]);return;}
        float own=mounts.ResolveControllerRadius(node.Value?.BaseAsset??retained.RadiusObject!.BaseAsset);
        if(node.Children.Count==0 || !HasLegoRadiusModel(retained,node.Value))
        {retained.RadiusDiagnostic=null;radius.Refresh(own,false,Vector3.Zero,[]);return;}
        var inputs=CaptureLegoRadiusNotificationInputs(retained);
        string path=node.Value is null?"root":"lego/"+node.Value.CanonicalPath;
        if(inputs is null || !inputs.TryGetValue(path,out var parent))
        {retained.RadiusDiagnostic=$"Lego radius notification has no retained query model for {path}.";return;}
        var children=new List<(float Radius,Vector3 Pivot)>();
        foreach(var child in node.Children.Values)
        {
            if(!mounts.TryResolveMount(node.Value?.BaseAsset??retained.RadiusObject!.BaseAsset,child.Path[^1],out _))continue;
            if(!TryResolveLegoRadiusPivot(parent,child.Path[^1],out var pivot))
            {retained.RadiusDiagnostic=$"Lego radius notification cannot query {path}/{child.Path[^1]}.";return;}
            children.Add((EnsureLegoRadius(retained,child).Radius,pivot));
        }
        retained.RadiusDiagnostic=null;
        radius.Refresh(own,true,parent.World.Translation,children);
    }

    private IReadOnlyDictionary<string,LegoRadiusController>? CaptureLegoRadiusNotificationInputs(RetainedLegoControllers retained)
    {
        using var sourceScope=new SceneOwnerScope(this,retained.SceneSource);
        var obj=retained.RadiusObject!;var assets=retained.RadiusAssets!;var resolver=retained.RadiusResolver!;
        if(!TryBuildObjectWorld(obj,out var world))return null;
        var previousUpdates=_frameRslObjectUpdates;
        bool previousQuery=_preparingLegoRadiusQueries;
        bool previousIsolation=_isolatedLegoNotification;
        var previousHidden=_hiddenCombatSourceRoots.ToArray();
        var previousEmitterRoots=_hiddenRslEmitterRoots.ToArray();
        var previousSources=_legoSourceInstances.ToDictionary(p=>p.Key,p=>p.Value);
        var previousWrappers=_legoRslOwners.Values.ToDictionary(w=>w,w=>(Pose:EnbNext.Presentation.EnbRenderPose.FromObject(w.Object),w.QueryRoot,w.QueryDiagnostic));
        bool hadEmpty=_emptyRslSourcePoses.TryGetValue(obj.RenderIdentity,out var previousEmpty);
        var boneKey=(obj.RenderIdentity,obj.RenderObjectGeneration);
        bool hadBones=_rslBoneOwners.TryGetValue(boneKey,out var previousBones);
        bool isolatedBones=false;
        _preparingLegoRadiusQueries=true;_isolatedLegoNotification=true;
        _hiddenCombatSourceRoots.Clear();
        try
        {
            var factory=ResolveSceneBaseFactory(obj,assets);
            var resolution=factory?.Resolution??ResolveBaseAsset(resolver,obj.BaseAsset);
            LiveRootInstance? root=null;
            if(resolution.Rsl is {} rsl)
            {
                if(!previousUpdates.TryGetValue(obj.RenderIdentity,out var update))return null;
                if(retained.PacketBoundary is not null)
                {
                    var query=retained.RadiusRootReplacement?update.FactoryLegoQuery:
                        update.LegoControllerQueryUpdates.GetValueOrDefault(retained.Sequence);
                    if(query is null)return null;
                    update=query;
                    _frameRslObjectUpdates=previousUpdates.ToDictionary(p=>p.Key,p=>p.Key==obj.RenderIdentity?query:p.Value);
                    if(query.Graph is {} graph && graph.States.Any(s=>s.Definition.Type==EnbRslStateType.Bone))
                    {
                        var bones=new LiveRslBoneOwner();
                        ConsumeRslBoneActionsCore(obj,graph,assets,query,bones);
                        if(bones.Diagnostic is not null)return null;
                        _rslBoneOwners[boneKey]=bones;isolatedBones=true;
                    }
                }
                root=ResolveRslSourceQueryRoot(obj,rsl,update,assets,retained.RadiusTime,out _);
                if(root is null && update.CurrentObjectStateId is not null)return null;
                if(root is null)_emptyRslSourcePoses[obj.RenderIdentity]=(obj,new(world,
                    System.Collections.Immutable.ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.Ordinal),
                    System.Collections.Immutable.ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.OrdinalIgnoreCase),
                    System.Collections.Immutable.ImmutableDictionary.Create<string,EnbNext.Net7.Gameplay.Net7EffectSourceControllerPose>(StringComparer.Ordinal)));
            }
            else if(factory?.Root is {} plain)
                root=new(obj,plain,false,"root",world,ResolveShipWarpAnimation(obj,plain,"root",world.Translation));
            else return null;
            var roots=new RootTaskList();if(root is not null)roots.Add(root);
            TryAppendCurrentRetailShipLegoInstances(obj,root?.Root,root?.AnimationBlend,assets,resolver,roots,[],new(),out _);
            IReadOnlyDictionary<string,LegoRadiusController>? result=null;
            VisitCurrentRetailRadiusControllers(roots,assets.ClientBaseAssetMounts!,(owner,inputs)=>
            {if(owner.RenderIdentity==obj.RenderIdentity)result=inputs;});
            return result;
        }
        finally
        {
            _frameRslObjectUpdates=previousUpdates;_preparingLegoRadiusQueries=previousQuery;_isolatedLegoNotification=previousIsolation;
            _hiddenCombatSourceRoots.Clear();_hiddenCombatSourceRoots.AddRange(previousHidden);
            _hiddenRslEmitterRoots.Clear();_hiddenRslEmitterRoots.AddRange(previousEmitterRoots);
            _legoSourceInstances.Clear();foreach(var pair in previousSources)_legoSourceInstances.Add(pair.Key,pair.Value);
            foreach(var (wrapper,previous) in previousWrappers)
            {if(previous.Pose.HasSample)((EnbRenderSpatialState)wrapper.Object.Spatial!).Pose.Update(previous.Pose);wrapper.QueryRoot=previous.QueryRoot;wrapper.QueryDiagnostic=previous.QueryDiagnostic;}
            if(hadEmpty)_emptyRslSourcePoses[obj.RenderIdentity]=previousEmpty;
            else _emptyRslSourcePoses.Remove(obj.RenderIdentity);
            if(isolatedBones)
            {
                if(hadBones)_rslBoneOwners[boneKey]=previousBones!;
                else _rslBoneOwners.Remove(boneKey);
            }
        }
    }

    private static bool TryResolveLegoRadiusPivot(LegoRadiusController parent,string name,out Vector3 pivot)
    {
        pivot=parent.World.Translation;
        if(parent.Model is not {} model)return true;
        if(!TryResolveConfiguredAttachmentHierarchy(model.Root,out var hierarchy,out _))return false;
        if(hierarchy is null)return true;
        var post=ConfiguredHierarchyPostTransforms(model);
        if(!EnbCurrentRetailShipLegoRuntime.TryResolveRetailHierarchyPivot(hierarchy,name,out int index,out _,out _) ||
            !TryResolveRetainedHierarchyPose(model,hierarchy,
                post?.RigidWorldTransform??parent.World,post?.HierarchyTranslationScale??parent.Scale,
                model.AnimationBlend?.ResolvedSamples??[],post?.Transforms,out var pose,out _))return false;
        pivot=pose.WorldTransforms[index].Translation;return true;
    }
}
