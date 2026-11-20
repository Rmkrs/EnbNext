using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private static long _legoPhysicalOperationSequence;
    //Controller lookup and concrete RenderObject ownership are separate. In
    //particular,460260 changes the wrapper getter without moving HLOD children.
    private abstract class LegoPhysicalObject(LiveShipLegoSpec? spec)
    {
        public LiveShipLegoSpec? Spec=spec;
    }
    private sealed class LegoPhysicalModel(LiveCpuRoot root,LiveShipLegoSpec? spec=null):LegoPhysicalObject(spec)
    {
        private LiveCpuRoot _root=root;
        public readonly HashSet<LegoSceneState> SceneOwners=[];
        public readonly HashSet<SceneOwner> SpecPlanOwners=[];
        public void InvalidateScenes(){foreach(var scene in SceneOwners)scene.Dirty=true;foreach(var source in SpecPlanOwners)source.PhysicalSpecsDirty=true;}
        public LiveCpuRoot Root
        {
            get=>_root;
            set{if(ReferenceEquals(_root,value))return;_root=value;InvalidateScenes();}
        }
        public LiveRootInstance? SceneInstance;
        public readonly Dictionary<int,LegoPhysicalModel> AuthoredChildren=[];
        public readonly List<LegoPhysicalObject> Children=[];
        public readonly Dictionary<int,EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride> Overrides=[];
        private LegoOverrideBinding? _overrideBinding;
        public void SetOverride(EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride value)
        {Overrides[value.PivotIndex]=value;_overrideBinding=null;}
        public void RemoveOverride(int pivot)
        {if(Overrides.Remove(pivot))_overrideBinding=null;}
        // Published dictionaries are never mutated. Queued poses keep the
        // prior state; only an actual attachment write needs another binding.
        public LegoOverrideBinding GetOverrideBinding()=>_overrideBinding??=new(
            Overrides.ToDictionary(p=>p.Key,p=>p.Value),
            Overrides.ToDictionary(p=>p.Key,p=>p.Value.Transform));
        public readonly List<LegoPhysicalCheckpoint> History=[];
        public LegoPhysicalCheckpoint At(int ordinal)=>History.LastOrDefault(h=>(h.WrapperPosition??h.Position)<=ordinal)??new(0,[],[]);
        public void Capture(int position,int? wrapperPosition=null,int? packetBoundary=null)
        {
            History.Add(new(position,Children.ToArray(),Overrides.ToDictionary(p=>p.Key,p=>p.Value),
                Interlocked.Increment(ref _legoPhysicalOperationSequence),wrapperPosition,packetBoundary));
            InvalidateScenes();
        }
    }
    private sealed record LegoOverrideBinding(
        Dictionary<int,EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride> Definitions,
        Dictionary<int,Matrix4x4> Draw);

    private sealed record LegoPhysicalCheckpoint(int Position,LegoPhysicalObject[] Children,
        Dictionary<int,EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride> Overrides,long Sequence=0,int? WrapperPosition=null,int? PacketBoundary=null)
    {
    }

    private sealed class LegoPhysicalOwner
    {
        public ulong Generation;
        public readonly Dictionary<int,LegoPhysicalModel> RootModels=[];
        public readonly Dictionary<object,LegoPhysicalModel> ChildModels=[];
    }

    private readonly Dictionary<ulong,LegoPhysicalOwner> _legoPhysicalOwners=[];

    private static LegoPhysicalModel? CurrentLegoPhysicalModel(LegoPhysicalObject physical)
    {
        if(physical is LegoPhysicalModel model)return model;
        var wrapper=(LegoRslOwner)physical;
        if(wrapper.Update?.CurrentObjectStateId is not int state || wrapper.QueryRoot is not {} query)return null;
        if(!wrapper.Models.TryGetValue(state,out var retained))
            wrapper.Models.Add(state,retained=new(query.Root,wrapper.Spec));
        return retained;
    }

    private LegoPhysicalModel? ResolveLegoWrapperModel(LegoRslOwner wrapper,int state,EnbRetailAssetBundle assets)
    {
        if(wrapper.Models.TryGetValue(state,out var retained))
        {RefreshBoneSceneRoot(retained,wrapper.Object.RenderIdentity,wrapper.Object.RenderObjectGeneration,state);return retained;}
        var graph=wrapper.Update?.Graph??wrapper.Reference.ResourceGraph;
        if(graph is null || !graph.TryFindState(state,out var definition) ||
            !TryMaterializeRslObjectRoot(wrapper.Object.BaseAsset,state,definition,assets,out var root,out _))return null;
        root=ResolveEffectRslRoot(wrapper.Object,state,root);
        wrapper.Models.Add(state,retained=new(root,wrapper.Spec));
        RefreshBoneSceneRoot(retained,wrapper.Object.RenderIdentity,wrapper.Object.RenderObjectGeneration,state);
        return retained;
    }

    private LegoPhysicalModel? ResolveLegoSceneRootModel(IEnbRenderObject obj,int? state,
        EnbRslObjectState? update,EnbRetailAssetBundle assets)
    {
        if(!_legoPhysicalOwners.TryGetValue(obj.RenderIdentity,out var owner))return null;
        if(update is null)return owner.RootModels.GetValueOrDefault(-1);
        if(state is not int id)return null;
        if(owner.RootModels.TryGetValue(id,out var retained))
        {RefreshBoneSceneRoot(retained,obj.RenderIdentity,obj.RenderObjectGeneration,id);return retained;}
        var graph=update.Graph;
        if(graph is null || !graph.TryFindState(id,out var definition) ||
            !TryMaterializeRslObjectRoot(obj.BaseAsset,id,definition,assets,out var root,out _))return null;
        owner.RootModels.Add(id,retained=new(ResolveEffectRslRoot(obj,id,root)));
        NotifyOwnedLegoMembership(obj.RenderIdentity);
        RefreshBoneSceneRoot(retained,obj.RenderIdentity,obj.RenderObjectGeneration,id);
        return retained;
    }

    private LegoPhysicalObject? ResolveLegoPhysicalChild(IEnbRenderObject obj,LiveShipLegoSpec spec,
        EnbRetailAssetBundle assets)
    {
        if(ResolveLegoBaseFactory(obj,spec,assets) is not null)return ResolveLegoPhysicalModel(obj,spec,assets);
        if(!_legoRslOwners.TryGetValue((obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,
            spec.BaseAsset,spec.CanonicalPath),out var wrapper))return null;
        wrapper.Spec=spec;
        return wrapper;
    }

    private HashSet<LegoRslOwner> RetainedPhysicalLegoWrappers(ulong identity)
    {
        var wrappers=new HashSet<LegoRslOwner>();
        var seen=new HashSet<LegoPhysicalObject>();
        void Visit(LegoPhysicalObject value)
        {
            if(!seen.Add(value))return;
            if(value is LegoPhysicalModel model)foreach(var child in model.Children)Visit(child);
            else if(value is LegoRslOwner wrapper)
            {
                wrappers.Add(wrapper);
                foreach(var retained in wrapper.Models.Values)Visit(retained);
            }
        }
        if(_legoPhysicalOwners.TryGetValue(identity,out var physical))
            foreach(var root in physical.RootModels.Values)Visit(root);
        if(_legoControllers.TryGetValue(identity,out var controllers))
            foreach(var spec in StoredLegoSpecs(controllers.Tree.Root))
            {
                if(_legoRslOwners.TryGetValue((identity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath),out var wrapper))Visit(wrapper);
                else if(_legoPhysicalOwners.TryGetValue(identity,out var owner))
                    foreach(var model in owner.ChildModels.Values)
                        if(ReferenceEquals(model.Spec,spec))Visit(model);
            }
        return wrappers;
    }

    private LegoPhysicalOwner RetainLegoPhysicalOwner(IEnbRenderObject obj)
    {
        var source=_buildingSceneOwner is {} bound&&bound.Definition.RenderIdentity==obj.RenderIdentity?bound:null;
        var owner=source?.PhysicalOwner;
        if(owner is null&&!_legoPhysicalOwners.TryGetValue(obj.RenderIdentity,out owner))
            _legoPhysicalOwners.Add(obj.RenderIdentity,owner=new(){Generation=obj.RenderObjectGeneration});
        if(source is not null)source.PhysicalOwner=owner;
        if(owner.Generation!=obj.RenderObjectGeneration)
        {
            owner.RootModels.Clear();
            foreach(var baseline in owner.ChildModels.Values){baseline.History.Clear();baseline.Capture(0);}
            owner.Generation=obj.RenderObjectGeneration;
        }
        return owner;
    }

    private LegoPhysicalModel? ResolveLegoPhysicalModel(IEnbRenderObject obj,LiveShipLegoSpec? spec,
        EnbRetailAssetBundle assets,int? packetBoundary=null)
    {
        var owner=RetainLegoPhysicalOwner(obj);
        LiveCpuRoot? root;
        if(spec is not null)
        {
            root=ResolveLegoBaseFactory(obj,spec,assets)?.Root;
            if(root?.PhysicalFactoryOwner is not {} identity)
                return _legoRslOwners.TryGetValue((obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,
                    spec.BaseAsset,spec.CanonicalPath),out var wrapper)?CurrentLegoPhysicalModel(wrapper):null;
            if(!owner.ChildModels.TryGetValue(identity,out var child))
                owner.ChildModels.Add(identity,child=new(root,spec));
            if(!_legoSourceInstances.ContainsKey(identity))
                _legoSourceInstances.Add(identity,new(obj,root,false,"lego/"+spec.CanonicalPath,Matrix4x4.Identity)
                    {IsShipLegoController=true,LegoControllerBaseAsset=spec.BaseAsset});
            return child;
        }
        var resolution=ResolveSceneBaseFactory(obj,assets);
        int stateId=-1;
        if(resolution?.Root is {} plain)root=plain;
        else
        {
            var rsl=ResolveBaseAsset(assets.BaseAssetResolver!,obj.BaseAsset).Rsl;
            if(rsl is null || !_frameRslObjectUpdates.TryGetValue(obj.RenderIdentity,out var update))return null;
            int? selected=update.CurrentObjectStateId;
            if(packetBoundary is int boundary)
            {
                selected=null;
                foreach(var observation in update.ObjectSceneEvents)
                {
                    if(observation.PacketActionOrdinal is int ordinal && ordinal>=boundary)break;
                    if(observation.Kind==EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent)selected=observation.StateId;
                }
            }
            if(selected is not int id)return null;
            stateId=id;
            if(owner.RootModels.TryGetValue(id,out var retained))return retained;
            var graph=update.Graph??rsl.ResourceGraph;
            if(graph is null || !graph.TryFindState(id,out var state) ||
                !TryMaterializeRslObjectRoot(obj.BaseAsset,id,state,assets,out root,out _))return null;
            root=ResolveEffectRslRoot(obj,id,root);
        }
        if(!owner.RootModels.TryGetValue(stateId,out var model)){owner.RootModels.Add(stateId,model=new(root));NotifyOwnedLegoMembership(obj.RenderIdentity);}
        return model;
    }

    private void ChangeLegoPhysicalMembership(IEnbRenderObject obj,LiveShipLegoSpec? parent,
        LiveShipLegoSpec child,EnbRetailAssetBundle assets,int? boundary,bool attach)
    {
        NotifyOwnedLegoMembership(obj.RenderIdentity);
        var model=ResolveLegoPhysicalModel(obj,parent,assets,boundary);
        var childObject=ResolveLegoPhysicalChild(obj,child,assets);
        var childModel=childObject is null?null:CurrentLegoPhysicalModel(childObject);
        if(model is null || childObject is null || assets.ClientBaseAssetMounts is not {} mounts ||
            !mounts.TryResolveMount(parent?.BaseAsset??obj.BaseAsset,child.Path[^1],out var mount) ||
            HasNoOpLegoMounts(model.Root) || !TryResolveConfiguredAttachmentHierarchy(model.Root,out var hierarchy,out _) || hierarchy is null)return;
        if(!attach)
        {
            //72D2A0 releases the CURRENT model pivot before461EF0 removal.
            //A miss leaves the child retained by another model untouched.
            model.RemoveOverride(mount.BoneIndex);
            model.Children.Remove(childObject);
            model.Capture(LegoSceneEventPosition(obj,packetBoundary:boundary),LegoModelWrapperPosition(model),boundary);
            return;
        }
        EnbW3dRenderHierarchy? childHierarchy=null;
        if(childModel is not null && !TryResolveConfiguredAttachmentHierarchy(childModel.Root,out childHierarchy,out _))return;
        if(!EnbCurrentRetailShipLegoRuntime.TryCreateParentHierarchyOverride(mount,childHierarchy,childModel?.Root.ConfiguredHierarchyTranslations is not null?1:childModel?.Root.ConfiguredFactoryInputs?.Scale??1,out var pivot,out _,out _))return;
        model.SetOverride(pivot);
        if(!EnbCurrentRetailAggregateBoneLookup.TryResolve(hierarchy,mount.BoneName,out _))return;
        if(childModel is not null)childModel.Root=ApplyLegoInheritedLodBias(obj,child,childModel.Root,model.Root.LodBias);
        model.Children.Add(childObject);
        model.Capture(LegoSceneEventPosition(obj,packetBoundary:boundary),LegoModelWrapperPosition(model),boundary);
    }

    private void RefreshLegoPhysicalParentPivot(IEnbRenderObject obj,EnbRetailLegoControllerTree<LiveShipLegoSpec> tree,
        EnbRetailLegoControllerTree<LiveShipLegoSpec>.Node child,EnbRetailAssetBundle assets,int? boundary)
    {
        var path=child.Path.Take(child.Path.Count-1).ToArray();
        var parent=path.Length==0?tree.Root:tree.Find(path);
        if(parent is null || !parent.Children.Values.Contains(child))return;
        var model=ResolveLegoPhysicalModel(obj,parent.Value,assets,boundary);
        var childObject=ResolveLegoPhysicalChild(obj,child.Value!,assets);
        var childModel=childObject is null?null:CurrentLegoPhysicalModel(childObject);
        if(model is null || childObject is null || assets.ClientBaseAssetMounts is not {} mounts ||
            !mounts.TryResolveMount(parent.Value?.BaseAsset??obj.BaseAsset,child.Path[^1],out var mount) ||
            HasNoOpLegoMounts(model.Root))return;
        EnbW3dRenderHierarchy? hierarchy=null;
        if(childModel is not null && !TryResolveConfiguredAttachmentHierarchy(childModel.Root,out hierarchy,out _))return;
        if(!EnbCurrentRetailShipLegoRuntime.TryCreateParentHierarchyOverride(mount,hierarchy,childModel?.Root.ConfiguredHierarchyTranslations is not null?1:childModel?.Root.ConfiguredFactoryInputs?.Scale??1,out var pivot,out _,out _))return;
        model.SetOverride(pivot);
        model.Capture(LegoSceneEventPosition(obj,packetBoundary:boundary),LegoModelWrapperPosition(model),boundary);
    }

    private int? LegoModelWrapperPosition(LegoPhysicalModel model)
    {
        bool Contains(LegoPhysicalModel root)=>ReferenceEquals(root,model)||root.Children.OfType<LegoPhysicalModel>().Any(Contains);
        foreach(var wrapper in _legoRslOwners.Values)
            if(wrapper.Models.Values.Any(Contains))return wrapper.Update?.ObjectSceneEvents.Count;
        return null;
    }

    private LegoPhysicalModel? FindLegoSceneModel(ulong identity,ulong generation,int state)
    {
        if(_legoPhysicalOwners.TryGetValue(identity,out var owner) && owner.Generation==generation)
            return owner.RootModels.GetValueOrDefault(state);
        var wrapper=LegoRslOwnerFor(identity);
        return wrapper?.Object.RenderObjectGeneration==generation?wrapper.Models.GetValueOrDefault(state):null;
    }

    private int LegoSceneEventPosition(IEnbRenderObject obj,int? packetBoundary)
    {
        if(!_frameRslObjectUpdates.TryGetValue(obj.RenderIdentity,out var update))return 0;
        if(packetBoundary is int boundary)
            foreach(var observation in update.ObjectSceneEvents)
                if(observation.PacketActionOrdinal is int ordinal && ordinal>=boundary)return observation.Ordinal;
        return update.ObjectSceneEvents.Count;
    }

    private bool PhysicalLegoMemberAt(ulong identity,ulong generation,int state,object factory,int? eventPosition,bool direct=false)
    {
        if(FindLegoSceneModel(identity,generation,state) is not {} root)return false;
        bool Contains(LegoPhysicalModel parent)
        {
            var children=eventPosition is int position?parent.At(position).Children:parent.Children.ToArray();
            return children.Any(child=>child is LegoPhysicalModel model &&
                (ReferenceEquals(model.Root.PhysicalFactoryOwner,factory) && (!direct || model.Root.RetainedEmitter is not null) || !direct&&Contains(model)));
        }
        return Contains(root);
    }

    private bool TryResolvePhysicalLegoEmitterEventWorld(RetainedEmitterKey key,RetainedEmitterOwner owner,
        EnbCurrentRetailRslObjectSceneEvent observation,LiveRootInstance stagedRoot,LiveRootInstance retained,
        out Matrix4x4 world,out string? diagnostic)
    {
        world=default;diagnostic=null;
        var binding=owner.SceneWrapperOwner!.Value;
        if(FindLegoSceneModel(binding.Identity,binding.Generation,observation.StateId) is not {} model)
        {diagnostic="Lego emitter event has no retained parent model.";return false;}
        var checkpoint=model.At(observation.Ordinal);
        var child=checkpoint.Children.OfType<LegoPhysicalModel>().FirstOrDefault(c=>ReferenceEquals(c.Root.PhysicalFactoryOwner,key.PhysicalFactory));
        if(child?.Spec is not {} spec || !TryResolveConfiguredAttachmentHierarchy(stagedRoot.Root,out var hierarchy,out diagnostic) || hierarchy is null)
        {diagnostic??="Lego emitter event has no direct retained HLOD membership.";return false;}
        var source=retained.Object;
        for(var ancestor=retained.Parent;ancestor is not null;ancestor=ancestor.Parent)
            if(ancestor.IsRsl && ancestor.Object.RenderIdentity==binding.Identity){source=ancestor.Object;break;}
        var assets=_retailAssetsAccessor();
        if(assets?.ClientBaseAssetMounts?.TryResolveMount(source.BaseAsset,spec.Path[^1],out var mount)!=true ||
            !EnbCurrentRetailShipLegoRuntime.TryBuildRigidObjectWorld(source,out var rigid,out diagnostic))return false;
        if(!EnbCurrentRetailShipLegoRuntime.TryResolveParentMountWorld(hierarchy,rigid,ResolvePendingHierarchyScale(stagedRoot,source.Scale),
            mount with {BoneIndex=EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,mount.BoneName)},
            stagedRoot.AnimationBlend?.ResolvedSamples,checkpoint.Overrides.Values,out var mounted,out diagnostic))return false;
        world=EnbCurrentRetailShipLegoRuntime.CreateAttachedChildDrawWorld(mounted);
        return true;
    }

    private IReadOnlyList<LiveShipLegoSpec>? PhysicalLegoSpecs(IEnbRenderObject obj,EnbRetailAssetBundle assets,
        out LegoPhysicalModel? current)
    {
        if(_shipExhaustOwner is {} primary)return primary.PhysicalLegoSpecs(obj,assets,out current);
        var source=!_isolatedLegoNotification&&_buildingSceneOwner is {} node&&node.Definition.RenderIdentity==obj.RenderIdentity?node:null;
        if(source is {PhysicalSpecsDirty:false}){current=source.PhysicalSpecsRoot;return source.PhysicalSpecs;}
        source?.ClearPhysicalSpecDependencies();
        if(source is not null)source.LegoDraw=null;
        current=null;
        if(obj.LegoControllerMutations.IsDefaultOrEmpty)return null;
        var logical=RetainedLegoSpecs(obj,all:true)??[];

        current=ResolveLegoPhysicalModel(obj,null,assets);
        if(current is null)return null;
        var result=new List<LiveShipLegoSpec>();
        var visited=new HashSet<LegoPhysicalObject>();
        void Visit(LegoPhysicalModel parent,string parentPath)
        {
            if(source is not null&&source.PhysicalSpecDependencies.Add(parent))parent.SpecPlanOwners.Add(source);
            for(int i=0;i<parent.Children.Count;i++)
            {
                var child=parent.Children[i];
                if(!visited.Add(child))continue;
                var spec=child.Spec!;
                string path=parentPath.Length==0?"physical-"+i:parentPath+"/physical-"+i;
                result.Add(spec with {PhysicalDrawPath=path,PhysicalParentPath=parentPath,PhysicalModel=CurrentLegoPhysicalModel(child)});
                if(CurrentLegoPhysicalModel(child) is {} childModel)Visit(childModel,path);
            }
        }
        Visit(current,string.Empty);
        //An orphan controller still owns its model and descendants. Query them
        //at the retained pose, without making a new attachment to the current
        //root merely because a later RSL state now has a suitable hierarchy.
        int orphan=0;
        foreach(var spec in logical)
        {
            var physical=ResolveLegoPhysicalChild(obj,spec,assets);
            if(physical is null || !visited.Add(physical))continue;
            var model=CurrentLegoPhysicalModel(physical);
            string path="detached-"+orphan++;
            result.Add(spec with {PhysicalDrawPath=path,PhysicalParentPath="$detached",PhysicalModel=model});
            if(model is not null)Visit(model,path);
        }
        if(source is not null){source.PhysicalSpecs=result;source.PhysicalSpecsRoot=current;source.PhysicalSpecsDirty=false;}
        return result;
    }
}
