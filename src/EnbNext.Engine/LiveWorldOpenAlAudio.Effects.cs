using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private readonly Dictionary<(uint,ulong,ulong,bool,bool,bool),EffectSoundInstance> _effectSounds = [];
    private readonly EnbWarpSoundRuntime _warpSounds = new();
    private readonly Dictionary<string,EffectSoundResource> _effectSoundResources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _effectSoundPrefetched = [];
    private readonly List<EffectSoundInstance> _effectSoundTails = [];
    private readonly Dictionary<(uint,ulong,ulong,bool,bool,bool),EnbCurrentRetailEffectSoundNode> _pendingSoundActivations = [];
    private static readonly SemaphoreSlim EffectDecodeSlots = new(2);
    private string _effectSoundStatus = "effect SFX idle";
    private int _effectSoundsStarted;
    private string _lastEffectSound = "none";
    public Func<uint,string,Vector3?>? EffectBonePosition { get; set; }
    public Func<uint,ulong?>? EffectObjectIdentity { get; set; }
    public Func<EnbCurrentRetailEffectSoundNode,Vector3?>? EffectRenderPosition { get; set; }
    internal Func<uint> EffectChoiceSample { get; set; } = EnbRetailMovieRandom.Damage.NextWord;
    private object? _effectListener;
    internal EnbResolvedRenderCamera? EffectListener => Volatile.Read(ref _effectListener) is EnbResolvedRenderCamera camera ? camera : null;

    public void UpdateEffects(IReadOnlyList<EnbCurrentRetailEffectSoundNode> nodes,
        EnbRetailAssetBundle assets,EnbResolvedRenderCamera camera,Func<uint,Vector3?> position,float gain,
        bool contextEnded=false, IReadOnlyList<IEnbRenderObject>? warpObjects=null,
        uint sampleTime=0, bool dopplerEnabled=true)
    {
        if (_disposed) return;
        if (contextEnded) { StopEffectSounds(); return; }
        Volatile.Write(ref _effectListener,camera);
        if (warpObjects is not null) nodes = nodes.Concat(_warpSounds.Update(warpObjects)).ToArray();
        foreach (var node in nodes.Where(n=>n.EndedAtActivation || n.ReleasedWorldPosition.HasValue))
            _pendingSoundActivations.TryAdd((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence,node.ClientWarp,node.ClientAnimation,node.ClientDamage),node);
        if(_pendingSoundActivations.Count!=0)
            nodes=nodes.Concat(_pendingSoundActivations.Values).DistinctBy(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence,n.ClientWarp,n.ClientAnimation,n.ClientDamage)).ToArray();
        var present = nodes.Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence,n.ClientWarp,n.ClientAnimation,n.ClientDamage)).ToHashSet();
        if (_alc is not null && _context!=null) _alc.MakeContextCurrent(_context);
        int pending=0, playing=0, blocked=0, tails=0, limited=0;
        foreach (var tail in _effectSoundTails.ToArray())
        {
            if (UpdateFiniteTail(tail,camera,gain)) { playing++; tails++; }
            else { RetireEffectSound(tail); _effectSoundTails.Remove(tail); }
        }
        foreach (var key in _effectSounds.Keys.Where(k=>!present.Contains(k)).ToArray())
        {
            var instance=_effectSounds[key];
            // Retail 006E7330 checks the authored loop count (+DC getter): only
            // zero/continuous voices are stopped. Finite voices lose their effect
            // owner but finish in the audio scene at their last world position.
            if (instance.Source!=0 && (!instance.Continuous || instance.Released))
            {
                _al!.GetSourceProperty(instance.Source,GetSourceInteger.SourceState,out int state);
                if (state==(int)SourceState.Playing)
                {
                    SetEffectPosition(instance,camera,instance.Flat,instance.LastPosition);
                    SetEffectGain(instance,gain);
                    playing++; tails++; continue;
                }
            }
            RetireEffectSound(instance); _effectSounds.Remove(key);
        }
        string? failure=null;
        foreach (var node in nodes)
        {
            Vector3? OriginalPosition(uint id)=>EnbCurrentRetailEffectSoundRuntime.IsOriginalOwner(node,id,EffectObjectIdentity)?position(id):null;
            Vector3? OriginalBonePosition(uint id,string bone)=>EnbCurrentRetailEffectSoundRuntime.IsOriginalOwner(node,id,EffectObjectIdentity)?EffectBonePosition?.Invoke(id,bone):null;
            var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence,node.ClientWarp,node.ClientAnimation,node.ClientDamage);
            if (!_effectSounds.TryGetValue(key,out var instance))
            {
                instance=new() { Name=node.Definition.Sfx,Released=node.ReleasedWorldPosition.HasValue }; _effectSounds.Add(key,instance);
                // Prewarm this root's authored links without starting any sounds.
                if (!node.ClientWarp && !node.ClientAnimation && !node.ClientDamage && _effectSoundPrefetched.Add(node.Root.DefinitionId) && assets.PresentationCatalog is { } catalog)
                    foreach (var d in catalog.ResolveEffectGraph(node.Root.DefinitionId).Definitions)
                        if (!string.IsNullOrWhiteSpace(d.Sfx)) GetEffectResource(d.Sfx,assets,false);
            }
            bool radiusSound=EnbRetailIniDocument.TryReadFloat(node.Definition.Properties,"TriggerSFXRadius",out float triggerRadius) && triggerRadius>=0;
            if (radiusSound && !node.Active) continue;
            if (radiusSound && node.Active)
            {
                try
                {
                    if (instance.Radius is null)
                    {
                        instance.Radius=new(node.Definition,node.ActualStartTick);
                        instance.Completed=true; // No voice until an in-range due attempt.
                    }
                    if (!instance.Radius.Resolve(node,OriginalPosition,OriginalBonePosition,out var bindingError,EffectRenderPosition))
                    { blocked++; failure??=$"E{node.Definition.DefinitionId}: {bindingError}"; continue; }
                    var radius=instance.Radius;
                    radius.TryAttempt(sampleTime,camera.Position,()=>
                    {
                        // 006E70F0 releases the old owner before attempting even
                        // a NULL/missing replacement. Finite voices keep playing.
                        if (instance.Source!=0 && !instance.Continuous) _effectSoundTails.Add(instance);
                        else RetireEffectSound(instance);
                        instance=new() { Name=node.Definition.Sfx,Radius=radius,Completed=true };
                        _effectSounds[key]=instance;
                        try
                        {
                            string soundName=node.Definition.Sfx!;
                            var definition=assets.Sounds?.GetDefinitionOrFile(soundName)
                                ?? throw new InvalidDataException("sounds.ini is unavailable");
                            if (!_stationSoundCalls.TryCall(definition)) return;
                            // Shared replacement presentation randomness; preserve
                            // authored weights, rejection sampling and NULL tickets.
                            int choice=EnbCurrentRetailWeightedSoundSelection.NextChoice(definition.Files,
                                EnbRetailMovieRandom.Damage.NextWord);
                            if (definition.Files[choice].IsNullChoice) return;
                            instance.SelectedDefinition=definition with { Files=[definition.Files[choice] with { Weight=null }] };
                            instance.ResourceName=$"\u001Fradius:{soundName}:{choice}";
                            instance.Completed=false;
                        }
                        catch (Exception ex) { instance.Error=ex.Message; }
                    },EnbRetailPresentationRandom.Shared.Next);
                }
                catch (Exception ex) { instance.Error=ex.Message; }
            }
            if (instance.Error is not null) { blocked++; failure??=instance.Error; continue; }
            if (instance.Completed) continue; // Natural PCM completion is not a new effect start.
            if (!node.Active) continue;
            if(!radiusSound && !node.Doppler && instance.SelectedDefinition is null)
            {
                try
                {
                    var definition=assets.Sounds?.GetDefinitionOrFile(node.Definition.Sfx!) ?? throw new InvalidDataException("sounds.ini unavailable");
                    if(node.ReleasedWorldPosition.HasValue || definition.Files.Count!=1 || definition.Files[0].IsNullChoice || definition.Files[0].Weight is not null)
                    {
                        // 0043E530 gates the call before 0043E230 selects once.
                        // NULL consumes the attempt; failed media never rerolls.
                        if(!_stationSoundCalls.TryCall(definition)){instance.Completed=true;continue;}
                        int choice=EnbCurrentRetailWeightedSoundSelection.NextChoice(definition.Files,EffectChoiceSample);
                        if(definition.Files[choice].IsNullChoice){instance.Completed=true;continue;}
                        instance.SelectedDefinition=definition with {Files=[definition.Files[choice] with {Weight=null}]};
                        instance.ResourceName=$"\u001Fweighted:{node.Definition.Sfx}:{choice}";
                    }
                }
                catch(Exception ex){instance.Error=ex.Message;blocked++;failure??=ex.Message;continue;}
            }
            Vector3 world=default; bool follows=true; string? error=null;
            if (node.EndedAtActivation && instance.ActivationPosition is {} captured) { world=captured; follows=false; }
            else if (radiusSound && instance.Radius?.Position is Vector3 radiusPosition) world=radiusPosition;
            else if (!EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,OriginalPosition,out world,out follows,out error,OriginalBonePosition,
                findEffectPosition:EffectRenderPosition,listenerPosition:camera.Position))
            {
                // Missing spatial objects can arrive after the effect packet; keep waiting,
                // but never place an unresolved sound at the origin or at the listener.
                RetireEffectSound(instance); blocked++; failure??=$"E{node.Definition.DefinitionId}: {error} (effect {node.Root.EffectId:X}/g{node.Root.InstanceGeneration}, node {node.Sequence}, source {node.Root.SourceGameId:X}, target {node.Root.TargetGameId:X}, ended-at-start {node.EndedAtActivation}, start {node.ActualStartTick}, now {sampleTime})";
                if(node.EndedAtActivation || node.ReleasedWorldPosition.HasValue)instance.Error=failure;
                continue;
            }
            string name=node.Definition.Sfx!;
            if(node.EndedAtActivation)instance.ActivationPosition ??= world;
            var resource=GetEffectResource(instance.ResourceName??name,assets,true,node.Doppler,instance.SelectedDefinition);
            if (resource is null) { blocked++; failure??="effect sound cache is at its safety bound"; continue; }
            if (!node.Active) continue;
            try
            {
                if (!resource.Load.IsCompleted) { pending++; continue; }
                var decoded=resource.Load.GetAwaiter().GetResult();
                if (decoded.Error is not null) throw new InvalidDataException(decoded.Error);
                if (decoded.MissingMedia) { instance.Completed=true; continue; }
                if (!EnsureInitialized()) { blocked++; failure??=_status; continue; }
                if (!_alc!.MakeContextCurrent(_context)) throw new InvalidOperationException("Cannot bind effect sound context");
                  var d=decoded.Definition!;
                  instance.SoundType = d.EffectiveSoundType;
                  // 006E70F0 uses the same authored audio dispatch for ordinary
                  // activation and radius attempts, including SoundType2 speech.
                  string? categoryError=node.Doppler ? EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(d) : EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(d,allowRateLimiting:radiusSound || instance.SelectedDefinition is not null);
                  if(categoryError is not null)throw new InvalidDataException(categoryError);
                bool flat=EnbCurrentRetailEffectSoundRuntime.Is2D(node.Definition);
                // 009F0350 also sends compressed mono buffers to 009FF910.
                // Decoding to PCM must not change the authored spatial voice family.
                bool softwareStereo=!flat && !UsesSpatialBank(decoded.Pcm!);
                if (instance.Source==0)
                {
                    if (resource.Buffer==0)
                    {
                        var pcm=decoded.Pcm!;
                        resource.Buffer=_al!.GenBuffer();
                        fixed (byte* samples=pcm.Pcm16LittleEndian)
                            _al.BufferData(resource.Buffer,pcm.Channels==1?BufferFormat.Mono16:BufferFormat.Stereo16,
                                samples,pcm.Pcm16LittleEndian.Length,pcm.SampleRate);
                        CheckMusicError("effect buffer upload");
                    }
                    instance.Source=_al!.GenSource(); instance.Name=name; instance.Origin=world;
                    instance.Continuous=d.EffectiveLoopCount==0; instance.Flat=flat;
                    if(softwareStereo)
                    {
                        EnsureEffectStereoBuffers(resource,decoded.Pcm!);
                        instance.RightSource=_al.GenSource();
                        instance.FullVolumeRadius=d.EffectiveMaxVolumeRadius*node.RadiusMultiplier;
                        instance.DropOffRadius=d.EffectiveDropOffRadius*node.RadiusMultiplier;
                    }
                    instance.DropOffRadius=d.EffectiveDropOffRadius*node.RadiusMultiplier;
                    RegisterVoice(instance.Source,name,d.EffectiveMaxInstances,d.EffectiveSoundType,d.EffectivePriority,flat?0:EnbRetailSoundArbitration.SpatialScore(world-camera.Position,instance.DropOffRadius),instance.RightSource,spatialMono:!flat && UsesSpatialBank(decoded.Pcm!));
                    var modifiers=EnbCurrentRetailEffectSoundRuntime.SampleModifiers(d,EnbNext.Rendering.EnbRetailCrtRandomSource.Current.NextUnit(),EnbNext.Rendering.EnbRetailCrtRandomSource.Current.NextUnit());
                    instance.Volume=modifiers.Volume;
                    // 0043E530 configures Doppler for shared spatial voices,
                    // not just the engine/warp controller. 2D voices have no motion.
                    if (!flat && (node.Doppler || d.EffectiveMinDopplerShift!=1 || d.EffectiveMaxDopplerShift!=1))
                    {
                        instance.Doppler=new EnbCurrentRetailEngineDopplerRuntime(VoiceDopplerDefinition(d) with { Pitch=modifiers.Pitch });
                        instance.Doppler.Update(world,camera.Position,sampleTime,dopplerEnabled);
                    }
                    _al.SetSourceProperty(instance.Source,SourceBoolean.SourceRelative,true);
                    _al.SetSourceProperty(instance.Source,SourceFloat.RolloffFactor,flat||softwareStereo?0f:1f);
                    _al.SetSourceProperty(instance.Source,SourceFloat.ReferenceDistance,Math.Max(1f,d.EffectiveMaxVolumeRadius*node.RadiusMultiplier));
                    _al.SetSourceProperty(instance.Source,SourceFloat.MaxDistance,d.EffectiveDropOffRadius*node.RadiusMultiplier);
                    _al.SetSourceProperty(instance.Source,SourceFloat.Pitch,modifiers.Pitch);
                    // LoopCount belongs to sounds.ini here, NOT the RSL loop override.
                    if (d.EffectiveLoopCount==0)
                    {
                        _al.SetSourceProperty(instance.Source,SourceInteger.Buffer,(int)(softwareStereo?resource.LeftBuffer:resource.Buffer));
                        _al.SetSourceProperty(instance.Source,SourceBoolean.Looping,true);
                    }
                    else
                    {
                        uint buffer=softwareStereo?resource.LeftBuffer:resource.Buffer;
                        for (int i=0;i<d.EffectiveLoopCount;i++) _al.SourceQueueBuffers(instance.Source,1,&buffer);
                    }
                    if(softwareStereo)
                    {
                        _al.SetSourceProperty(instance.RightSource,SourceBoolean.SourceRelative,true);
                        _al.SetSourceProperty(instance.RightSource,SourceFloat.RolloffFactor,0);
                        _al.SetSourceProperty(instance.RightSource,SourceFloat.Pitch,modifiers.Pitch);
                        _al.SetSourceProperty(instance.Source,SourceVector3.Position,0,0,0);
                        _al.SetSourceProperty(instance.RightSource,SourceVector3.Position,0,0,0);
                        if(instance.Continuous)
                        {
                            _al.SetSourceProperty(instance.RightSource,SourceInteger.Buffer,(int)resource.RightBuffer);
                            _al.SetSourceProperty(instance.RightSource,SourceBoolean.Looping,true);
                        }
                        else
                        {
                            uint buffer=resource.RightBuffer;
                            for(int i=0;i<d.EffectiveLoopCount;i++)_al.SourceQueueBuffers(instance.RightSource,1,&buffer);
                        }
                    }
                    _al.DistanceModel(DistanceModel.LinearDistanceClamped);
                    SetEffectPosition(instance,camera,flat,world);
                    SetEffectGain(instance,gain);
                    CheckMusicError("effect configure");
                    if(instance.RightSource==0)_al.SourcePlay(instance.Source);
                    else PlayEffectStereo(instance);
                    CheckMusicError("effect start");
                    _effectSoundsStarted++; _lastEffectSound=$"E{node.Definition.DefinitionId} {name} loops {d.EffectiveLoopCount}";
                    // 006E7330 stops continuous voices on immediate owner end;
                    // finite voices finish. Decode latency must not erase the start.
                    if(node.EndedAtActivation && instance.Continuous)
                    { RetireEffectSound(instance); instance.Completed=true; }
                }
                else
                {
                    _al!.GetSourceProperty(instance.Source,GetSourceInteger.SourceState,out int state);
                    if (state!=(int)SourceState.Playing)
                    { RetireEffectSound(instance); instance.Completed=true; continue; }
                    SetEffectPosition(instance,camera,flat,follows?world:instance.Origin);
                    SetEffectGain(instance,gain);
                    if (instance.Doppler is not null)
                    {
                        float pitch=instance.Doppler.Update(world,camera.Position,sampleTime,dopplerEnabled);
                        _al.SetSourceProperty(instance.Source,SourceFloat.Pitch,pitch);
                        if(instance.RightSource!=0)_al.SetSourceProperty(instance.RightSource,SourceFloat.Pitch,pitch);
                    }
                    // OpenAL errors are context-wide. Consume an update failure
                    // in this owner's catch, before a new engine inherits it.
                    CheckMusicError($"effect update {name}");
                }
                playing++;
            }
            catch (Exception ex) { RetireEffectSound(instance); instance.Error=$"{name}: {ex.Message}"; blocked++; failure??=instance.Error; }
        }
        foreach(var key in _pendingSoundActivations.Keys.ToArray())
            if(_effectSounds.TryGetValue(key,out var activation) &&
                (activation.Source!=0 || activation.Completed || activation.Error is not null))
                _pendingSoundActivations.Remove(key);
        _effectSoundStatus=$"effect SFX playing {playing} tails {tails} pending {pending} blocked {blocked} · limited {limited} · starts {_effectSoundsStarted} · last {_lastEffectSound}"+
            (failure is null?"":$" · {failure}");
    }

    private EffectSoundResource? GetEffectResource(string name,EnbRetailAssetBundle assets,bool required,bool doppler=false,
        EnbRetailSoundDefinition? selectedDefinition=null)
    {
        if (_effectSoundResources.TryGetValue(name,out var existing)) return existing;
        // Bounded cache; active/pending instances protect their resources. Prewarming
        // never evicts. No filesystem or PCM decoding runs in the render callback.
        if (_effectSoundResources.Count>=32)
        {
            if (!required) return null;
            bool Active(StationSound sound,string key)=>
                string.Equals(sound.ResourceName??sound.Name,key,StringComparison.OrdinalIgnoreCase) &&
                (sound.Source!=0 || !sound.Completed);
            var victim=_effectSoundResources.FirstOrDefault(r=>r.Value.Load.IsCompleted &&
                !Speech.Protects(this,r.Key) &&
                !new[]{_landingSound,_beamSound,_speechSound,_footstepSound,_frontendSpeech,_interactionSound}
                    .Concat(_interactionOverlaps).Concat(_weaponRangeVoices)
                    .Any(s=>Active(s,r.Key)) &&
                !_clientSoundChannels.Any(c=>string.Equals(c.Current,r.Key,StringComparison.OrdinalIgnoreCase)) &&
                !_effectSounds.Values.Concat(_effectSoundTails).Any(s=>string.Equals(s.ResourceName??s.Name,r.Key,StringComparison.OrdinalIgnoreCase) && (!s.Completed || s.Source!=0)));
            if (victim.Key is null) return null;
            DeleteEffectBuffers(victim.Value);
            _effectSoundResources.Remove(victim.Key);
        }
        var resource=new EffectSoundResource(_stationPreparedSounds.TryRemove(name,out var prepared)?prepared:Task.Run(()=>
        {
            EffectDecodeSlots.Wait();
            try { return selectedDefinition is null ? DecodeEffectSound(name,assets,engineLoop:doppler) : DecodeSoundDefinition(selectedDefinition,assets); }
            finally { EffectDecodeSlots.Release(); }
        }));
        _effectSoundResources.Add(name,resource); return resource;
    }

    internal static DecodedEffectSound DecodeEffectSound(string name,EnbRetailAssetBundle assets, bool engineLoop = false)
    {
        if (EnbNextAudioAssetCatalog.TryResolve(name,AppContext.BaseDirectory,out string path))
            return DecodeApplicationMp3(name,path,assets);
        return DecodeSoundDefinition(assets.Sounds?.GetDefinitionOrFile(name),assets,engineLoop);
    }

    private static DecodedEffectSound DecodeApplicationMp3(string name,string path,EnbRetailAssetBundle assets)
    {
        try
        {
            var fallback=assets.Sounds?.GetDefinitionOrFile(name)
                ?? throw new InvalidDataException("sounds.ini is unavailable");
            var definition=EnbNextAudioAssetCatalog.CharacterCreationSpeechDefinition(name,fallback);
            string? validation=EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(
                definition,allowSpeech:true,allowRateLimiting:true);
            if(validation is not null)throw new InvalidDataException(validation);
            if(!EnbNext.Installation.EnbPathCase.FileExists(path))throw new FileNotFoundException("Packaged EnbNext audio is unavailable",path);
            byte[] bytes=File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(path));
            if(bytes.Length>4*1024*1024)throw new InvalidDataException("Sound exceeds encoded size limit");
            return new(definition,DecodeMp3(bytes,8*1024*1024),null);
        }
        catch(Exception ex){return new(null,null,ex.Message);}
    }

    internal static DecodedEffectSound DecodeSoundDefinition(EnbRetailSoundDefinition? definition,EnbRetailAssetBundle assets,bool engineLoop=false,bool ambient=false)
    {
        try
        {
            if (assets.Sounds is null || definition is null) throw new InvalidDataException("sounds.ini is unavailable");
            // PCM resources are shared; each playback path separately validates its category.
            var validation = ambient && definition.EffectiveSoundType == 0 ? definition with { SoundType=1 } : definition;
            if (ambient)
            {
                string? dopplerError = EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(VoiceDopplerDefinition(validation));
                if (dopplerError is not null) throw new InvalidDataException(dopplerError);
                validation = validation with { MinDopplerShift=1, MaxDopplerShift=1 };
            }
            string? error=engineLoop ? EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(definition) : EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(validation,allowSpeech:true,allowRateLimiting:true);
            if (error is not null) throw new InvalidDataException(error);
            var resolved=assets.Sounds.Resolve(definition,assets.Paths,assets.RetailMixResources);
            // 009F0350/009FD840: an absent selected buffer stays silent.
            // Do not reroll weighted choices or confuse absence with decode/I/O failure.
            if (resolved.Choices.Count == 1 && resolved.Choices[0].Status == EnbRetailSoundResourceStatus.Missing)
                return new(definition,null,null,MissingMedia:true);
            if (resolved.Choices.Count!=1 || !EnbRetailSoundResourceReader.TryRead(resolved.Choices[0],assets.RetailMixResources,out var bytes,out _,out error))
                throw new InvalidDataException(error??"Sound file did not resolve uniquely");
            // Installed retail sounds are decoded whole; the former size caps were
            // EnbNext constants with no retail counterpart.
            EnbRetailWavePcm16 pcm;
            if(definition.Files[0].LogicalResourceName!.EndsWith(".mp3",StringComparison.OrdinalIgnoreCase))
                pcm=DecodeMp3(bytes,int.MaxValue);
            else if (!EnbRetailWaveAudio.TryDecodePcm16(bytes,out pcm,out error)) throw new InvalidDataException(error);
            return new(definition,pcm,null);
        }
        catch (Exception ex) { return new(null,null,ex.Message); }
    }

    private static EnbRetailWavePcm16 DecodeMp3(byte[] bytes,int pcmLimit)
    {
        var decoded=EnbMp3Decoder.Decode(bytes,pcmLimit);
        return new(decoded.SampleRate,decoded.Channels,decoded.Bytes,"MP3");
    }

    private void SetEffectPosition(EffectSoundInstance instance,EnbResolvedRenderCamera camera,bool flat,Vector3 world)
    {
        instance.LastPosition=world;
        SetVoiceScore(instance.Source,flat?0:EnbRetailSoundArbitration.SpatialScore(world-camera.Position,instance.DropOffRadius));
        if(instance.RightSource!=0)
        {
            instance.WorldDelta=world-camera.Position;
            instance.ViewDelta=camera.WorldToCanonicalView(world);
            return;
        }
        if (flat) _al!.SetSourceProperty(instance.Source,SourceVector3.Position,0f,0f,0f);
        else ApplyCameraRelativePosition(_al!,instance.Source,camera,world);
    }

    private void SetEffectGain(EffectSoundInstance instance,float effectsGain)
    {
        float category=Math.Clamp(instance.SoundType==2?DialogGain:effectsGain,0,1);
        if(instance.RightSource==0)SetVoiceGain(instance.Source,category*instance.Volume);
        else
        {
            var channels=EnbCurrentRetailSoftwareSpatialSound.Gains(instance.WorldDelta,instance.ViewDelta,
                instance.Volume*category,instance.FullVolumeRadius,instance.DropOffRadius);
            SetVoiceGain(instance.Source,channels.Left);
            SetVoiceGain(instance.RightSource,channels.Right);
        }
    }

    private void PlayEffectStereo(EffectSoundInstance instance)
    {
        uint* pair=stackalloc uint[]{instance.Source,instance.RightSource};
        _al!.SourcePlay(2,pair);
    }

    private void EnsureEffectStereoBuffers(EffectSoundResource resource,EnbRetailWavePcm16 pcm)
    {
        if(resource.LeftBuffer!=0)return;
        // Native software spatial voices use the 2D mixer. Keep two stereo
        // channel planes so OpenAL does not add mono-source 3D spatialization
        // on top of the already computed native left/right coefficients.
        int stride=pcm.Channels*2;
        byte[] left=new byte[pcm.Pcm16LittleEndian.Length/stride*4],right=new byte[left.Length];
        for(int frame=0;frame<left.Length/4;frame++)
        {
            pcm.Pcm16LittleEndian.AsSpan(frame*stride,2).CopyTo(left.AsSpan(frame*4));
            pcm.Pcm16LittleEndian.AsSpan(frame*stride+(pcm.Channels==1?0:2),2).CopyTo(right.AsSpan(frame*4+2));
        }
        resource.LeftBuffer=_al!.GenBuffer();resource.RightBuffer=_al.GenBuffer();
        fixed(byte* data=left)_al.BufferData(resource.LeftBuffer,BufferFormat.Stereo16,data,left.Length,pcm.SampleRate);
        fixed(byte* data=right)_al.BufferData(resource.RightBuffer,BufferFormat.Stereo16,data,right.Length,pcm.SampleRate);
        CheckMusicError("software spatial stereo upload");
    }

    private void DeleteEffectBuffers(EffectSoundResource resource)
    {
        if(resource.Buffer==0 && resource.LeftBuffer==0 && resource.RightBuffer==0)return;
        // UI/speech cache requests can precede their context bind. Numeric buffer
        // names belong to this device, not whichever audio owner ran last.
        var prior=_alc!.GetCurrentContext();
        try
        {
            if(!_alc.MakeContextCurrent(_context))
                throw new InvalidOperationException("Cannot bind effect buffer owner context");
            if(resource.Buffer!=0)_al!.DeleteBuffer(resource.Buffer);
            if(resource.LeftBuffer!=0)_al!.DeleteBuffer(resource.LeftBuffer);
            if(resource.RightBuffer!=0)_al!.DeleteBuffer(resource.RightBuffer);
        }
        finally { _alc.MakeContextCurrent(prior); }
    }

    private void RetireEffectSound(EffectSoundInstance instance)
    {
        if (instance.Source!=0 && _al is not null)
        { _al.SourceStop(instance.Source); DeleteVoiceSource(instance.Source); _al.GetError(); }
        if(instance.RightSource!=0 && _al is not null)
        { _al.SourceStop(instance.RightSource); DeleteVoiceSource(instance.RightSource); _al.GetError(); }
        instance.Source=0;instance.RightSource=0;
    }

    private bool UpdateFiniteTail(EffectSoundInstance instance,EnbResolvedRenderCamera camera,float gain)
    {
        if (instance.Source==0 || instance.Continuous) return false;
        _al!.GetSourceProperty(instance.Source,GetSourceInteger.SourceState,out int state);
        if (state!=(int)SourceState.Playing) return false;
        SetEffectPosition(instance,camera,instance.Flat,instance.LastPosition);
        SetEffectGain(instance,gain);
        return true;
    }

    private void StopEffectSounds(bool clearCache=false)
    {
        if (_alc is not null && _context!=null) _alc.MakeContextCurrent(_context);
        foreach (var instance in _effectSounds.Values) RetireEffectSound(instance);
        foreach (var instance in _effectSoundTails) RetireEffectSound(instance);
        _effectSoundTails.Clear();
        _pendingSoundActivations.Clear();
        _effectSounds.Clear(); _effectSoundPrefetched.Clear();
        _warpSounds.Clear();
        if (clearCache)
        {
            foreach (var resource in _effectSoundResources.Values)
                DeleteEffectBuffers(resource);
            _effectSoundResources.Clear();
        }
        _effectSoundStatus="effect SFX stopped";
    }

    private sealed class EffectSoundInstance
    {
        public uint Source; public string? Name; public Vector3 Origin; public Vector3 LastPosition;
        public uint RightSource; public float FullVolumeRadius; public float DropOffRadius;
        public Vector3 WorldDelta; public Vector3 ViewDelta;
        public Vector3? ActivationPosition;
        public bool Continuous; public bool Flat; public float Volume;
        public bool Released;
        public bool Completed; public string? Error;
        public string? ResourceName;
        public EnbRetailSoundDefinition? SelectedDefinition;
        public EnbRadiusSoundRuntime? Radius;
        public int SoundType = 1;
        public EnbCurrentRetailEngineDopplerRuntime? Doppler;
    }
    internal sealed class EffectSoundResource(Task<DecodedEffectSound> load)
    { public Task<DecodedEffectSound> Load { get; }=load; public uint Buffer; public uint LeftBuffer; public uint RightBuffer; }
    internal sealed record DecodedEffectSound(EnbRetailSoundDefinition? Definition,EnbRetailWavePcm16? Pcm,string? Error, bool MissingMedia = false);
}
