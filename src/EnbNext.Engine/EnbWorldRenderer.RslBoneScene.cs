using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private uint _boneSceneTime;
    private readonly List<string> _boneSceneErrors=[];
    private sealed record BoneSceneMembership(LiveRslBoneNode[][] Levels,LiveRslBoneNode[] Aggregate);
    private sealed record BoneSceneCheckpoint(int Position,int EffectiveActionCount,int? PacketOrdinal,long Sequence,
        Dictionary<LiveRslBoneNode,BoneSceneMembership> Membership);

    private static BoneSceneMembership BoneMembership(LiveRslBoneNode node,
        IReadOnlyDictionary<EnbCurrentRetailRslBoneRenderObjectRuntime,LiveRslBoneNode>? states=null,bool initial=false)
    {
        LiveRslBoneNode[] Resolve(IEnumerable<EnbCurrentRetailRslBoneMember> members)
            =>members.Select(member=>node.RetainedChildren.GetValueOrDefault(member.Child)??
                node.Children.GetValueOrDefault(member.Child)?.Node??
                (member.Child is EnbCurrentRetailRslBoneRenderObjectRuntime runtime
                    ?states?.GetValueOrDefault(runtime)??node.DynamicChildren.GetValueOrDefault(runtime):null))
                .OfType<LiveRslBoneNode>().ToArray();
        return new((initial?node.InitialLevels:node.Runtime.Levels).Select(Resolve).ToArray(),
            Resolve(initial?node.InitialAggregate:node.Runtime.Aggregate));
    }

    private static Dictionary<LiveRslBoneNode,BoneSceneMembership> CaptureBoneMembership(LiveRslBoneOwner owner)
    {
        var states=owner.Nodes.Values.ToDictionary(node=>node.Runtime);
        var result=new Dictionary<LiveRslBoneNode,BoneSceneMembership>(ReferenceEqualityComparer.Instance);
        void Visit(LiveRslBoneNode node)
        {
            if(result.ContainsKey(node))return;
            var membership=BoneMembership(node,states);result.Add(node,membership);
            foreach(var child in membership.Levels.SelectMany(level=>level).Concat(membership.Aggregate))Visit(child);
        }
        foreach(var node in owner.Nodes.Values)Visit(node);
        return result;
    }

    private void InitializeBoneScene(LegoSceneState scene,ulong identity,ulong generation)
    {
        if(!_rslBoneOwners.TryGetValue((identity,generation),out var owner)||scene.BoneEvents.ContainsKey(owner))return;
        scene.BoneEvents[owner]=owner.SceneHistory.Count;
        foreach(var pair in CaptureBoneMembership(owner))scene.BoneMembership[pair.Key]=pair.Value;
    }

    private void RefreshBoneSceneRoot(LegoPhysicalModel model,ulong identity,ulong generation,int state)
    {
        if(_rslBoneOwners.TryGetValue((identity,generation),out var owner)&&owner.Nodes.TryGetValue(state,out var node))
        {
            model.Root=node.Exported??node.Root with {RslBoneNode=node};
            node.SceneModel=model;
        }
    }

    private static void AddBoneSceneMutations(LegoSceneState scene,LiveRslBoneOwner owner,
        List<(int Position,long Order,long Sequence,Action Apply)> mutations)
    {
        for(int index=scene.BoneEvents.GetValueOrDefault(owner);index<owner.SceneHistory.Count;index++)
        {
            int consumed=index+1;
            var checkpoint=owner.SceneHistory[index];
            mutations.Add((checkpoint.Position,checkpoint.PacketOrdinal is int packet?2L*packet+1:long.MaxValue,
                checkpoint.Sequence,()=>
                {
                    foreach(var pair in checkpoint.Membership)scene.BoneMembership[pair.Key]=pair.Value;
                    scene.BoneEvents[owner]=consumed;
                }));
        }
    }

    private void SetModelEmitterSceneAttached(LegoPhysicalModel model,bool attached)
    {
        foreach(var (key,owner) in _retainedEmitters)
            if(ReferenceEquals(key.Factory,model.Root.PrimitiveFactoryIdentity)&&
                ReferenceEquals(RetainedEmitterSceneModel(key,owner),model))owner.State.SetSceneAttached(attached);
    }

    private void ApplyBoneSceneObservation(LegoSceneState scene,(ulong Identity,ulong Generation) source,
        EnbRslObjectState update,EnbCurrentRetailRslObjectSceneEvent observation)
    {
        //PrepareRetainedEmitters discards the old timeline on a clock rewind.
        //Do not replay new observations into those soon-to-be-discarded owners.
        if(_boneSceneTime<_retainedEmitterTime)return;
        foreach(var (key,emitter) in _retainedEmitters)
        {
            if((key.Owner,key.Generation)!=source||emitter.LastInstance?.Root.RslBoneNode is not {} node||
                emitter.SceneEventCount>observation.Ordinal)continue;
            if(observation.Kind is EnbCurrentRetailRslObjectSceneEventKind.Hidden or EnbCurrentRetailRslObjectSceneEventKind.BoneHidden &&
                BoneHiddenObservationApplies(node,source,observation))
            {
                int savedState=emitter.SceneStateId;
                var savedPacket=emitter.PacketPoseInstance;
                try
                {
                    var world=emitter.World;
                    if(!observation.Hidden&&emitter.State.SceneAttached&&!emitter.State.Started&&!emitter.State.AnimationHidden)
                    {
                        int? selected=scene.Object.RenderIdentity==source.Identity?scene.Selected:
                            scene.WrapperStates.FirstOrDefault(pair=>pair.Key.Object.RenderIdentity==source.Identity).Value;
                        if(selected is int current)emitter.SceneStateId=current;
                        if(observation.Phase==EnbCurrentRetailRslObjectSceneEventPhase.Packet)emitter.PacketPoseInstance=emitter.LastInstance;
                        if(!TryResolveRetainedEmitterEventWorld(key,emitter,update,observation,out world,out var error))
                            throw new InvalidDataException(error);
                    }
                    emitter.State.SetHidden(checked((uint)(_boneSceneTime-emitter.Start)),world,emitter.Scale,observation.Hidden);
                    emitter.State.UpdateTransform(emitter.World);
                }
                catch(InvalidDataException error)
                {
                    string message=$"Bone particle visibility: {error.Message}";
                    _boneSceneErrors.Add(message);_retainedEmitterErrors.Add(message);
                    continue;
                }
                finally {emitter.SceneStateId=savedState;emitter.PacketPoseInstance=savedPacket;}
            }
            emitter.SceneEventCount=observation.Ordinal+1;
        }
    }

    private bool BoneHiddenObservationApplies(LiveRslBoneNode child,(ulong Identity,ulong Generation) key,
        EnbCurrentRetailRslObjectSceneEvent observation)
    {
        if(!_rslBoneOwners.TryGetValue(key,out var owner)||!owner.Nodes.TryGetValue(observation.StateId,out var parent))return false;
        if(ReferenceEquals(child,parent))return true;
        if(parent.Hierarchy is null)return false;
        var capture=owner.SceneHistory.LastOrDefault(c=>c.Position<=observation.Ordinal&&c.EffectiveActionCount<=observation.EffectiveActionCount);
        var membership=capture?.Membership.GetValueOrDefault(parent)??BoneMembership(parent,initial:true);
        return membership.Aggregate.Any(node=>ReferenceEquals(node,child));
    }

    private bool IsRetainedBoneParticle(RetainedEmitterKey key,RetainedEmitterOwner emitter)
        =>emitter.LastInstance?.Root.RslBoneNode?.StateId is int state&&
            TryGetRetainedEmitterUpdate((key.Owner,key.Generation),emitter,out var update)&&
            update.Graph?.TryFindState(state,out var definition)==true&&
            definition.Definition.Type==EnbNext.Assets.EnbRslStateType.Bone;

    private void RetireBoneParticle(RetainedEmitterKey key,RetainedEmitterOwner emitter,long now)
    {
        if(emitter.LastInstance?.Root.RslBoneNode is not {} node||node.Runtime.Parent is not {} parent||
            !parent.RemoveChild(node.Runtime))return;
        //97DEF0 ->9662E0 invokes parent+6C.98E0F0 removes its scene
        //reference, but45EDC0's cached +30 still owns this RenderObj.
        //Do not destroy the emitter/buffer or blacklist its retained factory.
        emitter.State.SetSceneAttached(false);
        if(node.StateId is null)
        {
            //An authored child has no independent RSL +30 reference.
            _removedRetainedEmitters.Add(key);
            emitter.State.Destroy(checked((uint)(now-emitter.Start)),emitter.World,emitter.Scale);
        }
        if(!_rslBoneOwners.TryGetValue((key.Owner,key.Generation),out var owner)||
            !TryGetRetainedEmitterUpdate((key.Owner,key.Generation),emitter,out var update))return;
        var membership=CaptureBoneMembership(owner);
        owner.SceneHistory.Add(new(update.ObjectSceneEvents.Count,update.Actions.Count,null,
            Interlocked.Increment(ref _legoPhysicalOperationSequence),membership));
        var assets=_retailAssetsAccessor();
        foreach(var scene in _legoScenes.Values)
            if(scene.BoneEvents.ContainsKey(owner))
            {
                foreach(var pair in membership)scene.BoneMembership[pair.Key]=pair.Value;
                scene.BoneEvents[owner]=owner.SceneHistory.Count;
                if(assets is not null)RebuildLegoScene(scene,assets);
            }
    }
}
