using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record LegoSceneNode(LegoPhysicalObject Value,LegoSceneNode[] Children);
    private sealed class LegoSceneState
    {
        public required IEnbRenderObject Object;
        public EnbRslObjectState? Update;
        public int? Selected;
        public int EventCount;
        public long Sequence;
        public LegoSceneNode? Tree;
        public bool Dirty=true,PendingFactory;
        public bool SourceOwned;
        public readonly HashSet<LegoPhysicalModel> Dependencies=[];
        public readonly List<(LegoRslOwner Wrapper,int State,LegoPhysicalModel Model)> WrapperModels=[];
        public readonly System.Runtime.CompilerServices.ConditionalWeakTable<LegoPhysicalObject,LegoSceneNodeCache> Nodes=new();
        public readonly Dictionary<LegoPhysicalModel,LegoPhysicalCheckpoint> Checkpoints=[];
        public readonly Dictionary<LegoRslOwner,int?> WrapperStates=[];
        public readonly Dictionary<LegoRslOwner,int> WrapperEvents=[];
        public readonly Dictionary<LiveRslBoneOwner,int> BoneEvents=[];
        public readonly Dictionary<LiveRslBoneNode,BoneSceneMembership> BoneMembership=new(ReferenceEqualityComparer.Instance);
    }
    private sealed class LegoSceneNodeCache { public LegoSceneNode? Node; }
    private readonly Dictionary<(ulong Identity,ulong Generation),LegoSceneState> _legoScenes=[];

    private readonly HashSet<(ulong Identity,ulong Generation)> _compatLegoScenes=[];
    private void DetachOwnedLegoScene(SceneOwner owner)
    {
        if(owner.LegoScene is not {} scene)return;
        owner.LegoScene=null;
        _legoScenes.Remove((scene.Object.RenderIdentity,scene.Object.RenderObjectGeneration));
        ReleaseLegoSceneDependencies(scene);ChangeLegoSceneTree(scene.Tree,null);
    }
    private void ClearLegoScenes()
    {
        _legoScenes.Clear();_compatLegoScenes.Clear();
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)node.State.LegoScene=null;
    }
    private void RetainWorldSceneModel(IEnbRenderObject obj,LiveRootInstance? instance,SceneOwner? source)
    {
        if(_shipExhaustOwner is not null||instance is null)return;
        using var ownerScope=new SceneOwnerScope(this,source);
        var root=instance.Root;
        var owner=RetainLegoPhysicalOwner(obj);
        //The concrete RenderObj exists even without a Lego controller.
        //Reuse the same model owner if a controller is subsequently created.
        int slot=instance.IsRsl?root.RslFactoryStateId:-1;
        var model=source is not null&&source.WorldSceneSlot==slot?source.WorldSceneModel:null;
        if((model is null&&!owner.RootModels.TryGetValue(slot,out model))||
            !ReferenceEquals(model.Root.PrimitiveFactoryIdentity,root.PrimitiveFactoryIdentity))
            {owner.RootModels[slot]=model=new(root);NotifyOwnedLegoMembership(obj.RenderIdentity);}
        if(source is not null){source.WorldSceneModel=model;source.WorldSceneSlot=slot;}
        model.Root=root;model.SceneInstance=instance;
        if(root.RslBoneNode is {} bone)bone.SceneModel=model;
    }

    private void ChangeLegoSceneTree(LegoSceneNode? before,LegoSceneNode? after)
    {
        if(ReferenceEquals(before,after))return;
        //461650 removes the selected model first;461600 registers the wrapper
        //first. These calls mutate the same9661C0 list that is being visited.
        void Remove(LegoSceneNode node)
        {
            foreach(var child in node.Children)Remove(child);
            if(node.Value is LegoPhysicalModel {Root.RetainedEmitter:not null} emitter)
            {
                SetModelEmitterSceneAttached(emitter,false);
                _retainedEmitterCallbacks.RemoveReservation(emitter);
            }
            if(node.Value is LegoRslOwner wrapper)
            {
                var key=(wrapper.Object.RenderIdentity,wrapper.Object.RenderObjectGeneration);
                _ordinaryRslCallbacks.Remove(key);
                _ordinaryRslCallbackMembers.Remove(key);_compatRslCallbackMembers.Remove(key);
            }
        }
        void Add(LegoSceneNode node)
        {
            if(node.Value is LegoPhysicalModel {Root.RetainedEmitter:not null} emitter)
            {
                _retainedEmitterCallbacks.Reserve(emitter);
                SetModelEmitterSceneAttached(emitter,true);
            }
            if(node.Value is LegoRslOwner wrapper)
            {
                var key=(wrapper.Object.RenderIdentity,wrapper.Object.RenderObjectGeneration);
                if(_ordinaryRslCallbackMembers.Add(key)){_ordinaryRslCallbacks.Register(key);_compatRslCallbackMembers.Add(key);}
            }
            foreach(var child in node.Children)Add(child);
        }
        if(before is null){if(after is not null)Add(after);return;}
        if(after is null){Remove(before);return;}
        if(!ReferenceEquals(before.Value,after.Value)){Remove(before);Add(after);return;}
        foreach(var child in before.Children)
            if(!after.Children.Any(n=>ReferenceEquals(n.Value,child.Value)))Remove(child);
        foreach(var child in after.Children)
        {
            var old=before.Children.FirstOrDefault(n=>ReferenceEquals(n.Value,child.Value));
            if(old is null)Add(child);else ChangeLegoSceneTree(old,child);
        }
    }

    private static void MergeLegoSceneChild(LegoSceneNode? previous,ref int count,
        ref List<LegoSceneNode>? changed,LegoSceneNode next)
    {
        if(changed is null && (previous is null || count>=previous.Children.Length ||
            !ReferenceEquals(previous.Children[count],next)))
        {
            changed=[];
            if(previous is not null)
                for(int i=0;i<count;i++)changed.Add(previous.Children[i]);
        }
        changed?.Add(next);count++;
    }

    private long _legoSceneBuilds,_legoSceneReuses;
    private static void ReleaseLegoSceneDependencies(LegoSceneState scene)
    {
        foreach(var model in scene.Dependencies)model.SceneOwners.Remove(scene);
        scene.Dependencies.Clear();scene.WrapperModels.Clear();
    }
    private void InvalidateLegoSceneOwner(ulong identity)
    {
        var owner=_shipExhaustOwner??this;
        foreach(var scene in owner._legoScenes.Values)
            if(scene.Object.RenderIdentity==identity||scene.WrapperModels.Any(item=>item.Wrapper.Object.RenderIdentity==identity))scene.Dirty=true;
    }
    private void RebuildLegoScene(LegoSceneState scene,EnbRetailAssetBundle assets,bool allowRetained=false)
    {
        if(allowRetained&&scene.SourceOwned&&!scene.Dirty&&!scene.PendingFactory){_legoSceneReuses++;return;}
        var root=ResolveLegoSceneRootModel(scene.Object,scene.Selected,scene.Update,assets);
        if(allowRetained&&!scene.Dirty&&!scene.PendingFactory&&scene.Tree is {} tree&&ReferenceEquals(tree.Value,root))
        {
            foreach(var item in scene.WrapperModels)
            {
                if(!item.Wrapper.Models.TryGetValue(item.State,out var model)||!ReferenceEquals(model,item.Model))
                {scene.Dirty=true;break;}
                RefreshBoneSceneRoot(model,item.Wrapper.Object.RenderIdentity,item.Wrapper.Object.RenderObjectGeneration,item.State);
            }
            foreach(var model in scene.Dependencies)
                if(model.Root.RslBoneNode is {} bone)
                {
                    if(!ReferenceEquals(bone.SceneModel,model)){scene.Dirty=true;break;}
                    model.Root=bone.Exported??bone.Root with {RslBoneNode=bone};
                }
            if(!scene.Dirty){_legoSceneReuses++;return;}
        }
        _legoSceneBuilds++;
        ReleaseLegoSceneDependencies(scene);
        scene.PendingFactory=false;

        LegoSceneNode Build(LegoPhysicalObject value)
        {
            // A scene owns membership snapshots, not frame poses. Preserve the
            // exact child order and publish a new node only on membership change.
            var cache=scene.Nodes.GetValue(value,static _=>new());
            var previous=cache.Node;
            int count=0;
            List<LegoSceneNode>? changed=null;
            if(value is LegoRslOwner wrapper)
            {
                InitializeBoneScene(scene,wrapper.Object.RenderIdentity,wrapper.Object.RenderObjectGeneration);
                if(!scene.WrapperStates.TryGetValue(wrapper,out var state))
                {
                    scene.WrapperStates[wrapper]=state=wrapper.Update?.CurrentObjectStateId;
                    scene.WrapperEvents[wrapper]=wrapper.Update?.ObjectSceneEvents.Count??0;
                }
                if(state is int id)
                {
                    if(ResolveLegoWrapperModel(wrapper,id,assets) is {} model)
                    {
                        scene.WrapperModels.Add((wrapper,id,model));
                        MergeLegoSceneChild(previous,ref count,ref changed,Build(model));
                    }
                    else scene.PendingFactory=true;
                }
            }
            else
            {
                var parent=(LegoPhysicalModel)value;
                if(scene.Dependencies.Add(parent))parent.SceneOwners.Add(scene);
                foreach(var child in AuthoredLegoSceneChildren(parent,scene))MergeLegoSceneChild(previous,ref count,ref changed,Build(child));
                if(scene.Checkpoints.TryGetValue(parent,out var checkpoint))
                    foreach(var child in checkpoint.Children)MergeLegoSceneChild(previous,ref count,ref changed,Build(child));
                else if(parent.History.Count==0)
                    foreach(var child in parent.Children)MergeLegoSceneChild(previous,ref count,ref changed,Build(child));
            }
            if(changed is null && previous is not null && count==previous.Children.Length)return previous;
            var children=changed is not null?changed.ToArray():
                count==0?Array.Empty<LegoSceneNode>():previous!.Children[..count];
            return cache.Node=new(value,children);
        }
        var next=root is null?null:Build(root);
        scene.Dirty=false;
        ChangeLegoSceneTree(scene.Tree,next);
        scene.Tree=next;
    }

    private IEnumerable<LegoPhysicalModel> LegoSceneModels(ulong identity)
    {
        if(_legoPhysicalOwners.TryGetValue(identity,out var owner))
        {
            foreach(var model in owner.RootModels.Values)yield return model;
            foreach(var model in owner.ChildModels.Values)yield return model;
        }
        foreach(var pair in LegoRslOwnersFor(identity))
            foreach(var model in pair.Value.Models.Values)yield return model;
    }

    private void SynchronizeLegoScene(IEnbRenderObject obj,EnbRslObjectState? update,
        EnbRetailAssetBundle assets,bool attached,SceneOwner? owner=null)
    {
        if(owner is not null&&!attached){DetachOwnedLegoScene(owner);return;}
        if(owner?.LegoScene is {Dirty:false,PendingFactory:false} retained)
        {
            // Source/RSL/physical-model writes dirty this scene at mutation time.
            // Poses still advance independently; no history or model walk is needed.
            retained.Object=obj;retained.Update=update;_legoSceneReuses++;return;
        }
        var key=(obj.RenderIdentity,obj.RenderObjectGeneration);
        if(!attached)
        {
            _compatLegoScenes.Remove(key);
            if(_legoScenes.Remove(key,out var removed)){ReleaseLegoSceneDependencies(removed);ChangeLegoSceneTree(removed.Tree,null);}
            return;
        }
        var scene=owner?.LegoScene;
        if(scene is null && (owner is not null || !_legoScenes.TryGetValue(key,out scene)))
        {
            scene=new(){Object=obj,Update=update,Selected=update?.CurrentObjectStateId,SourceOwned=owner is not null,
                EventCount=update?.ObjectSceneEvents.Count??0};
            foreach(var model in LegoSceneModels(obj.RenderIdentity))
                if(model.History.LastOrDefault() is {} checkpoint)
                {
                    scene.Checkpoints[model]=checkpoint;
                    scene.Sequence=Math.Max(scene.Sequence,checkpoint.Sequence);
                }
            _legoScenes.Add(key,scene);
            if(owner is not null)owner.LegoScene=scene;else _compatLegoScenes.Add(key);
            InitializeBoneScene(scene,obj.RenderIdentity,obj.RenderObjectGeneration);
            RebuildLegoScene(scene,assets);
            return;
        }
        scene.Object=obj;scene.Update=update;
        List<(int Position,long Order,long Sequence,Action Apply)>? mutations=null;
        foreach(var model in LegoSceneModels(obj.RenderIdentity))
        {
            // Capture sequences are append-only. Do not rescan/sort already
            // applied history; final ordering below still includes every event.
            for(int index=model.History.Count-1;index>=0;index--)
            {
                var checkpoint=model.History[index];
                if(checkpoint.Sequence<=scene.Sequence)break;
                (mutations??=[]).Add((checkpoint.Position,checkpoint.PacketBoundary is int boundary?2L*boundary:long.MaxValue,
                    checkpoint.Sequence,()=>
                    {
                        scene.Checkpoints[model]=checkpoint;
                        scene.Sequence=Math.Max(scene.Sequence,checkpoint.Sequence);
                    }));
            }
        }
        if(_rslBoneOwners.TryGetValue(key,out var boneOwner) &&
            scene.BoneEvents.GetValueOrDefault(boneOwner)<boneOwner.SceneHistory.Count)
            AddBoneSceneMutations(scene,boneOwner,mutations??=[]);
        var ordered=mutations is null?Array.Empty<(int Position,long Order,long Sequence,Action Apply)>():
            mutations.OrderBy(m=>m.Position).ThenBy(m=>m.Order).ThenBy(m=>m.Sequence).ToArray();
        int captureIndex=0;
        int count=update?.ObjectSceneEvents.Count??0;
        //A checkpoint at N happened before scene event N. Replaying the final
        //tree alone would erase remove/re-add operations and change list order.
        for(int position=scene.EventCount;position<=count;position++)
        {
            while(captureIndex<ordered.Length && ordered[captureIndex].Position<=position)
            {
                ordered[captureIndex++].Apply();
                RebuildLegoScene(scene,assets);
            }
            if(position<count)ApplyBoneSceneObservation(scene,key,update!,update!.ObjectSceneEvents[position]);
            if(position<count && update!.ObjectSceneEvents[position] is {Kind:EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent} change)
            {
                ChangeLegoSceneTree(scene.Tree,null);
                scene.Tree=null;
                scene.Selected=change.StateId;
                RebuildLegoScene(scene,assets);
            }
        }
        scene.EventCount=count;
        RebuildLegoScene(scene,assets,allowRetained:true);
    }

    private void SynchronizeMountedLegoScene(LegoRslOwner wrapper,EnbRetailAssetBundle assets)
    {
        foreach(var scene in _legoScenes.Values)
        {
            if(!scene.WrapperEvents.TryGetValue(wrapper,out int consumed) || wrapper.Update is not {} update)continue;
            var mutations=new List<(int Position,long Order,long Sequence,Action Apply)>();
            if(_rslBoneOwners.TryGetValue((wrapper.Object.RenderIdentity,wrapper.Object.RenderObjectGeneration),out var bones))
                AddBoneSceneMutations(scene,bones,mutations);
            var ordered=mutations.OrderBy(m=>m.Position).ThenBy(m=>m.Order).ThenBy(m=>m.Sequence).ToArray();
            int mutationIndex=0;
            for(int position=consumed;position<=update.ObjectSceneEvents.Count;position++)
            {
                while(mutationIndex<ordered.Length&&ordered[mutationIndex].Position<=position)
                {ordered[mutationIndex++].Apply();RebuildLegoScene(scene,assets);}
                if(position<update.ObjectSceneEvents.Count)
                    ApplyBoneSceneObservation(scene,(wrapper.Object.RenderIdentity,wrapper.Object.RenderObjectGeneration),update,update.ObjectSceneEvents[position]);
                if(position<update.ObjectSceneEvents.Count&&update.ObjectSceneEvents[position] is {Kind:EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent} change)
                {
                    LegoSceneNode DetachModel(LegoSceneNode node)
                    {
                        if(ReferenceEquals(node.Value,wrapper))
                        {
                            foreach(var child in node.Children)ChangeLegoSceneTree(child,null);
                            return new(node.Value,[]);
                        }
                        return new(node.Value,node.Children.Select(DetachModel).ToArray());
                    }
                    if(scene.Tree is not null)scene.Tree=DetachModel(scene.Tree);
                    scene.WrapperStates[wrapper]=change.StateId;
                    RebuildLegoScene(scene,assets);
                }
            }
            scene.WrapperEvents[wrapper]=update.ObjectSceneEvents.Count;
        }
    }
}
