using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private bool _preparingLegoRadiusQueries;

    //Controller construction/packet mutations publish radius before radar's
    //scene add/remove branch and the retained9661C0 callback walk.
    private IReadOnlyDictionary<ulong,float> PrepareLegoRadiusQueries(LiveFrame frame,
        EnbRetailAssetBundle assets,EnbRetailBaseAssetResolver resolver,
        IReadOnlyDictionary<ulong,EnbRslObjectState> updates)
    {
        bool previousQuery=_preparingLegoRadiusQueries;
        _preparingLegoRadiusQueries=true;
        try
        {
            _hiddenCombatSourceRoots.Clear();
            _emptyRslSourcePoses.Clear();
            using var roots=_ownedSceneFrame?_ownedRadiusQueryTasks.Rent(this):new RootTaskList();
            foreach(var (obj,source) in SceneOwnerObjects(frame.Snapshot))
            {
                if(!HasCurrentRetailShipLegoComposition(obj))continue;
                using var sourceScope=new SceneOwnerScope(this,source);
                if(obj.ObjectType==0x08 || !TryBuildObjectWorld(obj,out var world))continue;
                LiveRootInstance? query=null;
                var factory=ResolveOwnedSceneFactory(source,obj,assets);
                var resolution=factory?.Resolution??ResolveBaseAsset(resolver,obj.BaseAsset);
                var update=source?.QueryRslPublication.Value;
                if(source is null)updates.TryGetValue(obj.RenderIdentity,out update);
                if(resolution.Rsl is {} rsl && update is not null)
                {
                    query=ResolveRslSourceQueryRoot(obj,rsl,update,assets,frame.CurrentClientTime,out _);
                    if(source is not null)source.QueryEmptyRadiusCandidate=query is null&&update.CurrentObjectStateId is null;
                    if(query is null && update.CurrentObjectStateId is null)
                        _emptyRslSourcePoses[obj.RenderIdentity]=(obj,new(world,
                            System.Collections.Immutable.ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.Ordinal),
                            System.Collections.Immutable.ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.OrdinalIgnoreCase),
                            System.Collections.Immutable.ImmutableDictionary.Create<string,EnbNext.Net7.Gameplay.Net7EffectSourceControllerPose>(StringComparer.Ordinal)));
                    else if(query is null)continue;
                }
                else if(factory?.Root is {} root)
                    query=new(obj,root,false,"root",world,ResolveShipWarpAnimation(obj,root,"root",world.Translation));
                else continue;
                var local=new RootTaskList();
                if(query is not null)local.Add(query);
                TryAppendCurrentRetailShipLegoInstances(obj,query?.Root,query?.AnimationBlend,assets,resolver,local,[],new(),out _);
                roots.AddRange(local);
            }
            return BuildRetainedCurrentRetailDistanceRadii(roots,assets.ClientBaseAssetMounts);
        }
        finally{_preparingLegoRadiusQueries=previousQuery;}
    }

    // A 72CC10 controller can exist without a drawable current RSL model.
    // Its wrapper origin and the model's delegated hierarchy are distinct.
    private sealed record LegoRadiusController(string Path,int Base,Matrix4x4 World,
        LiveRootInstance? Model,float Scale,bool NoOpMounts,bool ExternalRsl=false);

    private static bool TryGetLegoRadiusWorld(LiveRootInstance instance,out Matrix4x4 world)
    {
        if(instance.WorldTransformOverride is {} retained)world=retained;
        else if(!TryBuildObjectWorld(instance.Object,out world))return false;
        return IsFinite(world);
    }

    private static bool TryCaptureLegoRadiusController(LiveRootInstance instance,out LegoRadiusController controller)
    {
        controller=null!;
        if(!TryGetLegoRadiusWorld(instance,out var world))return false;
        controller=new(instance.InstancePath,instance.LegoControllerBaseAsset??instance.Object.BaseAsset,world,
            instance,instance.IsShipLegoController?1:instance.RenderScale,HasNoOpLegoMounts(instance.Root));
        return true;
    }
    private static bool TryResolveLegoRadius(IReadOnlyDictionary<string,LegoRadiusController> controllers,
        EnbClientBaseAssetMountCatalog mounts,out float radius)
    {
        radius=0;
        var nodes=new List<EnbCurrentRetailAttachedRadiusNode>();
        foreach(var instance in controllers.Values)
        {
            Vector3? parentQuery=null;
            if(instance.Path!="root")
            {
                int slash=instance.Path.LastIndexOf('/');
                string parentPath=slash==4?"root":instance.Path[..slash];
                if(!controllers.TryGetValue(parentPath,out var parent))return false;
                parentQuery=parent.World.Translation;
                //4622D0 returns wrapper+1C when no current model exists.
                if(parent.Model is {} model)
                {
                    if(!TryResolveConfiguredAttachmentHierarchy(model.Root,out var hierarchy,out _))return false;
                    if(hierarchy is not null)
                    {
                        string name=instance.Path[(slash+1)..];
                        var post=ConfiguredHierarchyPostTransforms(model);
                        //72CCBF queries the controller key, not BoneIndex or
                        //the child's current matrix;98A000 falls back to root.
                        if(!EnbCurrentRetailShipLegoRuntime.TryResolveRetailHierarchyPivot(hierarchy,name,out int index,out _,out _) ||
                            !TryResolveRetainedHierarchyPose(model,hierarchy,
                                post?.RigidWorldTransform??parent.World,post?.HierarchyTranslationScale??parent.Scale,
                                model.AnimationBlend?.ResolvedSamples??[],post?.Transforms,out var pose,out _))return false;
                        parentQuery=pose.WorldTransforms[index].Translation;
                    }
                }
            }
            nodes.Add(new(instance.Path,instance.World.Translation,mounts.ResolveControllerRadius(instance.Base))
                {ParentQueryOrigin=parentQuery});
        }
        return EnbCurrentRetailAttachedRadiusRuntime.TryResolve(nodes,out radius);
    }
}
