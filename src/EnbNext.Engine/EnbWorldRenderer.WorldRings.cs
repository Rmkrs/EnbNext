using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private static LiveCpuRoot CreateRetainedRingRoot(EnbW3dRenderResource parent,string name,EnbRetailRingAttachment ring)
    {
        var empty=parent.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren([]).SetExternalRenderObjects([]);
        return new(false,empty,null,new(StringComparer.OrdinalIgnoreCase),name,name,null,null,[]){Ring=ring};
    }

    private readonly record struct WorldRingKey(ulong Identity,ulong Generation,string Path,object Factory,float Scale,Vector3 Hsv)
    {
        public object? PhysicalFactory {get;init;}
    }
    private readonly Dictionary<WorldRingKey,WorldRingOwner> _worldRings=[];
    private readonly HashSet<WorldRingKey> _worldRingObserved=[];
    private readonly Dictionary<(ulong Identity,ulong Generation),EnbRslObjectState> _worldRingRslUpdates=[];

    private sealed class WorldRingPlayback(EnbRetailRingAsset asset)
    {
        public readonly EnbRetailRingPlayback Clock=new();
        public EnbRetailRingValues Values=asset.InitialValues;
        public Vector2 StoredExtent=asset.Header.SecondRadii;
        public int Level=20;
    }

    private sealed class WorldRingOwner(IEnbRenderObject owner,EnbRetailRingAttachment binding,string path)
    {
        public IEnbRenderObject Owner=owner;
        public readonly EnbRetailRingAttachment Binding=binding;
        public string Path=path;
        public WorldRingPlayback Playback=new(binding.Asset);
        public bool BorrowedPlayback;
        public EnbRetailRingPlayback Clock=>Playback.Clock;
        public EnbRetailRingValues Values {get=>Playback.Values;set=>Playback.Values=value;}
        //00913261..277 overwrites constructor extents with unmultiplied radii.
        //+164 leaves them intact; only a sampled second-radius key changes them.
        public Vector2 StoredExtent {get=>Playback.StoredExtent;set=>Playback.StoredExtent=value;}
        public Matrix4x4 World;
        public int Level {get=>Playback.Level;set=>Playback.Level=value;}
        public bool Visible=true;
        public bool SceneVisible=true;
        public int RslStateId=-1;
        public ulong VisibilityTransitions;
        public List<WorldRingOwner>? VisibilityOwners;
        public LiveRootInstance? Parent;
        public LiveRootInstance? RetainedInstance;
    }

    private static WorldRingKey WorldRingOwnerKey(IEnbRenderObject obj,LiveRootInstance instance,string path,float scale,Vector3 hsv)
    {
        string retainedPath=path;
        if(instance.IsRsl)
            for(var ancestor=instance;ancestor is not null;ancestor=ancestor.Parent)
                if(ancestor.Root.RslFactoryStateId>=0)
                {retainedPath=$"rsl/{ancestor.Root.RslFactoryStateId}"+path[ancestor.InstancePath.Length..];break;}
        object? factory=PhysicalParticleFactory(instance);
        return new(obj.RenderIdentity,factory is not null?0:instance.IsRsl?obj.RenderObjectGeneration:obj.VisualGeneration,
            retainedPath,instance.Root.PrimitiveFactoryIdentity,scale,hsv){PhysicalFactory=factory};
    }

    private void ObserveRetainedWorldRings(IEnumerable<LiveRootInstance> roots)
    {
        foreach(var instance in roots)
        {
            if(instance.Root.Ring is null)continue;
            bool previousAdmission=instance.ShouldDraw;
            if(!TryRefreshOrdinaryHlodChildCore(instance,false,out _)){instance.ShouldDraw=false;continue;}
            if(!instance.IsOrdinaryHlodChild)instance.ShouldDraw&=previousAdmission;
            ObserveRetainedWorldRing(instance);
        }
    }

    private void ObserveRetainedWorldRing(LiveRootInstance instance)
    {
        if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out float scale,out var hsv))return;
        ResolvePrimitivePropertyInputs(instance,ref scale,ref hsv);
        var obj=instance.Object;
        var key=WorldRingOwnerKey(obj,instance,instance.InstancePath,scale,hsv);
        WorldRingOwner? borrowed=null;
        if(_shipExhaustOwner is {} source)
        {
            if(!source._worldRings.TryGetValue(key,out borrowed) &&
                TryResolveScannerPrimitiveSource(instance,source,out var retained) && retained.Root.Ring is not null &&
                TryResolvePrimitiveFactoryTransform(retained,out _,out float sourceScale,out var sourceHsv))
            {
                ResolvePrimitivePropertyInputs(retained,ref sourceScale,ref sourceHsv);
                source._worldRings.TryGetValue(WorldRingOwnerKey(retained.Object,retained,retained.InstancePath,sourceScale,sourceHsv),out borrowed);
            }
            if(borrowed is null && ScannerSourceRetainsFactory(instance,source))
                borrowed=source.RetainWorldRingOwner(key,instance,scale,hsv);
        }
        _worldRingObserved.Add(key);
        var state=RetainWorldRingOwner(key,instance,scale,hsv,borrowed?.Binding);
        if(state is null)return;
        state.Owner=obj;state.Path=instance.InstancePath;state.World=world;
        if(borrowed is not null){state.Playback=borrowed.Playback;state.BorrowedPlayback=true;}
        state.Parent=instance;state.RetainedInstance=instance;
        state.Visible=instance.PivotVisible&&!instance.Root.RenderObjectHidden;
        if(!state.BorrowedPlayback)state.Clock.ReconcileVisibility(state.Visible);
    }

    private WorldRingOwner? RetainWorldRingOwner(WorldRingKey key,LiveRootInstance instance,float scale,Vector3 hsv,
        EnbRetailRingAttachment? configured=null)
    {
        if(_worldRings.TryGetValue(key,out var state))return state;
        var binding=instance.Root.Ring!;
        if(configured is not null)binding=configured;
        else
        {
            if(hsv!=Vector3.Zero)
            {
                if(_retailAssetsAccessor() is not {} assets)return null;
                binding=binding.ApplyHsv(assets,hsv);
            }
            binding=binding with {Asset=binding.Asset.ScaleRadiusMultipliers(scale)};
        }
        var obj=instance.Object;
        _worldRings[key]=state=new(obj,binding,instance.InstancePath);
        //No primary draw/placement is implied by the first scanner access.
        if(instance.IsRsl)state.RslStateId=instance.Root.RslFactoryStateId;
        SceneOwner? source=null;
        if(_ownedSceneFrame&&_ownedScene!.TryFind(obj.RenderIdentity,out var node)&&node.Definition.RenderObjectGeneration==obj.RenderObjectGeneration)
            source=node.State;
        if(source is not null&&key.Generation==obj.RenderObjectGeneration)
        {state.VisibilityOwners=source.WorldRingVisibility;state.VisibilityOwners.Add(state);}
        var update=source?.RslUpdate;
        if(source is null)_worldRingRslUpdates.TryGetValue((obj.RenderIdentity,obj.RenderObjectGeneration),out update);
        if(update is not null)
            state.VisibilityTransitions=update.ObjectVisibilityTransitionCounts.GetValueOrDefault(state.RslStateId);
        return state;
    }

    private void ReconcileWorldRingOwners(IReadOnlySet<(ulong Identity,ulong Generation)> live)
    {
        foreach(var key in _worldRings.Keys.Where(key=>key.PhysicalFactory is {} factory
            ? !(_shipExhaustOwner??this).RetainsPhysicalFactory(factory):!live.Contains((key.Identity,key.Generation))).ToArray())if(_worldRings.Remove(key,out var retired))retired.VisibilityOwners?.Remove(retired);
        foreach(var key in _worldRingRslUpdates.Keys.Where(key=>!live.Contains(key)).ToArray())_worldRingRslUpdates.Remove(key);
    }

    private void ReconcileWorldRingRslVisibility(IEnbRenderObject obj,EnbRslObjectState update,SceneOwner? source=null)
    {
        if(source is not null)
        {
            foreach(var state in source.WorldRingVisibility)ApplyWorldRingVisibility(state,update);
            return;
        }
        _worldRingRslUpdates[(obj.RenderIdentity,obj.RenderObjectGeneration)]=update;
        foreach(var pair in _worldRings.Where(pair=>pair.Key.Identity==obj.RenderIdentity&&pair.Key.Generation==obj.RenderObjectGeneration))
            ApplyWorldRingVisibility(pair.Value,update);
    }
    private static void ApplyWorldRingVisibility(WorldRingOwner state,EnbRslObjectState update)
    {
        if(state.BorrowedPlayback)return;
        if(state.RslStateId<0)return;
        ulong transitions=update.ObjectVisibilityTransitionCounts.GetValueOrDefault(state.RslStateId);
        if(transitions==state.VisibilityTransitions)return;
        state.Clock.ReconcileVisibility(false);
        state.Clock.ReconcileVisibility(!update.HiddenObjectStateIds.Contains(state.RslStateId));
        state.VisibilityTransitions=transitions;
    }

    private static EnbRetailRingPreparedAttachment Snapshot(WorldRingOwner state)
        =>new(state.Binding,state.World,state.Clock.Progress,state.Values,
            new(Vector3.Transform(state.Binding.Asset.Header.Center,state.World),state.StoredExtent.Length()));

    private List<(WorldRingOwner Owner,EnbRenderDetailOwner Candidate)> PrepareWorldRings(EnbResolvedRenderCamera camera)
    {
        var observed=_worldRings.Where(pair=>_worldRingObserved.Contains(pair.Key)).Select(pair=>pair.Value).ToArray();
        foreach(var state in observed)
        {
            state.SceneVisible=true;
            if(state.RetainedInstance is {Parent:null} root && FrustumCullingEnabled&&!root.ForcesSceneVisibility)
                state.SceneVisible=EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(root.OrbitalCamera??camera,Snapshot(state).Bounds);
            if(state.RetainedInstance is {Parent:null} sceneRoot)sceneRoot.ShouldDraw&=state.SceneVisible;
        }
        return observed.Where(state=>state.Visible&&state.SceneVisible && (state.Parent is null ||
            !state.Parent.IsOrdinaryHlodChild&&state.Parent.ShouldDraw&&CollectionAncestorsDraw(state.Parent)&&!HasCollectionAncestor(state.Parent)))
            .Select(state=>(state,Snapshot(state).Candidate($"world-ring/{state.Owner.RenderIdentity}/{state.Path}",camera,state.Level,state.RetainedInstance?.Root.LodBias??1))).ToList();
    }

    private void AppendWorldRings(List<EnbW3dDrawTask> packets,EnbResolvedRenderCamera camera,uint now)
    {
        foreach(var state in _worldRings.Where(pair=>_worldRingObserved.Contains(pair.Key)).Select(pair=>pair.Value))
        {
            if(state.Parent is {} parent&&!TryPrepareRetainedRootDraw(parent,out _))continue;
            if(state.RetainedInstance is {} retained)ObserveRetainedWorldRing(retained);
            if(!state.Visible||!state.SceneVisible||state.Parent is {} hiddenParent&&(!hiddenParent.ShouldDraw||!CollectionAncestorsDraw(hiddenParent)))continue;
            if(state.RetainedInstance is {Parent:{} forceParent} instance && (instance.IsAggregateHlodChild||instance.IsShipLegoController||instance.IsExternalLegoChild) && forceParent.Root.ForceSubObjectLod && forceParent.Root.Hlod is {} hlod)
                state.Level=EnbSphereRingDetailBudgetSemantics.ClampRingLevel(forceParent.HlodSelection?.LevelOrdinal??EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod));
            if(state.Level==0)continue;
            var binding=state.Binding;
            state.Clock.Advance(binding.Asset,true,(state.BorrowedPlayback?_shipExhaustOwner!:this)._ringFrameDelta);
            if(binding.Asset.Tracks.Values.Any(track=>track.Count>0))
            {
                state.Values=binding.Asset.EvaluateValues(state.Clock.Progress);
                if(binding.Asset.Tracks.TryGetValue(5,out var keys)&&keys.Count>0)state.StoredExtent=state.Values.SecondRadii;
            }
            var sample=state.Values.Sample(state.Level);
            if(sample is null)continue;
            packets.Add((EnbRetailRingDrawPacket.Build(binding.Asset,sample,binding.Key,binding.Texture,Snapshot(state).DrawWorld(camera),
                true,now,state.Owner.RenderIdentity,state.Owner.VisualGeneration,state.Owner.GameId,binding.Ordinal)).ForkTask().SetRenderInstancePath("world-ring/"+state.Path));
        }
    }
}
