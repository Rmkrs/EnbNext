using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<(RetainedEmitterOwner,EnbRenderDetailOwner)> _detailRetainedEmitterOwners=[];
    private readonly record struct RetainedEmitterKey(ulong Owner,ulong Generation,string Path,object Factory,float Scale,Vector3 Hsv)
    {
        public object? PhysicalFactory {get;init;}
        public object? BoneFactory {get;init;}
    }
    private readonly PhysicalFactoryMembership _livePhysicalParticleFactories=new();
    private readonly RootTaskList _hiddenRslEmitterRoots=[];
    private readonly EnbRetainedOwnerRegistry<RetainedEmitterKey,RetainedEmitterOwner> _retainedEmitters=new(static key=>key.Owner);
    private readonly EnbCurrentRetailSceneCallbackList<(RetainedEmitterKey Key,bool Buffer)> _retainedEmitterCallbacks;
    private readonly List<KeyValuePair<RetainedEmitterKey,RetainedEmitterOwner>> _retiringEmitterBuffers=[];
    private readonly HashSet<RetainedEmitterKey> _discardedParticleFactories=[];
    private static void CollectDiscardedParticleConstructions(LiveCpuRoot root,List<EnbCurrentRetailParticleEmitterDefinition> result)
    {
        if(root.RetainedEmitter is {} emitter)result.Add(emitter.Definition);
        foreach(var child in root.CollectionChildren)CollectDiscardedParticleConstructions(child,result);
        foreach(var child in root.HmodelChildren)CollectDiscardedParticleConstructions(child.Root,result);
        foreach(var child in root.AggregateAttachments)
        {
            if(child.Root is not null)CollectDiscardedParticleConstructions(child.Root,result);
            else if(child.Exhaust is not null)result.Add(child.Exhaust.Definition);
        }
        result.AddRange(root.DiscardedParticleConstructions);
    }
    private readonly Dictionary<(ulong Identity,ulong Generation),EnbRslObjectState> _retainedEmitterRslUpdates=[];
    private readonly HashSet<(ulong Identity,ulong Generation)> _retainedEmitterWrappersSeen=[];
    private readonly Dictionary<(ulong Identity,ulong Generation),int> _compatRslTaskMembers=[];
    private readonly List<string> _retainedEmitterErrors=[];
    private long _retainedEmitterTime;
    private readonly List<(RetainedEmitterKey Key,RetainedEmitterOwner Owner,uint Age)> _dazzleRetainedBuffers=[];

    private void AppendRetainedBufferDazzleCollision()
    {
        foreach(var (key,owner,age) in _dazzleRetainedBuffers)
        {
            //75DEE0 checks +178 (463090: bit100), not emitter visibility.
            //Buffer +D0 -> 462CD0 leaves shared hit state untouched.
            _dazzleCollisionRoots.Add(new(default,true,false,
                static (start,end,fraction,inside)=>(fraction,inside)) { ResolveSphere=Resolve,DiagnosticName=owner.Asset.Definition.Name });
            Vector4? Resolve()
            {
                try
                {
                    //95E990 invalidates the cached sphere after scene culling;
                    //463340 -> 960DB0 refreshes it only when actually queried.
                    if(owner.State.BoundsDirty)
                    {
                        owner.State.EnsureBounds(age);
                        if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(owner,owner.State.BorrowLiveBirths(),
                            ComputeStableParticleEmitterSalt(key.Path),age,owner.Scale,owner.State.MotionHistory,owner.MaximumSize,out var bounds,out var error))
                            throw new InvalidDataException(error);
                        owner.Bounds=bounds
                            .WorldSphere(owner.Asset.Definition.LocalSpace?owner.World:Matrix4x4.Identity);
                    }
                    return new(owner.Bounds.Center,owner.Bounds.Radius);
                }
                catch(InvalidDataException error)
                { _shipExhaustStatus+=$" · buffer collision {owner.Asset.Definition.Name}: {error.Message}"; return null; }
            }
        }
    }
    private sealed class RetainedEmitterOwner(EnbCurrentRetailShipExhaustAsset asset,uint gameId,long start,float scale,EnbRetailParticleRandomOwner? cloneSource=null) : IEnbParticleRenderOwner
    {
        public EnbParticleBufferDetailBudgetSemantics.Owner? DetailOwner;
        public readonly EnbCurrentRetailShipExhaustAsset Asset=asset;
        public readonly uint GameId=gameId;
        string IEnbParticleRenderOwner.RenderObjectName=>Asset.Definition.Name;
        string IEnbParticleRenderOwner.EmitterResourceName=>Asset.Key.ResourceName;
        uint IEnbParticleRenderOwner.GameId=>GameId;
        EnbRenderPassKind IEnbParticleRenderOwner.RenderPass=>EnbRenderPassKind.OrdinaryWorld;
        EnbCurrentRetailParticleEmitterDefinition IEnbParticleRenderOwner.Definition=>Asset.Definition;
        EnbW3dRenderTextureLocation? IEnbParticleRenderOwner.TextureLocation=>Asset.Texture;
        Matrix4x4 IEnbParticleRenderOwner.WorldTransform=>World;
        string IEnbParticleRenderOwner.HierarchyName=>"";
        int IEnbParticleRenderOwner.PivotIndex=>0;
        int IEnbParticleRenderOwner.BindingOrdinal=>0;
        string? IEnbParticleRenderOwner.RuntimeTextureName=>Asset.RuntimeTextureName;
        EnbResolvedTextureResource? IEnbParticleRenderOwner.RetainedTexture=>Asset.RetainedTexture;

        public readonly long Start=start;
        public readonly EnbRetailParticleSceneLifetime State=new(asset.Definition,0,randomOwner:cloneSource?.Clone());
        public readonly EnbCurrentRetailParticleEmitterRuntime.GeometryOwner Geometry=new(asset.Definition);
        public readonly float Scale=scale;
        public readonly uint Capacity=EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition);
        public readonly int LevelCount=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition));
        public int Level=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition))-1;
        public readonly float MaximumSize=EnbRetailParticleBufferBounds.MaximumSize(asset.Definition.Size,
            EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(asset.Definition.LifetimeSeconds),scale);
        public Matrix4x4 World=Matrix4x4.Identity;
        public EnbW3dWorldBounds Bounds;
        public float ProjectedDetail;
        public float LodBias=1;
        public bool SceneVisible;
        public LiveRootInstance? Instance;
        public LiveRootInstance? LastInstance;
        public ulong? LastVisualGeneration;
        public LiveRootInstance? PacketPoseInstance;
        public int SceneStateId=-1;
        public int VisibilityStateId=-1;
        public int ParentVisibilityStateId=-1;
        public int SceneEventCount;
        public (ulong Identity,ulong Generation)? SceneWrapperOwner;
        public RslWrapperOwner? RslSourceOwner;
        public (ulong Identity,ulong Generation)? RslSourceBinding;
        public bool ExternalPhysicalLego;
        public bool? OrdinaryHidden;
        public bool EngineBlocked;
        public long? ChildRemovalClock;
        public long? SceneRemovalClock;
    }

    // RSObject+30 retains a distinct RenderObj per state (0045EDC0).
    // Activation resets animation, not HLOD+E8 (0098E5D0/009C58E0).
    // Keep logical traversal paths unchanged; only retained owner keys use this.
    private static string RetainedModelOwnerPath(LiveRootInstance instance)
    {
        string path=instance.InstancePath;
        if(instance.IsRsl)
            for(var ancestor=instance;ancestor is not null;ancestor=ancestor.Parent)
                if(ancestor.Root.RslFactoryStateId>=0)
                {
                    if(instance.CachedModelOwnerState==ancestor.Root.RslFactoryStateId &&
                        instance.CachedModelOwnerPrefix==ancestor.InstancePath && instance.CachedModelOwnerPath is {} cached)return cached;
                    path=$"rsl/{ancestor.Root.RslFactoryStateId}"+path[ancestor.InstancePath.Length..];
                    instance.CachedModelOwnerState=ancestor.Root.RslFactoryStateId;
                    instance.CachedModelOwnerPrefix=ancestor.InstancePath;instance.CachedModelOwnerPath=path;
                    break;
                }
        return path;
    }

    private static RetainedEmitterKey RetainedEmitterOwnerKey(LiveRootInstance instance,float scale,Vector3 hsv)
    {
        object? factory=PhysicalParticleFactory(instance);
        return new(instance.Object.RenderIdentity,factory is null?(instance.IsRsl?instance.Object.RenderObjectGeneration:instance.Object.VisualGeneration):0,
            instance.Root.RslBoneNode is not null?"rsl-retained-model":RetainedModelOwnerPath(instance),
            instance.Root.PrimitiveFactoryIdentity,scale,hsv)
            {PhysicalFactory=factory,BoneFactory=instance.Root.RslBoneNode?.Runtime};
    }

    private static object? PhysicalParticleFactory(LiveRootInstance instance)
    {
        if(instance.IsRsl)return null;
        for(var parent=instance;parent is not null;parent=parent.Parent)
            if(parent.Root.PhysicalFactoryOwner is {} factory)return factory;
        return null;
    }

    private bool RetainedParticleOwnerIsLive(RetainedEmitterKey key,IReadOnlySet<(ulong Identity,ulong Generation)> live)
        =>key.PhysicalFactory is {} factory?_livePhysicalParticleFactories.Contains(factory):live.Contains((key.Owner,key.Generation));

    private readonly EnbRetainedMembershipSet<(ulong Identity,ulong Generation)> _frameEmitterResidents=new();
    private readonly EmitterPreparationScratch _emitterPreparationScratch=new();
    private sealed class EmitterPreparationScratch : IDisposable
    {
        private bool active;
        public readonly HashSet<LiveRootInstance> Removed=new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<LiveRootInstance> QueryOnly=new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<LiveRootInstance> Refreshed=new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<LiveRootInstance> Seen=new(ReferenceEqualityComparer.Instance);
        public readonly Stack<LiveRootInstance> Constructing=new();
        public EmitterPreparationScratch Rent()
        {
            if(active)return new EmitterPreparationScratch().Rent();
            active=true;return this;
        }
        public void Dispose()
        {
            Removed.Clear();QueryOnly.Clear();Refreshed.Clear();Seen.Clear();Constructing.Clear();active=false;
        }
    }
    private void PrepareRetainedEmitters(long now,RootTaskList roots,IReadOnlySet<(ulong Identity,ulong Generation)> live,bool beginFrame=true)
        =>PrepareRetainedEmittersForOwners(now,roots,live,beginFrame,null);

    private void PrepareRetainedEmittersForOwners(long now,RootTaskList roots,IReadOnlySet<(ulong Identity,ulong Generation)> live,bool beginFrame,IReadOnlySet<ulong>? refreshedOwners,CallbackRootRefreshScratch? refresh=null)
    {
        if(_shipExhaustOwner is {} source)
        {BorrowRetainedChildMembership(roots,source);PrepareScannerParticleMembers(roots,source);return;}
        if(_ownedSceneFrame && refresh is {PersistentTopology:true} &&
            TryRefreshBoundCallbackEmitters(now,live,refreshedOwners!,refresh))return;
        if(now<_retainedEmitterTime){_retainedEmitterCallbacks.Clear();_retainedEmitters.Clear();_removedRetainedEmitters.Clear();ClearCompositeMembership();_retainedCompositeFrameRoots.Clear();_discardedParticleFactories.Clear();_retainedEmitterRslUpdates.Clear();ClearRetainedWrapperAttachmentHistory();}
        if(beginFrame)_retainedEmitterErrors.Clear();
        if(beginFrame)_retainedEmitterErrors.AddRange(_boneSceneErrors);
        _livePhysicalParticleFactories.BeginFrame(_ownedSceneFrame);
        foreach(var instance in roots)
            if(PhysicalParticleFactory(instance) is {} factory && !(_ownedSceneFrame&&factory is PhysicalFactoryLifetime {RetainedReferences:>0}))_livePhysicalParticleFactories.Add(factory);
        //Pending Lego controllers still own their constructed RenderObjects.
        //Their scene membership may disappear without destroying the buffer.
        foreach(var root in _ownedSceneFrame?_compatLegoFactoryBindings.Select(binding=>binding.Particle).OfType<LiveCpuRoot>():_legoParticleFactories.Values)
            if(root.PhysicalFactoryOwner is {} factory)_livePhysicalParticleFactories.Add(factory);
        foreach(var owner in _ownedSceneFrame?_ordinaryBaseFactories.Values:EnumerateOrdinaryFactories())
            if(owner.Root?.PhysicalFactoryOwner is {} factory)_livePhysicalParticleFactories.Add(factory);
        foreach(var helper in _influenceHelperFactories.Values)
            if(helper.Root.PhysicalFactoryOwner is {} factory)_livePhysicalParticleFactories.Add(factory);
        PruneRetainedChildMembership(live);
        using var scratch=_emitterPreparationScratch.Rent();
        var removed=scratch.Removed;
        if(beginFrame)_retiringEmitterBuffers.Clear();
        _retainedEmitterTime=now;
        foreach(var (ownerKey,owner) in _retainedEmitters.SelectCandidates(refreshedOwners))
        {
            if(refreshedOwners is not null && !refreshedOwners.Contains(ownerKey.Owner))continue;
            if(beginFrame)owner.PacketPoseInstance=owner.LastInstance;
            owner.Instance=null;
        }
        var constructing=scratch.Constructing;
        void FinishConstruction()
        {
            var instance=constructing.Pop();
            if(instance.Root.ParticleConstructionPrepared || instance.Root.DiscardedParticleConstructions.Count==0 || !_discardedParticleFactories.Add(RetainedEmitterOwnerKey(instance,1,Vector3.Zero)))return;
            //009A3A50 creates wrapper attachments after the base and its children,
            //even when +80/+84 insertion releases every temporary child.
            try
            {
                foreach(var definition in instance.Root.DiscardedParticleConstructions)_=new EnbRetailParticleRandomOwner(definition);
            }
            catch(InvalidDataException ex){_retainedEmitterErrors.Add($"Discarded particle constructor: {ex.Message}");}
        }
        var queryOnly=scratch.QueryOnly;
        queryOnly.UnionWith(_hiddenCombatSourceRoots);queryOnly.UnionWith(_hiddenRslEmitterRoots);
        var refreshedHierarchy=scratch.Refreshed;
        foreach(var instance in (refresh?.Current??roots).Concat(_hiddenRslEmitterRoots).Concat(_hiddenCombatSourceRoots))
        {
            if(refreshedOwners is not null && !refreshedOwners.Contains(instance.Object.RenderIdentity))continue;
            if(!scratch.Seen.Add(instance))continue;
            bool IsAncestor(LiveRootInstance candidate)
            {
                for(var parent=instance.Parent;parent is not null;parent=parent.Parent)
                    if(ReferenceEquals(parent,candidate))return true;
                return false;
            }
            while(constructing.TryPeek(out var previous) && !IsAncestor(previous))FinishConstruction();
            constructing.Push(instance);
            if(instance.Root.RetainedEmitter is not {} asset)continue;
            try
            {
                if(!TryRefreshHierarchyOnce(instance,refreshedHierarchy,out var error))throw new InvalidDataException(error);
                if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out float scale,out var hsv))
                    throw new InvalidDataException("Retained emitter factory transform unavailable");
                var key=RetainedEmitterOwnerKey(instance,scale,hsv);
                if(_removedRetainedEmitters.Contains(key)){removed.Add(instance);continue;}
                if(!_retainedEmitters.TryGetValue(key,out var owner))
                {
                    if(hsv!=Vector3.Zero && instance.Root.FactoryParticleRandom is null)
                    {
                        var assets=_retailAssetsAccessor()??throw new InvalidDataException("Particle HSV assets unavailable");
                        var shifted=new EnbCurrentRetailRObjParticleAsset(asset.Key,asset.Texture,asset.Definition)
                            {RuntimeTextureName=asset.RuntimeTextureName,RetainedTexture=asset.RetainedTexture};
                        shifted=shifted.ApplyHsv(assets,hsv);
                        asset=asset with {Definition=shifted.Definition,Texture=shifted.Texture,RuntimeTextureName=shifted.RuntimeTextureName,RetainedTexture=shifted.RetainedTexture};
                    }
                    if(!asset.IsReady)throw new InvalidDataException(asset.Error??asset.DeferredReason??"Retained emitter texture unavailable");
                    if(asset.Definition.MaximumEmissions==0)
                        EnbCurrentRetailShipParticleRuntime.Validate(asset.Definition,allowInheritedMotion:true);
                    else EnbCurrentRetailRObjParticleAsset.ValidateAnimated(asset.Definition);
                    owner=new(asset,instance.Object.GameId,now,scale,instance.Root.FactoryParticleRandom);
                    owner.VisibilityStateId=instance.Root.RslFactoryStateId;
                    //0098FB10 only forwards hidden to direct aggregate emitter15.
                    if(instance.IsAggregateHlodChild && instance.Parent is {Root.Hlod:not null} parent)
                        owner.ParentVisibilityStateId=parent.Root.RslFactoryStateId;
                    if(instance.IsRsl)
                        for(var ancestor=instance;ancestor is not null;ancestor=ancestor.Parent)
                            if(ancestor.Root.RslFactoryStateId is int id && id>=0 &&
                                TryGetRetainedEmitterUpdate((key.Owner,key.Generation),owner,out var stateUpdate) &&
                                stateUpdate.Graph?.TryFindState(id,out var stateResource)==true &&
                                stateResource.Definition.Type==EnbNext.Assets.EnbRslStateType.Object)
                            {owner.SceneStateId=id;break;}
                    _retainedEmitters.Add(key,owner);
                    owner.State.SceneAttachmentChanged+=attached=>
                    {
                        if(attached && RetainedEmitterSceneModel(key,owner) is {} model)
                            _retainedEmitterCallbacks.Register((key,false),model);
                        else if(attached)_retainedEmitterCallbacks.Register((key,false));
                        else _retainedEmitterCallbacks.Remove((key,false));
                    };
                }
                if((instance.PreparedEmitter is not {} binding || !ReferenceEquals(binding.Owner,owner)))
                    instance.PreparedEmitter=new(key,owner);
                BindRetainedLegoEmitterScene(key,owner,instance);
                owner.World=world;owner.LastInstance=instance;owner.LastVisualGeneration=instance.Object.VisualGeneration;
                owner.Instance=queryOnly.Contains(instance)?null:instance;
                owner.State.UpdateTransform(world);
                owner.LodBias=instance.Root.LodBias;
                if(owner.SceneRemovalClock is not null)removed.Add(instance);
            }
            catch(InvalidDataException ex){_retainedEmitterErrors.Add($"Retained emitter '{asset.Definition.Name}': {ex.Message}");}
        }
        while(constructing.Count!=0)FinishConstruction();
        ApplyRetainedChildMembership(roots,removed,refresh);
        _discardedParticleFactories.RemoveWhere(key=>!RetainedParticleOwnerIsLive(key,live));
        PrepareCallbackEmitterLifetimes(now,live,refreshedOwners);
        foreach(var key in _retainedEmitterRslUpdates.Keys.Where(key=>!live.Contains(key)).ToArray())_retainedEmitterRslUpdates.Remove(key);
        //Packet setters precede radar refresh. Save this frame's ordinary
        //wrapper scene membership for next frame's ordered observations,
        //including wrappers with no visible/current model. Mere lifetime in
        //`live` is not scene attachment (461650 ->98DFA0 ->97DE40).
        //Effect wrappers retain their separate effect-owner admission policy.
        if(_ownedSceneFrame)
        {
            _retainedEmitterWrappersSeen.Clear();
            _retainedEmitterWrappersSeen.UnionWith(_compatRslCallbackMembers);
            foreach(var key in _compatRslTaskMembers.Keys)_retainedEmitterWrappersSeen.Add(key);
            CommitWrapperAttachments();
            return;
        }
        var effectWrappers=(_shipExhaustOwner??this)._effectRslOwners.Values
            .Select(owner=>(owner.Object.RenderIdentity,owner.Object.RenderObjectGeneration)).ToHashSet();
        _retainedEmitterWrappersSeen.IntersectWith(effectWrappers);
        _retainedEmitterWrappersSeen.IntersectWith(live);
        _retainedEmitterWrappersSeen.UnionWith(_ownedSceneFrame?_compatRslCallbackMembers:_ordinaryRslCallbackMembers);
        _retainedEmitterWrappersSeen.UnionWith(_effectRslCallbackMembers);
        _retainedEmitterWrappersSeen.UnionWith(roots.Where(instance=>instance.IsRsl&&(!_ownedSceneFrame||instance.RslAttachment is null))
            .Select(instance=>(instance.Object.RenderIdentity,instance.Object.RenderObjectGeneration)));
        if(_ownedSceneFrame)CommitWrapperAttachments();
    }

    private void PrepareCallbackEmitterLifetimes(long now,IReadOnlySet<(ulong Identity,ulong Generation)> live,IReadOnlySet<ulong>? refreshedOwners)
    {
        foreach(var (key,owner) in _retainedEmitters.SelectCandidates(refreshedOwners))
        {
            if(refreshedOwners is not null && !refreshedOwners.Contains(key.Owner))continue;
            uint age=checked((uint)(now-owner.Start));
            bool destroy=!RetainedParticleOwnerIsLive(key,live) || owner.Instance is null &&
                ((_renderingMovie is not null && key.Path.Contains("/movie/damage/",StringComparison.Ordinal)) ||
                 key.Path.Contains("/shipdamage/damage/",StringComparison.Ordinal));
            //Packet setters precede a later removal in the same snapshot. Replay
            //their retained membership before marking the buffer destroyed.
            try{PrepareRetainedEmitterState(key,owner,age);}
            catch(InvalidDataException ex){_retainedEmitterErrors.Add($"Retained emitter '{owner.Asset.Definition.Name}': {ex.Message}");}
            if(destroy)
            {
                owner.State.Destroy(age,owner.World,owner.Scale);
                // The autonomous buffer retains births, motion and its last
                // visual generation. After emitter destruction it cannot replay
                // another owning-model setter; release the render-node graph.
                owner.LastVisualGeneration=owner.LastInstance?.Object.VisualGeneration??owner.LastVisualGeneration;
                owner.Instance=null;owner.LastInstance=null;owner.PacketPoseInstance=null;owner.RslSourceOwner=null;owner.RslSourceBinding=null;
            }
        }
    }

    private static bool RetainedEmitterSceneAttached(LiveRootInstance instance)
    {
        for(var child=instance;child is not null;child=child.Parent)
            if(child.IsOrdinaryHlodChild && child.OrdinaryHlodLevelOrdinal is int level && child.Parent?.Root.Hlod is {} hlod &&
                level!=(child.Parent.HlodSelection?.LevelOrdinal??EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod)))return false;
        return true;
    }

    private void BindRetainedLegoEmitterScene(RetainedEmitterKey key,RetainedEmitterOwner owner,LiveRootInstance instance)
    {
        if(instance.Root.RslBoneNode is not null && instance.IsRsl &&
            TryGetRetainedEmitterUpdate((key.Owner,key.Generation),owner,out var boneUpdate))
            for(var parent=instance;parent is not null;parent=parent.Parent)
                if(boneUpdate.Graph?.TryFindState(parent.Root.RslFactoryStateId,out var state)==true&&
                    state.Definition.Type==EnbNext.Assets.EnbRslStateType.Object)
                {owner.SceneStateId=parent.Root.RslFactoryStateId;break;}
        if(key.PhysicalFactory is {} factory && _legoPhysicalOwners.TryGetValue(key.Owner,out var physical) &&
            physical.ChildModels.ContainsKey(factory))
        {
            for(var ancestor=instance.Parent;ancestor is not null;ancestor=ancestor.Parent)
            {
                if(!ancestor.IsRsl || ancestor.Root.RslFactoryStateId<0)continue;
                var binding=(ancestor.Object.RenderIdentity,ancestor.Object.RenderObjectGeneration);
                if(owner.SceneWrapperOwner!=binding)owner.SceneEventCount=0;
                owner.SceneWrapperOwner=binding;
                owner.SceneStateId=ancestor.Root.RslFactoryStateId;
                owner.ExternalPhysicalLego=true;
                //98FB10 forwards only to direct aggregate class15 children.
                owner.ParentVisibilityStateId=ReferenceEquals(instance.Parent,ancestor) && ancestor.Root.Hlod is not null
                    ?owner.SceneStateId:-1;
                return;
            }
        }
        if(owner.SceneStateId>=0)owner.SceneWrapperOwner=(key.Owner,key.Generation);
    }

    private LegoPhysicalModel? RetainedEmitterSceneModel(RetainedEmitterKey key,RetainedEmitterOwner emitter)
    {
        var model=RetainedInstanceSceneModel(key,emitter.SceneStateId,emitter.LastInstance);
        return model?.Root.RetainedEmitter is not null?model:null;
    }

    private LegoPhysicalModel? RetainedInstanceSceneModel(RetainedEmitterKey key,int stateId,LiveRootInstance? initial)
        => RetainedInstanceSceneModel(key.Owner,key.Generation,key.PhysicalFactory,stateId,initial);

    private LegoPhysicalModel? RetainedInstanceSceneModel(ulong identity,ulong generation,object? physicalFactory,
        int stateId,LiveRootInstance? initial)
    {
        if(initial?.Root.RslBoneNode is {} bone)
        {
            var retained=BoneLegoSceneModel(bone);
            retained.SceneInstance=initial;
            return retained;
        }
        LegoPhysicalModel? model=null;
        if(_legoPhysicalOwners.TryGetValue(identity,out var physical))
        {
            if(physicalFactory is {} factory)physical.ChildModels.TryGetValue(factory,out model);
            else physical.RootModels.TryGetValue(stateId,out model);
            if(model is null&&physical.RootModels.TryGetValue(-1,out var root)&&
                ReferenceEquals(root.Root.PhysicalFactoryOwner,physicalFactory))model=root;
        }
        if(model is null)
            foreach(var wrapper in _legoRslOwners.Values)
                if(wrapper.Object.RenderIdentity==identity && wrapper.Object.RenderObjectGeneration==generation)
                {wrapper.Models.TryGetValue(stateId,out model);break;}
        if(model is null)return null;
        using var pathScope=_sceneModelPathScratch.Rent();
        var path=pathScope.Path;
        var instance=initial;
        while(instance is not null&&!ReferenceEquals(instance.Root.PrimitiveFactoryIdentity,model.Root.PrimitiveFactoryIdentity))
        {path.Push(instance);instance=instance.Parent;}
        if(instance is null)return model.Root.RetainedEmitter is not null?model:null;
        model.SceneInstance=instance;
        while(path.TryPop(out var child))
        {
            if(child.ParentStorageOrdinal is not int ordinal)return null;
            model=AuthoredLegoSceneChild(model,ordinal,child.Root);
            model.SceneInstance=child;
        }
        return model;
    }

    private bool TryGetRetainedEmitterUpdate((ulong Identity,ulong Generation) key,RetainedEmitterOwner emitter,
        out EnbRslObjectState update)
    {
        if(_ownedSceneFrame && emitter.RslSourceBinding!=key)
        {
            emitter.RslSourceBinding=key;emitter.RslSourceOwner=null;
            if(_ownedScene!.TryFind(key.Identity,out var node)&&node.Definition.RenderObjectGeneration==key.Generation)
                emitter.RslSourceOwner=node.State.Attachment;
            else if(_effectRslRootOwners.TryGetValue(key.Identity,out var effect)&&effect.Object.RenderObjectGeneration==key.Generation)
                emitter.RslSourceOwner=effect.Attachment;
        }
        if(_ownedSceneFrame&&emitter.RslSourceOwner is {} source){update=source.CurrentState!;return update is not null;}
        return _retainedEmitterRslUpdates.TryGetValue(key,out update!);
    }

    private void PrepareRetainedEmitterState(RetainedEmitterKey key,RetainedEmitterOwner owner,uint age)
    {
        if(owner.State.Destroyed)return;
        var instance=owner.Instance;
        bool attached=instance is not null && RetainedEmitterSceneAttached(instance) &&
            owner.SceneRemovalClock is null;
        var bone=owner.LastInstance?.Root.RslBoneNode;
        if(instance is not null)owner.State.UpdateTransform(owner.World);
        var wrapperKey=owner.SceneWrapperOwner??(key.Owner,key.Generation);
        if(owner.SceneStateId>=0 && TryGetRetainedEmitterUpdate(wrapperKey,owner,out var update))
        {
            bool wrapperAttached=(_ownedSceneFrame&&owner.RslSourceOwner is {} sourceOwner?sourceOwner.PreviousAttached:_retainedEmitterWrappersSeen.Contains(wrapperKey)) && !owner.State.Destroyed;
            bool selectedStorage=owner.LastInstance is {} retained && RetainedEmitterSceneAttached(retained);
            bool Member(int? state,int? position=null,bool direct=false)=>state is int id &&
                (owner.ExternalPhysicalLego?PhysicalLegoMemberAt(wrapperKey.Item1,wrapperKey.Item2,id,key.PhysicalFactory!,position,direct):id==owner.SceneStateId);
            if(_ownedSceneFrame&&owner.RslSourceOwner is {} callbackOwner?callbackOwner.CallbackAttached:(_ordinaryRslCallbackMembers.Contains(wrapperKey)||_effectRslCallbackMembers.Contains(wrapperKey)))
                attached=Member(update.CurrentObjectStateId)&&selectedStorage;
            int? initialCurrent=null;
            if(owner.SceneEventCount==0 && wrapperAttached)
                for(int initial=0;initial<Math.Min(update.InitialObjectSceneEventCount,update.ObjectSceneEvents.Count);initial++)
                {
                    var observation=update.ObjectSceneEvents[initial];
                    if(observation.Kind==EnbCurrentRetailRslObjectSceneEventKind.SwitchCurrent)initialCurrent=observation.StateId;
                }
            for(int i=owner.SceneEventCount;i<update.ObjectSceneEvents.Count;i++)
            {
                //The constructor's events precede the wrapper's actual SceneAdd.
                if(i==update.InitialObjectSceneEventCount && owner.SceneEventCount==0 && wrapperAttached)
                    owner.State.SetSceneAttached(Member(initialCurrent,i) && selectedStorage);
                var observation=update.ObjectSceneEvents[i];
                if(observation.Kind is EnbCurrentRetailRslObjectSceneEventKind.Hidden or EnbCurrentRetailRslObjectSceneEventKind.BoneHidden)
                {
                    if(bone is not null?BoneHiddenObservationApplies(bone,wrapperKey,observation):owner.ExternalPhysicalLego
                        ? Member(observation.StateId,observation.Ordinal,direct:true)
                        : observation.StateId==owner.VisibilityStateId || observation.StateId==owner.ParentVisibilityStateId)
                    {
                        Matrix4x4 eventWorld=owner.World;
                        if(!observation.Hidden && owner.State.SceneAttached && !owner.State.Started && !owner.State.AnimationHidden &&
                            !TryResolveRetainedEmitterEventWorld(key,owner,update,observation,out eventWorld,out var poseError))
                            throw new InvalidDataException(poseError);
                        owner.State.SetHidden(age,eventWorld,owner.Scale,observation.Hidden);
                        //The setter snapshots its event pose; the subsequent buffer
                        //callback still observes the published current transform.
                        owner.State.UpdateTransform(owner.World);
                    }
                }
                else if(i>=update.InitialObjectSceneEventCount && wrapperAttached)
                {
                    if(Member(observation.PreviousStateId,observation.Ordinal))owner.State.SetSceneAttached(false);
                    if(Member(observation.StateId,observation.Ordinal) && selectedStorage)owner.State.SetSceneAttached(true);
                }
            }
            owner.SceneEventCount=update.ObjectSceneEvents.Count;
        }
        if(bone is not null && bone.StateId is int boneState &&
            TryGetRetainedEmitterUpdate(wrapperKey,owner,out var boneUpdate) &&
            boneUpdate.Graph?.TryFindState(boneState,out var boneDefinition)==true &&
            boneDefinition.Definition.Type==EnbNext.Assets.EnbRslStateType.Bone)
            attached &= bone.Runtime.Parent is not null;
        owner.State.SetSceneAttached(attached);
        if(instance is null)
        {
            if(bone is not null&&owner.State.Hidden!=bone.Runtime.Hidden)
                owner.State.SetHidden(age,owner.World,owner.Scale,bone.Runtime.Hidden);
            return;
        }
        //Direct RSL hidden setters come only from the ordered observations.
        //Other roots preserve their actual flag changes, not synthetic resets.
        if(bone is not null || !owner.ExternalPhysicalLego && owner.VisibilityStateId<0 && owner.ParentVisibilityStateId<0)
        {
            bool hidden=instance.MovieHidden || (bone?.Runtime.Hidden??instance.Root.RenderObjectHidden) ||
                bone is null && instance is {IsAggregateHlodChild:true,Parent:{} aggregateParent} && aggregateParent.Root.RenderObjectHidden;
            if (_renderingMovie is { } movie)
            {
                hidden |= movie.Visuals.TryGetValue(instance.Object.RenderIdentity, out var visual) && visual.Hidden;
                for (var ancestor = instance.Parent; ancestor is not null; ancestor = ancestor.Parent) hidden |= ancestor.MovieHidden;
            }
            if(bone is not null?owner.State.Hidden!=hidden:owner.OrdinaryHidden!=hidden)
            {owner.State.SetHidden(age,owner.World,owner.Scale,hidden);owner.OrdinaryHidden=hidden;}
        }
        bool animatedChild=!instance.IsExternalRslModel && (instance.IsOrdinaryHlodChild||instance.IsAggregateHlodChild||instance.IsExternalLegoChild) && instance.Parent?.Root.Collection is null;
        bool engineAllowed=instance.IsExternalRslModel||RetainedEngineChildVisible(instance);
        //0098F090 invokes +18C even for an unchanged pivot flag. Collection
        //visibility never propagates this setter to its children.
        if(animatedChild || owner.EngineBlocked || !engineAllowed)
            owner.State.SetAnimationHidden(age,owner.World,owner.Scale,!instance.PivotVisible||!engineAllowed);
        owner.EngineBlocked=!engineAllowed;
    }
    private readonly HashSet<RetainedEmitterKey> _retainedEmitterPreparationFailed=[];
    private void UpdateRetainedEmitters(long now,RootTaskList roots)
        =>UpdateRetainedEmittersForOwners(now,roots,null);

    private void UpdateRetainedEmittersForOwners(long now,RootTaskList roots,IReadOnlySet<ulong>? refreshedOwners)
    {
        if(_shipExhaustOwner is not null)return;
        var failed=_retainedEmitterPreparationFailed;
        if(refreshedOwners is null)failed.Clear();
        else failed.RemoveWhere(key=>refreshedOwners.Contains(key.Owner));
        using var scratch=_emitterPreparationScratch.Rent();
        var refreshedHierarchy=scratch.Refreshed;
        // State preparation only changes scene callback membership. Actual
        // emitter dictionary retirement runs after the callback walk below.
        foreach(var (key,owner) in _retainedEmitters.SelectCandidates(refreshedOwners))
        {
            if(refreshedOwners is not null && !refreshedOwners.Contains(key.Owner))continue;
            try
            {
                uint age=checked((uint)(now-owner.Start));
                if(owner.Instance is {} instance)
                {
                    if(!TryRefreshHierarchyOnce(instance,refreshedHierarchy,out var error))throw new InvalidDataException(error);
                    if(TryResolvePrimitiveFactoryTransform(instance,out var world,out _,out _))owner.World=world;
                }
                PrepareRetainedEmitterState(key,owner,age);
            }
            catch(InvalidDataException ex){failed.Add(key);_retainedEmitterErrors.Add($"Retained emitter '{owner.Asset.Definition.Name}': {ex.Message}");}
        }
        // A wrapper callback only changes its owned models. The callback
        // dispatcher and the end-of-walk retirement sweep remain installed
        // once for the whole scene walk, including owners added during it.
        if(refreshedOwners is not null)return;
        _retainedEmitterCallbacks.SetCallback(callback=>
        {
            var key=callback.Key;var owner=_retainedEmitters[key];
            if(failed.Contains(key))return;
            try
            {
                uint age=checked((uint)(now-owner.Start));
                if(callback.Buffer)owner.State.BufferCallback(age,_ringFrameDelta,owner.World,owner.Scale);
                else if(owner.State.EmitterCallback(owner.World))_retainedEmitterCallbacks.Register((key,true));
                if(!callback.Buffer && owner.State.SceneAttached && owner.State.Emitter.HasCompletedFiniteRun &&
                    IsRetainedBoneParticle(key,owner))
                    QueueParticleSceneRemoval(owner.State,false,()=>RetireBoneParticle(key,owner,now));
                if(!callback.Buffer && owner.State.SceneAttached && owner.State.Emitter.HasCompletedFiniteRun && owner.Instance is {Parent:{} parent,ParentStorageOrdinal:not null} &&
                    !IsRetainedBoneParticle(key,owner) && (parent.Root.Hlod is not null || parent.Root.Collection is not null) && owner.ChildRemovalClock is null)
                {
                    owner.ChildRemovalClock=now;
                    QueueParticleSceneRemoval(owner.State,false,()=>RetireRetainedChild(key,owner,now));
                }
                //9662E0 -> 942160 -> 965DC0 -> 97DE40 removes a scene root,
                //but the game object's +AC reference (6F33F0) still owns it.
                //Direct RSL roots instead dispatch parent+6C and stay attached.
                if(!callback.Buffer && owner.State.SceneAttached && owner.State.Emitter.HasCompletedFiniteRun &&
                    owner.Instance is {Parent:null,IsRsl:false} && owner.SceneRemovalClock is null)
                {
                    owner.SceneRemovalClock=now;
                    QueueParticleSceneRemoval(owner.State,false,()=>owner.State.SetSceneAttached(false));
                }
                if(callback.Buffer && owner.State.CanRetire)
                {
                    //95E990 queues965E00(type2);9662E0 removes only after
                    //ordinary traversal. Keep this frame's buffer consumer.
                    _retiringEmitterBuffers.Add(new(key,owner));
                    QueueParticleSceneRemoval(owner.State,true,()=>
                    {
                        _retainedEmitterCallbacks.Remove((key,true));
                        _retainedEmitterCallbacks.Remove((key,false));
                        _retainedEmitters.Remove(key);
                    });
                }
            }
            catch(InvalidDataException ex){_retainedEmitterErrors.Add($"Retained emitter '{owner.Asset.Definition.Name}': {ex.Message}");}
        });
        _particleCallbackFinishes.Add(()=>
        {
        //An object destroyed before its first callback never registered a buffer.
        foreach(var (key,owner) in _retainedEmitters.Where(p=>p.Value.State.Destroyed&&!p.Value.State.BufferAttached).ToArray())
            _retainedEmitters.Remove(key);
        });
    }

    private Dictionary<ulong,EnbW3dWorldBounds> ResolveRetainedScannerFallbackSpheres(RootTaskList roots,long now)
    {
        var result=new Dictionary<ulong,EnbW3dWorldBounds>();
        var source=_shipExhaustOwner??this;
        //5C2920 -> proxy+60/+64 ->98E020/98E050 enumerates all retained
        //levels then secondary children, independent of draw admission/detail.
        foreach(var root in roots.Where(r=>r.Parent is null&&r.Root.Hlod is not null&&
            r.CurrentRetailWorldSphere is {Radius:<=0}))
        {
            var children=new List<EnbRetailScannerBounds.Child>();
            bool resolved=true;
            foreach(var child in roots.Where(r=>ReferenceEquals(r.Parent,root)&&r.Root.RetainedEmitter is not null)
                .OrderBy(r=>r.ParentStorageOrdinal))
            {
                if(!TryResolvePrimitiveFactoryTransform(child,out _,out float scale,out var hsv))
                {resolved=false;break;}
                var key=RetainedEmitterOwnerKey(child,scale,hsv);
                if(!source._retainedEmitters.TryGetValue(key,out var owner))
                    owner=source._retiringEmitterBuffers.FirstOrDefault(p=>p.Key==key).Value;
                if(owner is null){resolved=false;break;}
                try
                {
                    uint age=checked((uint)(now-owner.Start));
                    if(owner.State.BoundsDirty)
                    {
                        owner.State.EnsureBounds(age);
                        if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(owner,owner.State.BorrowLiveBirths(),
                            ComputeStableParticleEmitterSalt(key.Path),age,owner.Scale,owner.State.MotionHistory,owner.MaximumSize,out var bounds,out var error))
                            throw new InvalidDataException(error);
                        owner.Bounds=bounds
                            .WorldSphere(owner.Asset.Definition.LocalSpace?owner.World:Matrix4x4.Identity);
                    }
                    children.Add(new(15,owner.Bounds));
                }
                catch(InvalidDataException error)
                {source._retainedEmitterErrors.Add($"Scanner emitter bound '{owner.Asset.Definition.Name}': {error.Message}");resolved=false;break;}
            }
            if(!resolved)continue;
            var sphere=EnbRetailScannerBounds.Resolve(root.CurrentRetailWorldSphere!.Value,children).Sphere;
            if(sphere.IsValid)result[root.Object.RenderIdentity]=sphere;
        }
        return result;
    }

    private List<(RetainedEmitterOwner Owner,EnbRenderDetailOwner Candidate)> PrepareRetainedEmitterCandidates(EnbResolvedRenderCamera camera,long now)
    {
        var source=_shipExhaustOwner??this;
        var result=_detailRetainedEmitterOwners;result.Clear();
        if(_shipExhaustOwner is not null)return result;
        foreach(var (key,owner) in source._retainedEmitters)
        {
            owner.SceneVisible=false;
            if(!owner.State.BufferAttached)continue;
            try
            {
                uint age=checked((uint)(now-owner.Start));
                if(owner.State.BoundsDirty)
                {
                    owner.State.EnsureBounds(age);
                    if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(owner,owner.State.BorrowLiveBirths(),
                        ComputeStableParticleEmitterSalt(key.Path),age,owner.Scale,owner.State.MotionHistory,owner.MaximumSize,out var bounds,out var error))
                        throw new InvalidDataException(error);
                    owner.Bounds=bounds
                        .WorldSphere(owner.Asset.Definition.LocalSpace?owner.World:Matrix4x4.Identity);
                }
                owner.SceneVisible=owner.Asset.Definition.RenderMode is 3 or 4 || !FrustumCullingEnabled ||
                    EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,owner.Bounds);
                if(!owner.SceneVisible)continue;
                if(!EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,owner.Bounds,owner.Capacity,
                    owner.MaximumSize,owner.ProjectedDetail,out var projected))throw new InvalidDataException("Particle buffer detail metric unavailable");
                owner.ProjectedDetail=projected.Smoothed;
                result.Add((owner,(owner.DetailOwner ??= new($"world-particle/{key.Owner}/{key.Path}",owner.Capacity,(int)owner.Asset.Definition.RenderMode,owner.LevelCount)).Update(owner.Level,owner.ProjectedDetail,owner.LodBias)));
            }
            catch(InvalidDataException ex){source._retainedEmitterErrors.Add($"Retained buffer '{owner.Asset.Definition.Name}': {ex.Message}");}
        }
        return result;
    }

    private void AppendRetainedEmitters(List<EnbW3dDrawTask> packets,EnbResolvedRenderCamera camera,
        EnbW3dLightEnvironment lights,long now,HashSet<(ulong,ulong)>? viewOwners=null)
    {
        _dazzleRetainedBuffers.Clear();
        var source=_shipExhaustOwner??this;int draws=0,rejected=source._retainedEmitterErrors.Count;
        string? failure=source._retainedEmitterErrors.FirstOrDefault();
        var buffers=source._retainedEmitters;
        foreach(var (key,owner) in buffers)
        {
            if(_shipExhaustOwner is not null && !_scannerParticleMembers.Contains(key))continue;
            if(viewOwners is not null && !(key.PhysicalFactory is not null?viewOwners.Any(o=>o.Item1==key.Owner):viewOwners.Contains((key.Owner,key.Generation))) || !owner.State.BufferAttached || !owner.SceneVisible && _shipExhaustOwner is null)continue;
            _dazzleRetainedBuffers.Add((key,owner,checked((uint)(now-owner.Start))));
            owner.State.AdvanceForRender(checked((uint)(now-owner.Start)));
            if(!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(owner.Asset.Key,key.Owner,owner.LastVisualGeneration??key.Generation,owner,camera,lights,
                ComputeStableParticleEmitterSalt(key.Path),0,now-owner.Start,out var packet,out _,out var error,
                particleSizeScale:owner.Scale,worldBirths:owner.State.BorrowLiveBirths(),
                activeEmissionRun:owner.State.Emitter.HasLineEndpoint?owner.State.Emitter.EmissionRun:null,motionHistory:owner.State.MotionHistory,
                internalDetailLevel:owner.LevelCount-1-owner.Level,currentRetailFactoryScale:true,cullToCamera:FrustumCullingEnabled,frameStreams:_particleStreamFrame,geometryOwner:_retainedOrdinaryMeshes&&!_immutableParticleStreams?owner.Geometry:null))
            {rejected++;failure??=error;continue;}
            if(packet is not null){packets.Add((packet).ForkTask().SetRenderInstancePath("retained-emitter/"+key.Path));draws++;}
        }
        _shipExhaustStatus+=$" · retained emitters {draws} rejected {rejected}"+(failure is null?"":" · "+failure);
    }
}
