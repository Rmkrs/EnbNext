using EnbNext.Rendering;
using EnbNext.Assets;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    // 0043FAA0 -> 009F2240 updates the speech (SoundType 2) bus independently.
    public float DialogGain { get; set; } = 1f;
    internal sealed class StationSound
    {
        public string? Name;
        public string? ResourceName;
        public EnbRetailSoundDefinition? SelectedDefinition;
        public bool ForceSpeechFile;
        public bool BypassSpeechManager;
        public SpeechRequest? SpeechRequest;
        public uint Source;
        public float Volume;
        public float? VolumeOverride;
        public float? PriorityOverride;
        public EnbRetailFontSoundLifetime? FontLifetime;
        public bool Completed;
        public bool AllowSpeech;
        public bool UseAuthoredLoopCount;
        public int? LoopCountOverride;
        public string Status = "station audio idle";
    }
    private readonly StationSound _landingSound = new();
    private readonly StationSound _beamSound = new();
    private readonly StationSound _speechSound = new() { ForceSpeechFile = true, AllowSpeech = true };
    private readonly StationSound _footstepSound = new();
    private EnbRetailSoundCallGate _stationSoundCalls => Speech.Calls;
    private IReadOnlyDictionary<string,string>[]? _footstepSurfaces;
    private string? _footstepName;
    public void UpdateStationFootstep(bool trigger,uint surface,bool running,EnbRetailAssetBundle assets,float gain)
    {
        if(trigger && !_disposed && assets.Sounds is {} sounds)
        {
            _footstepSurfaces ??= EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"footsteps.ini"))
                .EnumerateSections().Select(s=>s.Value).ToArray();
            string? name=EnbRetailFootstepSounds.Name(_footstepSurfaces,surface,running);
            if(name is null)return;
            var definition=sounds.GetDefinitionOrFile(name);
            int total=definition.Files.Sum(f=>Math.Max(0,f.Weight??1));
            if(total<=0)return;
            int roll=EnbRetailMovieRandom.Damage.Next(1,total)-1;
            var choice=definition.Files.First(f=>(roll-=Math.Max(0,f.Weight??1))<0);
            StopStationSound(_footstepSound);
            _footstepName=null;
            if(choice.IsNullChoice)return;
            _footstepName=name+"/"+choice.LogicalResourceName;
            var selected=definition with {Files=[choice]};
            if(!_effectSoundResources.ContainsKey(_footstepName))
                _stationPreparedSounds.GetOrAdd(_footstepName,_=>Task.Run(()=>DecodeSoundDefinition(selected,assets)));
        }
        if(_footstepName is {} pending)UpdateStationSound(_footstepSound,pending,assets,gain);
    }
    private object? _speechPage;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,Task<DecodedEffectSound>> _stationPreparedSounds=new(StringComparer.OrdinalIgnoreCase);
    public void PrepareStationSound(string? name,EnbRetailAssetBundle assets)
    {
        if(!_disposed && !string.IsNullOrWhiteSpace(name))
            _stationPreparedSounds.GetOrAdd(name,n=>Task.Run(()=>DecodeEffectSound(n,assets)));
    }
    public void UpdateStationSpeech(object? page,string? name,EnbRetailAssetBundle assets,float gain)
    {
        if(!ReferenceEquals(page,_speechPage)){StopStationSound(_speechSound);_speechPage=page;}
        if(page is not null && !string.IsNullOrWhiteSpace(name))UpdateStationSound(_speechSound,name,assets,gain);
    }
    public string StationSoundStatus => _landingSound.Status;
    public string StationBeamSoundStatus => _beamSound.Status;
    public string StationSpeechStatus => _speechPage is null?"": " · speech "+_speechSound.Status;

    public void StopStationSound()
    { StopShipEmoteAudio(); StopStationSound(_landingSound); StopStationSound(_beamSound); StopStationSound(_speechSound); StopStationSound(_footstepSound);_footstepName=null;_speechPage=null; }
    private void StopStationSound(StationSound sound)
    {
        if (sound.SpeechRequest is { } request)
        {
            request.Cancelled = true;
            request.Handle?.Stop();
            sound.SpeechRequest = null;
            sound.Source = 0;
        }
        if(sound.Source!=0 && _al is not null)
        {
            _alc!.MakeContextCurrent(_context);
            _al.SourceStop(sound.Source); DeleteVoiceSource(sound.Source);
        }
        sound.Source=0; sound.Name=null; sound.ResourceName=null; sound.Completed=false;sound.VolumeOverride=null;
        sound.Status="station audio idle";
    }

    public void UpdateStationSound(string name,EnbRetailAssetBundle assets,float gain,double elapsedSeconds=0)
        => UpdateStationSound(_landingSound,name,assets,gain,elapsedSeconds);
    public void UpdateStationBeamSound(string name,EnbRetailAssetBundle assets,float gain)
        => UpdateStationSound(_beamSound,name,assets,gain);
    private void UpdateStationSound(StationSound sound,string name,EnbRetailAssetBundle assets,float gain,double elapsedSeconds=0)
    {
        if(_disposed)return;
        if (!sound.BypassSpeechManager && TryUpdateSpeech(sound, name, assets, gain, elapsedSeconds)) return;
        if(sound.Name!=name)
        {
            StopStationSound(sound); sound.Name=name;
            // Test once at the request boundary, not each frame while decoding.
            // A denied call is dropped; it must not play late when the deadline expires.
            if (assets.Sounds is {} sounds && !_stationSoundCalls.TryCall(
                sounds.GetDefinitionOrFile(name)))
            {
                sound.Completed=true; sound.Status="station audio suppressed by authored call interval";
            }
        }
        if(sound.Completed)return;
        try
        {
            var resource=GetStationResource(sound,name,assets);
            if(resource is null)throw new InvalidDataException("Station audio cache unavailable.");
            if(!resource.Load.IsCompleted) {sound.Status="station audio decoding"; return;}
            var decoded=resource.Load.GetAwaiter().GetResult();
            if(decoded.Error is not null)throw new InvalidDataException(decoded.Error);
            if(decoded.MissingMedia){sound.Completed=true;sound.Status="station audio silent";return;}
            if (decoded.Definition!.EffectiveSoundType == 2) gain = DialogGain;
            string? categoryError=EnbNext.Rendering.EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(decoded.Definition!,allowSpeech:sound.AllowSpeech || ReferenceEquals(sound,_speechSound),allowRateLimiting:true);
            if(categoryError is not null)throw new InvalidDataException(categoryError);
            if(!EnsureInitialized()) {sound.Status=_status; return;}
            _alc!.MakeContextCurrent(_context);
            var pcm=decoded.Pcm!;
            if(sound.Source==0)
            {
                double duration=pcm.Pcm16LittleEndian.Length/(double)(pcm.SampleRate*pcm.Channels*2);
                if(elapsedSeconds>=duration){sound.Completed=true;sound.Status="station audio complete";return;}
                if(resource.Buffer==0)
                {
                    resource.Buffer=_al!.GenBuffer();
                    fixed(byte* samples=pcm.Pcm16LittleEndian)
                        _al.BufferData(resource.Buffer,pcm.Channels==1?BufferFormat.Mono16:BufferFormat.Stereo16,
                            samples,pcm.Pcm16LittleEndian.Length,pcm.SampleRate);
                }
                sound.Source=_al!.GenSource();
                RegisterVoice(sound.Source,name,decoded.Definition!.EffectiveMaxInstances,decoded.Definition.EffectiveSoundType,sound.PriorityOverride??decoded.Definition.EffectivePriority,0);
                int loops=sound.LoopCountOverride ?? (sound.UseAuthoredLoopCount?decoded.Definition!.EffectiveLoopCount:1);
                if(loops>1)
                {uint buffer=resource.Buffer;for(int i=0;i<loops;i++)_al.SourceQueueBuffers(sound.Source,1,&buffer);}
                else _al.SetSourceProperty(sound.Source,SourceInteger.Buffer,(int)resource.Buffer);
                _al.SetSourceProperty(sound.Source,SourceBoolean.SourceRelative,true);
                _al.SetSourceProperty(sound.Source,SourceBoolean.Looping,loops==0);
                _al.SetSourceProperty(sound.Source,SourceFloat.RolloffFactor,0);
                var modifiers=EnbNext.Rendering.EnbCurrentRetailEffectSoundRuntime.SampleModifiers(decoded.Definition!,EnbNext.Rendering.EnbRetailCrtRandomSource.Current.NextUnit(),EnbNext.Rendering.EnbRetailCrtRandomSource.Current.NextUnit());
                sound.Volume=sound.VolumeOverride??modifiers.Volume;
                SetVoiceGain(sound.Source,sound.Volume*Math.Clamp(gain,0,1));
                _al.SetSourceProperty(sound.Source,SourceFloat.Pitch,modifiers.Pitch);
                if(elapsedSeconds>0)_al.SetSourceProperty(sound.Source,SourceFloat.SecOffset,(float)elapsedSeconds);
                _al.SourcePlay(sound.Source); CheckMusicError("station sound start");
            }
            else
            {
                _al!.GetSourceProperty(sound.Source,GetSourceInteger.SourceState,out int state);
                if(state!=(int)SourceState.Playing) { sound.Completed=true; sound.Status="station audio complete"; return; }
                SetVoiceGain(sound.Source,sound.Volume*Math.Clamp(gain,0,1));
            }
            sound.Status="station audio playing "+name;
        }
        catch(Exception ex) {StopStationSound(sound); sound.Name=name; sound.Completed=true; sound.Status="station audio blocked: "+ex.Message;}
    }
}

