using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public static partial class EnbW3dDrawPacketCompiler
{
    // Ordinary hierarchy membership belongs to the occurrence. Poses, pivot
    // visibility and skin values remain invocation inputs. Unusual external
    // children and malformed bindings continue through the diagnostic compiler.
    internal sealed class PreparedTraversal
    {
        internal readonly EnbW3dRenderResource Source;
        internal readonly EnbW3dHlodSelection? Selection;
        internal readonly ulong Identity, Generation;
        internal readonly uint GameId;
        internal readonly EnbRenderPassKind Pass;
        internal readonly TraversalStep[] Steps;
        internal EnbW3dMeshOccurrencePreparation.HierarchyDrawOwner[]? PoseOwners;
        private object? _animations;
        private readonly Dictionary<string,(EnbW3dRenderHierarchyAnimation Animation,int Count)> _animationNames=new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,(EnbW3dRenderHierarchyAnimation Animation,int Count)> _animationRegistryNames=new(StringComparer.OrdinalIgnoreCase);

        internal PreparedTraversal(EnbW3dRenderResource source,EnbW3dHlodSelection? selection,
            IEnbRenderObject owner,EnbRenderPassKind pass,TraversalStep[] steps)
        {Source=source;Selection=selection;Identity=owner.RenderIdentity;Generation=owner.VisualGeneration;GameId=owner.GameId;Pass=pass;Steps=steps;}

        internal bool Matches(EnbW3dRenderResource source,EnbW3dHlodSelection? selection,
            IEnbRenderObject owner,EnbRenderPassKind pass)
            => Selection==selection&&Identity==owner.RenderIdentity&&Generation==owner.VisualGeneration&&GameId==owner.GameId&&Pass==pass&&
                Source.Key==source.Key&&ReferenceEquals(Source.Meshes,source.Meshes)&&ReferenceEquals(Source.Hierarchies,source.Hierarchies)&&
                ReferenceEquals(Source.HierarchyModels,source.HierarchyModels)&&ReferenceEquals(Source.HierarchicalLods,source.HierarchicalLods)&&
                ReferenceEquals(Source.ExternalRenderObjects,source.ExternalRenderObjects)&&ReferenceEquals(Source.CompositeRenderChildren,source.CompositeRenderChildren);

        internal bool ResolveAnimations(EnbW3dRenderResource source,IReadOnlyList<EnbW3dWeightedAnimationSample> requested,
            List<EnbW3dResolvedWeightedAnimationSample> result,out string? diagnostic)
        {
            diagnostic=null;
            if(!ReferenceEquals(_animations,source.Animations))
            {
                _animationNames.Clear();_animationRegistryNames.Clear();
                foreach(var animation in source.Animations)
                {
                    if(_animationNames.TryGetValue(animation.Name,out var prior))_animationNames[animation.Name]=(prior.Animation,prior.Count+1);
                    else _animationNames.Add(animation.Name,(animation,1));
                    string registry=animation.HierarchyName+"."+animation.Name;
                    if(_animationRegistryNames.TryGetValue(registry,out var priorRegistry))_animationRegistryNames[registry]=(priorRegistry.Animation,priorRegistry.Count+1);
                    else _animationRegistryNames.Add(registry,(animation,1));
                }
                _animations=source.Animations;
            }
            foreach(var sample in requested)
            {
                var names=sample.HierarchyName is null?_animationNames:_animationRegistryNames;
                if(!names.TryGetValue(sample.HierarchyName is null?sample.Name:sample.HierarchyName+"."+sample.Name,out var selected)||selected.Count!=1)
                {
                    diagnostic=selected.Count==0?$"W3D animation '{sample.Name}' was not found in '{source.LogicalFileName}'.":
                        $"W3D animation '{sample.Name}' is ambiguous in '{source.LogicalFileName}' ({selected.Count} matches).";
                    return false;
                }
                if(!float.IsFinite(sample.Frame)||sample.Frame<=int.MinValue)
                {diagnostic=$"W3D animation '{sample.Name}' frame {sample.Frame} is not a finite samplable frame.";return false;}
                if(!float.IsFinite(sample.Weight)||sample.Weight<0f)
                {diagnostic=$"W3D animation '{sample.Name}' has invalid blend weight {sample.Weight}.";return false;}
                result.Add(new(selected.Animation,sample.Frame,sample.Weight));
            }
            return true;
        }
    }

    internal sealed class TraversalStep(EnbW3dRenderMesh mesh,EnbW3dRenderHierarchy? hierarchy,int pivot,string name)
    {
        internal readonly EnbW3dRenderMesh Mesh=mesh;
        internal readonly EnbW3dRenderHierarchy? Hierarchy=hierarchy;
        internal readonly int Pivot=pivot;
        internal readonly string Name=name;
        internal EnbW3dMeshOccurrenceIdentity Occurrence=null!;
        internal EnbW3dDrawTask[] Definitions=[];
        internal EnbW3dMeshOccurrencePreparation.HierarchyDrawOwner? PoseOwner;
    }

    private static PreparedTraversal? PrepareTraversal(EnbW3dRenderResource resource,EnbW3dHlodSelection? selection,
        IEnbRenderObject owner,EnbRenderPassKind pass)
    {
        if(resource.ExternalRenderObjects.Count!=0)return null;
        var bindings=MeshBindings.GetValue(resource.Meshes,static _=>new AuthoredBindingOwner()).Get(resource);
        EnbW3dRenderHlod? hlod=null;
        HlodMemberBindings? level=null;
        if(selection is {} selected)
        {
            hlod=resource.HierarchicalLods.FirstOrDefault(h=>string.Equals(h.Name,selected.Name,StringComparison.OrdinalIgnoreCase));
            var sourceLevel=hlod?.Levels.FirstOrDefault(l=>l.Ordinal==selected.LevelOrdinal);
            if(hlod is null||sourceLevel is null)return null;
            level=bindings.GetLevel(resource,hlod,sourceLevel);
        }
        var steps=new List<TraversalStep>();
        foreach(var mesh in resource.Meshes)
        {
            var nodes=bindings.Nodes[mesh];
            if(nodes.IsEmpty)
            {
                if(hlod is null){steps.Add(new(mesh,null,-1,mesh.Name));continue;}
                foreach(var member in level!.MeshMembers[mesh])
                {
                    var hierarchy=resource.Hierarchies.FirstOrDefault(h=>string.Equals(h.Name,hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
                    if(hierarchy is null||member.ReferenceIndex>=hierarchy.Pivots.Count)return null;
                    steps.Add(new(mesh,hierarchy,(int)member.ReferenceIndex,member.Name));
                }
            }
            else foreach(var (model,node) in nodes)
            {
                if(level is not null&&!level.Names.Contains(node.RenderObjectName)&&level.MeshMembers[mesh].IsEmpty)continue;
                var hierarchy=resource.Hierarchies.FirstOrDefault(h=>string.Equals(h.Name,model.HierarchyName,StringComparison.OrdinalIgnoreCase));
                if(hierarchy is null||node.PivotIndex<0||node.PivotIndex>=hierarchy.Pivots.Count)return null;
                steps.Add(new(mesh,hierarchy,node.PivotIndex,node.RenderObjectName));
            }
        }
        if(level is {Names.Count:>0,NoOpOnly:false}&&steps.Count==0)return null;
        var occurrences=new Dictionary<(int Mesh,int Pivot,string Name),int>();
        foreach(var step in steps)
        {
            var key=(step.Mesh.Ordinal,step.Pivot,step.Name);
            int repetition=occurrences.GetValueOrDefault(key);occurrences[key]=repetition+1;
            step.Occurrence=new(repetition);
        }
        return new(resource,selection,owner,pass,steps.ToArray());
    }

    private static bool? TryBuildPreparedTraversal(IEnbRenderObject owner,EnbW3dRenderResource resource,
        Matrix4x4 world,long time,EnbRenderPassKind pass,EnbW3dHlodSelection? selection,EnbW3dLightEnvironment lights,
        IReadOnlyList<EnbW3dWeightedAnimationSample> animations,EnbW3dHierarchyState? transforms,
        EnbW3dMeshOccurrencePreparation preparation,EnbW3dHierarchyPoseOwner? parentPose,
        out EnbW3dDrawInvocation list,out string? diagnostic,float renderScale)
    {
        list=null!;diagnostic=null;
        var plan=preparation.Traversal;
        if(plan is null||!plan.Matches(resource,selection,owner,pass))
        {
            plan=PrepareTraversal(resource,selection,owner,pass);
            preparation.Traversal=plan;
            if(plan is null)return null;
            preparation.TraversalPreparationCount++;
        }
        using var scope=preparation.Begin(resource);
        if(!plan.ResolveAnimations(resource,animations,preparation.FrameAnimations,out diagnostic))return false;
        if(plan.PoseOwners is null)
        {
            var owners=new List<EnbW3dMeshOccurrencePreparation.HierarchyDrawOwner>();
            foreach(var step in plan.Steps)if(step.Hierarchy is {} hierarchy)
            {
                step.PoseOwner=preparation.HierarchyOwner(hierarchy);
                if(!owners.Contains(step.PoseOwner))owners.Add(step.PoseOwner);
            }
            plan.PoseOwners=owners.ToArray();
        }
        foreach(var poseOwner in plan.PoseOwners)
            if(!poseOwner.Update(preparation,world,preparation.FrameAnimations,transforms,out diagnostic))return false;
        var invocation=EnbW3dDrawInvocation.Create(resource.Key,owner.RenderIdentity,owner.VisualGeneration,time,selection,
            animations.Count==1?new EnbW3dAnimationSample(animations[0].Name,animations[0].Frame):null);
        var packets=invocation.Packets;
        int ordinal=0;
        foreach(var step in plan.Steps)
        {
            var mesh=step.Mesh;
            var matrix=world;
            var pose=parentPose;
            float scale=renderScale;
            if(step.Hierarchy is {} hierarchy)
            {
                pose=step.PoseOwner!.Pose;
                if(!pose.Visibility[step.Pivot])continue;
                matrix=pose.WorldTransforms[step.Pivot];
                if(transforms is not null&&string.Equals(transforms.HierarchyName,hierarchy.Name,StringComparison.OrdinalIgnoreCase))scale=transforms.RenderObjectScale;
            }
            EnbW3dDeformedGeometry? geometry=null;
            if(mesh.BoneIndices.Count!=0&&pose is not null&&!preparation.TryDeform(mesh,pose,out geometry,out diagnostic))return false;
            if(!TryCreateBounds(mesh.BoundingSphereCenter,mesh.BoundingRadius,matrix,out var bounds))
            {diagnostic=$"Mesh '{mesh.Name}' produced invalid world culling bounds.";return false;}
            if(step.Definitions.Length==0&&mesh.MaterialPasses.Count!=0)
            {
                step.Definitions=new EnbW3dDrawTask[mesh.MaterialPasses.Count];
                for(int i=0;i<step.Definitions.Length;i++)
                {
                    var material=mesh.MaterialPasses[i];
                    var order=new EnbW3dDrawOrderKey(pass,ClassifyBlend(material),mesh.StaticSort,resource.Key,mesh.Ordinal,material.Ordinal,owner.RenderIdentity,ordinal+i);
                    var definition=new EnbW3dDrawTask(order,owner.RenderIdentity,owner.VisualGeneration,owner.GameId,Matrix4x4.Identity,default,mesh,null,
                        OwnerCache.Get(ref material.DerivedDrawMaterial,material,static source=>new EnbW3dDrawMaterialBinding(source,source.SingleBindingSemantics)),EnbW3dLightEnvironment.BlackNoLights,0,
                        step.Hierarchy?.Name,step.Pivot,step.Name){RenderInstancePath=preparation.InstancePath,MeshOccurrence=step.Occurrence};
                    definition.RetainDefinition();step.Definitions[i]=definition;preparation.PreparedDefinitionCreated();
                }
            }
            EnbW3dDrawTask? meshFrame=null;
            for(int i=0;i<step.Definitions.Length;i++)
            {
                var definition=step.Definitions[i];
                if(definition.OrderKey.BindingOrdinal!=ordinal)
                    step.Definitions[i]=definition=(definition).ForkTask().SetOrderKey(definition.OrderKey with {BindingOrdinal=ordinal});
                definition.RetainDefinition();
                var packet=meshFrame is null
                    ?definition.SnapshotFrame(geometry is null?matrix:Matrix4x4.Identity,bounds,geometry,lights,time,scale)
                    :definition.SnapshotFrameFrom(meshFrame);
                meshFrame??=packet;
                packets.Add(packet);
                ordinal++;
            }
        }
        preparation.TraversalUseCount++;
        list=invocation;
        return true;
    }
}
