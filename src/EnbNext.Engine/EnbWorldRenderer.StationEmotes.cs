using EnbNext.Assets;
using EnbNext.Net7.Social;
using EnbNext.Presentation;
using EnbNext.Rendering;
using System.Numerics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private Guid _stationEmoteStream;
    private long _stationEmoteSequence;
    private sealed class StationReceivedCommands
    {
        public readonly EnbRetailAvatarReceivedSpeech Received=new();
        public readonly List<Net7AvatarEmoteEvent> Pending=[];
    }
    private readonly Dictionary<uint,StationReceivedCommands> _stationEmotes=[];
    private readonly List<EnbCurrentRetailEffectSoundNode> _stationAnimationSoundRequests=[];
    private ulong _stationAnimationSoundSequence;

    private void UpdateStationEmotes(long elapsed)
    {
        var chat=_stationScene?.Hud.Chat;
        if(chat is null){_stationEmotes.Clear();return;}
        if(_stationEmoteStream!=chat.StreamId)
        {_stationEmoteStream=chat.StreamId;_stationEmoteSequence=0;_stationEmotes.Clear();}
        var present=_stationScene!.StationAvatars.Where(a=>a.Avatar is not null)
            .Select(a=>a.AvatarId).ToHashSet();
        // 736F10 looks up the cached avatar, not the room display record.
        // Already attached actors retain received mood/queue while hidden.
        present.UnionWith(_stationRemotePeople.Keys);
        if(_stationScene.LocalAvatarObjectId is {} local)present.Add(local);
        foreach(var id in _stationEmotes.Keys.Where(id=>!present.Contains(id)).ToArray())_stationEmotes.Remove(id);
        foreach(var message in chat.AvatarEmotes.Where(e=>e.Sequence>_stationEmoteSequence))
        {
            _stationEmoteSequence=message.Sequence;
            if(!present.Contains(message.AvatarId))continue;
            // 736F10 ->784B80 admits only zero input and residual speed.
            if(message.AvatarId==_stationScene.LocalAvatarObjectId && _stationMovement is {} movement &&
                (movement.Thrust!=0 || movement.Turn!=0 || movement.Look!=0 || movement.Speed!=0))continue;
            if(_stationRemotePeople.TryGetValue(message.AvatarId,out var movingRemote) && !movingRemote.Motion.AcceptsReceivedSpeech)continue;
            if(message.AvatarId!=_stationScene.LocalAvatarObjectId && !_stationRemotePeople.ContainsKey(message.AvatarId) &&
                _stationScene.StationAvatars.FirstOrDefault(a=>a.AvatarId==message.AvatarId)?.Avatar is {} appearance)
            {
                // 74C840 ->77E610 lazily initializes NLP and idle before
                // 784B80 consumes the first message, even without a room record.
                var person=new StationPerson(appearance);
                _stationRemotePeople[message.AvatarId]=person;
                try
                {
                    person.Speech=new(_retailAssetsAccessor()!,appearance,portraitIdle:true);
                    person.Speech.StartIdle(elapsed*.001);
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
                {person.Failure=ex.Message;continue;}
            }
            if(!_stationEmotes.TryGetValue(message.AvatarId,out var owner))_stationEmotes[message.AvatarId]=owner=new();
            owner.Pending.Add(message);
        }
        foreach(var (id,person) in _stationRemotePeople)
            if(!_stationPeople.ContainsKey((false,id)) && person.Speech is {} playback)
                ReceiveStationCommands(id,playback,elapsed);
        if(_stationScene.LocalAvatarObjectId is uint localId && _stationAvatarPlayback is {} localPlayback)
            ReceiveStationCommands(localId,localPlayback,elapsed);
    }

    private void ReceiveStationCommands(uint id,LiveAvatarSpeechPlayback playback,long elapsed)
    {
        if(!_stationEmotes.TryGetValue(id,out var owner))return;
        foreach(var message in owner.Pending)
        {
            // Cache construction starts +204=0; room attachment sets it to1.
            int mode=_stationRemotePeople.TryGetValue(id,out var remote) && !remote.Attached?0:1;
            var commands=owner.Received.Consume(message.Commands,mode,playback.Mood);
            playback.Idle.Posture=owner.Received.Posture;
            playback.ReplaceReceived(commands,owner.Received.Mood,elapsed*.001);
        }
        owner.Pending.Clear();
    }

    private void CollectStationAnimationSounds(uint id,LiveAvatarSpeechPlayback playback,EnbAvatarPortraitAsset pose,Matrix4x4 world,long elapsed)
    {
        foreach(var cue in playback.AnimationSounds)
        {
            Vector3 position=Vector3.Transform(pose.AnimationSoundPosition!(cue.Bone),world);
            QueueStationAnimationSound(id,cue.Sound,position,elapsed);
        }
    }
    private void QueueStationAnimationSound(uint id,EnbW3dAnimationSoundCue cue,Vector3 position,long elapsed)
    {
            var root=new EnbSceneEffectSnapshot(id,0,id,0,0,unchecked((uint)elapsed),0,"",0,
                false,1,Vector3.Zero,0,0,false,Vector3.Zero,1,EnbSceneEffectGeometryKind.Point,position,0,Vector3.Zero,0,0,null);
            var definition=new EnbEffectDefinition(0,"SoundEffect",EnbEffectClassKind.SoundEffect,
                null,null,null,null,false,null,null,null,null,false,false,false,false,
                false,false,false,false,false,false,cue.Sound,
                new Dictionary<string,string>{{"SFX2D",cue.Flat?"TRUE":"FALSE"}});
            // 440280/4402B0 ->43E530 flags2 releases the voice at the captured
            // cue bone. Replacement/movement/actor removal cannot stop it.
            _stationAnimationSoundRequests.Add(new(++_stationAnimationSoundSequence,definition,root,null,
                EnbCurrentRetailEffectLinkKind.Root,true,unchecked((uint)elapsed))
                {ClientAnimation=true,ReleasedWorldPosition=position});
    }
}
