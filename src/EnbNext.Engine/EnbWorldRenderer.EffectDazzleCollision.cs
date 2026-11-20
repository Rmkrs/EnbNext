using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<EffectDazzleCollision> _effectDazzleCollision = [];
    private sealed class EffectDazzleCollision(EnbCurrentRetailRObjEffectAsset Asset,
        EnbW3dRenderResource? Resource, Matrix4x4 World, float Scale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> Animations, EnbResolvedRenderCamera Camera)
    {
        public EnbCurrentRetailRObjEffectAsset Asset {get;set;} = Asset;
        public EnbW3dRenderResource? Resource {get;set;} = Resource;
        public Matrix4x4 World {get;set;} = World;
        public float Scale {get;set;} = Scale;
        public IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> Animations {get;set;} = Animations;
        public EnbResolvedRenderCamera Camera {get;set;} = Camera;
        public float? RemainingGeometryScale {get;set;}
        public float? HierarchySourceScale {get;set;}
        private readonly EnbW3dResolvedWeightedAnimationSample[] _animation = new EnbW3dResolvedWeightedAnimationSample[1];
        public void SetAnimation(EnbW3dRenderHierarchyAnimation? animation,float frame)
        {
            if(animation is null)Animations=Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
            else { _animation[0]=new(animation,frame,1); Animations=_animation; }
        }
    }

    // View-local mesh occurrences retain query structure. Membership changes
    // (98E0F0/98F1D0) invalidate it; camera/pose/animation are invocation fields.
    private sealed class EffectQueryOwner
    {
        public EnbCurrentRetailRObjEffectAsset? Source;
        public CompositeBoundsState? Membership;
        public long Revision;
        public EffectDazzleCollision? Collision;
    }

    private EffectDazzleCollision PrepareEffectQueryOwner(EnbCurrentRetailEffectOwner node,
        EnbCurrentRetailRObjEffectAsset asset,EffectObjectMeshOwners meshes,CompositeBoundsState membership)
    {
        var owner=meshes.Query;
        if(owner.Collision is {} current && ReferenceEquals(owner.Source,asset) &&
            ReferenceEquals(owner.Membership,membership) && owner.Revision==membership.Revision)return current;
        var children=new EnbRObjModelAttachment[asset.ModelAttachments.Count];
        for(int i=0;i<children.Length;i++)
        {
            var child=asset.ModelAttachments[i];
            children[i]=child with {Asset=child.Asset with
                {Resource=RetainedCompositeQueryResource(node,i,meshes.Children[i].Resource,child.Asset)}};
        }
        var queryAsset=asset with {ModelAttachments=EnbRObjModelAttachment.RebuildHierarchy(children)};
        var resource=meshes.Root is {} root
            ? RetainedCompositeQueryResource(node,-1,root.Resource,asset).Copy().SetCompositeRenderChildren(asset.Resource!.CompositeRenderChildren)
            : asset.Resource;
        if(owner.Collision is null)owner.Collision=new(queryAsset,resource,Matrix4x4.Identity,1,[],default!);
        else {owner.Collision.Asset=queryAsset;owner.Collision.Resource=resource;}
        owner.Source=asset;owner.Membership=membership;owner.Revision=membership.Revision;
        return owner.Collision;
    }

    private readonly List<EnbDazzleCollisionRoot> _effectEmitterCollision = [];

    private void RetainEffectEmitterCollision(EffectObjectInstance instance, Matrix4x4 world,
        EnbResolvedRenderCamera camera, uint clock, int maximum, long emitted)
    {
        instance.EmitterRemovalClock=RetainEmitterSceneRoot(instance.EmitterRemovalClock,world,camera,clock,maximum,emitted);
    }

    private uint? RetainEmitterSceneRoot(uint? removed,Matrix4x4 world,EnbResolvedRenderCamera camera,uint clock,int maximum,long emitted)
    {
        //97DEF0 queues finite-emitter removal;9662E0 processes it after traversal.
        if(removed is {} previous && previous!=clock)return removed;
        if(maximum>0 && emitted>=maximum)removed??=clock;
        //97E4D0 zero-radius world bound;462CD0 leaves the shared hit unchanged.
        var bounds=new EnbW3dWorldBounds(world.Translation,0);
        _effectEmitterCollision.Add(new(new(bounds.Center,0),
            EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds),false,
            static (start,end,fraction,inside)=>(fraction,inside)));
        return removed;
    }

    // 00734F43..5E / 00727780 / 006EF200: effect render objects join the
    // same scene traversed by 0075DEE0. Retain their collision inputs before
    // draw LOD selection; the ray consumer uses the final retained level.
    private void CaptureDiagnosticEffectCollision(EnbCurrentRetailRObjEffectAsset asset,
        EnbW3dRenderResource? resource, Matrix4x4 world, float scale,
        uint start, uint now, EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed,
        EnbResolvedRenderCamera camera,float? remainingGeometryScale=null,float? hierarchySourceScale=null)
        => UpdateEffectDazzleCollision(new(asset,resource,world,scale,[],camera),world,scale,start,now,observed,camera,remainingGeometryScale,hierarchySourceScale);

    private void UpdateEffectDazzleCollision(EffectDazzleCollision query,Matrix4x4 world,float scale,
        uint start,uint now,EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed,
        EnbResolvedRenderCamera camera,float? remainingGeometryScale=null,float? hierarchySourceScale=null)
    {
        var asset=query.Asset;
        var animation = asset.Animation;
        float frame = 0;
        if (asset.Sequence is not null)
        {
            if (observed is not {} sample) return;
            animation = sample.BoundAnimation;
            frame = sample.Frame;
        }
        else if (animation is not null &&
            !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(animation, asset.AnimationFlags!, start, now, out frame, out var why))
            throw new InvalidDataException(why);
        query.World=world;query.Scale=scale;query.Camera=camera;
        query.RemainingGeometryScale=remainingGeometryScale;query.HierarchySourceScale=hierarchySourceScale;
        query.SetAnimation(animation,frame);
        _effectDazzleCollision.Add(query);
    }
}
