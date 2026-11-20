using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private static LiveCpuRoot CreateRetainedSphereRoot(EnbW3dRenderResource parent,string name,EnbRetailSphereAttachment sphere)
    {
        var empty=parent.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren([]).SetExternalRenderObjects([]);
        return new(false,empty,null,new(StringComparer.OrdinalIgnoreCase),name,name,null,null,[]){Sphere=sphere};
    }

    private readonly record struct WorldSphereKey(ulong Identity,ulong Generation,string Path,object Factory,float Scale,Vector3 Hsv)
    {
        public object? PhysicalFactory {get;init;}
    }
    private readonly Dictionary<WorldSphereKey, WorldSphereOwner> _worldSpheres = [];
    private readonly HashSet<WorldSphereKey> _worldSphereObserved = [];
    private readonly Dictionary<(ulong Identity,ulong Generation),EnbRslObjectState> _worldSphereRslUpdates=[];
    private sealed class WorldSpherePlayback(EnbRetailSphereValues initial)
    {
        public readonly EnbRetailSpherePlayback Clock=new();
        public EnbRetailSphereValues Values=initial;
        public int Level=10;
    }
    private sealed class WorldSphereOwner(IEnbRenderObject owner, EnbRetailSphereAttachment binding, string path)
    {
        public IEnbRenderObject Owner = owner;
        public readonly EnbRetailSphereAttachment Binding = binding;
        public string Path = path;
        public WorldSpherePlayback Playback=new(binding.Asset.Properties.InitialValues);
        public bool BorrowedPlayback;
        public EnbRetailSpherePlayback Clock=>Playback.Clock;
        public EnbRetailSphereValues Values {get=>Playback.Values;set=>Playback.Values=value;}
        public Matrix4x4 World;
        public int Level {get=>Playback.Level;set=>Playback.Level=value;}
        public bool Visible = true;
        public bool SceneVisible = true;
        public int RslStateId = -1;
        public ulong VisibilityTransitions;
        public List<WorldSphereOwner>? VisibilityOwners;
        public LiveRootInstance? Parent;
        public LiveRootInstance? RetainedInstance;
    }

    private void ObserveWorldSphere(IEnbRenderObject obj, EnbRetailSphereAttachment sphere, Matrix4x4 world, string path, bool visible = true, LiveRootInstance? parent = null,
        float factoryScale=1,Vector3 factoryHsv=default)
    {
        var key = WorldSphereOwnerKey(obj,sphere,parent,path,factoryScale,factoryHsv);
        WorldSphereOwner? borrowed=null;
        if(_shipExhaustOwner is {} source)
        {
            if(!source._worldSpheres.TryGetValue(key,out borrowed) && parent is {} instance &&
                TryResolveScannerPrimitiveSource(instance,source,out var retained) && retained.Root.Sphere is {} sourceSphere &&
                TryResolvePrimitiveFactoryTransform(retained,out _,out float scale,out var hsv))
            {
                ResolvePrimitivePropertyInputs(retained,ref scale,ref hsv);
                source._worldSpheres.TryGetValue(WorldSphereOwnerKey(retained.Object,sourceSphere,retained,retained.InstancePath,scale,hsv),out borrowed);
            }
            if(borrowed is null && parent is {} firstInstance && ScannerSourceRetainsFactory(firstInstance,source))
                borrowed=source.RetainWorldSphereOwner(key,obj,sphere,path,parent,factoryScale,factoryHsv);
        }
        _worldSphereObserved.Add(key);
        var state=RetainWorldSphereOwner(key,obj,sphere,path,parent,factoryScale,factoryHsv,borrowed?.Binding);
        if(state is null)return;
        state.Owner = obj;
        if(borrowed is not null){state.Playback=borrowed.Playback;state.BorrowedPlayback=true;}
        state.Path = path;
        state.Visible = visible;
        state.Parent = parent;
        // Sphere0090B060 reconciles admission and resets progress on either
        // visibility transition; sampled properties remain owned by the sphere.
        if(!state.BorrowedPlayback)state.Clock.ReconcileVisibility(visible);
        state.World = world;
    }

    private WorldSphereOwner? RetainWorldSphereOwner(WorldSphereKey key,IEnbRenderObject obj,
        EnbRetailSphereAttachment sphere,string path,LiveRootInstance? parent,float scale,Vector3 hsv,
        EnbRetailSphereAttachment? configured=null)
    {
        if(_worldSpheres.TryGetValue(key,out var state))return state;
        if(configured is not null)sphere=configured;
        else
        {
            if(hsv!=Vector3.Zero)
            {
                if(_retailAssetsAccessor() is not {} assets)return null;
                sphere=sphere.ApplyHsv(assets,hsv);
            }
            sphere=sphere with {Asset=sphere.Asset.Scale(scale)};
        }
        _worldSpheres[key]=state=new(obj,sphere,path);
        //Object+184 targets its root; HLOD only forwards it to emitters.
        //Construct history without admitting a draw or changing its placement.
        if(parent?.IsRsl==true && parent.Root.Sphere is not null)state.RslStateId=parent.Root.RslFactoryStateId;
        SceneOwner? source=null;
        if(_ownedSceneFrame&&_ownedScene!.TryFind(obj.RenderIdentity,out var node)&&node.Definition.RenderObjectGeneration==obj.RenderObjectGeneration)
            source=node.State;
        if(source is not null&&key.Generation==obj.RenderObjectGeneration)
        {state.VisibilityOwners=source.WorldSphereVisibility;state.VisibilityOwners.Add(state);}
        var update=source?.RslUpdate;
        if(source is null)_worldSphereRslUpdates.TryGetValue((obj.RenderIdentity,obj.RenderObjectGeneration),out update);
        if(update is not null)
            state.VisibilityTransitions=update.ObjectVisibilityTransitionCounts.GetValueOrDefault(state.RslStateId);
        return state;
    }

    private List<(WorldSphereOwner Owner, EnbRenderDetailOwner Candidate)> PrepareWorldSpheres(EnbResolvedRenderCamera camera)
    {
        var observed=_worldSpheres.Where(pair=>_worldSphereObserved.Contains(pair.Key)).Select(pair=>pair.Value).ToArray();
        foreach(var state in observed)
        {
            state.SceneVisible=true;
            if(state.RetainedInstance is {Parent:null} root && FrustumCullingEnabled&&!root.ForcesSceneVisibility)
            {
                var sphere=Snapshot(state).Bounds;
                //RSL+F0 forwards to the active child through004616A0;
                //the copied wrapper locals do not control this scene query.
                state.SceneVisible=EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(root.OrbitalCamera??camera,sphere);
            }
            //Keep the scene wrapper's pre-callback admission when its RSL
            //callback replaces this primitive with another model.
            if(state.RetainedInstance is {Parent:null} sceneRoot)sceneRoot.ShouldDraw&=state.SceneVisible;
        }
        //0098E9A0 recurses only aggregate storage. Ordinary retained children
        //contribute their current integer cost to the parent's level instead.
        return observed.Where(s => s.Visible && s.SceneVisible && (s.Parent is null ||
            !s.Parent.IsOrdinaryHlodChild && s.Parent.ShouldDraw && CollectionAncestorsDraw(s.Parent) && !HasCollectionAncestor(s.Parent)))
            .Select(s => (s, Snapshot(s).Candidate($"world-sphere/{s.Owner.RenderIdentity}/{s.Path}", camera, s.Level,s.Parent?.Root.LodBias??1))).ToList();
    }

    private void ReconcileWorldSphereOwners(IReadOnlySet<(ulong Identity,ulong Generation)> live)
    {
        foreach(var key in _worldSpheres.Keys.Where(key=>key.PhysicalFactory is {} factory
            ? !(_shipExhaustOwner??this).RetainsPhysicalFactory(factory):!live.Contains((key.Identity,key.Generation))).ToArray())if(_worldSpheres.Remove(key,out var retired))retired.VisibilityOwners?.Remove(retired);
        foreach(var key in _worldSphereRslUpdates.Keys.Where(key=>!live.Contains(key)).ToArray())_worldSphereRslUpdates.Remove(key);
    }

    private void ReconcileWorldSphereRslVisibility(IEnbRenderObject obj,EnbRslObjectState update,SceneOwner? source=null)
    {
        if(source is not null)
        {
            foreach(var state in source.WorldSphereVisibility)ApplyWorldSphereVisibility(state,update);
            return;
        }
        _worldSphereRslUpdates[(obj.RenderIdentity,obj.RenderObjectGeneration)]=update;
        foreach(var pair in _worldSpheres.Where(pair=>pair.Key.Identity==obj.RenderIdentity&&pair.Key.Generation==obj.RenderObjectGeneration))
            ApplyWorldSphereVisibility(pair.Value,update);
    }
    private static void ApplyWorldSphereVisibility(WorldSphereOwner state,EnbRslObjectState update)
    {
        if(state.BorrowedPlayback)return;
        if(state.RslStateId<0)return;
        ulong transitions=update.ObjectVisibilityTransitionCounts.GetValueOrDefault(state.RslStateId);
        if(transitions!=state.VisibilityTransitions)
        {
            //Even hide+unhide within one update resets the native clock.
            //Preserve sampled properties and retained detail through both.
            state.Clock.ReconcileVisibility(false);
            state.Clock.ReconcileVisibility(!update.HiddenObjectStateIds.Contains(state.RslStateId));
            state.VisibilityTransitions=transitions;
        }
    }

    private void ObserveRetainedWorldSpheres(IEnumerable<LiveRootInstance> roots)
    {
        foreach(var instance in roots)
        {
            if(instance.Root.Sphere is null)continue;
            bool previousAdmission=instance.ShouldDraw;
            if(!TryRefreshOrdinaryHlodChildCore(instance,false,out _)){instance.ShouldDraw=false;continue;}
            if(!instance.IsOrdinaryHlodChild)instance.ShouldDraw&=previousAdmission;
            ObserveRetainedWorldSphere(instance);
        }
    }

    private void ObserveRetainedWorldSphere(LiveRootInstance instance)
    {
        var sphere=instance.Root.Sphere!;
        if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out float scale,out var hsv))return;
        ResolvePrimitivePropertyInputs(instance,ref scale,ref hsv);
        ObserveWorldSphere(instance.Object,sphere,world,instance.InstancePath,instance.PivotVisible&&!instance.Root.RenderObjectHidden,instance,scale,hsv);
        var key=WorldSphereOwnerKey(instance.Object,sphere,instance,instance.InstancePath,scale,hsv);
        if(_worldSpheres.TryGetValue(key,out var state))state.RetainedInstance=instance;
    }

    private static WorldSphereKey WorldSphereOwnerKey(IEnbRenderObject obj,EnbRetailSphereAttachment sphere,
        LiveRootInstance? instance,string path,float scale,Vector3 hsv)
    {
        string retainedPath=path;
        if(instance?.IsRsl==true)
            for(var ancestor=instance;ancestor is not null;ancestor=ancestor.Parent)
                if(ancestor.Root.RslFactoryStateId>=0)
                {
                    //A precreated state can move between Object and Bone
                    //placement without becoming a new RenderObj lifetime.
                    retainedPath=$"rsl/{ancestor.Root.RslFactoryStateId}"+path[ancestor.InstancePath.Length..];
                    break;
                }
        object? factory=instance is null?null:PhysicalParticleFactory(instance);
        return new(obj.RenderIdentity,factory is not null?0:instance?.IsRsl==true?obj.RenderObjectGeneration:obj.VisualGeneration,
            retainedPath,instance?.Root.Sphere is not null?instance.Root.PrimitiveFactoryIdentity:sphere,scale,hsv){PhysicalFactory=factory};
    }

    //Only children already owned during0048B220 configuration inherit+164
    //and HSV. A separately created fixed/bone attachment has its own factory.
    private static bool TryResolvePrimitiveFactoryTransform(LiveRootInstance instance,out Matrix4x4 world,out float scale,out Vector3 hsv)
    {
        world=default;scale=instance.PrimitiveFactoryScale;hsv=instance.PrimitiveFactoryHsv;
        bool inherited=instance.Parent is {} owningParent && (instance.IsOrdinaryHlodChild||instance.IsAggregateHlodChild||owningParent.Root.Collection is not null);
        if(inherited && instance.Parent is {} parent)
        {
            if(!TryResolvePrimitiveFactoryTransform(parent,out world,out scale,out hsv))return false;
            if(parent.Root.Collection is not null)hsv=Vector3.Zero; //0048D710 skips class23.
            else
            {
                if(!TryResolveLiveAttachmentHierarchy(parent.Root,out var hierarchy,out _)||hierarchy is null)return false;
                hierarchy=parent.Root.ConfiguredHierarchyTranslations?.Single(tree=>tree.Name.Equals(hierarchy.Name,StringComparison.OrdinalIgnoreCase))??hierarchy;
                var animations=parent.AnimationBlend?.ForHierarchy(hierarchy.Name)??Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
                var transforms=parent.HierarchyPostTransforms?.Transforms;
                if(!TryResolveRetainedHierarchyPose(parent,hierarchy,world,ResolvePendingHierarchyScale(parent,scale),animations,transforms,out var pose,out _) ||
                    instance.OrdinaryHlodPivot>=(uint)pose.WorldTransforms.Length)return false;
                world=pose.WorldTransforms[(int)instance.OrdinaryHlodPivot];
            }
        }
        else if(instance.PrimitiveFactoryWorld is {} factoryWorld)world=factoryWorld;
        else if(instance.WorldTransformOverride is {} supplied)world=supplied;
        else if(!TryBuildObjectWorld(instance.Object,out world,1))return false;
        if(!inherited && instance.HierarchyPostTransforms is {} post && !instance.Root.IndependentRslFactory)
        {world=post.RigidWorldTransform;scale=post.RenderObjectScale;}
        if(instance.Root.IndependentRslFactory)
        {
            //0045C990 configures each state before Bone attaches it. The
            //attachment's+12C transfer changes detail bias, not factory data.
            scale=EnbCurrentRetailPrototypeVariantName.ChangesScale(instance.RenderScale)?instance.RenderScale:1;
            var shift=instance.Appearance.Hsv;
            hsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(shift)?shift:Vector3.Zero;
        }
        else if(inherited && instance.Root.NamedChildFactoryScale is {} childScale)scale*=childScale;
        if(instance.Root.InfluenceFactoryHsv is {} configuredHsv)hsv=configuredHsv;
        return true;
    }

    private static float ResolvePendingHierarchyScale(LiveRootInstance instance,float unconfiguredScale)
    {
        if(instance.Root.ConfiguredHierarchyTranslations is null)return unconfiguredScale;
        var owner=instance;
        var configured=instance;
        while(!owner.Root.IndependentRslFactory && owner.Parent is {} parent &&
            (owner.IsOrdinaryHlodChild||owner.IsAggregateHlodChild||parent.Root.Collection is not null))
        {
            owner=parent;
            if(ReferenceEquals(owner.Root.HierarchyConfigurationOwner,instance.Root.HierarchyConfigurationOwner))configured=owner;
        }
        if(ReferenceEquals(owner,configured))return owner.HierarchySourceScale;
        float remaining=owner.PrimitiveFactoryScale;
        for(var ancestor=configured.Parent;ancestor is not null&&!ReferenceEquals(ancestor,owner);ancestor=ancestor.Parent)
            if(ancestor.Root.NamedChildFactoryScale is {} scale)remaining*=scale;
        return remaining;
    }

    //48B220/98EF60 configure child property tables before publishing the
    //HLOD prototype. Its retained scale still positions pivots, but must not
    //multiply those tables again. Effect source radius is a later +164 call.
    private void ResolvePrimitivePropertyInputs(LiveRootInstance instance,ref float scale,ref Vector3 hsv)
    {
        if(instance.Root.PrimitiveConfigurationOwner is null)return;
        var owner=instance;
        var configured=instance;
        while(!owner.Root.IndependentRslFactory && owner.Parent is {} parent &&
            (owner.IsOrdinaryHlodChild||owner.IsAggregateHlodChild||parent.Root.Collection is not null))
        {
            owner=parent;
            if(ReferenceEquals(owner.Root.PrimitiveConfigurationOwner,instance.Root.PrimitiveConfigurationOwner))configured=owner;
        }
        if(!ReferenceEquals(owner,configured))
        {
            //The named child already contains its own configured properties.
            //Only factories above that retained subtree remain to be applied.
            scale=owner.PrimitiveFactoryScale;
            for(var ancestor=configured.Parent;ancestor is not null && !ReferenceEquals(ancestor,owner);ancestor=ancestor.Parent)
                if(ancestor.Root.NamedChildFactoryScale is {} parentScale)scale*=parentScale;
            return;
        }
        scale=owner.IsRsl && (_shipExhaustOwner??this)._effectRslRootOwners.TryGetValue(owner.Object.RenderIdentity,out var effect)
            ?effect.SourceRadiusScale:1;
        hsv=Vector3.Zero;
    }

    private static EnbRetailSpherePreparedAttachment Snapshot(WorldSphereOwner state)
        => new(state.Binding, state.World, 1, state.Clock.Progress, state.Values,
            EnbRetailSphereBounds.World(state.Binding.Asset.Header.Center, state.Binding.Asset.Header.AuthoredExtent,
                state.Values.Scale, state.World));

    private void AppendWorldSpheres(List<EnbW3dDrawTask> packets, EnbResolvedRenderCamera camera, uint now)
    {
        foreach (var state in _worldSpheres.Where(pair=>_worldSphereObserved.Contains(pair.Key)).Select(pair=>pair.Value))
        {
            if(state.Parent is {} ownerParent&&!TryPrepareRetainedRootDraw(ownerParent,out _))continue;
            if(state.RetainedInstance is {} retained)
            {
                //The final parent level may have changed during allocation.
                //Level selection itself is not a Sphere visibility setter.
                ObserveRetainedWorldSphere(retained);
            }
            if (!state.Visible || !state.SceneVisible || state.Parent is {} hiddenParent && (!hiddenParent.ShouldDraw || !CollectionAncestorsDraw(hiddenParent))) continue;
            //0098DDC7..DDE5 dispatches aggregate child+120 immediately before
            //draw. Sphere00B10458+120 is0090D270, a signed clamp to0..10.
            var forceParent=state.RetainedInstance is {IsAggregateHlodChild:true} or {IsShipLegoController:true} or {IsExternalLegoChild:true} ?state.RetainedInstance.Parent:
                state.RetainedInstance is null?state.Parent:null;
            if(forceParent is {} parent && parent.Root.ForceSubObjectLod && parent.Root.Hlod is {} hlod)
                state.Level=EnbSphereRingDetailBudgetSemantics.ClampSphereLevel(parent.HlodSelection?.LevelOrdinal??EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod));
            var geometry = EnbRetailSphereGeometry.ForDetail(state.Level);
            if (geometry is null) continue;
            var binding = state.Binding;
            state.Clock.Advance(binding.Asset.Header.Duration, binding.Asset.Header.Flags, binding.Asset.HasPropertyKeys, (state.BorrowedPlayback?_shipExhaustOwner!:this)._ringFrameDelta);
            if (binding.Asset.HasPropertyKeys) state.Values = binding.Asset.Properties.Evaluate(state.Clock.Progress);
            packets.Add((EnbRetailSphereDrawPacket.Build(binding.Asset.Header, state.Values, geometry, binding.Key,
                binding.Texture, Snapshot(state).DrawWorld(camera), true, now, state.Owner.RenderIdentity,
                state.Owner.VisualGeneration, state.Owner.GameId, binding.Ordinal)).ForkTask().SetRenderInstancePath("world-sphere/" + state.Path));
        }
    }
}
