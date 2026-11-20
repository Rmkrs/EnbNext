using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<Vector3,Task<EnbWarpPlumeAsset>> _afterburnAssets=[];
    private readonly Dictionary<AttachVisualKey,AttachVisualInstance> _attachedEffects=[];
    private readonly HashSet<(uint Root,ulong Generation,ulong Sequence)> _attachActivations=[];
    private string _attachedEffectStatus="attachments idle";

    private void ClearAttachedEffects()
    {
        foreach(var particle in _attachedEffects.Values.SelectMany(p=>p.Particles))RemoveAttachmentCallbacks(particle);
        _afterburnAssets.Clear();_attachedEffects.Clear();_attachActivations.Clear();_attachedEffectStatus="attachments idle";
    }

    private void UpdateAttachedEffects(LiveFrame frame,EnbRetailAssetBundle assets,RootTaskList roots,
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes, IReadOnlySet<ulong>? refreshedOwners = null)
    {
        if(_shipExhaustOwner is not null)return;
        if(frame.Snapshot.IsTransitioning){ClearAttachedEffects();return;}
        var used=new HashSet<AttachVisualKey>();int pending=0,blocked=0;string? failure=null;
        var active=nodes.Where(n=>n.Active && n.Definition.EffectClassKind==EnbEffectClassKind.AttachEffect)
            .Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        _attachActivations.RemoveWhere(key=>!active.Contains(key));
        var required=new HashSet<Vector3>();
        foreach(var candidate in nodes.Where(n=>n.Definition.DefinitionId==674))
            if(EnbCurrentRetailEffectFactoryHsv.TryRead(candidate,out var shift,out _))required.Add(shift);
        foreach(var node in nodes.Where(n=>n.Active && n.Definition.EffectClassKind==EnbEffectClassKind.AttachEffect))
        {
            try
            {
                if(node.Definition.DefinitionId!=674 || node.Root.GeometryKind!=EnbSceneEffectGeometryKind.Object ||
                    node.LinkKind!=EnbCurrentRetailEffectLinkKind.Root ||
                    node.Root.SpatialOffset!=Vector3.Zero)
                    throw new InvalidDataException("Attachment drawing currently requires ordinary E674 with zero spatial offset");
                // 00734EB2 -> 006E9642/4C/57 -> 006EC570: the optional 0009
                // EffectOffset vector is prototype HSV, not a bone displacement.
                if(!EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var hsv,out var hsvError))throw new InvalidDataException(hsvError);
                float scale=node.Root.Scale;
                if(!float.IsFinite(scale)||scale<=0)throw new InvalidDataException("Attachment requires finite positive scale");
                if(MathF.Abs(scale-1)<=.01f)scale=1;
                if(!_afterburnAssets.TryGetValue(hsv,out var load))
                {
                    EnbEffectAssetCacheAdmission.TrimForAdmission(_afterburnAssets,required,32);
                    _afterburnAssets[hsv]=load=LoadVisualAsset(()=>EnbWarpPlumeAsset.LoadAttachEffect(assets,674,hsv));
                }
                if(!load.IsCompleted){pending++;continue;}
                var asset=load.GetAwaiter().GetResult();
                if(asset.Resource.Meshes.Count!=0)throw new InvalidDataException("E674 emitter-only prototype changed");
                var owner=node.Attachment?.Owner;if(owner is null)continue;
                if(refreshedOwners is not null && !refreshedOwners.Contains(owner.RenderIdentity))continue;
                if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),Matrix4x4.Identity,out var local,out var error))
                    throw new InvalidDataException(error);
                var activation=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
                bool scanned=false;
                foreach(var root in roots.Where(r=>r.Object.RenderIdentity==owner.RenderIdentity))
                {
                    scanned=true;
                    var parent=CompositeMembershipKey(root);
                    if(!TryResolveConfiguredAttachmentHierarchy(root.Root,out var hierarchy,out _) || hierarchy is null ||
                        !hierarchy.Pivots.Any(p=>p.Name.StartsWith(asset.BonePrefix,StringComparison.OrdinalIgnoreCase)))continue;
                    var post=ConfiguredHierarchyPostTransforms(root);Matrix4x4 world;
                    if(post is not null)world=post.RigidWorldTransform;
                    else if(root.WorldTransformOverride is {} attached)world=attached;
                    else if(owner.Spatial?.Transform is not {} spatial ||
                        !EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,out world,out _))continue;
                    if(!TryResolveRetainedHierarchyPose(root,hierarchy,world,post?.HierarchyTranslationScale??owner.Scale,
                        root.AnimationBlend?.ResolvedSamples??[],post?.Transforms,out var pose,out error))throw new InvalidDataException(error);
                    for(int pivot=0;pivot<hierarchy.Pivots.Count;pivot++)
                    {
                        if(!hierarchy.Pivots[pivot].Name.StartsWith(asset.BonePrefix,StringComparison.OrdinalIgnoreCase))continue;
                        var key=new AttachVisualKey(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence,owner.RenderIdentity,
                            parent.Generation,parent.Path,pivot) {Parent=parent};
                        if(_attachActivations.Contains(activation) && !_attachedEffects.ContainsKey(key))continue;
                        if(!used.Add(key))continue;
                        if(!_attachedEffects.TryGetValue(key,out var instance))
                        {
                            instance=new(owner,asset,scale,frame.CurrentClientTime);_attachedEffects.Add(key,instance);
                            foreach(var particle in instance.Particles)TrackAttachmentCallbacks(particle);
                        }
                        instance.Owner=owner;
                        // 00810750 toggles every child attached to an exhaust bone,
                        // including these clones after 006E4D00's initial hidden copy.
                        // An authored sibling is relevant to the initial hidden
                        // copy, not subsequent thrust updates of attached children.
                        bool emitting=!(asset.HideAsOtherObjects && !EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(owner).ShouldDraw);
                        for(int i=0;i<instance.Buffers.Length;i++)
                        {
                            var childLocal=local[asset.Particles[i].Pivot];childLocal.Translation*=scale;
                            instance.Buffers[i].Prepare(frame.CurrentClientTime,childLocal*pose.WorldTransforms[pivot],scale,true,emitting);
                        }
                    }
                }
                if(scanned)_attachActivations.Add(activation);
            }
            catch(Exception ex){blocked++;failure??=$"E{node.Definition.DefinitionId}: {ex.Message}";}
        }
        foreach(var (key,instance) in _attachedEffects.ToArray())
        {
            if(refreshedOwners is not null && !refreshedOwners.Contains(key.Owner))continue;
            if(used.Contains(key))continue;
            foreach(var buffer in instance.Buffers)
                if(active.Contains((key.Root,key.Generation,key.Sequence)))buffer.SetSceneAttached(false);
                else buffer.Destroy(frame.CurrentClientTime,buffer.Emitter.CurrentWorld,instance.Scale);

        }
        _attachedEffectStatus=$"attachments bound {used.Count} tails {_attachedEffects.Count-used.Count} pending {pending} blocked {blocked}"+
            (failure is null?"":" · "+failure);
    }

    private void DrawAttachedEffects(List<EnbW3dDrawTask> packets,LiveFrame frame,EnbW3dLightEnvironment lights)
    {
        var source=_shipExhaustOwner??this;int draws=0;string? error=null;
        try
        {
            foreach(var (key,instance) in source._attachedEffects.ToArray())
            {
                if(_shipExhaustOwner is not null && (!SceneObjects(frame.Snapshot).Any(o=>o.RenderIdentity==key.Owner) || !ScannerContainsParent(key.Parent)))continue;
                string path=$"attach/{key.Root}/{key.Generation}/{key.Sequence}/{key.Owner}/{key.OwnerGeneration}/{key.Path}/{key.Pivot}";
                for(int i=0;i<instance.Particles.Length;i++)
                    if(_shipExhaustOwner is null || !instance.Particles[i].State.Destroyed)
                    if(DrawAttachmentParticle(packets,instance.Particles[i],instance.Owner,path+"/"+i,i,frame,lights))draws++;
                if(_shipExhaustOwner is null && instance.Particles.All(p=>p.Retired))_attachedEffects.Remove(key);
            }
        }
        catch(Exception ex){error=ex.Message;}
        _shipExhaustStatus+=$" · {source._attachedEffectStatus} draws {draws}"+(error is null?"":" · attachment draw blocked: "+error);
    }

    private readonly record struct AttachVisualKey(uint Root,ulong Generation,ulong Sequence,ulong Owner,ulong OwnerGeneration,string Path,int Pivot)
    { public RetainedEmitterKey Parent {get;init;} }
    private sealed class AttachVisualInstance(IEnbRenderObject owner,EnbWarpPlumeAsset asset,float scale,uint clock)
    {
        public IEnbRenderObject Owner{get;set;}=owner;
        public EnbWarpPlumeAsset Asset{get;}=asset;
        public float Scale{get;}=scale;
        public AttachmentComposite Composite{get;}=new(asset,scale);
        public AttachmentParticle[] Particles{get;}=asset.Particles.Select(p=>new AttachmentParticle(p.Asset,clock,scale)).ToArray();
        public EnbRetailParticleSceneLifetime[] Buffers=>Particles.Select(p=>p.State).ToArray();
    }
}
