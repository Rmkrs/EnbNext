using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<(ParticleJetInstance,EnbRenderDetailOwner)> _detailParticleJetInstances=[];
    private sealed class ParticleJetInstance(EnbCurrentRetailRObjParticleAsset asset,IEnbRenderObject owner,
        bool overbrightOff,uint start,EnbRetailParticleRandomOwner random,uint initialAge,bool rebuilt)
    {
        public EnbParticleBufferDetailBudgetSemantics.Owner? DetailOwner;
        public readonly EnbCurrentRetailRObjParticleAsset Asset=asset;
        public readonly IEnbRenderObject Owner=owner;
        public readonly bool OverbrightOff=overbrightOff;
        public readonly uint Start=start;
        public readonly EnbRetailParticleSceneLifetime Buffer=new(asset.Definition,initialAge,randomOwner:random,initiallyStarted:!rebuilt);
        public EnbCurrentRetailAnimatedParticleRuntime.Emitter State=>Buffer.Emitter;
        public readonly uint Capacity=EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition);
        public readonly int LevelCount=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition));
        public int Level=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition))-1;
        public float ProjectedDetail;
        public EnbW3dWorldBounds Bounds;
        public bool SceneVisible;
        public uint? EmitterRemovalClock;
        public Vector3? Velocity;
        public string? Diagnostic;
        public uint? LastAge;
        public Matrix4x4 World=Matrix4x4.Identity;
    }
    private readonly Dictionary<(uint,ulong,ulong),ParticleJetInstance> _particleJets=[];
    private readonly EnbCurrentRetailSceneCallbackList<((uint,ulong,ulong) Key,bool Buffer)> _particleJetCallbacks;

    private void TrackParticleJetCallbacks((uint,ulong,ulong) key,ParticleJetInstance instance)
    {
        instance.Buffer.SceneAttachmentChanged+=attached=>
        {
            if(attached)_particleJetCallbacks.Register((key,false));
            else _particleJetCallbacks.Remove((key,false));
        };
    }

    private void AdvanceParticleJetCallbacks(uint now,HashSet<(uint,ulong,ulong)> active)
    {
        // Scene culling/bounds precede9661C0. Each emitter and its independent
        // buffer then occupy a retained registration, not one dictionary slot.
        foreach(var (key,instance) in _particleJets.ToArray())
        {
            uint age=unchecked(now-instance.Start);
            if(!active.Contains(key))instance.Buffer.Destroy(age,instance.World,1);
            if(instance.Buffer.Destroyed&&!instance.Buffer.BufferAttached){_particleJets.Remove(key);continue;}
            try
            {
                if(instance.Buffer.BufferAttached && instance.LastAge!=age)JetBufferBounds(instance,age);
            }
            catch(InvalidDataException error){instance.Diagnostic=error.Message;}
        }
        _particleJetCallbacks.SetCallback(entry=>
        {
            var instance=_particleJets[entry.Key];
            if(instance.Diagnostic is not null)return;
            uint age=unchecked(now-instance.Start);
            try
            {
                if(entry.Buffer)
                {
                    instance.Buffer.BufferCallback(age,instance.LastAge is {} previous?unchecked(age-previous):0,instance.World,1,instance.Velocity);
                    if(instance.Buffer.CanRetire)QueueParticleSceneRemoval(instance.Buffer,true,()=>
                    {
                        _particleJetCallbacks.Remove((entry.Key,false));_particleJetCallbacks.Remove((entry.Key,true));
                        _particleJets.Remove(entry.Key);
                    });
                }
                else
                {
                    if(instance.Buffer.EmitterCallback(instance.World))_particleJetCallbacks.Register((entry.Key,true));
                    //97DEF0 observes completion here. A reattached emitter can
                    //precede its own buffer, whose later callback finishes it.
                    if(instance.Buffer.SceneAttached && instance.State.HasCompletedFiniteRun && instance.EmitterRemovalClock is null)
                    {
                        instance.EmitterRemovalClock=now;
                        QueueParticleSceneRemoval(instance.Buffer,false,()=>instance.Buffer.SetSceneAttached(false));
                    }
                }
            }
            catch(InvalidDataException error){instance.Diagnostic=error.Message;}
        });
        _particleCallbackFinishes.Add(()=>{foreach(var instance in _particleJets.Values)instance.LastAge=unchecked(now-instance.Start);});
    }
    private sealed record ParticleJetFactory(EnbCurrentRetailRObjParticleAsset? Asset,
        EnbRetailParticleRandomOwner? Random,bool OverbrightOff,string? Diagnostic);
    private readonly Dictionary<(uint,ulong,ulong),ParticleJetFactory> _particleJetFactories=[];
    private ParticleJetFactory CreateParticleJetFactory(EnbRetailAssetBundle assets,int id,float scale,Vector3 hsv)
    {
        //6EC950 calls6F51D0 before testing class0F. Use the shared NAME /
        //configured-clone / FILE factory, including its constructor tables.
        //This request is not admitted as a world object or ship controller.
        var request=new EnbSceneObjectSnapshot(0,0,0,false,false,byte.MaxValue,id,scale,hsv.X,hsv.Y,hsv.Z,
            0,false,null,null!,null,null,null,[],[],[],[],[],[],null,[],null,0,0);
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(id);
        var factory=CreateOrdinaryBaseFactory(request,assets,definition);
        if(factory.Root is not {RetainedEmitter:{} emitter,FactoryParticleRandom:{} random} root)
            return new(null,null,false,factory.Diagnostic??"ParticleJet base factory did not return an emitter (class 0F)");
        float retainedScale=root.ConfiguredFactoryInputs?.Scale??1;
        var copy=random.Clone();
        copy.Properties.ScaleSize(retainedScale);
        var particle=new EnbCurrentRetailRObjParticleAsset(emitter.Key,emitter.Texture,
            EnbCurrentRetailParticleJetScale.Apply(emitter.Definition,retainedScale))
            {RuntimeTextureName=emitter.RuntimeTextureName,RetainedTexture=emitter.RetainedTexture};
        return new(particle,copy,definition.OverbrightOff,null);
    }

    private readonly List<EnbDazzleCollisionRoot> _jetBufferCollision=[];

    private static EnbW3dWorldBounds JetBufferBounds(ParticleJetInstance instance,uint age)
    {
        if(!instance.Buffer.BoundsDirty)return instance.Bounds;
        instance.Buffer.EnsureBounds(age);
        var asset=instance.Asset;
        var draw=new EnbW3dParticleEmitterDrawInstance(asset.Definition.Name,asset.Key.ResourceName,instance.Owner.GameId,
            EnbRenderPassKind.OrdinaryWorld,asset.Definition,asset.Texture,instance.World,"",0,0);
        float size=EnbRetailParticleBufferBounds.MaximumSize(asset.Definition.Size,
            EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(asset.Definition.LifetimeSeconds),1);
        if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(draw,instance.Buffer.BorrowLiveBirths(),0,age,1,
            instance.Buffer.MotionHistory,size,out var bounds,out var error))throw new InvalidDataException(error);

        return instance.Bounds=bounds
            .WorldSphere(asset.Definition.LocalSpace?instance.World:Matrix4x4.Identity);
    }

    private List<(ParticleJetInstance Owner,EnbRenderDetailOwner Candidate)> PrepareParticleJetCandidates(
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,EnbResolvedRenderCamera camera,uint clock)
    {
        var result=_detailParticleJetInstances;result.Clear();
        if(_shipExhaustOwner is not null)return result;
        var active=nodes.Where(n=>n.Active).Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        foreach(var (key,instance) in (_shipExhaustOwner??this)._particleJets)
        {
            instance.SceneVisible=false;
            try
            {
                if(_shipExhaustOwner is null && active.Contains(key) && _combatEffectPoses.TryGetValue(key,out var world))
                {
                    instance.World=world;instance.Buffer.UpdateTransform(world);
                    if(instance.EmitterRemovalClock is not null)instance.Buffer.SetSceneAttached(false);
                }
                if(!instance.Buffer.BufferAttached || instance.Diagnostic is not null)continue;
                var bounds=JetBufferBounds(instance,unchecked(clock-instance.Start));
                var definition=instance.Asset.Definition;
                if(definition.RenderMode is not (3 or 4) && FrustumCullingEnabled && !EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds))continue;
                instance.SceneVisible=true;
                float size=EnbRetailParticleBufferBounds.MaximumSize(definition.Size,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds),1);
                if(!EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,instance.Capacity,size,instance.ProjectedDetail,out var detail))continue;
                instance.ProjectedDetail=detail.Smoothed;
                result.Add((instance,(instance.DetailOwner ??= new($"particle-jet/{key}",instance.Capacity,(int)definition.RenderMode,instance.LevelCount)).Update(instance.Level,instance.ProjectedDetail,1f)));
            }
            catch(InvalidDataException error){instance.Diagnostic=error.Message;}
        }
        return result;
    }

    private (int Pending,int Blocked,string? Failure) _particleJetPreparation;
    private void PrepareParticleJets(IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,LiveFrame frame,EnbRetailAssetBundle assets)
    {
        uint now=frame.CurrentClientTime;
        int pending=0,blocked=0;string? failure=null;
        var active=new HashSet<(uint,ulong,ulong)>();
        var retained=nodes.Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        foreach(var key in _particleJetFactories.Keys.Where(k=>!retained.Contains(k)).ToArray())_particleJetFactories.Remove(key);
        foreach(var node in nodes.Where(n=>_shipExhaustOwner is null && n.Definition.EffectClassKind==EnbEffectClassKind.ParticleJetEffect))
        {
            var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
            if(_particleJetFactories.ContainsKey(key))continue;
            try
            {
                if(node.Definition.BaseAssetId is not int id || !EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var hsv,out _))
                    throw new InvalidDataException("ParticleJetEffect requires a base asset and finite HSV");
                _particleJetFactories.Add(key,CreateParticleJetFactory(assets,id,EnbCurrentRetailParticleJetScale.Read(node),hsv));
            }
            catch(Exception error){_particleJetFactories.Add(key,new(null,null,false,error.Message));}
        }
        foreach(var node in nodes.Where(n=>_shipExhaustOwner is null && n.Active && n.Definition.EffectClassKind==EnbEffectClassKind.ParticleJetEffect))
        {
            var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
            try
            {
                var loaded=_particleJetFactories[key];
                var particle=loaded.Asset??throw new InvalidDataException(loaded.Diagnostic);
                if(!_combatEffectPoses.TryGetValue(key,out var world) || !_combatEffectBindings.TryGetValue(key,out var binding) ||
                    !_combatLookTargets.TryGetValue(key,out var target) || target.End is not {} end)
                    throw new InvalidDataException(_combatPoseErrors.GetValueOrDefault(key,"ParticleJet endpoints unavailable"));
                float distance=Vector3.Distance(binding.Source.Translation,end);
                if(!_particleJets.TryGetValue(key,out var instance))
                {
                    var owner=ResolveCombatResourceOwner(node,SceneObjects(frame.Snapshot)) ?? throw new InvalidDataException("ParticleJet retained owner unavailable");
                    var random=loaded.Random!.Clone();
                    var d=particle.Definition;
                    float current=(uint)((double)d.LifetimeSeconds*1000)*.001f,minimum=-1;
                    if(node.Definition.Properties.ContainsKey("MinLifetime") && !EnbRetailIniDocument.TryReadFloat(node.Definition.Properties,"MinLifetime",out minimum))
                        throw new InvalidDataException("ParticleJet MinLifetime unavailable");
                    float selected=EnbCurrentRetailParticleJetLifetime.Select(current,current,minimum,d.VelocityPerSecond.X,distance);
                    if(selected!=current)
                    {
                        d=EnbCurrentRetailParticleJetLifetime.Retime(d,current,selected);
                        //006ED130 destroys the first emitter and constructs a
                        //replacement from its already-adjusted property snapshots.
                        d=d.Copy().SetColor(d.Color with {BeforeRuntimeSetter=null});
                        random=new(d);
                    }
                    EnbCurrentRetailRObjEffectAsset.ValidateParticleJet(d);
                    _particleJets[key]=instance=new(particle with {Definition=d},owner,loaded.OverbrightOff,node.ActualStartTick,random,unchecked(now-node.ActualStartTick),selected!=current);
                    TrackParticleJetCallbacks(key,instance);
                    instance.Buffer.SetSceneAttached(true);
                    //Scene culling+17C/97D520 invokes97F390 even when offscreen.
                    //A rebuilt primary emitter starts here; a clone already started.
                    instance.Buffer.SetHidden(unchecked(now-instance.Start),world,1,false);
                }
                active.Add(key);
                instance.World=world;
                var definition=instance.Asset.Definition;
                float lifetime=(uint)((double)definition.LifetimeSeconds*1000)*.001f;
                var velocity=definition.VelocityPerSecond with {X=distance/lifetime};
                instance.Velocity=velocity;
                instance.Buffer.UpdateTransform(world);
                if(instance.EmitterRemovalClock is not null)instance.Buffer.SetSceneAttached(false);
            }
            catch(Exception ex){blocked++;failure??=$"E{node.Definition.DefinitionId}: {ex.Message}";}
        }
        if(_shipExhaustOwner is null)AdvanceParticleJetCallbacks(now,active);
        _particleJetPreparation=(pending,blocked,failure);
    }
    private void DrawParticleJets(List<EnbW3dDrawTask> packets,IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        LiveFrame frame,EnbRetailAssetBundle assets,EnbW3dLightEnvironment lights)
    {
        uint now=frame.CurrentClientTime;_jetBufferCollision.Clear();
        int draws=0;var (pending,blocked,failure)=_shipExhaustOwner is null?_particleJetPreparation:default;
        var source=_shipExhaustOwner??this;
        foreach(var (key,instance) in source._particleJets.ToArray())
        {
            if(_shipExhaustOwner is not null && !nodes.Any(n=>n.Active && (n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)==key))continue;
            try
            {
                uint age=unchecked(now-instance.Start);
                if(instance.Diagnostic is {} diagnostic)throw new InvalidDataException(diagnostic);
                if(_shipExhaustOwner is null && instance.Buffer.SceneAttached)
                    instance.EmitterRemovalClock=RetainEmitterSceneRoot(instance.EmitterRemovalClock,instance.World,frame.Camera,now,
                        0,0);
                if((instance.SceneVisible || _shipExhaustOwner is not null) && instance.Buffer.BufferAttached)
                {
                    _jetBufferCollision.Add(new(default,true,false,static (start,end,fraction,inside)=>(fraction,inside))
                    {ResolveSphere=()=>
                        {
                            try {var bounds=JetBufferBounds(instance,age);return new Vector4(bounds.Center,bounds.Radius);}
                            catch(InvalidDataException error){instance.Diagnostic=error.Message;return null;}
                        }});
                    instance.Buffer.AdvanceForRender(age);
                    var asset=instance.Asset;
                    var emitter=new EnbW3dParticleEmitterDrawInstance(asset.Definition.Name,"particle-jet",instance.Owner.GameId,
                        EnbRenderPassKind.OrdinaryWorld,asset.Definition,asset.Texture,instance.World,"",0,0)
                        {RuntimeTextureName=asset.RuntimeTextureName,RetainedTexture=asset.RetainedTexture};
                    if(!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,instance.Owner.RenderIdentity,instance.Owner.VisualGeneration,
                        emitter,frame.Camera,lights,unchecked((uint)key.Item3),0,age,out var packet,out _,out var error,
                        worldBirths:instance.Buffer.BorrowLiveBirths(),activeEmissionRun:instance.State.HasLineEndpoint?instance.State.EmissionRun:null,
                        motionHistory:instance.Buffer.MotionHistory,internalDetailLevel:instance.LevelCount-1-instance.Level,
                        currentRetailFactoryScale:true,cullToCamera:FrustumCullingEnabled))throw new InvalidDataException(error);
                    if(packet is not null){packets.Add((_overbrightMaterials.Apply(packet,instance.OverbrightOff,out _)).ForkTask().SetRenderInstancePath($"particle-jet/{key.Item1}/{key.Item2}/{key.Item3}"));draws++;}
                }
            }
            catch(Exception ex){blocked++;failure??=ex.Message;}
        }
        _effectObjectVisualStatus+=$" · particle jets draws {draws} pending {pending} blocked {blocked}"+(failure is null?"":" · "+failure);

    }
}
