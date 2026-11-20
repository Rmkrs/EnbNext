using System.Numerics;

namespace EnbNext.Rendering;

public sealed record EnbRObjModelAttachment(string Name,int PivotIndex,EnbCurrentRetailRObjEffectAsset Asset)
{
    public IReadOnlyList<EnbRObjModelAttachment> Ancestors {get;init;}=[];
    public int ParentModelIndex {get;init;}=-1;
    public int BoundsOrdinal {get;init;}=-1;
    public string? BoundsMemberName {get;init;}
    public int? SceneMemberOrdinal {get;init;}
    public int? LevelOrdinal {get;init;}
    //98E9A0 prepares only permanent children. A level-owned subtree keeps
    //its constructor detail; its integer cost is already in the parent level.
    public bool PreparesDetail=>LevelOrdinal is null && Ancestors.All(a=>a.LevelOrdinal is null);
    public bool IsSelected(IReadOnlyList<EnbRObjModelAttachment> models,Func<int,int> parentLevel)
    {
        var child=this;
        while(true)
        {
            if(child.LevelOrdinal is {} level && level!=parentLevel(child.ParentModelIndex))return false;
            if(child.ParentModelIndex<0)return true;
            child=models[child.ParentModelIndex];
        }
    }

    public static IReadOnlyList<EnbRObjModelAttachment> Flatten(IReadOnlyList<EnbRObjModelAttachment> children)
    {
        //0098DD50/0098E9A0 dispatch recursively in insertion order. Keep
        //the separate direct-child ordinal used by the bounds descriptor.
        var result=new List<EnbRObjModelAttachment>();
        for(int i=0;i<children.Count;i++)
        {
            var parent=children[i];
            int parentIndex=result.Count;
            result.Add(parent with {Asset=parent.Asset with {ModelAttachments=[]},BoundsOrdinal=i});
            var nested=parent.Asset.ModelAttachments;
            int start=result.Count;
            foreach(var child in nested)
                result.Add(child with {Ancestors=[parent,..child.Ancestors],
                    ParentModelIndex=child.ParentModelIndex<0?parentIndex:start+child.ParentModelIndex});
        }
        return result;
    }

    public static IReadOnlyList<EnbRObjModelAttachment> RebuildHierarchy(IReadOnlyList<EnbRObjModelAttachment> flattened)
    {
        var retained=flattened.ToArray();
        for(int i=retained.Length-1;i>=0;i--)
        {
            var children=Enumerable.Range(i+1,retained.Length-i-1).Where(j=>flattened[j].ParentModelIndex==i).Select(j=>retained[j]).ToArray();
            retained[i]=retained[i] with {Ancestors=[],ParentModelIndex=-1,Asset=retained[i].Asset with {ModelAttachments=children}};
        }
        return retained.Where((_,i)=>flattened[i].ParentModelIndex<0).ToArray();
    }
    internal static EnbW3dRenderResource BindBounds(EnbW3dRenderResource parent,IReadOnlyList<EnbRObjModelAttachment> children)
    {
        var hlod=parent.HierarchicalLods.Single();
        var members=new List<EnbW3dRenderHlodMember>(hlod.Aggregate?.Members??[]);
        var descriptors=parent.CompositeRenderChildren.ToList();
        for(int i=0;i<children.Count;i++)
        {
            var child=children[i];var resource=child.Asset.Resource!;
            if(child.Asset.IsNullObject)
            {
                //00941540/00941560: class16's fixed local sphere and box.
                string leafName=$"<0600:{hlod.Name}:{i}:{child.Name}>";
                descriptors.Add(new(leafName,0x16,new(new(0,0,0,.1f),Vector3.Zero,new(.1f)),0));
                members.Add(new(checked((uint)child.PivotIndex),leafName));continue;
            }
            if(child.Asset.StandaloneDazzle is {} dazzle)
            {
                string leafName=$"<0600:{hlod.Name}:{i}:{child.Name}>";
                float radius=dazzle.DazzleDefinition!.Radius*dazzle.DazzleInstanceScale;
                descriptors.Add(new(leafName,0x20,new(new(0,0,0,radius),Vector3.Zero,new(radius)),0)
                    {DazzleDefinitionRadius=dazzle.DazzleDefinition.Radius,DazzleInstanceScale=dazzle.DazzleInstanceScale});
                members.Add(new(checked((uint)child.PivotIndex),leafName));continue;
            }
            if(child.Asset.StandaloneBox is {} boxLeaf)
            {
                string leafName=$"<0600:{hlod.Name}:{i}:{child.Name}>";
                descriptors.Add(new(leafName,boxLeaf.RetailClassId,EnbCurrentRetailRenderBoundsStorage.BoxLocalBounds(boxLeaf.Center,boxLeaf.Extents),boxLeaf.IntegerRenderCost));
                members.Add(new(checked((uint)child.PivotIndex),leafName));continue;
            }
            if(child.Asset.SpecialMaterial is {} special)
            {
                string specialName=$"<0600:{hlod.Name}:{i}:{child.Name}>";
                descriptors.Add(new(specialName,0x24,new(new(special.SphereCenter,special.SphereRadius),
                    (special.Minimum+special.Maximum)*.5f,(special.Maximum-special.Minimum)*.5f),
                    special.Members.Aggregate(0,(sum,m)=>unchecked(sum+(int)m.TriangleCount))));
                members.Add(new(checked((uint)child.PivotIndex),specialName));continue;
            }
            if(resource.HierarchicalLods.Count==0 && resource.HierarchyModels.Count==0 && resource.Meshes.Count==1)
            {
                //009A3A50 attaches concrete MeshClass too. Its bounds remain
                //the authored mesh bounds, not a manufactured HLOD sphere.
                var mesh=resource.Meshes[0];
                if(!EnbW3dHlodBudgetCompiler.TryResolveNamedRenderObjectCost(resource,EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh),out int cost,out var meshError))
                    throw new InvalidDataException(meshError);
                var box=EnbCurrentRetailRenderBoundsStorage.MeshLocalBox(mesh.BoundingBoxMin,mesh.BoundingBoxMax);
                string meshName=$"<0600:{hlod.Name}:{i}:{child.Name}>";
                descriptors.Add(new(meshName,0,new(new(mesh.BoundingSphereCenter,mesh.BoundingRadius),box.Center,box.Extent),cost)
                    {MeshMinimum=mesh.BoundingBoxMin,MeshMaximum=mesh.BoundingBoxMax});
                members.Add(new(checked((uint)child.PivotIndex),meshName));continue;
            }
            var childHlod=resource.HierarchicalLods.Single();
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,childHlod,Matrix4x4.Identity,null,0,out var bounds,out var error) ||
                !EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,childHlod,_=>true,out var costs,out error))
                throw new InvalidDataException(error);
            //Separate pointer identity avoids shadowing a same-named base mesh.
            string name=$"<0600:{hlod.Name}:{i}:{child.Name}>";
            int childCost=costs[EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(childHlod)];
            //0098DCA0 includes permanent children in the object's integer cost.
            foreach(var member in childHlod.Aggregate?.Members??[])
            {
                if(!EnbW3dHlodBudgetCompiler.TryResolveNamedRenderObjectCost(resource,member.Name,out int permanentCost,out error))
                    throw new InvalidDataException(error);
                childCost=unchecked(childCost+permanentCost);
            }
            descriptors.Add(new(name,0x19,new(new(bounds.LocalSphere.Center,bounds.LocalSphere.Radius),bounds.LocalBox.Center,bounds.LocalBox.Extents),
                childCost){HlodResource=resource,Hlod=childHlod});
            members.Add(new(checked((uint)child.PivotIndex),name));
        }
        return parent.Copy().SetHierarchicalLods([hlod with {Aggregate=new(hlod.Aggregate?.RawValue??0,members)}]).SetCompositeRenderChildren(descriptors);
    }

    public static void Validate(EnbCurrentRetailRObjEffectAsset child)
    {
        if(child.IsNullObject || child.SpecialMaterial is not null || child.StandaloneDazzle is not null || child.StandaloneBox is not null)return;
        bool directMesh=child.Resource is {HierarchicalLods.Count:0,HierarchyModels.Count:0,Meshes.Count:1};
        if(child.Resource is not {} r || (!directMesh && (r.HierarchicalLods.Count!=1 || r.Hierarchies.Count!=1)) ||
            r.HierarchyModels.Count!=0 || r.ExternalRenderObjects.Any(e=>e.DazzleDefinition is null && e.ParticleEmitterDefinition is null &&
                e.BoxDefinition is null && e.SpecialMaterialDefinition is null && e.Kind!=EnbW3dExternalRenderObjectKind.NullObject) ||
            child.Animation is not null || child.Sequence is not null ||
            child.Particle is not null || child.AggregateParticles is not null || child.Mixed is not null ||
            child.Rings is not null || child.Spheres is not null || child.StandaloneStreak is not null || child.ObjectLine is not null)
            throw new InvalidDataException("Effect model attachment requires a static mesh or local mesh HLOD");
    }

    public (Matrix4x4 World,bool Visible) Pose(EnbCurrentRetailRObjEffectAsset parent,Matrix4x4 world,uint start,uint now,float? storedTranslationScale=null,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
    {
        var (pose,ancestorsVisible)=EvaluateParent(parent,world,start,now,storedTranslationScale,observed);
        if(PivotIndex<0 || PivotIndex>=pose.WorldTransforms.Length)
            throw new InvalidDataException("Effect model attachment pivot out of range");
        return (pose.WorldTransforms[PivotIndex],ancestorsVisible && pose.Visibility[PivotIndex]);
    }

    public EnbW3dHierarchyPoseOwner ParentPose(EnbCurrentRetailRObjEffectAsset parent,Matrix4x4 world,uint start,uint now,float? storedTranslationScale=null,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null)
        =>EvaluateParent(parent,world,start,now,storedTranslationScale,observed).Pose;

    private (EnbW3dHierarchyPoseOwner Pose,bool Visible) EvaluateParent(EnbCurrentRetailRObjEffectAsset parent,Matrix4x4 world,uint start,uint now,
        float? storedTranslationScale,EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed)
    {
        bool visible=true;
        foreach(var ancestor in Ancestors)
        {
            var pose=EvaluatePose(parent,world,start,now,storedTranslationScale,observed);
            if(ancestor.PivotIndex<0 || ancestor.PivotIndex>=pose.WorldTransforms.Length)
                throw new InvalidDataException("Effect ancestor attachment pivot out of range");
            world=pose.WorldTransforms[ancestor.PivotIndex];visible &= pose.Visibility[ancestor.PivotIndex];
            parent=ancestor.Asset;observed=null;
        }
        return (EvaluatePose(parent,world,start,now,storedTranslationScale,observed),visible);
    }

    private static EnbW3dHierarchyPoseOwner EvaluatePose(EnbCurrentRetailRObjEffectAsset parent,Matrix4x4 world,uint start,uint now,
        float? storedTranslationScale,EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed)
    {
        var resource=parent.Resource!;
        float frame=0;string? error=null;
        var animation=parent.Animation;
        if(parent.Sequence is not null)
        {
            if(observed is not {} sample)throw new InvalidDataException("Model attachment pose requires an animation observation");
            animation=sample.BoundAnimation;frame=sample.Frame;
        }
        else if(animation is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(animation,parent.AnimationFlags!,start,now,out frame,out error))
            throw new InvalidDataException(error);
        if(!EnbW3dHierarchyPose.TryEvaluate(resource.Hierarchies.Single(),world,
            animation,frame,null,out var pose,out error,storedTranslationScale:storedTranslationScale))
            throw new InvalidDataException(error);
        return pose;
    }
}
