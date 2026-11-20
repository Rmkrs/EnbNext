using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

// Complete child plan. Optional emitter ownership is separate from rings;
// ring-only assemblies do not manufacture particle runtimes or fixed costs.
public sealed record EnbRetailRingAssembly(EnbW3dRenderResource? Resource,
    EnbW3dRenderHierarchyAnimation? Animation,EnbRslStateFlags? AnimationFlags,
    IReadOnlyList<EnbRetailRingAttachment> Rings,EnbCurrentRetailMixedEffectAsset? Emitters)
{
    internal static EnbRetailRingAssembly LoadStandalone(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
        => new(null,null,null,[EnbRetailRingAttachment.LoadStandalone(assets,resolved)],null);

    public EnbRetailRingAssembly(EnbCurrentRetailMixedEffectAsset emitters,IReadOnlyList<EnbRetailRingAttachment> rings)
        :this(emitters.Resource,emitters.Animation,emitters.AnimationFlags,rings,emitters){}

    internal EnbRetailRingAssembly BindParentBounds()
    {
        var resource=Resource!;var hlod=resource.HierarchicalLods.Single();
        var members=new List<EnbW3dRenderHlodMember>(hlod.Aggregate?.Members??[]);
        var children=resource.CompositeRenderChildren.ToList();
        foreach(var ring in Rings)
        {
            //00913261..277 initializes local extents from raw second radii,
            //with zero Z.00912480 scales multipliers/keys, not these extents.
            var header=ring.Asset.Header;var extent=new Vector3(header.SecondRadii,0);
            string name=$"<0600-ring:{hlod.Name}:{ring.Ordinal}>";
            children.Add(new(name,0x1e,new(new(header.Center,extent.Length()),header.Center,extent),
                (int)EnbSphereRingDetailBudgetSemantics.RingLevelCosts[EnbSphereRingDetailBudgetSemantics.RingInitialLevelOrdinal]));
            members.Add(new(checked((uint)ring.PivotIndex),name));
        }
        resource=resource.Copy().SetCompositeRenderChildren(children).SetHierarchicalLods([hlod with {Aggregate=new(hlod.Aggregate?.RawValue??0,members)}]);
        return this with {Resource=resource,Emitters=Emitters is {} emitters?emitters with {Resource=resource}:null};
    }
    public static EnbRetailRingAssembly Load(EnbRetailAssetBundle assets,EnbW3dRenderResource resource,
        EnbCurrentRetailW3dAggregateDefinition aggregate,EnbW3dRenderHierarchyAnimation? animation=null,
        EnbRslStateFlags? animationFlags=null,IReadOnlyDictionary<string,EnbResolvedW3dResource>? prototypes=null)
    {
        if(aggregate.Attachments.Count <1)throw new InvalidDataException("Ring assembly requires attachments");
        var tree=resource.Hierarchies.Single();
        var rings=new List<EnbRetailRingAttachment>();
        var others=new List<EnbCurrentRetailW3dAggregateAttachment>();
        for(int ordinal=0;ordinal<aggregate.Attachments.Count;ordinal++)
        {
            var attachment=aggregate.Attachments[ordinal];
            if(prototypes?.TryGetValue(attachment.SubobjectName,out var selected)==true)
            {
                if((selected.CurrentRetailAggregate?.BaseRegisteredPrototype??selected.RegisteredPrototype)?.Kind==EnbW3dPrototypeKind.Ring)
                    rings.Add(EnbRetailRingAttachment.Load(assets,tree,attachment,ordinal,source:selected));
                else others.Add(attachment);
                continue;
            }
            if(!assets.RetailMixResources!.TryReadResource(attachment.SubobjectName+".w3d",out var bytes,out _) || bytes.Length<8)
                throw new InvalidDataException("Ring assembly child missing or truncated");
            if(BinaryPrimitives.ReadUInt32LittleEndian(bytes)==0x742)
                rings.Add(EnbRetailRingAttachment.Load(assets,tree,attachment,ordinal));
            else others.Add(attachment);
        }
        if(rings.Count==0)throw new InvalidDataException("Ring assembly requires ring children");
        if(others.Count==0)
        {
            var h=resource.HierarchicalLods.SingleOrDefault();
            if(h is null || h.Levels.Count<1 ||
                h.HasAggregateExtension ||
                !h.HierarchyName.Equals(tree.Name,StringComparison.OrdinalIgnoreCase) ||
                h.Levels.SelectMany(l=>l.Members).Any(m=>m.ReferenceIndex>=tree.Pivots.Count ||
                    (!EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(m.Name) &&
                     resource.Meshes.Count(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,m.Name))!=1)) ||
                resource.Meshes.Any(mesh=>!h.Levels.SelectMany(l=>l.Members).Any(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,m.Name))) ||
                (animation is not null && animationFlags is null))
                throw new InvalidDataException("Ring assembly requires a complete mesh HLOD and supported animation");
            resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
            return new(resource,animation,animationFlags,rings.AsReadOnly(),null);
        }
        // Existing mixed loader validates every non-ring sibling and the HLOD.
        var emitters=EnbCurrentRetailMixedEffectAsset.Load(assets,resource,aggregate with {Attachments=others},animation,animationFlags,prototypes);
        return new(emitters,rings.AsReadOnly());
    }

    public EnbW3dHierarchyPoseOwner Pose(Matrix4x4 ownerRigidWorld,float scale,uint age,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        if(!float.IsFinite(scale))throw new ArgumentOutOfRangeException(nameof(scale));
        // A standalone primitive has its owner's transform and visibility,
        // without an authored hierarchy or an attachment animation.
        if(Resource is null)
        {
            if(Animation is not null || AnimationFlags is not null || Emitters is not null ||
                Rings.Count!=1 || Rings[0].PivotIndex!=0)
                throw new InvalidDataException("Invalid standalone ring ownership plan");
            return new([ownerRigidWorld],[true],null,null);
        }
        float frame=0;string? error=null;
        var animation=observed is {} observation?observation.BoundAnimation:Animation;
        if(observed is {} sample)frame=sample.Frame;
        else if(animation is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,AnimationFlags!,0,age,out frame,out error))throw new InvalidDataException(error);
        // 0098EF60 scales each ring's radius properties and calls0098A0A0 for
        // authored/animated translations. The object basis remains rigid.
        if(!EnbW3dHierarchyPose.TryEvaluate(Resource.Hierarchies.Single(),ownerRigidWorld,
            animation,frame,null,out var pose,out error,storedTranslationScale:scale))throw new InvalidDataException(error);
        return pose;
    }
}
