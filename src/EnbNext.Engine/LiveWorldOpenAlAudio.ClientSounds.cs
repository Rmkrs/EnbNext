using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private sealed class ClientSoundChannel
    {
        public readonly StationSound Voice=new(){AllowSpeech=true};
        public readonly Queue<string> Pending=[];
        public string? Current;
        public string LastReceipt="";
    }
    private readonly ClientSoundChannel[] _clientSoundChannels=Enumerable.Range(0,16).Select(_=>new ClientSoundChannel()).ToArray();
    private Guid _clientSoundStream;
    private int _clientSoundCursor;
    private string? _clientSoundFailure;
    private readonly Queue<string> _clientSoundReceipts=[];
    private string _clientSoundReceiptText="";
    private void RecordClientSound(string text)
    {
        _clientSoundReceipts.Enqueue(text);
        while(_clientSoundReceipts.Count>24)_clientSoundReceipts.Dequeue();
        Volatile.Write(ref _clientSoundReceiptText,string.Join(" | ",_clientSoundReceipts));
    }
    private StationSound _interactionSound=new(){AllowSpeech=true};
    private readonly List<StationSound> _interactionOverlaps=[];
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Name,float? Volume)> _interactionSounds=new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Name,float Volume,float Duration,uint Started)> _fontSounds=new();
    public void QueueFontSound(string name,float volume,float duration,uint started)
    {Interlocked.Increment(ref _interactionRequests);_fontSounds.Enqueue((name,volume,duration,started));}
    private ulong _dockingSoundRevision;
    private long _interactionRequests;
    private readonly Queue<string> _interactionReceipts=[];
    private string _interactionReceiptText="",_lastInteractionState="";
    public void QueueInteractionSound(string name){Interlocked.Increment(ref _interactionRequests);_interactionSounds.Enqueue((name,null));}
    internal bool InteractionSoundDecoding => _interactionSound.Status == "station audio decoding";
    internal void LeaveFrontend()
    {
        StopFrontendSpeech();
        StopMusic();
    }
    public void QueueAuthoredInteractionSound(string name,float volume){Interlocked.Increment(ref _interactionRequests);_interactionSounds.Enqueue((name,volume));}
    // 00694078..0069408D sets utility-click volume to 0.5 before playback.
    public void QueueUtilitySound(string name){Interlocked.Increment(ref _interactionRequests);_interactionSounds.Enqueue((name,.5f));}
    // The HUD can become visually idle immediately after its last panel closes.
    // Keep pumping asynchronous decoding and source completion independently.
    public bool ClientSoundsNeedUpdate => !_disposed &&
        (Speech.NeedsUpdate || !_interactionSounds.IsEmpty || !_fontSounds.IsEmpty || !_weaponRangeChanges.IsEmpty || NeedsUpdate(_interactionSound) || _interactionOverlaps.Any(NeedsUpdate) || _weaponRangeVoices.Any(NeedsUpdate) ||
         _clientSoundChannels.Any(c=>NeedsUpdate(c.Voice) || c.Pending.Count>0 || c.Current is not null && c.Voice.Completed));
    private static bool NeedsUpdate(StationSound sound) => !sound.Completed &&
        (sound.Source!=0 || sound.Status=="station audio decoding");
    public string ClientSoundStatus => (_interactionSound.Name is {} cue?$" · interaction {cue}: {_interactionSound.Status}":"")+(_clientSoundFailure is {} failure?" · client sound failure: "+failure:"")+string.Join("",_clientSoundChannels.Select((c,i)=>c.Current is null?"":$" · client sound {i}: {c.Voice.Status}"))+
        $" · UI sound requests {Interlocked.Read(ref _interactionRequests)}; receipts [{Volatile.Read(ref _interactionReceiptText)}] | server sound events {_clientSoundCursor}; receipts [{Volatile.Read(ref _clientSoundReceiptText)}]";
    private void RecordInteraction(string text)
    {
        _interactionReceipts.Enqueue(text);
        while(_interactionReceipts.Count>12)_interactionReceipts.Dequeue();
        Volatile.Write(ref _interactionReceiptText,string.Join(" | ",_interactionReceipts));
    }

    private void StopInteractionOverlaps()
    {
        foreach(var voice in _interactionOverlaps)StopStationSound(voice);
        _interactionOverlaps.Clear();
    }
    private void StopClientSounds()
    {
        StopWeaponRangeSounds();
        _clientSoundFailure=null;
        foreach(var channel in _clientSoundChannels)
        {StopStationSound(channel.Voice);channel.Pending.Clear();channel.Current=null;channel.LastReceipt="";}
    }

    // Current retail 007383D0 / 007384A0 / 0075AE30 / 0075B0D0.
    // Keep packet order; republishing a scene must not replay any event.
    public void UpdateInteractionSounds(EnbRetailAssetBundle assets,float gain)
    {
        Speech.Pump();
        while(_interactionSounds.TryDequeue(out var cue))
        {
            RecordInteraction(cue.Name+" requested");_lastInteractionState="";
            if(_interactionSound.Name is not null && !_interactionSound.Completed)
            {
                _interactionOverlaps.Add(_interactionSound);
                _interactionSound=new(){AllowSpeech=true};
            }
            StopStationSound(_interactionSound);_interactionSound.Name=cue.Name;_interactionSound.VolumeOverride=cue.Volume;
            UpdateStationSound(_interactionSound, cue.Name, assets, gain);
        }
        while(_fontSounds.TryDequeue(out var font))
        {
            RecordInteraction(font.Name+" font requested");
            _interactionOverlaps.Add(new(){AllowSpeech=true,Name=font.Name,VolumeOverride=font.Volume,PriorityOverride=.5f,
                LoopCountOverride=1,FontLifetime=new(font.Duration,font.Started)});
        }
        foreach(var voice in _interactionOverlaps)
        {
            if(voice.FontLifetime?.Advance(unchecked((uint)Environment.TickCount))==true)
            {
                string? expired=voice.Name;StopStationSound(voice);voice.Name=expired;
                voice.Completed=true;voice.Status="font duration complete";
            }
            else if(voice.Name is {} name)UpdateStationSound(voice,name,assets,gain);
        }
        foreach(var voice in _interactionOverlaps.Where(v=>v.Completed).ToArray())
        {RecordInteraction(voice.Name+": "+voice.Status);StopStationSound(voice);_interactionOverlaps.Remove(voice);}
        if(_interactionSound.Name is {} interaction)
        {
            UpdateStationSound(_interactionSound,interaction,assets,gain);
            string state=_interactionSound.Status;
            if(state!=_lastInteractionState)
            {
                _lastInteractionState=state;
                RecordInteraction(interaction+": "+state+(_interactionSound.Source!=0?
                    FormattableString.Invariant($"; source {_interactionSound.Source}; gain {_interactionSound.Volume*Math.Clamp(gain,0,1):F3}"):"; no source"));
            }
        }
    }

    public void UpdateClientSounds(IEnbSceneFrame scene,EnbRetailAssetBundle assets,float gain)
    {
        if(_disposed)return;
        if(_clientSoundStream!=scene.ClientSoundStreamId)
        {StopClientSounds();_clientSoundStream=scene.ClientSoundStreamId;_clientSoundCursor=0;}
        if(scene.IsTransitioning)StopWeaponRangeSounds();
        else UpdateWeaponRangeSounds(assets,gain);
        if(_dockingSoundRevision!=scene.DockingSoundRevision)
        {_dockingSoundRevision=scene.DockingSoundRevision;if(_dockingSoundRevision!=0)QueueInteractionSound("Docking_Init");}
        UpdateInteractionSounds(assets,gain);
        while(_clientSoundCursor<scene.ClientSoundEvents.Count)
        {
            var entry=scene.ClientSoundEvents[_clientSoundCursor++];
            RecordClientSound($"event {_clientSoundCursor}: {entry}");
            if(entry.Command==2){StopClientSounds();continue;}
            if(entry.Channel>=16)continue;
            var channel=_clientSoundChannels[entry.Channel];
            if(entry.Command is {} command)
            {
                StopStationSound(channel.Voice);channel.Current=null;
                if(command==1)channel.Pending.Clear();
            }
            else if(!string.IsNullOrWhiteSpace(entry.Name))
            {
                PrepareStationSound(entry.Name,assets);
                if(entry.Queue!=0)channel.Pending.Enqueue(entry.Name);
                else {StopStationSound(channel.Voice);channel.Current=entry.Name;channel.LastReceipt="";}
            }
        }
        foreach(var channel in _clientSoundChannels)
        {
            if(channel.Voice.Completed)
            {
                if(channel.Voice.Status.StartsWith("station audio blocked:"))_clientSoundFailure=channel.Current+": "+channel.Voice.Status;
                StopStationSound(channel.Voice);channel.Current=null;
            }
            if(channel.Current is null && channel.Pending.TryDequeue(out var next))channel.Current=next;
            if(channel.Current is {} name)
            {
                UpdateStationSound(channel.Voice,name,assets,gain);
                string receipt=name+": "+channel.Voice.Status;
                if(receipt!=channel.LastReceipt)
                {
                    channel.LastReceipt=receipt;
                    RecordClientSound(receipt+(channel.Voice.Source!=0?
                        FormattableString.Invariant($"; source {channel.Voice.Source}; gain {channel.Voice.Volume*Math.Clamp(gain,0,1):F3}"):"; no source"));
                }
            }
        }
    }
}
