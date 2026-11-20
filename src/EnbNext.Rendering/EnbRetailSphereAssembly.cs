using System.Buffers.Binary;
using EnbNext.Assets;
using System.Numerics;

namespace EnbNext.Rendering;

// Complete HLOD plan with independently budgeted primitive/emitter children.
public sealed record EnbRetailSphereAssembly(EnbW3dRenderResource? Resource,
    EnbW3dRenderHierarchyAnimation? Animation,EnbRslStateFlags? AnimationFlags,
    IReadOnlyList<EnbRetailSphereAttachment> Spheres,IReadOnlyList<EnbRetailRingAttachment> Rings,
    EnbCurrentRetailMixedEffectAsset? Emitters)
{
    internal static EnbRetailSphereAssembly LoadStandalone(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
        => new(null,null,null,[EnbRetailSphereAttachment.LoadStandalone(assets,resolved)],[],null);

    internal EnbRetailSphereAssembly BindParentBounds()
    {
        var resource=Resource!;var emitters=Emitters;
        if(Rings.Count>0)
        {
            var rings=new EnbRetailRingAssembly(resource,Animation,AnimationFlags,Rings,emitters).BindParentBounds();
            resource=rings.Resource!;emitters=rings.Emitters;
        }
        var hlod=resource.HierarchicalLods.Single();
        var members=new List<EnbW3dRenderHlodMember>(hlod.Aggregate?.Members??[]);
        var children=resource.CompositeRenderChildren.ToList();
        foreach(var sphere in Spheres)
        {
            //0090D140 returns raw center/extents;0090D340 scales playback
            //multipliers and keys without modifying those construction bounds.
            var header=sphere.Asset.Header;string name=$"<0600-sphere:{hlod.Name}:{sphere.Ordinal}>";
            children.Add(new(name,0x1d,new(new(header.Center,header.AuthoredExtent.Length()),header.Center,header.AuthoredExtent),
                (int)EnbSphereRingDetailBudgetSemantics.SphereLevelCosts[EnbSphereRingDetailBudgetSemantics.SphereInitialLevelOrdinal]));
            members.Add(new(checked((uint)sphere.PivotIndex),name));
        }
        resource=resource.Copy().SetCompositeRenderChildren(children).SetHierarchicalLods([hlod with {Aggregate=new(hlod.Aggregate?.RawValue??0,members)}]);
        return this with {Resource=resource,Emitters=emitters is {} mixed?mixed with {Resource=resource}:null};
    }

    public EnbW3dHierarchyPoseOwner Pose(Matrix4x4 ownerRigidWorld,float scale,uint age,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        if(!float.IsFinite(scale))throw new ArgumentOutOfRangeException(nameof(scale));
        if(Resource is null)
        {
            if(Animation is not null || AnimationFlags is not null || Emitters is not null ||
                Rings.Count!=0 || Spheres.Count!=1 || Spheres[0].PivotIndex!=0)
                throw new InvalidDataException("Invalid standalone sphere ownership plan");
            return new([ownerRigidWorld],[true],null,null);
        }
        float frame=0;string? error=null;
        var animation=observed is {} observation?observation.BoundAnimation:Animation;
        if(observed is {} sample)frame=sample.Frame;
        else if(animation is not null&&!EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(animation,AnimationFlags!,0,age,out frame,out error))
            throw new InvalidDataException(error);
        //98EF60 scales primitive properties and98A0A0's stored translations;
        //neither scales the basis. A zero source radius is valid here too.
        if(!EnbW3dHierarchyPose.TryEvaluate(Resource.Hierarchies.Single(),ownerRigidWorld,
            animation,frame,null,out var pose,out error,storedTranslationScale:scale))throw new InvalidDataException(error);
        return pose;
    }
    public static EnbRetailSphereAssembly Load(EnbRetailAssetBundle assets,EnbW3dRenderResource resource,
        EnbCurrentRetailW3dAggregateDefinition aggregate,EnbW3dRenderHierarchyAnimation? animation=null,
        EnbRslStateFlags? animationFlags=null,IReadOnlyDictionary<string,EnbResolvedW3dResource>? prototypes=null)
    {
        var h=resource.HierarchicalLods.SingleOrDefault();var tree=resource.Hierarchies.SingleOrDefault();
        if(aggregate.Attachments.Count <1||
            h is null||tree is null||h.Levels.Count<1||
            h.HasAggregateExtension||!h.HierarchyName.Equals(tree.Name,StringComparison.OrdinalIgnoreCase)||
            h.Levels.SelectMany(l=>l.Members).Any(m=>m.ReferenceIndex>=tree.Pivots.Count ||
                (!EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(m.Name) &&
                 resource.Meshes.Count(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,m.Name))!=1))||
            resource.Meshes.Any(mesh=>!h.Levels.SelectMany(l=>l.Members).Any(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,m.Name)))||
            (animation is not null&&animationFlags is null))
            throw new InvalidDataException("Sphere assembly requires a complete mesh HLOD with supported animation");
        resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,assets);
        var spheres=new List<EnbRetailSphereAttachment>();var rings=new List<EnbRetailRingAttachment>();
        var others=new List<EnbCurrentRetailW3dAggregateAttachment>();
        for(int ordinal=0;ordinal<aggregate.Attachments.Count;ordinal++)
        {
            var attachment=aggregate.Attachments[ordinal];
            if(prototypes?.TryGetValue(attachment.SubobjectName,out var selected)==true)
            {
                if((selected.CurrentRetailAggregate?.BaseRegisteredPrototype??selected.RegisteredPrototype)?.Kind==EnbW3dPrototypeKind.Sphere)
                    spheres.Add(EnbRetailSphereAttachment.Load(assets,tree,attachment,ordinal,source:selected));
                else if((selected.CurrentRetailAggregate?.BaseRegisteredPrototype??selected.RegisteredPrototype)?.Kind==EnbW3dPrototypeKind.Ring)
                    rings.Add(EnbRetailRingAttachment.Load(assets,tree,attachment,ordinal,source:selected));
                else others.Add(attachment);
                continue;
            }
            if(!assets.RetailMixResources!.TryReadResource(attachment.SubobjectName+".w3d",out var bytes,out _)||bytes.Length<8)
                throw new InvalidDataException("Sphere assembly child missing or truncated");
            switch(BinaryPrimitives.ReadUInt32LittleEndian(bytes))
            {
                case 0x741:spheres.Add(EnbRetailSphereAttachment.Load(assets,tree,attachment,ordinal));break;
                case 0x742:rings.Add(EnbRetailRingAttachment.Load(assets,tree,attachment,ordinal));break;
                default:others.Add(attachment);break;
            }
        }
        if(spheres.Count==0)throw new InvalidDataException("Sphere assembly requires sphere children");
        var emitters=others.Count==0?null:EnbCurrentRetailMixedEffectAsset.Load(assets,resource,
            aggregate with {Attachments=others},animation,animationFlags,prototypes);
        return new(emitters?.Resource??resource,animation,animationFlags,spheres.AsReadOnly(),rings.AsReadOnly(),emitters);
    }
}
