using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private sealed class AmbientVoice(EnbRetailSoundDefinition definition, Task<DecodedEffectSound> decode, Vector3? position)
    {
        public readonly EnbRetailSoundDefinition Definition = definition;
        public readonly Task<DecodedEffectSound> Decode = decode;
        public Vector3? Position = position;
        public EnbStationSoundBinding? Binding;
        public EnbCurrentRetailEngineDopplerRuntime? Doppler;
        public uint Source, Buffer, RightSource, RightBuffer;
        public Vector3 WorldDelta, ViewDelta;
        public float Volume;
        public bool SamplePan;
    }
    private uint? _ambientStation;
    private Task<EnbRetailStationSoundCatalog>? _ambientCatalog;
    private (uint Type, string Model)? _ambientRoomKey;
    private EnbStationAmbientRoom? _ambientRoom;
    private EnbStationAmbientRuntime _ambientRuntime = new(EnbRetailPresentationRandom.Shared.Next);
    private readonly List<AmbientVoice> _ambientVoices = [];
    private readonly Dictionary<string, long> _ambientLastCall = new(StringComparer.OrdinalIgnoreCase);
    private string? _ambientFailure;
    private int _ambientStarts;
    private IReadOnlyList<EnbStationSoundObject> _ambientObjects = [];
    private uint _ambientSampleTime;
    private bool _ambientDopplerEnabled;
    // The shared 3D voice Doppler calculation is independent of repeat count
    // and random factory modifiers; the latter are sampled once before use.
    private static EnbRetailSoundDefinition VoiceDopplerDefinition(EnbRetailSoundDefinition d)
        => d with { SoundType=1, LoopCount=0, MinVolumeShift=0, MaxVolumeShift=0, MinPitchShift=0, MaxPitchShift=0 };
    public string StationAmbientStatus => $" · ambience {_ambientRoom?.Section ?? "idle"}: {_ambientVoices.Count(v => v.Source != 0)} playing, {_ambientVoices.Count(v => v.Source == 0)} decoding, {_ambientStarts} starts" +
        (_ambientFailure is null ? "" : " · ambient blocked: " + _ambientFailure);

    public void StopStationAmbient()
    {
        foreach (var voice in _ambientVoices) RetireAmbient(voice);
        _ambientVoices.Clear(); _ambientLastCall.Clear();
        _ambientCatalog = null; _ambientStation = null; _ambientRoom = null; _ambientRoomKey = null;
        _ambientRuntime = new(EnbRetailPresentationRandom.Shared.Next); _ambientFailure = null; _ambientStarts = 0;
        _ambientObjects = [];
    }

    public void UpdateStationAmbient(uint stationId, uint roomType, string model, EnbRetailAssetBundle assets,
        long now, float sfxGain, float musicGain, EnbResolvedRenderCamera camera, Func<Vector3> randomRoomPosition,
        IReadOnlyList<EnbStationSoundObject>? soundObjects = null, bool dopplerEnabled = true)
    {
        if (_disposed) return;
        try
        {
            if (_ambientStation != stationId)
            {
                StopStationAmbient(); _ambientStation = stationId;
                StopMusic(); // Station room music replaces the space playlist owner.
                _ambientCatalog = Task.Run(() =>
                {
                    string path = Path.Combine(assets.Paths.ClientIniDirectory, $"starbasesounds{stationId}.ini");
                    return new EnbRetailStationSoundCatalog(EnbNext.Installation.EnbPathCase.FileExists(path) ? EnbRetailIniDocument.Load(path) : EnbRetailIniDocument.Parse(""));
                });
            }
            if (!_ambientCatalog!.IsCompleted) return;
            _ambientObjects = soundObjects ?? [];
            _ambientSampleTime = unchecked((uint)now); _ambientDopplerEnabled = dopplerEnabled;
            if (_ambientRoomKey != (roomType, model))
            {
                _ambientRoom = EnbStationSoundObject.Bind(_ambientCatalog.GetAwaiter().GetResult().Room(roomType, model), _ambientObjects);
                _ambientRoomKey = (roomType, model);
            }
            foreach (var voice in _ambientVoices.ToArray())
            {
                try
                {
                    if (PumpAmbient(voice, sfxGain, musicGain, camera)) continue;
                }
                catch (Exception ex) { _ambientFailure = voice.Definition.Name + ": " + ex.Message; }
                RetireAmbient(voice); _ambientVoices.Remove(voice);
            }
            // 00441A90 draws once before 00435610, even when no voice is due.
            Vector3 tickPosition=randomRoomPosition();
            var due = _ambientRuntime.Update(_ambientRoom!, now, _ambientVoices.Any(v => v.Binding?.Follow != true && v.Definition.EffectiveLoopCount != 0));
            if (_ambientRuntime.ChangedRoom)
            {
                foreach (var voice in _ambientVoices) RetireAmbient(voice);
                _ambientVoices.Clear();
            }
            // 00441A90 samples one room bone transform for this tick; fixed sounds
            // due together share it and retain that position for their lifetime.
            foreach (var entry in due)
            {
                try
                {
                    var definition = assets.Sounds?.GetDefinitionOrFile(entry.Sound) ?? throw new InvalidDataException("sounds.ini unavailable");
                    // 00435610 appends each started ambient sound to a growable vector (this+4..+0xC); no voice limit.
                    if (_ambientLastCall.TryGetValue(definition.Name, out var last) && now - last < definition.EffectiveSecondsBetweenCalls * 1000) continue;
                    _ambientLastCall[definition.Name] = now;
                    int total = definition.Files.Sum(f => Math.Max(0, f.Weight ?? 1));
                    if (total <= 0) throw new InvalidDataException("No positive sound weights");
                    int choice = EnbRetailMovieRandom.Damage.Next(1,total)-1;
                    var file = definition.Files.First(f => (choice -= Math.Max(0, f.Weight ?? 1)) < 0);
                    if (file.IsNullChoice) continue;
                    if (!entry.FixedPosition && (entry.MinPan != .5f || entry.MaxPan != .5f))
                        throw new InvalidDataException("Noncentral authored ambient pan is not supported");
                    var selected = definition with { Files = [file], SecondsBetweenCalls = 0 };
                    Vector3? position;
                    if (entry.Binding is {} binding)
                    {
                        var obj = _ambientObjects.FirstOrDefault(o => o.Key == binding.ObjectKey);
                        if (obj is null || !obj.TryPosition(binding.Bone, false, out var bonePosition)) continue;
                        position = bonePosition;
                    }
                    else position = entry.FixedPosition ? tickPosition : null;
                    // Decode away from the render thread; per-voice buffers are released
                    // at completion, so long music clips cannot grow a persistent cache.
                    var decode = Task.Run(() =>
                    {
                        EffectDecodeSlots.Wait();
                        try { return DecodeSoundDefinition(selected, assets, ambient: true); }
                        finally { EffectDecodeSlots.Release(); }
                    });
                    _ambientVoices.Add(new(selected, decode, position) { Binding = entry.Binding, SamplePan = !entry.OnEntry && !entry.FixedPosition });
                }
                catch (Exception ex) { _ambientFailure = entry.Sound + ": " + ex.Message; }
            }
        }
        catch (Exception ex) { _ambientFailure = ex.Message; }
    }

    private bool PumpAmbient(AmbientVoice voice, float sfxGain, float musicGain, EnbResolvedRenderCamera camera)
    {
        if (!voice.Decode.IsCompleted) return true;
        var decoded = voice.Decode.GetAwaiter().GetResult();
        if (decoded.Error is not null) throw new InvalidDataException(decoded.Error);
        if (decoded.MissingMedia) return false;
        if (!EnsureInitialized()) throw new InvalidOperationException(_status);
        _alc!.MakeContextCurrent(_context);
        var definition = voice.Definition;
        if (voice.Source == 0)
        {
            var pcm = decoded.Pcm!;
            bool softwareSpatial=voice.Position.HasValue && !UsesSpatialBank(pcm);
            if(softwareSpatial)
            {
                var resource=new EffectSoundResource(voice.Decode);
                EnsureEffectStereoBuffers(resource,pcm);
                voice.Buffer=resource.LeftBuffer;voice.RightBuffer=resource.RightBuffer;
                voice.RightSource=_al!.GenSource();
            }
            else
            {
                voice.Buffer = _al!.GenBuffer();
                fixed (byte* samples = pcm.Pcm16LittleEndian)
                    _al.BufferData(voice.Buffer, pcm.Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16,
                        samples, pcm.Pcm16LittleEndian.Length, pcm.SampleRate);
            }
            voice.Source = _al.GenSource();
            RegisterVoice(voice.Source,definition.Name,definition.EffectiveMaxInstances,definition.EffectiveSoundType,definition.EffectivePriority,voice.Position is {} point?EnbRetailSoundArbitration.SpatialScore(point-camera.Position,definition.EffectiveDropOffRadius):0,voice.RightSource,spatialMono:voice.Position.HasValue && UsesSpatialBank(pcm));
            var modifiers = EnbCurrentRetailEffectSoundRuntime.SampleModifiers(definition, EnbNext.Rendering.EnbRetailCrtRandomSource.Current.NextUnit(), EnbNext.Rendering.EnbRetailCrtRandomSource.Current.NextUnit());
            // 004356D3/00435BA5 consume a pan draw even for the installed .5/.5 range.
            if (voice.SamplePan) EnbRetailCrtRandomSource.Current.NextUnit();
            voice.Volume = modifiers.Volume;
            if (voice.Position.HasValue)
                voice.Doppler = new(VoiceDopplerDefinition(definition) with { Pitch=modifiers.Pitch });
            void Configure(uint source,uint buffer)
            {
                _al.SetSourceProperty(source,SourceBoolean.SourceRelative,true);
                _al.SetSourceProperty(source,SourceFloat.RolloffFactor,voice.Position.HasValue && !softwareSpatial?1:0);
                _al.SetSourceProperty(source,SourceFloat.ReferenceDistance,Math.Max(1,definition.EffectiveMaxVolumeRadius));
                _al.SetSourceProperty(source,SourceFloat.MaxDistance,definition.EffectiveDropOffRadius);
                _al.SetSourceProperty(source,SourceFloat.Pitch,modifiers.Pitch);
                if(definition.EffectiveLoopCount==0)
                {
                    _al.SetSourceProperty(source,SourceInteger.Buffer,(int)buffer);
                    _al.SetSourceProperty(source,SourceBoolean.Looping,true);
                }
                else for(int i=0;i<definition.EffectiveLoopCount;i++)_al.SourceQueueBuffers(source,1,&buffer);
            }
            Configure(voice.Source,voice.Buffer);
            if(softwareSpatial)Configure(voice.RightSource,voice.RightBuffer);
            ConfigureAmbientPosition(voice, camera);
            SetAmbientGain(voice,sfxGain,musicGain);
            if(voice.RightSource==0)_al.SourcePlay(voice.Source);
            else {uint* pair=stackalloc uint[]{voice.Source,voice.RightSource};_al.SourcePlay(2,pair);}
            CheckMusicError("ambient start"); _ambientStarts++;
        }
        else
        {
            _al!.GetSourceProperty(voice.Source, GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing) return false;
            ConfigureAmbientPosition(voice, camera);
            SetAmbientGain(voice,sfxGain,musicGain);
        }
        return true;
    }

    private void ConfigureAmbientPosition(AmbientVoice voice, EnbResolvedRenderCamera camera)
    {
        // 004354C0 attaches looping sources. Timed tracked sounds (004357DF)
        // instead retain the bone transform sampled at the time of the request.
        if (voice.Binding is { Follow: true } binding &&
            _ambientObjects.FirstOrDefault(o => o.Key == binding.ObjectKey) is {} obj &&
            obj.TryPosition(binding.Bone, false, out var movingPosition)) voice.Position = movingPosition;
        if (voice.Position is {} position)
        {
            _al!.DistanceModel(DistanceModel.LinearDistanceClamped);
            if(voice.RightSource==0)ApplyCameraRelativePosition(_al, voice.Source, camera, position);
            else {voice.WorldDelta=position-camera.Position;voice.ViewDelta=camera.WorldToCanonicalView(position);}
            SetVoiceScore(voice.Source,EnbRetailSoundArbitration.SpatialScore(position-camera.Position,voice.Definition.EffectiveDropOffRadius));
            if (voice.Doppler is {} doppler)
            {
                float pitch=doppler.Update(position,camera.Position,_ambientSampleTime,_ambientDopplerEnabled);
                _al.SetSourceProperty(voice.Source,SourceFloat.Pitch,pitch);
                if(voice.RightSource!=0)_al.SetSourceProperty(voice.RightSource,SourceFloat.Pitch,pitch);
            }
        }
        else _al!.SetSourceProperty(voice.Source, SourceVector3.Position, 0f, 0f, 0f);
    }
    private void SetAmbientGain(AmbientVoice voice,float sfxGain,float musicGain)
    {
        var definition=voice.Definition;
        float gain=voice.Volume*Math.Clamp(definition.EffectiveSoundType==0?musicGain:definition.EffectiveSoundType==2?DialogGain:sfxGain,0,1);
        if(voice.RightSource==0)SetVoiceGain(voice.Source,gain);
        else
        {
            var channels=EnbCurrentRetailSoftwareSpatialSound.Gains(voice.WorldDelta,voice.ViewDelta,gain,
                definition.EffectiveMaxVolumeRadius,definition.EffectiveDropOffRadius);
            SetVoiceGain(voice.Source,channels.Left);SetVoiceGain(voice.RightSource,channels.Right);
        }
    }
    private void RetireAmbient(AmbientVoice voice)
    {
        if (_al is null) return;
        _alc!.MakeContextCurrent(_context);
        if (voice.Source != 0) { _al.SourceStop(voice.Source); DeleteVoiceSource(voice.Source); voice.Source = 0; }
        if (voice.RightSource != 0) { _al.SourceStop(voice.RightSource); DeleteVoiceSource(voice.RightSource); voice.RightSource=0; }
        if (voice.RightBuffer != 0) { _al.DeleteBuffer(voice.RightBuffer); voice.RightBuffer=0; }
        if (voice.Buffer != 0) { _al.DeleteBuffer(voice.Buffer); voice.Buffer = 0; }
    }
}
