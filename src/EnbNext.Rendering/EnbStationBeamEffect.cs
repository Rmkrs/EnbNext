using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>00788F90's station teleport aggregate, including animated emitter birth transforms.</summary>
public sealed class EnbStationBeamEffect
{
    private readonly EnbW3dRenderResource _resource;
    private readonly EnbW3dRenderHierarchyAnimation _animation;
    private readonly string _animationSoundSection;
    private readonly EnbW3dAnimationSoundTrack? _soundTrack;
    private readonly List<EnbW3dParticleEmitterDrawInstance> _emitters=[];
    private readonly List<EnbRetailParticleRandomOwner> _random=[];
    private readonly List<EnbRetailParticleSceneLifetime> _particles=[];
    private readonly EnbCurrentRetailHierarchyAnimationState _playback=new()
        {Mode=2,Playback=2,HasAnimation=true,HasHierarchy=true};
    private EnbRetailTeleportScene? _scene;
    private Matrix4x4 _world;
    private uint _lastElapsed;
    private float _remaining;
    private bool _destroyed;
    public string AnimationName => _animationSoundSection;
    public double FrameRate => _animation.FrameRate;
    public double DurationMilliseconds => (_animation.NumFrames-1)*1000d/_animation.FrameRate;
    public double TailDurationMilliseconds => OwnerDurationMilliseconds+_emitters.Select(e=>e.Definition.LifetimeSeconds).DefaultIfEmpty().Max()*1000;
    public double OwnerDurationMilliseconds { get; }
    public bool Retired => _destroyed && _particles.All(p=>!p.BufferAttached || p.CanRetire);

    private EnbStationBeamEffect(EnbStationBeamEffect source)
    {
        _resource=source._resource;_animation=source._animation;_animationSoundSection=source._animationSoundSection;
        _soundTrack=source._soundTrack;
        _emitters.AddRange(source._emitters);
        _random.AddRange(source._random.Select(owner=>owner.Clone()));
        OwnerDurationMilliseconds=source.OwnerDurationMilliseconds;
    }
    public EnbStationBeamEffect CreateInstance()=>new(this);

    public EnbStationBeamEffect(EnbRetailAssetBundle assets, bool departing)
        : this(assets,departing ? "av_tele02" : "av_tele03") { }

    //788F90's avatar state countdown (+0x37C) is independent of HAnim length; 7897D0 ends the state at zero.
    public static double OwnerDuration(string name) => name.ToLowerInvariant() switch
        {"av_tele01"=>3200,"av_tele02" or "av_tele03"=>1000,"av_tele04"=>500,
            _=>throw new ArgumentException("Unknown avatar teleport state",nameof(name))};
    // 006390A0 arrival: SetAvatarPresentationStateAndClearAttachment(2) selects av_tele03.
    public static double ArrivalOwnerDurationMilliseconds => OwnerDuration("av_tele03");

    // 00788F90 selects AV_TELE04 for the character-creation actor presentation state 4.
    public EnbStationBeamEffect(EnbRetailAssetBundle assets, string name)
    {
        OwnerDurationMilliseconds=OwnerDuration(name);
        var resources=assets.RetailMixResources!;
        if(!resources.TryReadResource(name+".w3d",out var bytes,out _) ||
            !EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes,out var aggregate,out _))
            throw new InvalidDataException("Missing teleport aggregate "+name);
        if(!resources.TryReadResource(aggregate.BaseModelName+".w3d",out bytes,out var location) ||
            !EnbW3dModel.TryDecode(bytes,out var model))throw new InvalidDataException("Missing teleport hierarchy");
        var textures=model.Meshes.SelectMany(m=>m.TextureNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n=>new EnbResolvedTextureReference(n,resources.TryLocateResource(n,out var t)?t:null)).ToArray();
        var source=new EnbResolvedW3dResource(location.Name,aggregate.BaseModelName,location,model,textures);
        if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out var error))throw new InvalidDataException(error);
        string animation=aggregate.BaseModelName+"."+aggregate.BaseModelName+"-1";
        if(!EnbCurrentRetailRslHAnimResolver.TryResolve(resource,new("",EnbRslFileDependencyStatus.NotAuthored),
            assets.Paths,assets.MixResources,resources,animation,out var resolved,out error))throw new InvalidDataException(error);
        _resource=resolved.DrawResource; _animation=resolved.Animation;_animationSoundSection=animation;
        if(assets.AnimationSounds?.TryGet(animation,out var track)==true)_soundTrack=track;
        var hierarchy=_resource.Hierarchies.Single();
        foreach(var attachment in aggregate.Attachments)
        {
            if(!EnbInstalledW3dPrototypeTokenAudit.TryResolveDirectParticleEmitterPrototype(resources,attachment.SubobjectName,out var emitter) ||
                !resources.TryReadResource(emitter,out bytes) || !EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var definition,out error,assets.Textures))
                throw new InvalidDataException("Teleport emitter unavailable: "+attachment.SubobjectName);
            var particle=new EnbCurrentRetailRObjParticleAsset(EnbW3dRenderResourceKey.From(emitter),null,definition).PrepareTexture(assets);
            int pivot=hierarchy.Pivots.Single(p=>p.Name.Equals(attachment.BoneName,StringComparison.OrdinalIgnoreCase)).Ordinal;
            _emitters.Add(new(attachment.SubobjectName,emitter.Name,0,EnbRenderPassKind.OrdinaryWorld,definition,
                particle.Texture,Matrix4x4.Identity,hierarchy.Name,pivot,_emitters.Count) {RuntimeTextureName=particle.RuntimeTextureName, RetainedTexture=particle.RetainedTexture});
            _random.Add(new(definition));
        }
    }

    public IReadOnlyList<EnbW3dDrawTask> Build(long elapsed,Matrix4x4 world,EnbResolvedRenderCamera camera)
    {
        if(elapsed<0)return [];
        uint clock=unchecked((uint)elapsed),delta=unchecked(clock-_lastElapsed);
        Prepare(_scene??new(),clock,clock,world);
        _scene!.Advance(clock,delta);
        try {return Draw(clock,camera);}
        finally {_scene.CompleteRender(clock);}
    }

    /// <summary>Bind the activation transform once, then update the retained owner before the scene walk.</summary>
    public void Prepare(EnbRetailTeleportScene scene,uint clock,uint elapsed,Matrix4x4 world,Action<EnbW3dAnimationSoundCue,Vector3>? sound=null)
    {
        if(_scene is null)
        {
            _scene=scene;_world=world;
            _playback.PreviousTime=unchecked(clock-elapsed);
            _remaining=(float)(OwnerDurationMilliseconds/1000);
            for(int i=0;i<_emitters.Count;i++)
            {
                var state=new EnbRetailParticleSceneLifetime(_emitters[i].Definition,
                    unchecked(clock-elapsed),randomOwner:_random[i]);
                _particles.Add(state);scene.Attach(state);
            }
        }
        else if(!ReferenceEquals(_scene,scene))throw new InvalidOperationException("Teleport owner changed scenes");
        using(var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24))
            _remaining=(float)((EnbRetailLineArithmetic.Register)_remaining-
                (EnbRetailLineArithmetic.Register)unchecked(elapsed-_lastElapsed)*.001f);
        _lastElapsed=elapsed;
        if(_remaining<=0)Destroy(clock);
        if(_destroyed)return;
        Vector3 soundPosition=_world.Translation;
        _playback.RefreshSingle(()=>clock,()=>_animation.FrameRate,()=>checked((int)_animation.NumFrames),frame=>
        {
            if(!EnbW3dHierarchyPose.TryEvaluate(_resource.Hierarchies.Single(),_world,_animation,
                frame,out var pose,out var error))throw new InvalidDataException(error);
            int soundPivot=_resource.Hierarchies.Single().Pivots.FirstOrDefault(p=>p.Name.Equals(_soundTrack?.BoneName,StringComparison.OrdinalIgnoreCase))?.Ordinal??0;
            soundPosition=pose.WorldTransforms[soundPivot].Translation;
            for(int i=0;i<_particles.Count;i++)
            {
                var state=_particles[i];int pivot=_emitters[i].PivotIndex;
                if(state.Destroyed)continue;
                state.UpdateTransform(pose.WorldTransforms[pivot]);
                state.SetAnimationHidden(clock,state.Emitter.CurrentWorld,1,!pose.Visibility[pivot]);
            }
        },()=>(byte)(sound is not null && _soundTrack is not null?1:0),()=>0,(_,previous,current)=>
        {
            float last=previous;
            foreach(var cue in _soundTrack!.Crossed(previous,current)){sound!(cue,soundPosition);last=cue.Frame;}
            return last;
        });
    }

    public void Destroy(uint clock)
    {
        if(_destroyed)return;
        _destroyed=true;
        foreach(var particle in _particles)_scene!.Destroy(particle,clock);
    }

    public IReadOnlyList<EnbW3dDrawTask> Draw(uint clock,EnbResolvedRenderCamera camera)
    {
        List<EnbW3dDrawTask> packets=[];
        if(!_destroyed)
        {
            packets.AddRange(EnbCurrentRetailStationInterior.BuildModelDraws(_resource,_world,clock,
                new(_animation.Name,_playback.CurrentFrame)).Packets);
        }
        for(int i=0;i<_particles.Count;i++)
        {
            var state=_particles[i];if(!state.BufferAttached || state.CanRetire)continue;
            state.AdvanceForRender(clock);
            var emitter=_emitters[i] with {WorldTransform=state.Emitter.CurrentWorld};
            if(!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(_resource.Key,3,1,emitter,camera,
                EnbW3dLightEnvironment.CreateCurrentRetail(0x7f7f7f,[]),0,0,clock,out var packet,out _,out var error,
                worldBirths:state.BorrowLiveBirths(),motionHistory:state.MotionHistory,
                activeEmissionRun:state.Emitter.HasLineEndpoint?state.Emitter.EmissionRun:null))throw new InvalidDataException(error);
            if(packet is not null)packets.Add(packet);
        }
        return packets;
    }

}
