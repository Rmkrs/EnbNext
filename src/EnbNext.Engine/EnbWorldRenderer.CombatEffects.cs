using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Net7.Gameplay;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    public Action<System.Collections.Immutable.ImmutableDictionary<EnbNext.Net7.Gameplay.Net7EffectPoseKey,EnbNext.Net7.Gameplay.Net7RetainedEffectPosition>>? EffectPositionsPublished { get; set; }
    private readonly RootTaskList _hiddenCombatSourceRoots=[];
    private readonly Dictionary<ulong,(IEnbRenderObject Object,Net7EffectSourceControllerPose Pose)> _emptyRslSourcePoses=[];
    private sealed record CombatEffectBinding(Matrix4x4 Source, EnbRetailSeekEffectPath? Path, ulong SourceIdentity, bool SourceDetached = false,
        bool SourceCapturedAtActivation = false, EnbRetailLinearSeekEffectPath? LinearPath = null,
        IEnbRenderObject? ResourceOwner = null, bool SourcePositionOnly = false, bool Unary = false);
    private readonly Dictionary<(uint,ulong,ulong),CombatEffectBinding> _combatEffectBindings=[];
    private readonly Dictionary<(uint,ulong,ulong),Matrix4x4> _combatEffectPoses=[];
    private readonly Dictionary<(uint,ulong,ulong),Matrix4x4> _combatFrontFramePoses=[];
    private sealed record SeekEndpointBinding(Matrix4x4 Source, IEnbRenderObject? Target, Vector3 End,
        EnbEffectDefinition Definition, ulong Sequence, bool Detached=false, bool FromPublication=false, Matrix4x4? RenderWorld=null,
        IEnbRenderObject? CoordinateOwner=null)
    {
        public IEnbRenderObject ResourceOwner=>Target ?? CoordinateOwner ?? throw new InvalidDataException("Seek resource identity unavailable");
    }
    private sealed record FixedCombatBinding(Matrix4x4 World, IEnbRenderObject ResourceOwner, bool RenderObjectLink=false);
    // ResourceOwner supplies stable GPU identity only. It is never restored to
    // world membership or used as a live object for fixed-transform placement.
    private readonly Dictionary<(uint,ulong,ulong),SeekEndpointBinding> _combatSeekEndpoints=[];
    private sealed record ChildSeekRenderBinding(EnbEffectDefinition Definition,IEnbRenderObject ResourceOwner,Matrix4x4? World);
    private readonly Dictionary<(uint,ulong,ulong),ChildSeekRenderBinding> _combatChildSeekRenders=[];
    private readonly Dictionary<(uint,ulong,ulong),FixedCombatBinding> _combatFixedBindings=[];
    private sealed record LookTarget(IEnbRenderObject? Object, bool Detached, Vector3? End=null, bool PositionOnly=false, bool CapturedAtActivation=false);
    private readonly Dictionary<(uint,ulong,ulong),LookTarget> _combatLookTargets=[];

    private Vector3? ResolveBinaryEffectSoundPosition(EnbCurrentRetailEffectSoundNode node,Vector3 listener)
    {
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
        if(!_combatEffectBindings.TryGetValue(key,out var binding) ||
            !_combatLookTargets.TryGetValue(key,out var target) || target.End is not Vector3 end)return null;
        // 006EFF20 reads A8/B4, independently of the visual origin chosen by
        // TargetCenterToPoint. 006ECFF0 projects onto those same retained ends.
        var start=binding.Source.Translation;
        return node.Definition.EffectClassKind==EnbEffectClassKind.ParticleJetEffect
            ? EnbCurrentRetailEffectSoundRuntime.ClosestLinePosition(start,end,listener)
            : EnbRetailIniDocument.ReadBool(node.Definition.Properties,"SourceBased")?start:end;
    }
    private readonly Dictionary<(uint,ulong,ulong),string> _combatPoseErrors=[];

    private IEnbRenderObject? ResolveCombatResourceOwner(EnbCurrentRetailEffectOwner node, IReadOnlyList<IEnbRenderObject> objects)
    {
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
        if(_combatFixedBindings.TryGetValue(key,out var fixedBinding))return fixedBinding.ResourceOwner;
        _combatEffectBindings.TryGetValue(key,out var binding);
        if(binding is not null && (node.HasUnboundEndpoints || node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint ||
            binding.SourcePositionOnly && node.BoundGameId==node.Root.SourceGameId))return binding.ResourceOwner;
        if(node.Definition.EffectClassKind==EnbEffectClassKind.RObjLookAtEffect && node.BoundGameId==node.Root.TargetGameId &&
            _combatLookTargets.TryGetValue(key,out var target))return target.Object;
        return PublishedSeekEndpoints(node) is {} published && binding?.SourceIdentity==published.Source.RenderIdentity &&
            !objects.Any(o=>o.GameId==node.Root.SourceGameId && o.RenderIdentity==published.Source.RenderIdentity)?published.Source:node.ResolveSource(objects);
    }

    private static EnbSceneEffectEndpointSnapshots? PublishedSeekEndpoints(EnbCurrentRetailEffectOwner node)
        => node.LinkKind==EnbCurrentRetailEffectLinkKind.Root && node.Active &&
            node.Definition.EffectClassKind is (EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect) &&
            node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToObject && string.IsNullOrEmpty(node.Root.BoneName) &&
            node.Root.EndpointShortA==0 && node.Root.EndpointShortB==0 && !node.Root.DescriptorFlag && node.Root.SpatialOffset==Vector3.Zero &&
            node.DurationMilliseconds!=uint.MaxValue && !node.Definition.InfiniteDuration &&
            !node.Definition.GoAwayWhenSourceDoes && !node.Definition.GoAwayWhenTargetDoes && !node.Root.EndWithTarget
                ? node.Root.PublishedEndpoints is {CompleteAtFirstPublication:true} published ? published : null : null;

    private Matrix4x4 PlaceCombatEffect(EnbCurrentRetailEffectOwner node, EnbCurrentRetailRObjEffectDefinition definition,
        Matrix4x4 world, EnbResolvedRenderCamera camera, float radius)
    {
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
        bool retainedFront=!definition.TrackSource && definition.InFrontOfObject &&
            node.Definition.EffectClassKind==EnbEffectClassKind.RObjEffect;
        if(retainedFront && _combatFrontFramePoses.TryGetValue(key,out var placed))return placed;
        if(retainedFront && _combatEffectPoses.TryGetValue(key,out var retainedWorld))world=retainedWorld;
        bool fixedTransform=_combatFixedBindings.ContainsKey(key);
        if(fixedTransform || _combatEffectBindings.TryGetValue(key,out var binding) && binding.SourceDetached ||
            node.Definition.EffectClassKind==EnbEffectClassKind.RObjEffect && node.SourceBinding?.IsDetached==true)
        {
            // 006E7690 clears the pointer on loss; 006EF291 skips camera-front
            // displacement for both newly fixed and previously object-bound effects.
            definition=definition with {InFrontOfObject=false};
        }
        placed=definition.ApplyCameraPlacement(world,camera,radius);
        if(retainedFront)
        {
            //006E6C3A updates once per active client frame, including equal
            //clock values.006EF250 mutates +94;006EF120 publishes that pose.
            //Budget/primitive/draw consumers borrow the same frame result.
            _combatFrontFramePoses[key]=placed;
            _combatEffectPoses[key]=placed;
            if(_combatEffectBindings.TryGetValue(key,out var retained))
                _combatEffectBindings[key]=retained with {Source=placed};
        }
        return placed;
    }

    private bool IsMatrixBoundEffect(EnbCurrentRetailEffectOwner node)
        => _combatFixedBindings.ContainsKey((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence));

    private bool IsCoordinateBoundEffect(EnbCurrentRetailEffectOwner node)
    {
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
        // LookAt setters006EFF90/006F0060 scale only the endpoint selected by
        // SourceBased. Losing the other endpoint must not suppress its radius.
        if(node.Definition.EffectClassKind==EnbEffectClassKind.RObjLookAtEffect &&
            !EnbRetailIniDocument.ReadBool(node.Definition.Properties,"SourceBased"))
            return _combatLookTargets.TryGetValue(key,out var target) && target.PositionOnly;
        return _combatEffectBindings.TryGetValue(key,out var binding) && binding.SourcePositionOnly;
    }

    private float ResolveCombatRenderScale(EnbCurrentRetailEffectOwner node, EnbCurrentRetailRObjEffectDefinition definition,float sourceRadius)
        // 006EF160 scales only through the GameID setter. A fresh matrix-bound
        // child (006E48A0) has no source pointer and retains factory scale.
        => (IsMatrixBoundEffect(node) || IsCoordinateBoundEffect(node)?definition with {ScaleToRadius=false}:definition).ResolveRenderScale(sourceRadius);

    private readonly HashSet<SceneOwner> _dirtyCombatRadiusSources=[];
    private long _baseCombatRadiusPublications;
    private void PublishChangedCombatRadii(EnbRetailAssetBundle assets)
    {
        if(assets.ClientBaseAssets is not {} catalog)return;
        foreach(var source in _dirtyCombatRadiusSources)
        {
            var obj=source.Definition;
            if(HasCurrentRetailShipLegoComposition(obj))continue;
            if(catalog.TryResolve(obj.BaseAsset,out var geometry))
            {
                float radius=(geometry.Radius+geometry.RadiusAdjust)*obj.Scale;
                if(float.IsFinite(radius)){_lastEffectObjectRadii[obj.RenderIdentity]=radius<0?0:radius;_baseCombatRadiusPublications++;}
            }
        }
        _dirtyCombatRadiusSources.Clear();
    }
    private readonly Dictionary<ulong,float> _lastEffectObjectRadii = [];
    private readonly HashSet<ulong> _effectRadiusObjects = [];
    private readonly Dictionary<ulong,float> _effectControllerRadii = [];
    private IReadOnlyDictionary<ulong,float> BuildCombatEffectRadii(IReadOnlyCollection<LiveRootInstance> roots,EnbRetailAssetBundle assets,
        IReadOnlyList<IEnbRenderObject> objects)
    {
        _effectControllerRadii.Clear();
        if(_ownedSceneFrame&&roots is RootTaskList tasks&&tasks.IsOwnedBy(this))PublishOwnedEffectRadii(tasks,assets.ClientBaseAssetMounts);
        else PublishRetainedCurrentRetailDistanceRadii(roots,assets.ClientBaseAssetMounts,_effectControllerRadii);
        foreach(var pair in _effectControllerRadii)_lastEffectObjectRadii[pair.Key]=pair.Value;
        _effectRadiusObjects.Clear();
        //006F39F0 initializes the ordinary object's distance radius from
        //cbasset.ini;006E8890 reads that object field, not the GPU root list.
        //Attached ship controllers still require their composed radius above.
        void PublishOrdinary(IEnbRenderObject obj)
        {
            // First source wins, including a source whose radius is invalid.
            // Detached targets keep their last publication when no new value exists.
            if(!_effectRadiusObjects.Add(obj.RenderIdentity) || _effectControllerRadii.ContainsKey(obj.RenderIdentity) || HasCurrentRetailShipLegoComposition(obj))return;
            if(assets.ClientBaseAssets?.TryResolve(obj.BaseAsset,out var geometry)==true)
            {
                float radius=(geometry.Radius+geometry.RadiusAdjust)*obj.Scale;
                //6F3AF4 reports a negative final radius, then publishes zero.
                if(float.IsFinite(radius))_lastEffectObjectRadii[obj.RenderIdentity]=radius<0?0:radius;
            }
        }
        bool ownedObjects=_ownedSceneFrame&&ReferenceEquals(objects,_ownedScene!.OrderedStates);
        if(ownedObjects)PublishChangedCombatRadii(assets);
        else foreach(var obj in objects)PublishOrdinary(obj);
        foreach(var target in _combatLookTargets.Values)
            if(target.Object is {} obj&&(!ownedObjects||!_frameObjectIdentities.Contains(obj.RenderIdentity)))PublishOrdinary(obj);
        _effectRadiusObjects.Clear();
        return _lastEffectObjectRadii;
    }

    // Resolve once before both HLOD selection and draw compilation. Seek effects
    // default to capturing the launch point (006F0FD0/006F07F0 clear0x80);
    // explicit TrackSource overrides that default. Destination tracking uses
    //006E8890 flag0x100. Linked unary flashes retain the source
    // bone binding; a target-bound NextLink must not inherit that weapon mount.
    private void UpdateCombatEffectPoses(IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        IEnbSceneFrame snapshot,EnbRetailAssetBundle assets,IReadOnlyList<LiveRootInstance> roots,uint clientTime)
    {
        _combatEffectPoses.Clear();
        _combatFrontFramePoses.Clear();
        _combatPoseErrors.Clear();
        var radii=BuildCombatEffectRadii(roots,assets,SceneObjects(snapshot));
        var live=nodes.Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        var renderParents=nodes.Where(n=>n.ParentSequence.HasValue).Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.ParentSequence!.Value)).ToHashSet();
        foreach(var key in _combatChildSeekRenders.Keys.Where(k=>!live.Contains(k) && !renderParents.Contains(k)).ToArray())_combatChildSeekRenders.Remove(key);
        foreach(var key in _combatEffectBindings.Keys.Where(k=>!live.Contains(k) && !renderParents.Contains(k)).ToArray())_combatEffectBindings.Remove(key);
        foreach(var key in _combatFixedBindings.Keys.Where(k=>!live.Contains(k) && !renderParents.Contains(k)).ToArray())_combatFixedBindings.Remove(key);
        foreach(var key in _combatLookTargets.Keys.Where(k=>!live.Contains(k) && !renderParents.Contains(k)).ToArray())_combatLookTargets.Remove(key);
        // Ended binary parents still supply coordinate bindings to children.
        // Observers detach independently of whether the parent draws this frame.
        foreach(var key in _combatEffectBindings.Keys.ToArray())
        {
            var saved=_combatEffectBindings[key];
            if(!saved.SourceDetached && saved.ResourceOwner is {} owner &&
                FindCombatSceneObject(snapshot,owner.GameId,saved.SourceIdentity) is null)
                _combatEffectBindings[key]=saved with {SourceDetached=true};
        }
        foreach(var key in _combatLookTargets.Keys.ToArray())
        {
            var saved=_combatLookTargets[key];
            if(!saved.Detached && (saved.Object is null || FindCombatSceneObject(snapshot,saved.Object.GameId,saved.Object.RenderIdentity) is null))
                _combatLookTargets[key]=saved with {Detached=true};
        }
        // Observe retained non-line parents before creating their Line children.
        // Surviving endpoints still bind current objects inside the line runtime.
        EnbCurrentRetailLineBindingRuntime.Binding? RetainedBinaryParent(EnbCurrentRetailEffectOwner node)
        {
            var parentKey=(node.Root.EffectId,node.Root.InstanceGeneration,node.ParentSequence.GetValueOrDefault());
            if(!node.ParentSequence.HasValue || !_combatEffectBindings.TryGetValue(parentKey,out var source) || source.ResourceOwner is null ||
                !_combatLookTargets.TryGetValue(parentKey,out var target))return null;
            return new(source.ResourceOwner,target.Object,source.Source.Translation,
                target.End ?? target.Object!.Spatial!.Transform.Position+node.Root.SpatialOffset,source.SourceDetached,target.Detached);
        }
        void UpdateLineBindings(EnbCurrentRetailEffectOwner? selected)=>_lineBindings.Update(nodes,SceneObjects(snapshot),
            (owner,bone)=>ResolveCombatSourcePose(owner,bone,roots,assets),RetainedBinaryParent,
            target=>radii.GetValueOrDefault(target.RenderIdentity),node=>ReferenceEquals(node,selected));
        //Observe/prune all retained parents now, then update each line once in
        //the scheduler's parent-before-child traversal (006E6AD0).
        UpdateLineBindings(null);
        var rootSnapshots=nodes.Select(n=>n.Root).DistinctBy(r=>(r.EffectId,r.InstanceGeneration)).ToDictionary(r=>(r.EffectId,r.InstanceGeneration));
        var activeSeekNodes=nodes.Where(n=>n.Active &&
            n.Definition.EffectClassKind is (EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect))
            .Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        foreach(var key in _combatSeekEndpoints.Keys.ToArray())
        {
            if(!rootSnapshots.TryGetValue((key.Item1,key.Item2),out var root)){_combatSeekEndpoints.Remove(key);continue;}
            var endpoint=_combatSeekEndpoints[key];
            if(endpoint.Detached)continue;
            var target=FindCombatSceneObject(snapshot,root.TargetGameId);
            if(target is null || target.RenderIdentity!=endpoint.Target?.RenderIdentity)
                _combatSeekEndpoints[key]=endpoint with {Detached=true};
            // 006E6AD0 bypasses root update while scheduled or finished.
            // Observer detachment still clears the pointer and retains coordinates.
            else if(activeSeekNodes.Contains(key) &&
                (!endpoint.Definition.Properties.ContainsKey("TrackTarget") || EnbRetailIniDocument.ReadBool(endpoint.Definition.Properties,"TrackTarget")) &&
                target.Spatial?.Transform is {} transform)
                _combatSeekEndpoints[key]=endpoint with {Target=target,End=transform.Position+root.SpatialOffset};
        }
        foreach(var node in nodes)
        {
            if(node.Definition.EffectClassKind==EnbEffectClassKind.LineEffect)
            {UpdateLineBindings(node);continue;}
            if(node.Definition.EffectClassKind==EnbEffectClassKind.RObjEffect && node.Root.GeometryKind==EnbSceneEffectGeometryKind.Point &&
                node.TryGetRObjDefinition(out var pointDefinition,out _))
            {
                var pointKey=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
                if(!_combatFixedBindings.ContainsKey(pointKey))
                {
                    // 00735174 -> unary +2C (006E48A0): identity rotation,
                    // packet translation. This private record supplies GPU cache
                    // identity only; no world object or target is materialized.
                    var point=node.Root.WorldPoint;
                    var resourceOwner=new EnbSceneObjectSnapshot(ulong.MaxValue-node.Sequence,1,0,false,false,0,pointDefinition.BaseAssetId,1,0,0,0,0,false,null,
                        new(false,0,null),new(EnbSceneSpatialProviderKind.Fixed,new(node.ActualStartTick,point,Quaternion.Identity,Vector3.Zero,0,Vector3.Zero,Vector3.UnitX),null),
                        null,null,[],[],[],[],[],[],null,[],null,0,0);
                    _combatFixedBindings[pointKey]=new(Matrix4x4.CreateTranslation(point),resourceOwner);
                }
                _combatEffectPoses[pointKey]=_combatFixedBindings[pointKey].World;
                continue;
            }
            bool seek=node.Definition.EffectClassKind is EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect;
            bool jet=node.Definition.EffectClassKind==EnbEffectClassKind.ParticleJetEffect;
            bool look=node.Definition.EffectClassKind==EnbEffectClassKind.RObjLookAtEffect || jet;
            // PassEffect vtable00B00184 shares unary +24/006E78A0 and
            // +2C/006E48A0. Its surface may have no geometry after owner loss,
            // but its retained matrix still binds authored child objects.
            bool objectUnary=node.Root.GeometryKind==EnbSceneEffectGeometryKind.Object &&
                node.Definition.EffectClassKind is (EnbEffectClassKind.RObjEffect or EnbEffectClassKind.SoundEffect or EnbEffectClassKind.PassEffect);
            bool linked=objectUnary || node.Definition.EffectClassKind is (EnbEffectClassKind.RObjEffect or EnbEffectClassKind.SoundEffect or EnbEffectClassKind.PassEffect) &&
                (node.Root.IsObjectToObject || node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint) && node.LinkKind!=EnbCurrentRetailEffectLinkKind.Root;
            bool retainLinked=linked && (objectUnary || node.Definition.EffectClassKind is (EnbEffectClassKind.RObjEffect or EnbEffectClassKind.PassEffect) ||
                node.DurationMilliseconds!=uint.MaxValue && !node.Definition.InfiniteDuration && !node.Definition.GoAwayWhenSourceDoes);
            if(!seek && !linked && !look)continue;
            var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
            var parentKey=(node.Root.EffectId,node.Root.InstanceGeneration,node.ParentSequence.GetValueOrDefault());
            if(linked && _combatFixedBindings.TryGetValue(key,out var retainedUnary))
            {
                // A copied unary matrix belongs to the child (006E48A0).
                // It survives the parent's independently timed completion.
                _combatEffectPoses[key]=retainedUnary.World;
                continue;
            }
            if(linked && !_combatEffectBindings.ContainsKey(key) &&
                (_lineBindings.ResolveParent(node) ?? RetainedBinaryParent(node)) is {} lineBinding)
            {
                // Shared binary +3C/006E8DD0: a missing selected
                // endpoint supplies a rigid matrix through child+2C.
                bool toTarget=node.UsesTargetBinding;
                if(toTarget?lineBinding.TargetDetached:lineBinding.SourceDetached)
                {
                    var position=toTarget?lineBinding.End:lineBinding.Start;
                    var other=toTarget?lineBinding.Start:lineBinding.End;
                    // A packet world endpoint has no target object. Reuse the
                    // source only as a GPU resource identity for this fixed
                    // matrix; placement, radius and front work remain unbound.
                    var fixedLine=new FixedCombatBinding(EnbRetailSeekEffectPath.LookAlong(other-position,position),
                        toTarget?lineBinding.Target ?? lineBinding.Source:lineBinding.Source);
                    _combatFixedBindings[key]=fixedLine;_combatEffectPoses[key]=fixedLine.World;
                    continue;
                }
            }
            if(linked && node.ParentSequence is {} unaryParent &&
                _combatFixedBindings.TryGetValue((node.Root.EffectId,node.Root.InstanceGeneration,unaryParent),out var fixedUnary))
            {
                // Sound and RObj +24 both dispatch to 006E78A0. At 006E79CF
                // a unary parent without a source forwards its stored
                // matrix via child +2C. Never resolve a replacement GameID here.
                _combatFixedBindings[key]=new(fixedUnary.World,fixedUnary.ResourceOwner);
                _combatEffectPoses[key]=fixedUnary.World;
                continue;
            }
            if(linked && !_combatEffectBindings.ContainsKey(key) && node.ParentSequence.HasValue &&
                _combatEffectBindings.TryGetValue(parentKey,out var detachedUnary) &&
                detachedUnary.Unary && detachedUnary.SourceDetached && detachedUnary.ResourceOwner is {} unaryOwner)
            {
                // 006E78A0/006E79CF also forwards +94 when a previously live
                // unary owner disappears. The new child receives a matrix, not
                // the old GameID or the parent's captured ScaleToRadius value.
                _combatFixedBindings[key]=new(detachedUnary.Source,unaryOwner);
                _combatEffectPoses[key]=detachedUnary.Source;
                continue;
            }
            if(linked && node.LinkKind==EnbCurrentRetailEffectLinkKind.NextLink && node.ParentSequence is {} childParent &&
                _combatChildSeekRenders.TryGetValue((node.Root.EffectId,node.Root.InstanceGeneration,childParent),out var childRender) &&
                childRender.Definition.NextLinkId==node.Definition.DefinitionId && EnbRetailIniDocument.ReadBool(childRender.Definition.Properties,"UnaryNextLinkToRObj"))
            {
                // 006EFC20 uses this seek instance's render object, including
                // seeks created by StartLink; it does not select the packet root.
                if(childRender.World is not {} childWorld)
                {_combatPoseErrors[key]="child projectile has no retained active render matrix";continue;}
                _combatFixedBindings[key]=new(childWorld,childRender.ResourceOwner,true);
                _combatEffectPoses[key]=childWorld;
                continue;
            }
            if(linked && node.LinkKind==EnbCurrentRetailEffectLinkKind.NextLink &&
                assets.PresentationCatalog?.FindEffect(node.Root.DefinitionId) is {} rootDefinition &&
                rootDefinition.EffectClassKind is (EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect) &&
                EnbRetailIniDocument.ReadBool(rootDefinition.Properties,"UnaryNextLinkToRObj") &&
                _combatSeekEndpoints.TryGetValue(parentKey,out var renderParent) && node.ParentSequence==renderParent.Sequence)
            {
                if(!_combatFixedBindings.TryGetValue(key,out var renderLink))
                {
                    if(renderParent.Definition.NextLinkId!=node.Definition.DefinitionId || renderParent.RenderWorld is not {} renderWorld)continue;
                    // Completion removes the root from its scene before NextLink;
                    // 006EFC20 copies its retained render matrix, not a new endpoint.
                    _combatFixedBindings[key]=renderLink=new(renderWorld,renderParent.ResourceOwner,true);
                }
                _combatEffectPoses[key]=renderLink.World;
                continue;
            }
            if(retainLinked && node.LinkKind==EnbCurrentRetailEffectLinkKind.StartLink &&
                (_combatFixedBindings.ContainsKey(key) || !_combatEffectBindings.ContainsKey(key)) &&
                node.Root.PublishedEndpoints is {} launchPublication &&
                _combatSeekEndpoints.TryGetValue(parentKey,out var launchParent) && launchParent.FromPublication && node.ParentSequence==launchParent.Sequence &&
                !launchParent.Definition.UnaryStartLinkToTarget && launchParent.Definition.StartLinkId==node.Definition.DefinitionId &&
                FindCombatSceneObject(snapshot,node.Root.SourceGameId,launchPublication.Source.RenderIdentity) is null)
            {
                if(!_combatFixedBindings.TryGetValue(key,out var launchFixed))
                    _combatFixedBindings[key]=launchFixed=new(EnbRetailSeekEffectPath.LookAlong(launchParent.End-launchParent.Source.Translation,
                        launchParent.Source.Translation),launchPublication.Source);
                _combatEffectPoses[key]=launchFixed.World;
                continue;
            }
            if(linked && node.LinkKind==EnbCurrentRetailEffectLinkKind.NextLink &&
                (_combatFixedBindings.ContainsKey(key) || !_combatEffectBindings.ContainsKey(key)) &&
                _combatSeekEndpoints.TryGetValue(parentKey,out var lost) && lost.Detached &&
                node.ParentSequence==lost.Sequence &&
                lost.Definition.UnaryNextLinkToTarget && lost.Definition.NextLinkId==node.Definition.DefinitionId &&
                !EnbRetailIniDocument.ReadBool(lost.Definition.Properties,"UnaryNextLinkToRObj"))
            {
                if(!_combatFixedBindings.TryGetValue(key,out var fixedBinding))
                    _combatFixedBindings[key]=fixedBinding=new(EnbRetailSeekEffectPath.LookAlong(lost.Source.Translation-lost.End,lost.End),lost.ResourceOwner);
                _combatEffectPoses[key]=fixedBinding.World;
                continue;
            }
            _combatEffectBindings.TryGetValue(key,out var binding);
            bool unboundRoot=node.HasUnboundEndpoints && node.LinkKind==EnbCurrentRetailEffectLinkKind.Root && look;
            var source=unboundRoot?binding?.ResourceOwner ?? node.CreateUnboundResourceOwner():FindCombatSceneObject(snapshot,seek || look?node.Root.SourceGameId:node.BoundGameId);
            if(objectUnary && binding is null)
            {
                bool tracks=!node.Definition.Properties.ContainsKey("TrackSource") || EnbRetailIniDocument.ReadBool(node.Definition.Properties,"TrackSource");
                var receipt=tracks?node.Root.LastSourceObject:node.Root.InitialSourceObject;
                var identity=node.BoundRenderIdentity ?? receipt?.RenderIdentity;
                if(source is not null && identity is not null && source.RenderIdentity!=identity)source=null;
                source=tracks?source ?? receipt:receipt ?? source;
            }
            if(look || seek)
            {
                if(node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint && binding is null &&
                    node.Root.EndpointReceipt is {} pointReceipt &&
                    (pointReceipt.SourceCreationOrdinal is null || source?.WorldCreationOrdinal!=pointReceipt.SourceCreationOrdinal))source=null;
                // LookAt shares binary observer 006E7ED0. Pointer removal
                // retains endpoint coordinates; a reused ID is a new object.
                if(binding is not null && (source is null || source.RenderIdentity!=binding.SourceIdentity))
                    _combatEffectBindings[key]=binding=binding with {SourceDetached=true};
                if(!unboundRoot && (look || node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint) && binding is null && (source?.Spatial is null || node.Root.LastSourceObject is {} receipt && source.RenderIdentity!=receipt.RenderIdentity))
                    source=node.Root.LastSourceObject;
            }
            CombatEffectBinding? inheritedSource=null;
            if((seek || look) && binding is null && node.ParentSequence.HasValue &&
                _combatEffectBindings.TryGetValue(parentKey,out var parentSource) && parentSource.SourceDetached && parentSource.ResourceOwner is not null)
            {
                // 006E8C7E..89 passes +A8 coordinates to child +30, not a
                // GameID. Retain only a resource identity from the old owner.
                inheritedSource=parentSource;source=parentSource.ResourceOwner;
            }
            if((seek || look) && binding is null && inheritedSource is null &&
                _lineBindings.ResolveParent(node) is {SourceDetached:true} lineSource)
            {
                inheritedSource=new(Matrix4x4.CreateTranslation(lineSource.Start),null,lineSource.Source.RenderIdentity,
                    SourceDetached:true,ResourceOwner:lineSource.Source,SourcePositionOnly:true);
                source=lineSource.Source;
            }
            var publication=PublishedSeekEndpoints(node);
            if(binding is null && publication is not null &&
                (source is null || source.RenderIdentity!=publication.Source.RenderIdentity))source=publication.Source;
            // Unary observer 006E7690 permanently clears the original source
            // pointer. A reused GameID must not attach an existing linked flash
            // to a different object; retain the last resolved bone/world pose.
            if(retainLinked && binding is not null && !binding.SourceDetached &&
                (source is null || source.RenderIdentity!=binding.SourceIdentity))
                _combatEffectBindings[key]=binding=binding with {SourceDetached=true};
            // Binary observer 006E7ED0 clears the source pointer, retaining its
            // captured coordinates. A successfully bound seek no longer needs
            // a live source; lifecycle shortening belongs to the scheduler.
            if(source?.Spatial?.Transform is null && !(binding is not null && (seek || look || retainLinked && binding.SourceDetached)))
            {_combatPoseErrors[key]=$"source 0x{node.Root.SourceGameId:X8} has no current or retained spatial transform (bone '{node.Root.BoneName}')";continue;}
            try
            {
                bool track=node.Definition.Properties.ContainsKey("TrackSource") ? EnbRetailIniDocument.ReadBool(node.Definition.Properties,"TrackSource") : !seek;
                //RObj6EF200,Pass6EDC00 and Sound vtable+B00108 call6E7850
                //at activation even without TrackSource. Later6E7870
                //refreshes only when flag0x80 is set.
                bool unaryActivation=linked &&
                    node.Active && binding is {SourceCapturedAtActivation:false,SourceDetached:false};
                if(binding is null || !seek && (track || unaryActivation) && (!look || node.Active) && !(retainLinked && binding.SourceDetached) && !(look && (binding.SourceDetached || source?.Spatial is null)))
                {
                    var world=unboundRoot?Matrix4x4.Identity:inheritedSource?.Source ?? ResolveCombatSourcePose(source!,linked?node.UnaryBoneName:source!.GameId==node.Root.SourceGameId?node.Root.BoneName:null,roots,assets);
                    var path=EnbRetailSeekEffectConstruction.Spline(node.Definition,node.TravelDurationMilliseconds);
                    var linearPath=EnbRetailSeekEffectConstruction.Linear(node.Definition,node.TravelDurationMilliseconds);
                    _combatEffectBindings[key]=binding=new(world,path,source!.RenderIdentity,
                        SourceDetached:(look || seek || objectUnary) && FindCombatSceneObject(snapshot,source.GameId,source.RenderIdentity) is null,
                        SourceCapturedAtActivation:node.Active,LinearPath:linearPath,ResourceOwner:source,SourcePositionOnly:unboundRoot || inheritedSource is not null,Unary:linked);
                }
                // Binary activation006E8800 refreshes a surviving source before
                // activation/StartLink.006E8890 also refreshes every active frame
                // when the factory's authored TrackSource override sets0x80.
                // Preserve the instance's existing random/path history.
                if((seek || look) && node.Active && (seek && track || !binding.SourceCapturedAtActivation))
                {
                    bool captureEndpoints=!binding.SourceCapturedAtActivation;
                    if(!binding.SourceDetached && source?.Spatial is not null && source.RenderIdentity==binding.SourceIdentity)
                        binding=binding with {Source=ResolveCombatSourcePose(source,node.Root.BoneName,roots,assets)};
                    _combatEffectBindings[key]=binding=binding with {SourceCapturedAtActivation=true};
                    if(_combatSeekEndpoints.TryGetValue(key,out var scheduledEndpoint))
                    {
                        //006E8800 refreshes both surviving endpoints at activation,
                        //before006E8890's per-frame tracking flags take effect.
                        var activationTarget=captureEndpoints && !scheduledEndpoint.Detached
                            ? (scheduledEndpoint.Target is {} endpointTarget?FindCombatSceneObject(snapshot,node.Root.TargetGameId,endpointTarget.RenderIdentity):null):null;
                        _combatSeekEndpoints[key]=scheduledEndpoint with {Source=binding.Source,FromPublication=publication is not null,
                            Target=activationTarget ?? scheduledEndpoint.Target,
                            End=activationTarget?.Spatial is {} activationSpatial?activationSpatial.Transform.Position+node.Root.SpatialOffset:scheduledEndpoint.End};
                    }
                }
                var pose=binding.Source;
                if(look)
                {
                    var target=FindCombatSceneObject(snapshot,node.Root.TargetGameId);
                    _combatLookTargets.TryGetValue(key,out var lastTarget);
                    bool firstTarget=lastTarget is null;
                    if(unboundRoot && lastTarget is null)lastTarget=new(null,true,Vector3.Zero,true);
                    if(lastTarget is null && node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint)
                    {
                        var endpoint=node.Root.WorldPoint;
                        if(!float.IsFinite(endpoint.X)||!float.IsFinite(endpoint.Y)||!float.IsFinite(endpoint.Z))throw new InvalidDataException("Binary world endpoint is non-finite");
                        // 00735C31/47 -> +38/006E54A0 copies B4/B8/BC without
                        // a target pointer. The factory radius remains intact.
                        lastTarget=new(null,true,endpoint,true);
                    }
                    if(lastTarget is null && node.ParentSequence.HasValue &&
                        _combatLookTargets.TryGetValue(parentKey,out var inheritedLookTarget) && inheritedLookTarget.Detached)
                    {
                        // LookAt shares binary +30/+38 coordinate setters with
                        // seek effects. A child created after loss inherits its
                        // immediate parent's retained endpoint, not root receipt.
                        lastTarget=inheritedLookTarget with {PositionOnly=true};
                    }
                    if(lastTarget is null && node.ParentSequence.HasValue &&
                        _combatSeekEndpoints.TryGetValue(parentKey,out var inheritedSeekTarget) && inheritedSeekTarget.Detached)
                        lastTarget=new(inheritedSeekTarget.Target,true,inheritedSeekTarget.End,true);
                    if(lastTarget is null && _lineBindings.ResolveParent(node) is {TargetDetached:true} inheritedLineTarget)
                        lastTarget=new(inheritedLineTarget.Target,true,inheritedLineTarget.End,true);
                    if(lastTarget is null && node.Root.PublishedEndpoints?.Target is {} publishedTarget)
                        lastTarget=new(publishedTarget,false);
                    if(lastTarget is not null && (target is null || target.RenderIdentity!=lastTarget.Object?.RenderIdentity))
                        lastTarget=lastTarget with {Detached=true};
                    // 006E6AD0 bypasses scheduled updates; observers may detach
                    // those instances, but their construction coordinates stay.
                    bool tracksTarget=!node.Definition.Properties.ContainsKey("TrackTarget") || EnbRetailIniDocument.ReadBool(node.Definition.Properties,"TrackTarget");
                    bool refreshTarget=firstTarget || node.Active && (tracksTarget || lastTarget?.CapturedAtActivation!=true);
                    if(lastTarget?.Detached!=true && target?.Spatial is not null)
                        lastTarget=refreshTarget?new(target,false,CapturedAtActivation:node.Active):lastTarget! with {Object=target};
                    if(lastTarget is null || lastTarget.Object?.Spatial is null && lastTarget.End is null)throw new InvalidDataException($"target 0x{node.Root.TargetGameId:X8} has no current or retained spatial transform");
                    _combatLookTargets[key]=lastTarget;
                    target=lastTarget.Object;
                    // The object pointer remains live for intersection/TargetCenterToPoint
                    // even when flag0x100 freezes the stored B4 endpoint.
                    var end=lastTarget.End ?? target!.Spatial!.Transform.Position+node.Root.SpatialOffset;
                    if(node.Active && node.LinkKind==EnbCurrentRetailEffectLinkKind.Root && !lastTarget.Detached && node.Root.DescriptorFlag && target?.Spatial?.Transform is {} targetTransform)
                        end=EnbRetailSeekEffectPath.IntersectTarget(pose.Translation,end,targetTransform.Position,radii.GetValueOrDefault(target.RenderIdentity));
                    _combatLookTargets[key]=lastTarget with {End=end};
                    // 006F0130: source-based at source, otherwise at target;
                    // TargetCenterToPoint points from target centre to hit point.
                    bool sourceBased=jet ? !EnbRetailIniDocument.ReadBool(node.Definition.Properties,"Invert") : EnbRetailIniDocument.ReadBool(node.Definition.Properties,"SourceBased");
                    var origin=sourceBased?pose.Translation:end;
                    var point=sourceBased?end:pose.Translation;
                    if(!jet && !sourceBased && !lastTarget.Detached && EnbRetailIniDocument.ReadBool(node.Definition.Properties,"TargetCenterToPoint") && target?.Spatial?.Transform is {} centre)
                    {origin=centre.Position;point=end;}
                    pose=EnbRetailSeekEffectPath.BetweenPoints(origin,point);
                }
                if(seek)
                {
                    bool tracksTarget=!node.Definition.Properties.ContainsKey("TrackTarget") || EnbRetailIniDocument.ReadBool(node.Definition.Properties,"TrackTarget");
                    bool pointTarget=node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint;
                    var target=FindCombatSceneObject(snapshot,node.Root.TargetGameId);
                    if(pointTarget && !_combatSeekEndpoints.ContainsKey(key))
                    {
                        var point=node.Root.WorldPoint;
                        if(!float.IsFinite(point.X)||!float.IsFinite(point.Y)||!float.IsFinite(point.Z))throw new InvalidDataException("Seek world endpoint is non-finite");
                        // Packet000D invokes binary +38 directly. Capture a
                        // coordinate, with no target object or radius binding.
                        _combatSeekEndpoints[key]=new(binding.Source,null,point,node.Definition,node.Sequence,
                            Detached:true,CoordinateOwner:binding.ResourceOwner);
                    }
                    if(publication is not null && target?.RenderIdentity!=publication.Target.RenderIdentity)
                    {
                        target=null;
                        // Before first activation the binary retains its initial
                        // target coordinates. Surviving endpoints refresh there.
                        if(!_combatSeekEndpoints.ContainsKey(key) && publication.InitialTarget?.Spatial?.Transform is {} savedTarget)
                            _combatSeekEndpoints[key]=new(binding.Source,publication.InitialTarget,savedTarget.Position,node.Definition,node.Sequence,true,true);
                    }
                    // Binary observers (006E7ED0) retain coordinates on each
                    // instance, including projectile StartLink descendants.
                    bool retainTarget=!tracksTarget || node.DurationMilliseconds!=uint.MaxValue &&
                        !node.Definition.InfiniteDuration;
                    if(retainTarget && !_combatSeekEndpoints.ContainsKey(key) && node.ParentSequence.HasValue &&
                        _combatSeekEndpoints.TryGetValue(parentKey,out var inheritedTarget) && inheritedTarget.Detached)
                    {
                        // 006E8CA6..AF passes the parent's retained target to
                        // the new binary child's +38 when its pointer is gone.
                        // Do not bind a replacement GameID or copy flight state.
                        _combatSeekEndpoints[key]=new(binding.Source,inheritedTarget.Target,inheritedTarget.End,
                            node.Definition,node.Sequence,Detached:true,CoordinateOwner:inheritedTarget.CoordinateOwner);
                    }
                    if(retainTarget && !_combatSeekEndpoints.ContainsKey(key) && target?.Spatial?.Transform is {} initialTarget)
                        _combatSeekEndpoints[key]=new(binding.Source,target,initialTarget.Position+node.Root.SpatialOffset,node.Definition,node.Sequence,FromPublication:publication is not null);
                    Vector3 end;
                    if((pointTarget || retainTarget) && _combatSeekEndpoints.TryGetValue(key,out var endpoint) && (endpoint.Detached || !tracksTarget))end=endpoint.End;
                    else if(target?.Spatial?.Transform is {} destination)end=destination.Position+node.Root.SpatialOffset;
                    else continue;
                    if(node.Active && node.LinkKind==EnbCurrentRetailEffectLinkKind.Root && node.Root.DescriptorFlag &&
                        _combatSeekEndpoints.TryGetValue(key,out var surfaceTarget) && !surfaceTarget.Detached &&
                        target?.RenderIdentity==surfaceTarget.Target?.RenderIdentity && target?.Spatial?.Transform is {} targetCenter)
                    {
                        end=EnbRetailSeekEffectPath.IntersectTarget(pose.Translation,end,targetCenter.Position,radii.GetValueOrDefault(target.RenderIdentity));
                        _combatSeekEndpoints[key]=surfaceTarget with {End=end};
                    }
                    uint age=node.Active?unchecked(clientTime-node.ActualStartTick):0;
                    if(node.Definition.EffectClassKind==EnbEffectClassKind.SpiralSeekEffect)
                    {
                        var properties=node.Definition.Properties;
                        float radius=0,spin=0;
                        if(properties.ContainsKey("SpiralRadius") && !EnbRetailIniDocument.TryReadFloat(properties,"SpiralRadius",out radius) ||
                            properties.ContainsKey("SpiralSpinRate") && !EnbRetailIniDocument.TryReadFloat(properties,"SpiralSpinRate",out spin))
                            throw new InvalidDataException("Invalid authored spiral parameters");
                        pose=EnbRetailSpiralSeekEffectPath.Sample(node.TravelDurationMilliseconds,age,radius,spin,pose.Translation,end);
                    }
                    else if(binding.LinearPath is {} linearPath)pose=linearPath.Sample(age,pose.Translation,end);
                    else if(binding.Path is {} path)pose=path.Sample(age,pose.Translation,end);
                    else pose=EnbRetailSeekEffectPath.LookAlong(end-pose.Translation,
                        Vector3.Lerp(pose.Translation,end,Math.Clamp(age/(float)Math.Max(1,node.TravelDurationMilliseconds),0,1)));
                }
                _combatEffectPoses[key]=pose;
                if(node.LinkKind!=EnbCurrentRetailEffectLinkKind.Root && node.Definition.EffectClassKind is (EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect) &&
                    EnbRetailIniDocument.ReadBool(node.Definition.Properties,"UnaryNextLinkToRObj"))
                {
                    _combatChildSeekRenders.TryGetValue(key,out var retainedChild);
                    var owner=retainedChild?.ResourceOwner ?? source;
                    if(owner is not null)_combatChildSeekRenders[key]=new(node.Definition,owner,node.Active?pose:retainedChild?.World);
                }
                if(node.Active && seek && _combatSeekEndpoints.TryGetValue(key,out var renderedEndpoint))
                    _combatSeekEndpoints[key]=renderedEndpoint with {RenderWorld=pose};
            }
            catch(InvalidDataException ex) { _combatPoseErrors[key]=$"{ex.Message}; source 0x{node.Root.SourceGameId:X8}, target 0x{node.Root.TargetGameId:X8}, bone '{node.Root.BoneName}'"; }
        }
    }

    private void PublishEffectPositions(IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        IReadOnlyList<LiveRootInstance> roots,EnbRetailAssetBundle assets)
    {
        if(EffectPositionsPublished is {} publish)
        {
            var positions=System.Collections.Immutable.ImmutableDictionary.CreateBuilder<EnbNext.Net7.Gameplay.Net7EffectPoseKey,EnbNext.Net7.Gameplay.Net7RetainedEffectPosition>();
            foreach(var node in nodes.Where(n=>n.Active && n.LinkKind==EnbCurrentRetailEffectLinkKind.Root &&
                n.Root.PoseContext!=Guid.Empty && n.Definition.EffectClassKind is EnbEffectClassKind.RObjEffect or EnbEffectClassKind.SoundEffect or
                    EnbEffectClassKind.PassEffect or EnbEffectClassKind.AttachEffect or EnbEffectClassKind.WarpEffect or
                    EnbEffectClassKind.RObjLookAtEffect or
                    EnbEffectClassKind.LineEffect or EnbEffectClassKind.ParticleJetEffect or
                    EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SpiralSeekEffect or
                    EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect))
            {
                var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
                if(node.Definition.EffectClassKind==EnbEffectClassKind.WarpEffect)
                {
                    // 00B005F0 +8 inherits 006EF120. Publish after tunnel update;
                    // a faded tombstone is no longer a targetable effect.
                    if(_warpTunnels.TryGetValue(key,out var tunnel) && tunnel.Live && !tunnel.Fade.Ended && tunnel.Owner is not null)
                        positions[new(node.Root.PoseContext,node.Root.EffectId,node.Root.InstanceGeneration)]=new(tunnel.World.Translation);
                    continue;
                }
                if(node.Definition.EffectClassKind==EnbEffectClassKind.AttachEffect)
                {
                    // 006E4BA0 reads the retained unary translation, not a
                    // moving exhaust clone. Binding update precedes this pass.
                    if(node.Attachment?.SoundPosition is {} attachPosition)
                        positions[new(node.Root.PoseContext,node.Root.EffectId,node.Root.InstanceGeneration)]=new(attachPosition);
                    continue;
                }
                if(node.Definition.EffectClassKind==EnbEffectClassKind.LineEffect)
                {
                    if(_lineBindings.Resolve(node,out _) is {} line)
                        positions[new(node.Root.PoseContext,node.Root.EffectId,node.Root.InstanceGeneration)]=new(line.Start,line.End);
                    continue;
                }
                if(node.Definition.EffectClassKind==EnbEffectClassKind.ParticleJetEffect)
                {
                    if(_combatEffectBindings.TryGetValue(key,out var jetSource) &&
                        _combatLookTargets.TryGetValue(key,out var jetTarget) && jetTarget.End is {} jetEnd)
                        positions[new(node.Root.PoseContext,node.Root.EffectId,node.Root.InstanceGeneration)]=new(jetSource.Source.Translation,jetEnd);
                    continue;
                }
                if(node.Definition.EffectClassKind==EnbEffectClassKind.RObjLookAtEffect)
                {
                    // Native +8 006EFF20 selects A8 or B4 independently of
                    // visual-matrix placement and mesh preparation.
                    if(!_combatEffectBindings.TryGetValue(key,out var lookSource) ||
                        !_combatLookTargets.TryGetValue(key,out var lookTarget) || lookTarget.End is not {} lookEnd)continue;
                    var origin=EnbRetailIniDocument.ReadBool(node.Definition.Properties,"SourceBased")
                        ? lookSource.Source.Translation : lookEnd;
                    positions[new(node.Root.PoseContext,node.Root.EffectId,node.Root.InstanceGeneration)]=new(origin,lookEnd);
                    continue;
                }
                if(!_combatPoseErrors.ContainsKey(key) && _combatEffectPoses.TryGetValue(key,out var retained))
                {
                    Vector3? end=null;
                    if(node.Definition.EffectClassKind is EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SpiralSeekEffect or
                        EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect)
                    {
                        if(!_combatSeekEndpoints.TryGetValue(key,out var seek) || seek.RenderWorld is not {} renderWorld)continue;
                        retained=renderWorld;end=seek.End;
                    }
                    positions[new(node.Root.PoseContext,node.Root.EffectId,node.Root.InstanceGeneration)]=new(retained.Translation,end);
                }
            }
            //Retain the controller query state, including named/mount fallbacks,
            //so an incoming packet can sample any chain from this exact frame.
            if(nodes.Any(n=>n.Active && n.LinkKind==EnbCurrentRetailEffectLinkKind.Root &&
                n.Definition.EffectClassKind is EnbEffectClassKind.LineEffect or EnbEffectClassKind.ParticleJetEffect))
            {
                var controllers=ImmutableDictionary.CreateBuilder<(uint,ulong),Net7EffectSourceControllerPose>();
                IReadOnlyList<LiveRootInstance> queryRoots=[..roots,.._hiddenCombatSourceRoots];
                foreach(var root in queryRoots.Where(r=>r.InstancePath=="root"))
                {
                    if(root.Object.WorldCreationOrdinal is not {} ordinal)continue;
                    try
                    {
                        controllers[(root.Object.GameId,ordinal)]=CaptureCombatSourceController(root,queryRoots,assets);
                    }
                    catch(InvalidDataException) { } // Missing materialization stays unresolved.
                }
                foreach(var entry in _emptyRslSourcePoses.Values)
                    if(entry.Object.WorldCreationOrdinal is {} ordinal)
                    {
                        try { controllers[(entry.Object.GameId,ordinal)]=CaptureEmptyRslSourceController(entry.Object,entry.Pose,queryRoots,assets); }
                        catch(InvalidDataException) { } //Same local containment as drawable roots.
                    }
                var immutableControllers=controllers.ToImmutable();
                foreach(var key in positions.Keys.ToArray())positions[key]=positions[key] with {SourceControllers=immutableControllers};
            }
            publish(positions.ToImmutable());
        }
    }

    private Matrix4x4 ResolveCombatEffectPose(EnbCurrentRetailEffectOwner node,Matrix4x4 ordinary)
    {
        bool required=node.Definition.EffectClassKind is EnbEffectClassKind.StraightSeekEffect or EnbEffectClassKind.SplineRandomSeekEffect or EnbEffectClassKind.LinearRandomSeekEffect or EnbEffectClassKind.SpiralSeekEffect or EnbEffectClassKind.RObjLookAtEffect ||
            node.Root.GeometryKind==EnbSceneEffectGeometryKind.Object &&
                (node.Definition.EffectClassKind==EnbEffectClassKind.RObjEffect || !string.IsNullOrEmpty(node.Root.BoneName)) ||
            ((node.Root.IsObjectToObject || node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint) && node.LinkKind!=EnbCurrentRetailEffectLinkKind.Root) ||
            _combatFixedBindings.ContainsKey((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence));
        if(!required)return ordinary;
        return _combatEffectPoses.TryGetValue((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence),out var pose)
            ?pose:throw new InvalidDataException("Combat effect source/target pose unavailable: "+
                _combatPoseErrors.GetValueOrDefault((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence),"pose was not prepared"));
    }

    private Matrix4x4 ResolveCombatSourcePose(IEnbRenderObject source,string? boneName,
        IReadOnlyList<LiveRootInstance> roots,EnbRetailAssetBundle assets)
    {
        var spatial=source.Spatial!.Transform!;
        if(!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,out var world,out var error))throw new InvalidDataException(error);
        if(string.IsNullOrEmpty(boneName))return world;
        IReadOnlyList<LiveRootInstance> queryRoots=[..roots,.._hiddenCombatSourceRoots];
        var root=queryRoots.FirstOrDefault(r=>r.Object.RenderIdentity==source.RenderIdentity && r.InstancePath=="root");
        if(root is null && _emptyRslSourcePoses.TryGetValue(source.RenderIdentity,out var empty))
            return CaptureEmptyRslSourceController(empty.Object,empty.Pose,queryRoots,assets).ResolveEffectChain(boneName);
        if(root is null)throw new InvalidDataException("Combat source attachment unavailable");
        return CaptureCombatSourceController(root,queryRoots,assets).ResolveEffectChain(boneName);
    }

    private Net7EffectSourceControllerPose CaptureCombatSourceController(LiveRootInstance root,
        IReadOnlyList<LiveRootInstance> roots,EnbRetailAssetBundle assets)
    {
        var source=root.Object;
        if(!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(source.Spatial!.Transform,out var world,out var error))throw new InvalidDataException(error);
        var post=ConfiguredHierarchyPostTransforms(root);
        world=post?.RigidWorldTransform??root.WorldTransformOverride??world;
        var mounts=ImmutableDictionary.CreateBuilder<string,Matrix4x4>(StringComparer.Ordinal);
        var named=ImmutableDictionary.CreateBuilder<string,Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        if(!TryResolveConfiguredAttachmentHierarchy(root.Root,out var hierarchy,out error))throw new InvalidDataException(error);
        if(hierarchy is not null)
        {
            if(!TryResolveRetainedHierarchyPose(root,hierarchy,world,
                post?.HierarchyTranslationScale??(root.IsShipLegoController?1:source.Scale),root.AnimationBlend?.ResolvedSamples??[],
                post?.Transforms,out var pose,out error))throw new InvalidDataException(error);
            //98A000 chooses the first duplicate and pivot zero on a miss.
            world=pose.WorldTransforms[0];
            for(int i=0;i<hierarchy.Pivots.Count;i++)
                named.TryAdd(hierarchy.Pivots[i].Name,pose.WorldTransforms[i]);
            if(assets.ClientBaseAssetMounts?.TryGetMountSet(root.LegoControllerBaseAsset??source.BaseAsset,out var set)==true)
                foreach(var mount in set.Mounts)
                {
                    if((uint)mount.BoneIndex>=(uint)pose.WorldTransforms.Length)throw new InvalidDataException("Combat source mount outside hierarchy");
                    mounts[mount.BoneName]=pose.WorldTransforms[mount.BoneIndex];
                }
        }
        var children=ImmutableDictionary.CreateBuilder<string,Net7EffectSourceControllerPose>(StringComparer.Ordinal);
        foreach(var child in roots.Where(r=>ReferenceEquals(r.Parent,root) && r.IsShipLegoController))
            children.Add(child.InstancePath[(child.InstancePath.LastIndexOf('/')+1)..],CaptureCombatSourceController(child,roots,assets));
        CaptureRetainedCombatSourceChildren(source,root.InstancePath,root.LegoControllerBaseAsset??source.BaseAsset,roots,assets,children,HasNoOpLegoMounts(root.Root));
        return new(world,mounts.ToImmutable(),named.ToImmutable(),children.ToImmutable());
    }

    private Net7EffectSourceControllerPose CaptureEmptyRslSourceController(IEnbRenderObject source,
        Net7EffectSourceControllerPose pose,IReadOnlyList<LiveRootInstance> roots,EnbRetailAssetBundle assets)
    {
        var children=ImmutableDictionary.CreateBuilder<string,Net7EffectSourceControllerPose>(StringComparer.Ordinal);
        CaptureRetainedCombatSourceChildren(source,"root",source.BaseAsset,roots,assets,children,true);
        return pose with {Children=children.ToImmutable()};
    }

    private void CaptureRetainedCombatSourceChildren(IEnbRenderObject source,string instancePath,int baseAsset,
        IReadOnlyList<LiveRootInstance> roots,EnbRetailAssetBundle assets,
        ImmutableDictionary<string,Net7EffectSourceControllerPose>.Builder children,bool noOpMounts=false)
    {
        //72B7F0 stores a direct child even without a mount. Missing-parent
        //orphans remain unreachable until their parent's controller exists.
        string controllerPath=instancePath=="root"?"":instancePath[5..];
        var selected=RetainedLegoSpecs(source)?.Select(s=>(source.RenderIdentity,s.Source,s.Ordinal,s.MaterializationRevision,s.BaseAsset,s.CanonicalPath)).ToHashSet();
        foreach(var (key,factory) in (_shipExhaustOwner??this)._legoBaseFactories)
        {
            if(key.Identity!=source.RenderIdentity || selected is not null&&!selected.Contains(key))continue;
            int slash=key.Path.LastIndexOf('/');
            string parentPath=slash<0?"":key.Path[..slash];
            string name=key.Path[(slash+1)..];
            if((instancePath=="root" ? slash>=0 : slash<0 || parentPath!=controllerPath) || children.ContainsKey(name))continue;
            if(factory.Root?.PhysicalFactoryOwner is {} owner && _legoSourceInstances.TryGetValue(owner,out var retained))
                children.Add(name,CaptureCombatSourceController(retained,roots,assets));
        }
        foreach(var (key,owner) in (_shipExhaustOwner??this).LegoRslOwnersFor(source.RenderIdentity))
        {
            if(key.Identity!=source.RenderIdentity || selected is not null&&!selected.Contains(key))continue;
            int slash=key.Path.LastIndexOf('/');string name=key.Path[(slash+1)..];
            if((instancePath=="root" ? slash>=0 : slash<0 || key.Path[..slash]!=controllerPath) || children.ContainsKey(name))continue;
            if(owner.QueryDiagnostic is {} diagnostic)throw new InvalidDataException(diagnostic);
            if(owner.QueryRoot is {} model)
            {
                var child=new LiveRootInstance(source,model.Root,true,"lego/"+key.Path,
                    model.WorldTransformOverride??Matrix4x4.Identity,model.AnimationBlend)
                    {IsShipLegoController=true,LegoControllerBaseAsset=key.Base,HierarchyPostTransforms=model.HierarchyPostTransforms};
                children.Add(name,CaptureCombatSourceController(child,roots,assets));
            }
            else
            {
                //72B000 traverses controller children before querying the
                //RenderObj. A model-less RSL wrapper still owns that map.
                var nested=ImmutableDictionary.CreateBuilder<string,Net7EffectSourceControllerPose>(StringComparer.Ordinal);
                CaptureRetainedCombatSourceChildren(source,"lego/"+key.Path,key.Base,roots,assets,nested,true);
                children.Add(name,new(TryBuildObjectWorld(owner.Object,out var wrapperWorld)?wrapperWorld:Matrix4x4.Identity,
                    ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.Ordinal),
                    ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.OrdinalIgnoreCase),nested.ToImmutable()));
            }
        }
    }
}
