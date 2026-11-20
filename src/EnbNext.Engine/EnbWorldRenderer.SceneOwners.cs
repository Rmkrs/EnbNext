using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Separately queued view invocations own their mutable matrix. Metadata is
    // initialized on invocation creation; ordinary scene objects are never copied here.
    private static EnbRenderObjectState CreateRenderInvocation(IEnbRenderObject source)
    {
        var state=new EnbRenderObjectState(source);
        if(source.Spatial is {} definition)
        {
            var spatial=new EnbRenderSpatialState();spatial.AdoptDefinition(definition);state.Spatial=spatial;
        }
        return state;
    }
    private sealed class SceneVisibility {public bool RadarAttached=true;}
    private readonly HashSet<ulong> _radarDetachedOwners=[];
    private long _radarMembershipChanges;
    private long _radarFallbackLookups;
    private bool IsOwnedRadarDetached(LiveRootInstance instance)
    {
        if(instance.PreparedSceneVisibility is {} visibility)return !visibility.RadarAttached;
        _radarFallbackLookups++;return _radarDetachedOwners.Contains(instance.Object.RenderIdentity);
    }
    private sealed class SceneOwner : EnbRenderObjectState, IEnbRenderObjectLifetime
    {
        public ProjectedDecalOwner? ProjectedDecals;
        public bool IsRetired {get;private set;}
        public event Action? Retired;
        public void NotifySourceReplacement(){var listeners=Retired;Retired=null;listeners?.Invoke();}
        public void NotifyRetired(){if(IsRetired)return;IsRetired=true;NotifySourceReplacement();}
        public EnbSceneObjectSnapshot Definition;
        public SceneOwner(EnbSceneObjectSnapshot definition)
        {
            Definition=definition;AdoptDefinition(definition);
            PlanetEligible=IsRenderablePlanet(definition);
            MapperContext=new(new(definition.RenderIdentity,definition.VisualGeneration));
            CallbackIdentities.Add(definition.RenderIdentity);
            if(definition.Spatial is {} spatial){SpatialState.AdoptDefinition(spatial);Spatial=SpatialState;}

        }
        public readonly SceneVisibility Visibility=new();
        public readonly float?[] RadiusPublications=new float?[3];
        public float? QueryDistanceRadius,FrameDistanceRadius,LastEffectRadius,RadiusFallback;
        public bool RadiusFallbackReady,QueryEmptyRadiusCandidate,EmptyRadiusCandidate;
        public EnbCurrentRetailShipNameDecalTexture? NameDecalTexture;
        public Dictionary<string,EnbTextureImage>? NameDecalImages;
        public void SetNameDecalTexture(EnbCurrentRetailShipNameDecalTexture? texture)
        {
            if(NameDecalTexture is {} previous)NameDecalImages?.Remove(previous.LogicalName);
            NameDecalTexture=texture;
            if(texture is not null&&NameDecalImages is not null)NameDecalImages[texture.LogicalName]=texture.Image;
        }
        public readonly List<WorldSphereOwner> WorldSphereVisibility=[];
        public readonly List<WorldRingOwner> WorldRingVisibility=[];
        public bool PlanetEligible;
        public OwnedMapperContext MapperContext;
        public LivePlanetGpuIdentity PlanetIdentity=>MapperContext.PlanetIdentity;
        public LivePlanetMapperStateOwnership? PlanetMapper {get=>MapperContext.PlanetMapper;set=>MapperContext.PlanetMapper=value;}
        public uint? FrameMapperStartOverride=>MapperContext.FrameMapperStartOverride;
        public float? FrameDayNightOverride=>MapperContext.FrameDayNightOverride;
        public void UpdateMapperContext(LiveFrame frame)=>MapperContext.UpdateMapperContext(frame);
        public readonly RootTaskChain RootTasks=new();
        public readonly List<CompositeOccurrence> CompositeOccurrences=[];
        public readonly List<LiveRootInstance> SceneModelBindings=[];
        public void InvalidateSceneModelBindings()
        {
            foreach(var instance in SceneModelBindings){instance.SceneModelBound=false;instance.PreparedSceneModel=null;}
            SceneModelBindings.Clear();
        }
        public readonly HashSet<ulong> CallbackIdentities=[];
        public RslSoundSelections? SoundSelections;
        public IReadOnlyList<EnbCurrentRetailRslEffectiveAction>? EffectiveActions;
        public OwnedEffectiveActions? EffectiveActionView;
        public long EffectiveActionEpoch=-1;
        public long SoundSelectionEpoch;
        public readonly List<(LiveGpuKey Key,LiveGpuResource Resource)> GpuResources=[];
        public FixedVisualPreparationState? FixedVisuals;
        public RigidOwner Rigid=new(){SceneOwned=true};

        public EnbRenderPose CurrentPose;
        public EnbSceneShipPresentationSnapshot? CurrentPresentation;
        private EnbSceneShipPresentationSnapshot? preparedPresentation;
        public EnbSceneShipPresentationSnapshot? Presentation(EnbRenderPose pose)
        {
            if(Definition.ShipPresentation is not {} source)return null;
            int? engine=pose.HasWarpInputs?pose.EngineWarpState:source.EngineWarpState;
            int cached=pose.HasWarpInputs?pose.CachedWarpState:source.CachedWarpState;
            var warp=pose.HasWarpInputs?pose.WarpChanges:source.WarpChanges;
            var cockpit=pose.HasPrivateWarpInputs?pose.PrivateWarpChanges:source.PrivateWarpChanges;
            var current=preparedPresentation??source;
            if(current.EngineWarpState!=engine||current.CachedWarpState!=cached||
                !current.WarpChanges.Equals(warp)||!current.PrivateWarpChanges.Equals(cockpit))
                current=source with {EngineWarpState=engine,CachedWarpState=cached,WarpChanges=warp,PrivateWarpChanges=cockpit};
            return preparedPresentation=current;
        }
        public readonly EnbRenderSpatialState SpatialState=new();
        public EnbCurrentRetailSpatialTransformRuntime.Owner SpatialPose=>SpatialState.Pose;
        public readonly RootStagingScratch RootScratch=new();
        public OrdinaryBaseFactory? Factory;
        public object? BasePhysicalFactory;
        public EnbBaseAssetResolution? Resolution;
        public string? FactorySource;
        public bool FactoryResolved;
        public LiveRootInstance? PreparedFileRoot,PreparedRslRoot;
        public LiveRslBoneOwner? RslBones;
        public bool RslBonesResolved;
        public readonly List<LegoFactoryBinding> LegoFactories=[];
        public LegoDrawPlan? LegoDraw;
        public bool PhysicalSpecsDirty=true;
        public IReadOnlyList<LiveShipLegoSpec>? PhysicalSpecs;
        public LegoPhysicalModel? PhysicalSpecsRoot;
        public readonly HashSet<LegoPhysicalModel> PhysicalSpecDependencies=[];
        public void ClearPhysicalSpecDependencies()
        {foreach(var model in PhysicalSpecDependencies)model.SpecPlanOwners.Remove(this);PhysicalSpecDependencies.Clear();}
        public void InvalidatePhysicalSpecs()
        {RadiusFallbackReady=false;RadiusFallback=null;LegoDraw=null;PhysicalSpecsDirty=true;PhysicalSpecs=null;PhysicalSpecsRoot=null;ClearPhysicalSpecDependencies();}
        public LegoRslOwner[]? LegoRslChildren;
        public bool LegoRslChildrenDirty=true;
        public LegoSceneState? LegoScene;
        public LegoPhysicalOwner? PhysicalOwner;
        public readonly Dictionary<LiveHlodKey,HlodLevelState> RslHlodLevels=[];
        public readonly Dictionary<(ulong Generation,string Path,EnbW3dRenderResourceKey Asset,object? Factory),OwnedDazzleRoot> DazzleRoots=[];
        public LiveRootInstance? FrameAdmissionRoot;
        public LegoPhysicalModel? WorldSceneModel;
        public int WorldSceneSlot;
        public RetainedLegoControllers? LegoControllers;
        public bool LegoControllersDirty=true;
        public bool RslStructureDirty=true;
        public RetainedRslRootPlan? RslRootPlan,RslQueryRootPlan;
        public RetainedRslCallbackTree? RslCallbackTree;
        public long CallbackMutations,CallbackRebuilds;
        public void RslModelChanged(){RslCallbackTree=null;RadiusFallbackReady=false;RadiusFallback=null;PhysicalSpecsDirty=true;LegoRslChildrenDirty=true;RslStructureDirty=true;RslRootPlan=null;RslQueryRootPlan=null;RslSceneChanged();}
        public void RslSceneChanged(){if(LegoScene is {} scene)scene.Dirty=true;}
        public RslWrapperOwner Attachment {get;set;}=new();
        public OwnerPublicationMap<EnbRslObjectState>.Slot RslPublication=new();
        public OwnerPublicationMap<EnbRslObjectState>.Slot QueryRslPublication=new();
        public OwnerPublicationMap<EnbCurrentRetailRslEffectPassLibrary>.Slot PassPublication=new();
        public EnbRslObjectState? RslUpdate {get=>Attachment.CurrentState;set{Attachment.CurrentState=value;RslPublication.Value=value;}}
        public FactoryAppearance? PreparedFileInputs;
        public long StructuralPreparations;
        public EnbCurrentRetailEffectRslObjectRuntime.OwnerBinding? RslOwner;
        public EnbCurrentRetailRslEffectPassLibrary? RslPassOwner;
        public EnbCurrentRetailRslEffectPassLibrary? FrameRslPassOwner {get=>PassPublication.Value;set=>PassPublication.Value=value;}
        public bool RslPassPrepared,RslFactoriesPrepared,RslFactoryLifetimePrepared;
        public readonly List<(ulong Identity,ulong Generation,int State)> ConfiguredRslKeys=[];
        public bool RslMembershipAttached;
        public bool RslCallbackRegistered;
        public OrdinaryRslCallbackOwner? RslCallback;
        public bool RslAdvanceReady,RslCandidateReady;
        public void InvalidateAssets(){ProjectedDecals?.RetireSourceReceivers();RslCallbackTree=null;QueryDistanceRadius=null;FrameDistanceRadius=null;LastEffectRadius=null;WorldSphereVisibility.Clear();WorldRingVisibility.Clear();SetNameDecalTexture(null);InvalidateSceneModelBindings();PhysicalOwner=null;WorldSceneModel=null;FrameAdmissionRoot=null;InvalidatePhysicalSpecs();LegoRslChildren=null;LegoRslChildrenDirty=true;RslBones=null;RslBonesResolved=false;LegoControllers=null;LegoControllersDirty=true;RslRootPlan=null;RslQueryRootPlan=null;RslFactoriesPrepared=false;RslPassPrepared=false;RslPassOwner=null;FrameRslPassOwner=null;PreparedFileRoot=null;PreparedRslRoot=null;RslStructureDirty=true;PreparedFileInputs=null;FixedVisuals=null;Factory=null;Resolution=null;FactorySource=null;FactoryResolved=false;Rigid=new(){SceneOwned=true,SceneSource=this};}
        public void Update(EnbSceneObjectSnapshot next)
        {
            if(Definition.VisualGeneration!=next.VisualGeneration || Definition.NameDecalApplyCount!=next.NameDecalApplyCount ||
                !ReferenceEquals(Definition.NameDecal,next.NameDecal))SetNameDecalTexture(null);
            InvalidateSceneModelBindings();RslSceneChanged();InvalidatePhysicalSpecs();
            if(!Definition.LegoControllerMutations.Equals(next.LegoControllerMutations)||
                !Definition.SubParts.Equals(next.SubParts)||!ReferenceEquals(Definition.ShipLegos,next.ShipLegos))LegoRslChildrenDirty=true;
            if(Definition.RenderObjectGeneration!=next.RenderObjectGeneration ||
                !Definition.LegoControllerMutations.Equals(next.LegoControllerMutations))LegoControllersDirty=true;
            if(Definition.VisualGeneration!=next.VisualGeneration||Definition.RenderObjectGeneration!=next.RenderObjectGeneration||
                !Definition.RenderStateActions.Equals(next.RenderStateActions)||!Definition.Attachments.Equals(next.Attachments)||
                !Definition.LegoControllerMutations.Equals(next.LegoControllerMutations))RslModelChanged();
            if(Definition.RenderObjectGeneration!=next.RenderObjectGeneration ||
                OrdinaryBaseFactoryKey.From(Definition)!=OrdinaryBaseFactoryKey.From(next))InvalidateAssets();
            if(Definition.VisualGeneration!=next.VisualGeneration){Rigid=new(){SceneOwned=true,SceneSource=this};PreparedFileRoot=null;PreparedRslRoot=null;RslStructureDirty=true;PreparedFileInputs=null;}
            Definition=next;AdoptDefinition(next);
            if(next.Spatial is {} spatial){SpatialState.AdoptDefinition(spatial);Spatial=SpatialState;}
            preparedPresentation=null;
        }
    }
    private readonly EnbRetainedMembershipSet<ulong> _liveRslIdentities=new();
    private readonly EnbRetainedMembershipSet<(ulong RenderIdentity,ulong VisualGeneration)> _liveRslGenerations=new();
    private void AttachOwnedRslMembership(SceneOwner owner)
    {
        if(owner.RslMembershipAttached)return;
        owner.RslMembershipAttached=true;
        var obj=owner.Definition;var key=(obj.RenderIdentity,obj.RenderObjectGeneration);
        _liveRslIdentities.Attach(obj.RenderIdentity);_liveRslGenerations.Attach(key);
        _frameRetainedGpuOwners.Attach(key);
        if(obj.VisualGeneration!=obj.RenderObjectGeneration)_gpuLiveVisuals.Attach(key);
        if(obj.VisualGeneration!=obj.RenderObjectGeneration){_framePrimitiveOwners.Attach(key);_frameEmitterResidents.Attach(key);}
    }
    private void DetachOwnedRslMembership(SceneOwner owner)
    {
        if(!owner.RslMembershipAttached)return;
        owner.RslMembershipAttached=false;
        var obj=owner.Definition;var key=(obj.RenderIdentity,obj.RenderObjectGeneration);
        _liveRslIdentities.Detach(obj.RenderIdentity);_liveRslGenerations.Detach(key);
        _frameRetainedGpuOwners.Detach(key);
        if(obj.VisualGeneration!=obj.RenderObjectGeneration)_gpuLiveVisuals.Detach(key);
        if(obj.VisualGeneration!=obj.RenderObjectGeneration){_framePrimitiveOwners.Detach(key);_frameEmitterResidents.Detach(key);}
    }
    private readonly HashSet<(ulong Identity,ulong Generation)> _compatRslCallbackMembers=[];
    private Action<(ulong Identity,ulong Generation),Action,SceneOwner>? _dispatchOwnedRslCallback;
    // Keep a persistent callback out of RenderFrame's display class: that
    // closure owns the whole frame's task/root graph, including retired nodes.
    private void RegisterOwnedRslCallback(SceneOwner source,(ulong Identity,ulong Generation) key)
        =>_ordinaryRslCallbacks.RegisterBound(key,
            ()=>{if(source.RslAdvanceReady&&source.RslCallback is {} callback)_dispatchOwnedRslCallback?.Invoke(key,callback.Advance,source);});
    private void UnregisterOwnedRslCallback(SceneOwner owner)
    {
        if(!owner.RslCallbackRegistered)return;
        var key=(owner.Definition.RenderIdentity,owner.Definition.RenderObjectGeneration);
        _ordinaryRslCallbacks.Remove(key);_ordinaryRslCallbackMembers.Remove(key);
        SetOwnedWrapperAttached(owner,false);owner.RslCallbackRegistered=false;owner.RslAdvanceReady=false;owner.RslCandidateReady=false;
    }
    private void ClearOrdinaryRslCallbacks()
    {
        _ordinaryRslCallbacks.Clear();_compatRslCallbackMembers.Clear();
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)
        {SetOwnedWrapperAttached(node.State,false);node.State.RslCallbackRegistered=false;node.State.RslAdvanceReady=false;node.State.RslCandidateReady=false;}
    }
    private readonly List<EnbCurrentRetailEffectRslObjectRuntime.OwnerBinding> _retiredRslBindings=[];
    private void RetireRslBinding(SceneOwner owner)
    {if(owner.RslOwner is {} binding)_retiredRslBindings.Add(binding);owner.RslOwner=null;owner.RslCallback=null;owner.RslPassOwner=null;owner.FrameRslPassOwner=null;owner.RslPassPrepared=false;owner.RslFactoriesPrepared=false;}
    private EnbCurrentRetailRslEffectPassLibrary? ResolveOwnedRslPass(SceneOwner? owner,IEnbRenderObject obj,
        EnbResolvedRslReference rsl,EnbRetailAssetBundle assets)
    {
        if(owner is null)return RetainRslPassOwner(obj,rsl,assets);
        if(!owner.RslPassPrepared)
        {owner.RslPassOwner=RetainRslPassOwner(obj,rsl,assets);owner.RslPassPrepared=rsl.ResourceGraph is not null;}
        return owner.RslPassOwner;
    }
    private void PrepareOwnedRslFactories(SceneOwner? owner,IEnbRenderObject obj,EnbResolvedRslReference rsl,EnbRetailAssetBundle assets)
    {
        if(owner?.RslFactoryLifetimePrepared==true)return;
        PrepareRslObjectFactories(obj,rsl,assets,source:owner);
        if(owner is not null)
        {
            owner.RslFactoriesPrepared=true;owner.RslFactoryLifetimePrepared=true;
            // Factory construction can install a whole-library owner after the
            // first pass query. Publish it at the next invocation, as before.
            if(_rslPassOwners.TryGetValue((obj.RenderIdentity,obj.RenderObjectGeneration),out var entry))
                owner.RslPassOwner=entry.Library;
        }
    }
    private void DrainRetiredRslBindings()
    {foreach(var binding in _retiredRslBindings)_effectRslObjectRuntime.ReleaseOwner(binding);_retiredRslBindings.Clear();}
    private IEnumerable<(IEnbRenderObject Object,SceneOwner? Owner)> SceneOwnerObjects(IEnbSceneFrame snapshot)
    {
        if(_ownedSceneFrame&&ReferenceEquals(snapshot,_ownedFrameSnapshot))
        {foreach(var node in _ownedScene!.ActiveNodes)yield return (node.State,node.State);}
        else foreach(var obj in SnapshotObjects(snapshot))yield return(obj,null);
    }
    private long _snapshotObjectRequests;
    private IReadOnlyList<IEnbRenderObject> SnapshotObjects(IEnbSceneFrame snapshot)
    {
        _snapshotObjectRequests++;
        return snapshot.Objects;
    }
    private IEnbSceneFrame? _ownedFrameSnapshot;
    // Ordered membership is maintained only by the change feed. Readers receive
    // the actual owner list, not a projected snapshot or per-access view.
    private IReadOnlyList<IEnbRenderObject> SceneObjects(IEnbSceneFrame snapshot)
        =>_ownedSceneFrame&&ReferenceEquals(snapshot,_ownedFrameSnapshot)?_ownedScene!.OrderedStates:SnapshotObjects(snapshot);
    private readonly List<(IEnbRenderObject Object,SceneOwner? Source,bool NewCallback,IReadOnlyDictionary<ulong,EnbRslObjectState>? Updates)> _worldSceneAdmissions=[];
    private readonly Dictionary<ulong,LiveRootInstance> _compatWorldSceneRoots=[];
    private SceneOwner? _buildingSceneOwner;
    private SceneOwner? _localSceneOwner;
    private int _localScenePoseSlot=-1;
    private bool _localSceneOwnerDirty=true;
    private readonly struct SceneOwnerScope : IDisposable
    {
        private readonly EnbWorldRenderer _view;
        private readonly SceneOwner? _previous;
        public SceneOwnerScope(EnbWorldRenderer view,SceneOwner? owner)
        {_view=view;_previous=view._buildingSceneOwner;view._buildingSceneOwner=owner;}
        public void Dispose()=>_view._buildingSceneOwner=_previous;
    }
    private EnbRenderSceneSubscription? _sceneSubscription;
    private EnbRetainedRenderScene<SceneOwner>? _ownedScene;
    private bool _ownedSceneFrame;
    private ulong _ownedSceneFrameSequence;
    private long _sceneFactoryPreparations;

    internal void SetRenderSceneSubscription(EnbRenderSceneSubscription? subscription)
    {
        _sceneSubscription?.Dispose();
        if(!_retainedOrdinaryMeshes){subscription?.Dispose();return;}
        _sceneSubscription=subscription;
        _ownedScene??=new(CreateSceneOwner,UpdateSceneOwner,RetireSceneOwner,DetachSceneOwner,indexGameIds:true);
    }
    internal void DisposeRenderSceneSubscription(){_sceneSubscription?.Dispose();_sceneSubscription=null;}
    private void DetachSceneOwner(SceneOwner owner)
    {
        RetireProjectedDecals(owner);
        owner.NotifyRetired();
        owner.SetNameDecalTexture(null);
        DetachOwnedRadiusSource(owner);
        _dirtyCombatRadiusSources.Remove(owner);
        _radarDetachedOwners.Remove(owner.Definition.RenderIdentity);
        if(owner.Definition.IsLocalShip)_localSceneOwnerDirty=true;
        _ownedRslPublications.Detach(owner.Definition.RenderIdentity);_ownedRslQueryPublications.Detach(owner.Definition.RenderIdentity);_ownedPassPublications.Detach(owner.Definition.RenderIdentity);
        owner.InvalidateSceneModelBindings();
        owner.WorldSphereVisibility.Clear();owner.WorldRingVisibility.Clear();
        RetireOwnedCompositeOccurrences(owner);
        _shipMaterials.ReleaseOwner((owner.Definition.RenderIdentity,owner.Definition.VisualGeneration));
        DetachOwnedEffectiveActions(owner);
        DetachBasePhysicalLifetime(owner);
        DetachOwnedRslSoundSelections(owner);
        RetireOwnedRslAuxiliaries(owner);
        RetireOwnedPlanetMapper(owner);
        DetachOwnedDazzles(owner);
        RetireOrdinaryGpuResources(owner);
        DetachOwnedRslFactories(owner);
        UnregisterOwnedRslCallback(owner);
        DetachOwnedRslMembership(owner);
        DetachLegoFactories(owner);owner.InvalidatePhysicalSpecs();
        DetachOwnedRslPass(owner);
        DetachOwnedLegoScene(owner);
        DetachOwnedRslBones(owner);
        foreach(var pair in LegoRslOwnersFor(owner.Definition.RenderIdentity))RemoveLegoRslOwner(pair.Key);
        InvalidateLegoRslIndex();owner.LegoRslChildren=null;
        DetachOwnedMeshAppearances(owner.Definition);
        DetachSceneMembership(owner.Definition);
        ulong identity=owner.Definition.RenderIdentity;
        _legoControllers.Remove(identity);
        if(!_effectRslRootOwners.ContainsKey(identity))_legoPhysicalOwners.Remove(identity);
    }
    private SceneOwner CreateSceneOwner(EnbSceneObjectSnapshot definition)
    {if(definition.IsLocalShip)_localSceneOwnerDirty=true;AttachSceneMembership(definition);_shipMaterials.RetainOwner((definition.RenderIdentity,definition.VisualGeneration));var owner=new SceneOwner(definition){NameDecalImages=_ownedNameDecalImages};UpdateOwnedRadiusMembership(owner);_dirtyCombatRadiusSources.Add(owner);
        _ownedRslPublications.Attach(definition.RenderIdentity,owner.RslPublication);_ownedRslQueryPublications.Attach(definition.RenderIdentity,owner.QueryRslPublication);_ownedPassPublications.Attach(definition.RenderIdentity,owner.PassPublication);
        owner.Rigid.SceneSource=owner;_dirtyLegoFactoryOwners.Add(owner);return owner;}
    private void UpdateSceneOwner(SceneOwner owner,EnbSceneObjectSnapshot definition)
    {
        if(owner.GameId!=definition.GameId||owner.WorldCreationOrdinal!=definition.WorldCreationOrdinal){RetireProjectedDecals(owner);owner.NotifySourceReplacement();}
        if(owner.Definition.IsLocalShip!=definition.IsLocalShip)_localSceneOwnerDirty=true;
        if(owner.Definition.BaseAsset!=definition.BaseAsset||owner.Definition.Scale!=definition.Scale||
            HasCurrentRetailShipLegoComposition(owner.Definition)!=HasCurrentRetailShipLegoComposition(definition))_dirtyCombatRadiusSources.Add(owner);
        if(owner.Definition.RenderObjectGeneration!=definition.RenderObjectGeneration)
        {
            owner.WorldSphereVisibility.Clear();owner.WorldRingVisibility.Clear();
            SetOwnedWrapperAttached(owner,false);
            owner.Attachment.Retired=true;QueueWrapperAttachment(owner.Attachment);owner.Attachment=new();
            owner.RslPublication=new();owner.QueryRslPublication=new();owner.PassPublication=new();
            _ownedRslPublications.Replace(definition.RenderIdentity,owner.RslPublication);
            _ownedRslQueryPublications.Replace(definition.RenderIdentity,owner.QueryRslPublication);
            _ownedPassPublications.Replace(definition.RenderIdentity,owner.PassPublication);
        }
        InvalidateOwnedDazzles(owner,definition);
        if(owner.Definition.VisualGeneration!=definition.VisualGeneration||owner.PlanetEligible!=IsRenderablePlanet(definition))
        {
            RetireOwnedPlanetMapper(owner);
            owner.PlanetEligible=IsRenderablePlanet(definition);owner.MapperContext=new(new(definition.RenderIdentity,definition.VisualGeneration));
        }
        RetireOwnedCompositeOccurrences(owner,definition);
        if(owner.Definition.RenderObjectGeneration!=definition.RenderObjectGeneration||OrdinaryBaseFactoryKey.From(owner.Definition)!=OrdinaryBaseFactoryKey.From(definition))DetachBasePhysicalLifetime(owner);
        _dirtyLegoFactoryOwners.Add(owner);
        if(owner.Definition.VisualGeneration!=definition.VisualGeneration ||
            OrdinaryBaseFactoryKey.From(owner.Definition)!=OrdinaryBaseFactoryKey.From(definition))DetachOwnedRslMembership(owner);
        if(owner.Definition.RenderObjectGeneration!=definition.RenderObjectGeneration ||
            OrdinaryBaseFactoryKey.From(owner.Definition)!=OrdinaryBaseFactoryKey.From(definition))DetachOwnedRslPass(owner);
        if(owner.Definition.RenderObjectGeneration!=definition.RenderObjectGeneration){DetachOwnedRslSoundSelections(owner);RetireOwnedRslAuxiliaries(owner);owner.RslHlodLevels.Clear();DetachOwnedRslFactories(owner);owner.PhysicalOwner=null;owner.WorldSceneModel=null;UnregisterOwnedRslCallback(owner);DetachOwnedLegoScene(owner);DetachOwnedRslBones(owner);RetireRslBinding(owner);}
        if(owner.Definition.VisualGeneration!=definition.VisualGeneration){_shipMaterials.ReleaseOwner((owner.Definition.RenderIdentity,owner.Definition.VisualGeneration));_shipMaterials.RetainOwner((definition.RenderIdentity,definition.VisualGeneration));RetireOrdinaryGpuResources(owner);DetachOwnedMeshAppearances(owner.Definition);}
        DetachSceneMembership(owner.Definition);AttachSceneMembership(definition);
        if(OrdinaryBaseFactoryKey.From(owner.Definition)!=OrdinaryBaseFactoryKey.From(definition))
            foreach(var key in _ordinaryBaseFactories.Keys)
                if(key.Identity==owner.Definition.RenderIdentity)_ordinaryBaseFactories.Remove(key);
        InvalidateProjectedDecals(owner,definition);
        owner.Update(definition);UpdateOwnedRadiusMembership(owner);
    }
    private void RetireSceneOwner(SceneOwner owner)
    {
        RetireRslBinding(owner);
        // Only an actual removal visits these compatibility entries. Normal
        // ordinary factories live on the node and need no frame membership sweep.
        foreach(var key in _ordinaryBaseFactories.Keys)
            if(key.Identity==owner.Definition.RenderIdentity)_ordinaryBaseFactories.Remove(key);
        owner.Factory=null;owner.Resolution=null;owner.FactorySource=null;
    }
    private bool ApplyOwnedSceneChanges(LiveFrame frame)
    {
        if(_sceneSubscription is null || _shipExhaustOwner is not null || frame.Movie is not null || frame.Snapshot.ActiveStarbaseId is not null || _customizationPreview ||
            frame.Snapshot.RenderSceneSequence==0 || frame.Snapshot.RenderSceneSequence<_ownedSceneFrameSequence)return false;
        _ownedScene!.ApplyThrough(_sceneSubscription,frame.Snapshot.RenderSceneSequence);
        _ownedSceneFrameSequence=frame.Snapshot.RenderSceneSequence;
        if(_localSceneOwnerDirty)
        {
            _localSceneOwner=null;_localScenePoseSlot=-1;
            foreach(var node in _ownedScene.ActiveNodes)
                if(node.Definition.IsLocalShip){_localSceneOwner=node.State;_localScenePoseSlot=node.Slot;break;}
            _localSceneOwnerDirty=false;
        }
        return true;
    }
    private void BeginOwnedSceneFrame(LiveFrame frame)
    {
        _ownedSceneFrame=false;_ownedFrameSnapshot=null;
        if(!ApplyOwnedSceneChanges(frame))return;
        foreach(var node in _ownedScene!.ActiveNodes)
        {
            node.State.RslCandidateReady=false;node.State.FrameAdmissionRoot=null;node.State.QueryEmptyRadiusCandidate=false;node.State.EmptyRadiusCandidate=false;
            node.State.RslUpdate=null;node.State.QueryRslPublication.Value=null;node.State.FrameRslPassOwner=null;
            var definition=node.Definition;
            var pose=_frozenInputMode==0 && node.PoseOwner is {} poseOwner ? poseOwner.Value : !frame.Snapshot.RenderPoses.IsDefault&&node.Slot>=0
                ?frame.Snapshot.RenderPoses[node.Slot]:default;
            node.State.UpdateMapperContext(frame);
            node.State.CurrentPose=pose;
            node.State.CurrentPresentation=node.State.Presentation(pose);
            if(pose.HasSample)node.State.SpatialPose.Update(pose);
            node.State.ShipVisualBankRadians=pose.HasVisualInputs?pose.ShipVisualBankRadians:definition.ShipVisualBankRadians;
            node.State.ShipEmote=pose.HasVisualInputs?pose.ShipEmote:definition.ShipEmote;
            node.State.ShipPresentation=node.State.CurrentPresentation;

        }
        _ownedSceneFrameSequence=frame.Snapshot.RenderSceneSequence;
        _ownedFrameSnapshot=frame.Snapshot;
        _ownedSceneFrame=true;
    }
    private void InvalidateOwnedSceneAssets()
    {
        if(_ownedScene is null)return;
        foreach(var node in _ownedScene.ActiveNodes){_dirtyCombatRadiusSources.Add(node.State);DetachBasePhysicalLifetime(node.State);DetachOwnedDazzles(node.State);node.State.RslHlodLevels.Clear();DetachOwnedRslFactories(node.State);DetachOwnedRslMembership(node.State);node.State.InvalidateAssets();}
    }
    private OrdinaryBaseFactory? ResolveOwnedSceneFactory(SceneOwner? owner,IEnbRenderObject obj,EnbRetailAssetBundle assets)
    {
        if(owner is null)return ResolveSceneBaseFactory(obj,assets);
        if(owner.FactoryResolved){BindBasePhysicalLifetime(owner);return owner.Factory;}
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(obj.BaseAsset);
        var factory=definition.Kind is EnbBaseAssetDefinitionKind.FileName or EnbBaseAssetDefinitionKind.NullObject ||
            EnbCurrentRetailRObjEffectAsset.HasMissingFactoryBranch(definition)
            ?CreateOrdinaryBaseFactory(obj,assets,definition):null;
        owner.Factory=factory;owner.FactoryResolved=true;_sceneFactoryPreparations++;BindBasePhysicalLifetime(owner);
        return factory;
    }
    private OrdinaryBaseFactory? ResolveSharedSceneFactory(IEnbRenderObject obj,EnbRetailAssetBundle assets)
    {
        // A secondary view explicitly resolves its selected owner. The primary
        // frame walk reaches this same state directly from the persistent list.
        if(_ownedScene is not null && _ownedScene.TryFind(obj.RenderIdentity,out var node) &&
            OrdinaryBaseFactoryKey.From(node.Definition)==OrdinaryBaseFactoryKey.From(obj))
            return ResolveOwnedSceneFactory(node.State,obj,assets);
        return ResolveSceneBaseFactory(obj,assets);
    }
    private IEnumerable<OrdinaryBaseFactory> EnumerateOrdinaryFactories()
    {
        foreach(var factory in _ordinaryBaseFactories.Values)yield return factory;
        if(_ownedScene is not null)
            foreach(var node in _ownedScene.ActiveNodes)
                if(node.State.Factory is {} factory)yield return factory;
    }
}
