using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private bool TryResolveRetainedEmitterEventWorld(RetainedEmitterKey key,RetainedEmitterOwner owner,
        EnbRslObjectState update,EnbCurrentRetailRslObjectSceneEvent observation,
        out Matrix4x4 world,out string? diagnostic)
    {
        world=default;diagnostic=null;
        try
        {
        var retained=observation.Phase==EnbCurrentRetailRslObjectSceneEventPhase.Packet
            ?owner.PacketPoseInstance??owner.LastInstance:owner.Instance??owner.LastInstance;
        if(retained is null){diagnostic="Particle event has no retained owning transform.";return false;}
        var obj=retained.Object;
        if(owner.ExternalPhysicalLego && owner.SceneWrapperOwner is {} binding)
            for(var parent=retained.Parent;parent is not null;parent=parent.Parent)
                if(parent.IsRsl && parent.Object.RenderIdentity==binding.Identity)
                {obj=parent.Object;break;}
        var graph=observation.ResourceGraph;
        if(graph is null || !graph.TryFindState(owner.ExternalPhysicalLego?observation.StateId:owner.SceneStateId,out var objectState))
        {diagnostic="Particle event has no retained Object resource graph.";return false;}
        var assets=_retailAssetsAccessor();
        if(assets is null){diagnostic="Particle event assets unavailable.";return false;}
        if(!TryMaterializeRslObjectRoot(obj.BaseAsset,owner.ExternalPhysicalLego?observation.StateId:owner.SceneStateId,objectState,assets,out var root,out diagnostic))return false;
        //45EDC0 returns the live state's +30 object, after45F280 configured
        //replacement. Setter callbacks must sample that same instance tree.
        root=ResolveEffectRslRoot(obj,owner.ExternalPhysicalLego?observation.StateId:owner.SceneStateId,root);

        //Reconstruct the actual Bone action prefix into separate runtime nodes.
        //The live owner graph and its cached exported roots are never mutated.
        if(graph.States.Any(s=>s.Definition.Type==EnbRslStateType.Bone))
        {
            int? current=null;
            foreach(var previous in update.ObjectSceneEvents.Take(observation.Ordinal))
                if(previous.Kind==EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent)current=previous.StateId;
            var isolated=new LiveRslBoneOwner();
            ConsumeRslBoneActionsCore(obj,graph,assets,update,isolated,observation.EffectiveActionCount,current);
            if(isolated.Diagnostic is {} failure){diagnostic=failure;return false;}
            if(!isolated.Nodes.TryGetValue(owner.SceneStateId,out var node))
            {
                node=BuildRslBoneNode(root);
                isolated.AddNode(owner.SceneStateId,node);
            }
            //The native setter changes its flag before 0097F390 asks for the pose.
            if(observation.StateId==owner.SceneStateId)node.Runtime.SetHidden(observation.Hidden);
            root=ExportRslBoneNode(node,isolated.NodesByRuntime);
        }

        var bindings=new List<LiveRslAnimationBinding>();
        foreach(int id in observation.ActiveListStateIds)
        {
            if(!observation.AnimationClips.TryGetValue(id,out var clip) ||
                !observation.AnimationObjectStateIds.TryGetValue(id,out var boundObject) || boundObject!=(owner.ExternalPhysicalLego?observation.StateId:owner.SceneStateId))continue;
            if(!observation.AnimationFrames.TryGetValue(id,out float frame))
            {diagnostic=$"Particle setter animation {id} has no captured frame.";return false;}
            //45AD90 never installs authored PIVOTMAP in the animation blend
            //record. Setter-time poses use the same unmasked459FE0 submission.
            bindings.Add(new(id,clip.Name,"retained event",observation.AnimationActivationTicks.GetValueOrDefault(id),
                clip,new(clip.Name,frame),root.Resource,observation.AnimationWeights.GetValueOrDefault(id,1f)));
        }
        var staged=new RootTaskList();
        if(!TryAppendRootInstanceTree(obj,root,true,staged,CreateRslAnimationBlend(root,bindings),out diagnostic))return false;

        if(owner.ExternalPhysicalLego)
            return TryResolvePhysicalLegoEmitterEventWorld(key,owner,observation,staged.Single(i=>i.InstancePath=="root"),retained,out world,out diagnostic);

        //Non-RSL hierarchy overrides are retained inputs at the packet/controller
        //phase too. Match the concrete ancestor factory, not a previous child pose.
        for(var original=retained;original is not null;original=original.Parent)
        {
            if(original.HierarchyPostTransforms is not {} post)continue;
            foreach(var instance in staged.Where(i=>ReferenceEquals(i.Root.PrimitiveFactoryIdentity,original.Root.PrimitiveFactoryIdentity)))
                instance.HierarchyPostTransforms=post;
        }
        var matching=staged.Where(i=>i.Root.RetainedEmitter is not null &&
            ReferenceEquals(i.Root.PrimitiveFactoryIdentity,key.Factory)).ToArray();
        var selected=matching.FirstOrDefault(i=>RetainedEmitterOwnerKey(i,owner.Scale,key.Hsv).Path==key.Path);
        if(selected is null && matching.Length==1)selected=matching[0];
        if(selected is null)
        {diagnostic="Particle event hierarchy does not identify the retained factory occurrence.";return false;}
        if(!TryRefreshOrdinaryHlodChild(selected,out diagnostic))return false;
        if(!TryResolvePrimitiveFactoryTransform(selected,out world,out _,out _))
        {diagnostic="Particle event hierarchy transform unavailable.";return false;}
        return true;
        }
        catch(Exception ex) when(ex is InvalidDataException or NotSupportedException)
        {
            diagnostic=ex.Message;
            return false;
        }
    }
}
