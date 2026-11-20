using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<(ShipExhaustInstance,EnbRenderDetailOwner)> _detailShipExhaustInstances=[];
    private EnbWorldRenderer? _shipExhaustOwner;
    private readonly Dictionary<ShipExhaustKey, ShipExhaustFrame> _frameShipExhaust = [];
    private readonly Dictionary<ShipExhaustKey, ShipExhaustInstance> _shipExhaustInstances = [];
    private readonly EnbCurrentRetailSceneCallbackList<(ShipExhaustKey Key,bool Buffer)> _exhaustCallbacks;
    private long _shipExhaustLastTime;
    private string _shipExhaustStatus = "ship exhaust sprites 0 particles 0 deferred 0 blocked 0";

    private void ObserveShipExhaust(IEnbRenderObject obj, LiveAggregateAttachment attachment,
        Matrix4x4 world, string hierarchy, int pivot, string path, int? maneuverJetSlot = null, bool allowInheritedMotion = false,
        object? physicalFactory = null)
    {
        //Exhaust children are toggled only through 00810750; the visibility
        //runtime carries its type and IsOrganic terms.
        if (_shipExhaustOwner is not null || attachment.Exhaust is not { } asset) return;
        var prepared=attachment.Root?.FactoryParticleRandom;
        physicalFactory??=maneuverJetSlot is not null || path.StartsWith("lego/",StringComparison.Ordinal)?prepared:null;
        var key = new ShipExhaustKey(obj.RenderIdentity, physicalFactory is null?obj.VisualGeneration:0, path, asset.Key)
            { FixedRevision = maneuverJetSlot is not null ? obj.FixedShipVisualRevision : 0, PhysicalFactory=physicalFactory };
        bool active = maneuverJetSlot is int slot ? EnbShipManeuverJetRuntime.IsActive(obj, slot) :
            EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(obj).ShouldDraw;
        if(prepared is not null)
        {
            asset=attachment.Root!.RetainedEmitter!;
            //The hierarchy has already scaled bone translations. Native +164
            //scales the emitter/buffer data separately from its rigid transform.
            world=Matrix4x4.CreateFromQuaternion(EnbCurrentRetailParticleBirthTransform.ExtractOrientation(world))*
                Matrix4x4.CreateTranslation(world.Translation);
        }
        _frameShipExhaust[key] = new(asset, obj.GameId, world, hierarchy, pivot, active, maneuverJetSlot is not null, allowInheritedMotion)
            {FactoryRandom=prepared,Scale=attachment.FactoryParticleScale,VisualGeneration=obj.VisualGeneration};
    }

    private static bool HasExhaustOwner(ShipExhaustKey key,IReadOnlySet<(ulong,ulong)> owners)
        =>key.PhysicalFactory is null?owners.Contains((key.Owner,key.Generation)):owners.Any(o=>o.Item1==key.Owner);

    private readonly List<EnbDazzleCollisionRoot> _exhaustBufferCollision=[];

    private readonly HashSet<(ulong,ulong)> _exhaustAdmittedOwners=[];
    private void UpdateShipExhaust(long now, RootTaskList roots, IReadOnlySet<ulong>? refreshedOwners = null)
    {
        if (_shipExhaustOwner is not null) return;
        if (now < _shipExhaustLastTime) { _shipExhaustInstances.Clear(); _exhaustCallbacks.Clear(); }
        _shipExhaustLastTime = now;
        // Reconciliation is local to the owners refreshed by this callback.
        // Reuse membership storage; all frame/buffer changes still run below.
        var admitted = _exhaustAdmittedOwners;
        admitted.Clear();
        foreach(var root in roots)
            if(refreshedOwners is null || refreshedOwners.Contains(root.Object.RenderIdentity))
                admitted.Add((root.Object.RenderIdentity,root.Object.VisualGeneration));
        foreach (var (key, frame) in _frameShipExhaust)
        {
            if (refreshedOwners is not null && !refreshedOwners.Contains(key.Owner)) continue;
            if (!frame.Asset.IsReady) continue;
            bool attached=HasExhaustOwner(key,admitted);
            if (!_shipExhaustInstances.TryGetValue(key, out var instance))
            {
                if(!attached)continue;
                _shipExhaustInstances.Add(key,instance=new(frame,unchecked((uint)now)));
                TrackExhaustCallbacks(key,instance);
            }
            if(instance.Buffer.Destroyed)continue;
            instance.Frame = frame;
            //98E4B0 attaches the cloned emitter;97D4F0/97F390 reconciles
            //visibility without discarding the independent scene buffer.
            instance.Buffer.Prepare(unchecked((uint)now),frame.World,frame.Scale,attached,frame.Active);
        }
        // Destroy only removes current entries or callback-list membership.
        // Dictionary removal preserves enumeration on the target runtime;
        // factory insertion already finished in the preceding loop.
        foreach (var (key, instance) in _shipExhaustInstances)
        {
            if (refreshedOwners is not null && !refreshedOwners.Contains(key.Owner)) continue;
            //7BCA70 removes scene membership, not GameObj+AC ownership.
            //98DFA0 ->97DE40 retains the emitter and its independent buffer;
            //98DF20 ->97DDF0 reattaches that same emitter on radar reentry.
            //Root construction still observes its factory while outside radar.
            if (_frameShipExhaust.ContainsKey(key)) continue;
            instance.Buffer.Destroy(unchecked((uint)now),instance.Frame.World,instance.Frame.Scale);
            instance.Frame=instance.Frame with {Active=false};
            //An emitter destroyed before97DEF0's first attachment has no
            //independent scene buffer and therefore no95E990 removal callback.
            if(!instance.Buffer.BufferAttached)_shipExhaustInstances.Remove(key);
        }
        admitted.Clear();
    }

    private void TrackExhaustCallbacks(ShipExhaustKey key,ShipExhaustInstance instance)
    {
        instance.Buffer.SceneAttachmentChanged+=attached=>
        {
            if(attached)_exhaustCallbacks.Register((key,false));
            else _exhaustCallbacks.Remove((key,false));
        };
        if(instance.Buffer.SceneAttached)_exhaustCallbacks.Register((key,false));
        if(instance.Buffer.BufferAttached)_exhaustCallbacks.Register((key,true));
    }

    private void AdvanceExhaustCallbacks(uint clock)
    {
        // 97DDF0/95E9D0 register two independent scene owners. 9661C0
        // walks the retained head-inserted list, reading Next after each call.
        // A late emitter's newly attached buffer is first on the next walk.
        _exhaustCallbacks.SetCallback(entry=>
        {
            var instance=_shipExhaustInstances[entry.Key];var state=instance.Frame;
            if(instance.Diagnostic is not null)return;
            try
            {
                if(entry.Buffer)
                {
                    instance.Buffer.BufferCallback(clock,instance.LastClock is {} last?unchecked(clock-last):0,state.World,state.Scale);
                    if(instance.Buffer.CanRetire)QueueParticleSceneRemoval(instance.Buffer,true,()=>
                    {
                        _exhaustCallbacks.Remove((entry.Key,false));_exhaustCallbacks.Remove((entry.Key,true));
                        _shipExhaustInstances.Remove(entry.Key);
                    });
                }
                else if(instance.Buffer.EmitterCallback(state.World))_exhaustCallbacks.Register((entry.Key,true));
            }
            catch(InvalidDataException error){instance.Diagnostic=error.Message;}
        });
        _particleCallbackFinishes.Add(()=>{foreach(var instance in _shipExhaustInstances.Values)instance.LastClock=clock;});
    }

    private static EnbW3dParticleEmitterDrawInstance ExhaustEmitter(ShipExhaustInstance instance)
    {
        var frame=instance.Frame;var asset=frame.Asset;
        return new(asset.Definition.Name,asset.Key.ResourceName,frame.GameId,EnbRenderPassKind.OrdinaryWorld,
            asset.Definition,asset.Texture,frame.World,frame.Hierarchy,frame.Pivot,0)
            {RuntimeTextureName=asset.RuntimeTextureName,RetainedTexture=asset.RetainedTexture};
    }

    private static EnbW3dWorldBounds ExhaustBufferBounds(ShipExhaustInstance instance,uint clock)
    {
        if(!instance.Buffer.BoundsDirty)return instance.Bounds;
        instance.Buffer.EnsureBounds(clock);
        var definition=instance.Frame.Asset.Definition;
        if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(ExhaustEmitter(instance),instance.Buffer.BorrowLiveBirths(),0,
            clock,instance.Frame.Scale,instance.Buffer.MotionHistory,instance.MaximumSize,out var bounds,out var error))throw new InvalidDataException(error);
        return instance.Bounds=bounds
            .WorldSphere(definition.LocalSpace?instance.Frame.World:Matrix4x4.Identity);
    }

    private List<(ShipExhaustInstance Owner,EnbRenderDetailOwner Candidate)> PrepareExhaustCandidates(
        EnbResolvedRenderCamera camera,uint clock,IEnbSceneFrame snapshot)
    {
        var result=_detailShipExhaustInstances;result.Clear();
        //480A40 draws reachable buffers directly, without95EAD0 preparation.
        if(_shipExhaustOwner is not null)return result;
        foreach(var (key,instance) in (_shipExhaustOwner??this)._shipExhaustInstances)
        {
            instance.SceneVisible=false;
            if(_shipExhaustOwner is not null && !SceneObjects(snapshot).Any(o=>o.RenderIdentity==key.Owner && (key.PhysicalFactory is not null || o.VisualGeneration==key.Generation)))continue;
            if(!instance.Buffer.BufferAttached || instance.Diagnostic is not null)continue;
            try
            {
                var bounds=ExhaustBufferBounds(instance,clock);var definition=instance.Frame.Asset.Definition;
                if(definition.RenderMode is not (3 or 4) && FrustumCullingEnabled && !EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds))continue;
                instance.SceneVisible=true;
                if(!EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,instance.Capacity,instance.MaximumSize,
                    instance.ProjectedDetail,out var detail))continue;
                instance.ProjectedDetail=detail.Smoothed;
                result.Add((instance,(instance.DetailOwner ??= new($"ship-exhaust/{key}",instance.Capacity,(int)definition.RenderMode,instance.LevelCount)).Update(instance.Level,instance.ProjectedDetail,1f)));
            }
            catch(InvalidDataException error){instance.Diagnostic=error.Message;}
        }
        return result;
    }

    private void DrawShipExhaust(List<EnbW3dDrawTask> packets, LiveFrame frame, EnbW3dLightEnvironment lighting)
    {
        var source = _shipExhaustOwner ?? this;
        var owners = _frameObjectVisualOwners;
        int drawn = 0, lines = 0, trails = 0, particles = 0, blocked = 0;
        int deferred = source._frameShipExhaust.Count(p => HasExhaustOwner(p.Key,owners) && p.Value.Asset.DeferredReason is not null);
        string? failure = null;
        foreach (var (key, current) in source._frameShipExhaust)
            if (HasExhaustOwner(key,owners) && current.Asset.Error is { } error) { blocked++; failure ??= error; }
        _exhaustBufferCollision.Clear();
        foreach (var (key, instance) in source._shipExhaustInstances.ToArray())
        {
            if (_shipExhaustOwner is not null && (!HasExhaustOwner(key,owners) || !source._frameShipExhaust.ContainsKey(key) || instance.Buffer.Destroyed)) continue;
            try
            {
                var state=instance.Frame;var asset=state.Asset;uint clock=frame.CurrentClientTime;
                if(instance.Diagnostic is {} diagnostic)throw new InvalidDataException(diagnostic);
                if(instance.Buffer.BufferAttached && (instance.SceneVisible || _shipExhaustOwner is not null))
                {
                    _exhaustBufferCollision.Add(new(default,true,false,static(start,end,fraction,inside)=>(fraction,inside))
                    {ResolveSphere=()=>
                    {
                        try {var bounds=ExhaustBufferBounds(instance,clock);return new Vector4(bounds.Center,bounds.Radius);}
                        catch(InvalidDataException error){instance.Diagnostic=error.Message;return null;}
                    }});
                    instance.Buffer.AdvanceForRender(clock);
                    if(!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,key.Owner,state.VisualGeneration??key.Generation,ExhaustEmitter(instance),
                        frame.Camera,lighting,ComputeStableParticleEmitterSalt(key.Path),0,clock,out var packet,out var receipt,out var error,
                        worldBirths:instance.Buffer.BorrowLiveBirths(),activeEmissionRun:instance.Buffer.Emitter.HasLineEndpoint?instance.Buffer.Emitter.EmissionRun:null,
                        motionHistory:instance.Buffer.MotionHistory,internalDetailLevel:instance.LevelCount-1-instance.Level,
                        particleSizeScale:state.Scale,currentRetailFactoryScale:true,cullToCamera:FrustumCullingEnabled))throw new InvalidDataException(error);
                    particles+=receipt.LiveParticleCount;
                    if(packet is not null)
                    {
                        packets.Add((packet).ForkTask().SetRenderInstancePath($"ship-exhaust/{key.Path}"));
                        if(asset.Definition.RenderMode==2){lines++;if(!state.ManeuverJet)trails++;}else drawn++;
                    }
                }
            }
            catch(InvalidDataException error){blocked++;failure??=error.Message;}
        }
        _shipExhaustStatus = $"ship exhaust sprites {drawn} particles {particles} deferred {deferred} blocked {blocked}" +
            $" emitting {source._shipExhaustInstances.Values.Count(i => i.Frame.Active)} births {source._shipExhaustInstances.Values.Sum(i => i.Buffer.Emitter.EmittedCount)}" +
            $" jets {source._shipExhaustInstances.Values.Count(i => i.Frame.ManeuverJet && i.Frame.Active)}" +
            $" jet births {source._shipExhaustInstances.Values.Where(i => i.Frame.ManeuverJet).Sum(i => i.Buffer.Emitter.EmittedCount)} line draws {lines} engine trails {trails}" +
            (failure is null ? "" : $" [{failure}]");
    }

    private readonly record struct ShipExhaustKey(ulong Owner, ulong Generation, string Path, EnbW3dRenderResourceKey Asset)
    {
        public ulong FixedRevision { get; init; }
        //72A7C0 retains physical Lego children while replacing the hull.
        //97DE40 removes the emitter from the scene without destroying +F8.
        public object? PhysicalFactory { get; init; }
    }
    private sealed record ShipExhaustFrame(EnbCurrentRetailShipExhaustAsset Asset, uint GameId,
        Matrix4x4 World, string Hierarchy, int Pivot, bool Active, bool ManeuverJet, bool AllowInheritedMotion)
    {
        public EnbRetailParticleRandomOwner? FactoryRandom {get;init;}
        public float Scale {get;init;}=1;
        public ulong? VisualGeneration {get;init;}
    }
    private sealed class ShipExhaustInstance(ShipExhaustFrame frame,uint clock)
    {
        public EnbParticleBufferDetailBudgetSemantics.Owner? DetailOwner;
        public EnbRetailParticleSceneLifetime Buffer {get;}=new(frame.Asset.Definition,clock,randomOwner:frame.FactoryRandom?.Clone());
        public ShipExhaustFrame Frame {get;set;}=frame;
        public uint? LastClock;
        public readonly uint Capacity=EnbRetailParticleEmissionArithmetic.Capacity(frame.Asset.Definition);
        public readonly int LevelCount=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(frame.Asset.Definition));
        public int Level=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(frame.Asset.Definition))-1;
        public readonly float MaximumSize=EnbRetailParticleBufferBounds.MaximumSize(frame.Asset.Definition.Size,
            EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(frame.Asset.Definition.LifetimeSeconds),frame.Scale);
        public float ProjectedDetail;
        public EnbW3dWorldBounds Bounds;
        public bool SceneVisible;
        public string? Diagnostic;
    }
}
