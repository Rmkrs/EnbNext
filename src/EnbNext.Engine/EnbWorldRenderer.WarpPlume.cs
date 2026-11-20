using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly EnbWarpPlumeRuntime _warpPlume=new();
    private Task<EnbWarpPlumeAsset>? _warpPlumeAsset;
    private readonly Dictionary<WarpPlumeKey,WarpPlumeInstance> _warpPlumes=[];
    private readonly HashSet<(ulong Owner,ulong Sequence)> _warpPlumeActivations=[];
    private string _warpPlumeStatus="warp plume idle";
    private uint? _warpPlumeMapperStart;
    private void ClearWarpPlumes()
    {
        foreach(var particle in _warpPlumes.Values.SelectMany(p=>p.Particles))RemoveAttachmentCallbacks(particle);
        _warpPlume.Clear();_effectRslScheduler.ClearAttachmentBindings();_warpPlumes.Clear();_warpPlumeActivations.Clear();_attachmentEpoch++;_warpPlumeAsset=null;_warpPlumeMapperStart=null;
        ClearAttachedEffects();
        _warpPlumeStatus="warp plume idle";
    }

    private void UpdateWarpPlumes(LiveFrame frame,EnbRetailAssetBundle assets,RootTaskList roots,
        IReadOnlyList<EnbCurrentRetailEffectOwner> effects, IReadOnlySet<ulong>? refreshedOwners = null)
    {
        if(_shipExhaustOwner is not null)return;
        for(int effectIndex=0;effectIndex<effects.Count;effectIndex++)effects[effectIndex].UpdateAttachment(SceneObjects(frame.Snapshot));
        _warpPlume.Update(SceneObjects(frame.Snapshot));
        _warpPlumeActivations.RemoveWhere(key=>_warpPlume.Get(key.Owner)!=key.Sequence);
        if(!SceneObjects(frame.Snapshot).Any(o=>o.ObjectType==1) && _warpPlumes.Count==0)return;
        _warpPlumeAsset??=LoadVisualAsset(()=>EnbWarpPlumeAsset.Load(assets));
        if(!_warpPlumeAsset.IsCompleted){_warpPlumeStatus="warp plume loading";return;}
        var used=new HashSet<WarpPlumeKey>();
        var scanned=new HashSet<(ulong Owner,ulong Sequence)>();
        try
        {
            var asset=_warpPlumeAsset.GetAwaiter().GetResult();
            _warpPlumeMapperStart??=ReadMapperClock();
            foreach(var root in roots)
            {
                var obj=root.Object;
                if(refreshedOwners is not null && !refreshedOwners.Contains(obj.RenderIdentity))continue;
                if(_warpPlume.Get(obj.RenderIdentity) is not ulong sequence)continue;
                scanned.Add((obj.RenderIdentity,sequence));
                var parent=CompositeMembershipKey(root);
                if(!TryResolveConfiguredAttachmentHierarchy(root.Root,out var h,out _) || h is null)continue;
                bool hasPrefix=false;
                foreach(var pivot in h.Pivots)
                    if(pivot.Name.StartsWith(asset.BonePrefix,StringComparison.OrdinalIgnoreCase)){hasPrefix=true;break;}
                if(!hasPrefix)continue;
                var post=ConfiguredHierarchyPostTransforms(root);
                Matrix4x4 world;
                if(post is not null)world=post.RigidWorldTransform;
                else if(root.WorldTransformOverride is {} attached)world=attached;
                else if(obj.Spatial?.Transform is not {} spatial || !EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,out world,out _))continue;
                if(!TryResolveRetainedHierarchyPose(root,h,world,post?.HierarchyTranslationScale??obj.Scale,
                    root.AnimationBlend?.ResolvedSamples??[],post?.Transforms,out var pose,out var error))throw new InvalidDataException(error);
                for(int i=0;i<h.Pivots.Count;i++)
                {
                    if(!h.Pivots[i].Name.StartsWith(asset.BonePrefix,StringComparison.OrdinalIgnoreCase))continue;
                    var key=new WarpPlumeKey(obj.RenderIdentity,parent.Generation,sequence,parent.Path,i) {Parent=parent};
                    //006E4BE0/006E4D00 capture clones once, not on later hierarchy changes.
                    if(_warpPlumeActivations.Contains((obj.RenderIdentity,sequence)) && !_warpPlumes.ContainsKey(key))continue;
                    if(!used.Add(key))continue;
                    if(!_warpPlumes.TryGetValue(key,out var instance))
                    {
                        instance=new(obj,asset,pose.WorldTransforms[i],frame.CurrentClientTime);_warpPlumes.Add(key,instance);
                        foreach(var particle in instance.Particles)TrackAttachmentCallbacks(particle);
                    }
                    instance.Owner=obj;
                    instance.World=pose.WorldTransforms[i];
                    // 006E4D00 copies the first existing bone child's hidden bit.
                    // Normal engine children share the proven exhaust visibility rule.
                    bool hasExisting=false;
                    foreach(var attachment in root.Root.AggregateAttachments)
                        if(attachment.BoneName.Equals(h.Pivots[i].Name,StringComparison.OrdinalIgnoreCase)){hasExisting=true;break;}
                    instance.Active=!(asset.HideAsOtherObjects && hasExisting && !EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(obj).ShouldDraw);
                    if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),instance.World,out var childPose,out error))throw new InvalidDataException(error);
                    for(int j=0;j<instance.Particles.Length;j++)instance.Particles[j].State.Prepare(frame.CurrentClientTime,childPose[asset.Particles[j].Pivot],1,true,instance.Active);
                }
            }
            _warpPlumeActivations.UnionWith(scanned);
            foreach(var (key,instance) in _warpPlumes.ToArray())
            {
                if(refreshedOwners is not null && !refreshedOwners.Contains(key.Owner))continue;
                if(used.Contains(key))continue;
                instance.Active=false;
                foreach(var particle in instance.Particles)
                    if(_warpPlume.Get(key.Owner)==key.Sequence)particle.State.SetSceneAttached(false);
                    else particle.State.Destroy(frame.CurrentClientTime,particle.State.Emitter.CurrentWorld,1);

            }
            _warpPlumeStatus=$"warp plume attached {used.Count} active {_warpPlumes.Values.Count(i=>i.Active)} tails {_warpPlumes.Count-used.Count}";
        }
        catch(Exception ex){_warpPlumeStatus="warp plume blocked: "+ex.Message;}
    }

    private void DrawWarpPlumes(List<EnbW3dDrawTask> packets,LiveFrame frame,EnbW3dLightEnvironment lights)
    {
        var source=_shipExhaustOwner??this;
        int meshes=0,trails=0;
        _attachmentBufferCollision.Clear();
        try
        {
            foreach(var (key,instance) in source._warpPlumes.ToArray())
            {
                if(_shipExhaustOwner is not null && !SceneObjects(frame.Snapshot).Any(o=>o.RenderIdentity==key.Owner))continue;
                var asset=instance.Asset;string path=$"warp-plume/{key.Owner}/{key.Generation}/{key.Sequence}/{key.Path}/{key.Pivot}";
                if(instance.Active && AttachmentParentDraws(key.Parent))
                {
                    if(!EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(instance.Owner,asset.Resource,instance.World,frame.CurrentClientTime,
                        EnbRenderPassKind.OrdinaryWorld,asset.Selection,lights,[],out var list,out var error))throw new InvalidDataException(error);
                    foreach(var p in list.Packets)
                    {
                        packets.Add((_overbrightMaterials.Apply(p,asset.OverbrightOff,out _)).ForkTask().SetRenderInstancePath(path).SetCurrentRetailEffectMapperStartMilliseconds(source._warpPlumeMapperStart));meshes++;
                    }
                }
                for(int i=0;i<instance.Particles.Length;i++)
                    if(_shipExhaustOwner is null || instance.Active && ScannerContainsParent(key.Parent))
                    if(DrawAttachmentParticle(packets,instance.Particles[i],instance.Owner,path+"/trail/"+i,i,frame,lights))trails++;
                if(_shipExhaustOwner is null && instance.Particles.All(p=>p.Retired))_warpPlumes.Remove(key);
            }
            _shipExhaustStatus+=$" · {source._warpPlumeStatus} mesh draws {meshes} trail draws {trails}";
        }
        catch(Exception ex){_shipExhaustStatus+=" · warp plume draw blocked: "+ex.Message;}
    }
    private readonly record struct WarpPlumeKey(ulong Owner,ulong Generation,ulong Sequence,string Path,int Pivot)
    { public RetainedEmitterKey Parent {get;init;} }
    private sealed class WarpPlumeInstance(IEnbRenderObject owner,EnbWarpPlumeAsset asset,Matrix4x4 world,uint clock)
    {
        public IEnbRenderObject Owner{get;set;}=owner;
        public EnbWarpPlumeAsset Asset{get;}=asset;
        public Matrix4x4 World=world;
        public bool Active;
        public AttachmentComposite Composite{get;}=new(asset,1);
        public AttachmentParticle[] Particles{get;}=asset.Particles.Select(p=>new AttachmentParticle(p.Asset,clock,1)).ToArray();
    }
}
