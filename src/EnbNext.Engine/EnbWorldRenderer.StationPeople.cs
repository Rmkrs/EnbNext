using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    internal sealed record StationNameLabel(uint Slot,string Name,double X,double Y,double Depth,double ProjectedHeight=0,double CameraDistance=25,bool Occluded=false,bool Player=false)
    {public EnbNext.Assets.EnbRgb? Color {get;init;} public bool Guild {get;init;} public bool Hidden {get;init;} public bool Flash {get;init;}}
    private StationNameLabel[] _stationNameLabels=[];
    public bool StationLabelInteractionOpen {get;set;}
    public bool StationLabelServiceOpen {get;set;}
    public bool StationShowSelfName {get;set;}
    public System.Collections.Immutable.ImmutableArray<EnbNext.Assets.EnbRgb> StationChatColors {get;set;} = EnbNext.Assets.EnbRetailChatColors.Defaults;
    private EnbStationGuildLabel? _stationSelfGuild;
    private StationNameLabel? _stationSelfNameLabel,_stationSelfGuildLabel;
    private sealed record NpcPick(EnbSceneStationNpc Npc,EnbRetailMeshPick[] Meshes,bool InRange,Vector3 Origin);
    private NpcPick[] _stationNpcPicks=[];
    public EnbSceneStationNpc? PickStationNpc(double x,double y)
        =>PickStationInteraction(x,y) is {InRange:true} hit?hit.Npc:null;
    public IReadOnlyList<StationNameLabel> StationNameLabels=>Volatile.Read(ref _stationNameLabels);
    private string StationNameDiagnostics => StationVisibleNameCount==0 && _stationPeople.Count>0
        ? $" ({StationNameLabels.Count} projected, {_stationScene?.StationDefinition?.Npcs.Count(n=>n.RoomType==_stationInterior?.RoomType && string.IsNullOrWhiteSpace(n.DisplayName)) ?? 0} unnamed)" : "";
    private sealed class StationPerson(EnbSceneAvatarSnapshot identity)
    {
        public object? SpeechPage;
        public StationNameLabel? NameLabel,GuildLabel;
        public LiveAvatarSpeechPlayback? Speech;
        public bool Attached;
        public long SpeechStarted;


        public EnbSceneAvatarSnapshot Identity=identity;
        public EnbAvatarPortraitAsset? Asset;
        public string? Failure;
        public string? PickFailure;
        public float LabelHeight;
        public EnbStationCollision? Collision;
        public EnbCurrentRetailStationInterior? CollisionRoom;
        public EnbStationRemoteMotion Motion=new();
        public bool TeleportHidden;
        public readonly EnbRetailAvatarTeleportClock Teleport=new();
        public string? TeleportDiagnostic;
        public StationAmbientActor? Ambient;
        public bool AmbientAttempted;
        public Matrix4x4 World=Matrix4x4.Identity;
        public Matrix4x4? InteractionTransform;
        public ulong ConversationGeneration;

        public string AnimationTag="breathe1";

        public long AnimationStarted;
        public EnbAvatarPortraitAsset? LastPose;
        public List<(LiveLanePlan Lane,LiveGpuResource Gpu)> Draws=[];
    }
    private readonly Dictionary<(bool Npc,uint Id),StationPerson> _stationPeople=[];
    private readonly EnbStationRemotePopulation _stationPopulation=new();
    // 63C890/632E50 detach room records; 74C7B0 returns the retained avatar.
    // 69D030 switches rooms without the 514010 client-mode cache rebuild.
    private readonly Dictionary<uint,StationPerson> _stationRemotePeople=[];
    internal void AcquireStationGroupAvatar(uint id,EnbSceneAvatarSnapshot identity)
    {
        if(_stationScene is null || id==_stationScene.LocalAvatarObjectId || _retailAssetsAccessor() is not {} assets)return;
        if(!_stationRemotePeople.TryGetValue(id,out var person))_stationRemotePeople[id]=person=new(identity);
        if(person.Speech is not null || person.Failure is not null)return;
        try
        {
            person.Speech=new(assets,identity,portraitIdle:true);
            person.Speech.StartIdle(_stationElapsedMilliseconds*.001);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {person.Failure=ex.Message;}
    }
    internal EnbAvatarPortraitAsset? StationRemotePortrait(uint id,EnbSceneAvatarSnapshot identity)
        => _stationInterior is not null && _stationRemotePeople.TryGetValue(id,out var person) &&
            SamePortrait(person.Identity,identity)?person.LastPose is {} pose?pose with {PresentationHidden=person.Teleport.Hidden}:null:null;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Room,uint Slot),(EnbSceneAvatarSnapshot Identity,EnbAvatarPortraitAsset Asset,CancellationToken Token)> _stationPreparedPeople=new();
    public void PrepareStationPeople(EnbSceneStationDefinition definition,uint room,EnbNext.Assets.EnbRetailAssetBundle assets,CancellationToken token)
    {
        foreach(var npc in definition.RoomNpcs(room).Where(n=>n.Variant!=0 || n.Role!=7))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var asset=EnbAvatarPortraitAsset.Load(assets,npc.Avatar);
                token.ThrowIfCancellationRequested();
                _stationPreparedPeople[(room,npc.Slot)]=(npc.Avatar,asset,token);
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            { /* Active population loading owns the existing isolated diagnostic. */ }
        }
    }
    private string StationPeopleWarnings
    {
        get
        {
            var warnings=_stationPeople.Select(p=>p.Value.Failure is {} failure
                ? $"{(p.Key.Npc ? "NPC" : "player")} {p.Key.Id} omitted: {failure}"
                : p.Value.PickFailure is {} pickFailure ? $"actor {p.Key.Id} picking unavailable: {pickFailure}"
                : p.Value.Ambient?.Diagnostic is {} ambientDiagnostic ? $"NPC {p.Key.Id}: {ambientDiagnostic}"
                : p.Value.Speech?.Diagnostic is {} speechDiagnostic ? $"actor {p.Key.Id}: {speechDiagnostic}"
                : p.Value.TeleportDiagnostic is {} teleportDiagnostic ? $"actor {p.Key.Id} teleport: {teleportDiagnostic}"
                : p.Value.Asset is {MissingAccessories.IsEmpty:false} asset
                    ? $"accessory absent: {string.Join(", ",asset.MissingAccessories)}" : null)
                .Where(s=>s is not null).Distinct().ToArray();
            return warnings.Length==0 ? "" : " · "+string.Join("; ",warnings.Take(3))+(warnings.Length>3 ? $" (+{warnings.Length-3} more)" : "");
        }
    }
    private void DestroyStationPeople(EnbGlApi? gl,bool preserveScene=false,bool changingRoom=false)
    {
        DestroyStationRemoteBeams(gl,preserveScene);
        foreach(var person in _stationPeople.Values)ReleaseStationDraws(gl,person.Draws,preserveScene);
        if(!preserveScene)
        {
            _stationSelfGuild=null;_stationSelfNameLabel=_stationSelfGuildLabel=null;
            foreach(var person in _stationPeople.Where(p=>!p.Key.Npc).Select(p=>p.Value))
            {
                person.CollisionRoom=null;person.Collision=null;person.Motion=new();
                person.Teleport.Stop();
                if(changingRoom)person.Speech?.SetLocomotion(null);
            }
            _stationPeople.Clear();
        }
        if(!preserveScene && !changingRoom){_stationAmbientPeople.Clear();_stationRemotePeople.Clear();_stationEmotes.Clear();_stationPeoplePreviousClock=null;}
        DestroyStationNames(gl,preserveScene);
        Volatile.Write(ref _stationNameLabels,[]);
        Volatile.Write(ref _stationNpcPicks,[]);
        Volatile.Write(ref _stationPlayerPicks,[]);
    }
    private void PrepareStationNpcPopulation(EnbGlApi gl,EnbCurrentRetailStationInterior room,long elapsed)
    {
        if(_stationScene?.StationDefinition is not {} definition)return;
        // 634140 constructs the local avatar, then all admitted NPC slots,
        // before63AAF0 advances any actor. Asset preloading alone is not
        // construction: StartIdle consumes the shared retail random stream.
        foreach(var npc in definition.RoomNpcs(room.RoomType).Where(n=>n.Variant!=0 || n.Role!=7))
        {
            if(npc.Variant==0 && !room.Resource.Hierarchies.Single().Pivots.Any(p=>
                p.Name.Equals($"AVATAR{npc.Slot:00}",StringComparison.OrdinalIgnoreCase)))continue;
            var key=(true,npc.Slot);
            if(_stationPeople.TryGetValue(key,out var person) && !SamePortrait(person.Identity,npc.Avatar))
            {ReleaseStationDraws(gl,person.Draws,false);_stationPeople.Remove(key);person=null;}
            if(person is null)
            {
                person=npc.Variant!=0 && _stationAmbientPeople.TryGetValue((room.RoomType,npc.Slot),out var retained) &&
                    SamePortrait(retained.Identity,npc.Avatar)?retained:new(npc.Avatar);
                _stationPeople[key]=person;
                if(npc.Variant!=0)_stationAmbientPeople[(room.RoomType,npc.Slot)]=person;
            }
            if(person.Failure is not null)continue;
            try{InitializeStationPerson(person,true,npc.Slot,room,elapsed,npc.Variant!=0?npc:null);}
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {person.Failure=ex.Message;}
        }
    }
    private bool InitializeStationPerson(StationPerson person,bool npc,uint id,EnbCurrentRetailStationInterior room,long elapsed,EnbSceneStationNpc? ambientNpc)
    {
        if(ambientNpc is not null)
        {
            if(!person.AmbientAttempted)
            {
                person.AmbientAttempted=true;
                if(EnbStationAmbientNpcDefinition.Load(_retailAssetsAccessor()!,_stationScene!.StationDefinition!.DefinitionValue,ambientNpc,room) is {} ambient)
                    person.Ambient=new(ambient,ambientNpc.Variant);
            }
            if(person.Ambient is null)return false;
        }
        if(person.Asset is null)
        {
            person.Asset=npc && _stationPreparedPeople.TryGetValue((room.RoomType,id),out var prepared) &&
                !prepared.Token.IsCancellationRequested && SamePortrait(prepared.Identity,person.Identity)
                ? prepared.Asset : EnbAvatarPortraitAsset.Load(_retailAssetsAccessor()!,person.Identity);
            person.LabelHeight=person.Asset.Parts.SelectMany(p=>p.Geometry.Positions).Max(p=>p.Z);
            if(person.Speech is null)
            {
                person.Speech=new(_retailAssetsAccessor()!,person.Identity,portraitIdle:true);
                person.Speech.StartIdle(elapsed*.001);
            }
        }
        return true;
    }
    private void PrepareStationRemotePopulation(EnbGlApi gl,EnbCurrentRetailStationInterior room,long elapsed)
    {
        if(_stationScene is not {} scene)return;
        // 63CA50 ->74C7B0 ->77E3F0 initializes the cached actor during
        // admission, before 63AAF0's later hash-ordered update walk.
        _stationPopulation.Receive(scene,room.RoomType,elapsed,actor=>
        {
            if(actor.Pose.Avatar is not {} identity)return;
            uint id=actor.Pose.AvatarId;
            if(actor.ReplaceCachedActor || (_stationRemotePeople.TryGetValue(id,out var old) && !SamePortrait(old.Identity,identity)))
            {
                if(_stationPeople.Remove((false,id),out var replaced))ReleaseStationDraws(gl,replaced.Draws,false);
                _stationRemotePeople.Remove(id);actor.ReplaceCachedActor=false;
            }
            if(!_stationRemotePeople.TryGetValue(id,out var person))_stationRemotePeople[id]=person=new(identity);
            person.NameLabel=person.GuildLabel=null;
            if(person.Failure is not null || person.Asset is not null)return;
            try
            {
                person.Asset=EnbAvatarPortraitAsset.Load(_retailAssetsAccessor()!,identity);
                person.LabelHeight=person.Asset.Parts.SelectMany(p=>p.Geometry.Positions).Max(p=>p.Z);
                if(person.Speech is null)
                {
                    person.Speech=new(_retailAssetsAccessor()!,identity,portraitIdle:true);
                    person.Speech.StartIdle(elapsed*.001);
                }
                person.Attached=true;
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {person.Failure=ex.Message;}
        }, type=>type<StationChatColors.Length?StationChatColors[type]:EnbNext.Assets.EnbRetailChatColors.Default(type),actor=>
        {
            //63CA50/63CF30 create each teleport at ingress, before63E770's
            //removal pass. A later event must not overwrite an uncreated effect.
            if(!_stationRemotePeople.TryGetValue(actor.Pose.AvatarId,out var person) || person.Asset is null)return;
            if(!ReferenceEquals(person.CollisionRoom,room))
            {person.CollisionRoom=room;person.Collision=new(room,_retailAssetsAccessor()!,person.Asset.HeadAnchor.Z-person.Asset.SkeletonReference.Translation.Z);}
            if(!ReferenceEquals(actor.Teleport,person.Teleport))
            {person.Teleport.StartStation(actor.RequestedTeleport==3);actor.Teleport=person.Teleport;}
            BeginStationRemoteTeleport(actor,person,ReadMapperClock(),elapsed);
        });
    }
    private void RenderStationPeople(EnbGlApi gl,int fb,int width,int height,EnbCurrentRetailStationInterior room,long elapsed)
    {
        if(_stationScene is not {} scene)return;
        uint now=ReadMapperClock();uint delta=_stationPeoplePreviousClock is {} previous?unchecked(now-previous):0;
        _stationPeoplePreviousClock=now;
        _stationPopulation.RemoveCompleted(delta);
        HashSet<(bool,uint)> seen=[];
        void Draw(bool npc,uint id,EnbSceneAvatarSnapshot identity,Matrix4x4 world,uint actionFlag=0,EnbSceneStationNpc? ambientNpc=null,EnbSceneStationAvatarSnapshot? player=null,EnbStationRemotePopulation.Actor? roomActor=null)
        {
            var key=(npc,id); seen.Add(key);
            if(roomActor?.ReplaceCachedActor==true)
            {
                if(_stationPeople.Remove(key,out var replaced))ReleaseStationDraws(gl,replaced.Draws,false);
                _stationRemotePeople.Remove(id);roomActor.ReplaceCachedActor=false;
            }
            if(_stationPeople.TryGetValue(key,out var person) && !SamePortrait(person.Identity,identity))
            {foreach(var d in person.Draws)d.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);_stationPeople.Remove(key);person=null;}
            if(person is null)
            {
                if(ambientNpc is not null && _stationAmbientPeople.TryGetValue((room.RoomType,id),out var retained) && SamePortrait(retained.Identity,identity))person=retained;
                else if(!npc && _stationRemotePeople.TryGetValue(id,out var remote) && SamePortrait(remote.Identity,identity))person=remote;
                else person=new(identity);
                _stationPeople[key]=person;
                if(ambientNpc is not null)_stationAmbientPeople[(room.RoomType,id)]=person;
                if(!npc)_stationRemotePeople[id]=person;
            }
            // Population presentation must not prevent the room's first frame. Cache
            // failed appearances until they change or leave, including animation failures.
            if(person.Failure is not null)return;
            try
            {
                if(!InitializeStationPerson(person,npc,id,room,elapsed,ambientNpc))return;
                person.Attached=true; // 63CA50 ->77E3D0(1), retained on detach.
                if(!npc)
                {
                    if(!ReferenceEquals(person.CollisionRoom,room))
                    {person.CollisionRoom=room;person.Collision=new(room,_retailAssetsAccessor()!,person.Asset!.HeadAnchor.Z-person.Asset.SkeletonReference.Translation.Z);}
                    person.Motion=roomActor!.Motion;
                    if(!ReferenceEquals(roomActor.Teleport,person.Teleport))
                    {
                        if(roomActor.RequestedTeleport!=0)person.Teleport.StartStation(roomActor.RequestedTeleport==3);
                        roomActor.Teleport=person.Teleport;
                    }
                    roomActor.Teleport.Tick(delta);
                    person.TeleportHidden=roomActor.Teleport.Hidden;
                    person.Motion.Advance(elapsed,person.Speech!.Idle.Posture==0);
                    world=person.Motion.Transform;
                    world.Translation=person.Collision!.Move(world.Translation,Vector3.Zero);
                }
                if(person.Ambient is not null)world=AdvanceStationAmbient(person,id,delta,elapsed);
                else if(npc)world=StationConversationNpcTransform(id,world,person);
                person.World=world;
                var tree=npc && _stationConversationOwner?.Slot==id ? StationDialogueSpeechPage : null;
                if(!ReferenceEquals(person.SpeechPage,tree))
                {
                    person.SpeechPage=tree;
                    // 00638DA0 releases interaction ownership without resetting the
                    // ordinary NPC actor at +4. Its current speech finishes naturally.
                    if(tree is {Text.Length:>0})
                    {
                        person.SpeechStarted=elapsed;
                        person.Speech??=new(_retailAssetsAccessor()!,identity);
                        person.Speech.Replace(tree.Text,elapsed*.001,person.LastPose);
                    }
                }
                if(!npc)
                {
                    ReceiveStationCommands(id,person.Speech!,elapsed);
                    if(person.Motion.RequestIdle)person.Speech!.SetLocomotion(null);
                    if(person.Motion.RequestWalk)person.Speech!.SetLocomotion(person.Motion.AnimationTag);
                }
                var posed=person.Speech!.Evaluate(elapsed*.001);
                if(!npc && person.Speech.LocomotionSelected)person.Motion.WalkAnimationSelected();
                if(person.Ambient is {} ambientActor && person.Speech.LocomotionSelected)ambientActor.Motion.WalkAnimationSelected();
                if(person.AnimationTag!=person.Speech.AnimationTag){person.AnimationTag=person.Speech.AnimationTag??"breathe1";person.AnimationStarted=elapsed;}

                if(posed is null)return;
                person.LastPose=posed;
                CollectStationAnimationSounds(id,person.Speech,posed,world,elapsed);
                if(!person.TeleportHidden)
                    RenderStationPerson(gl,fb,width,height,posed,identity,world,StationCamera(room,width,height),elapsed,person.Draws,
                        _stationLightingState.Current.Select(world.Translation).Environment);
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                foreach(var d in person.Draws)d.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
                person.Draws.Clear();
                person.Failure=ex.Message;
            }
        }
        if(scene.StationDefinition is {} definition)
            foreach(var npc in definition.RoomNpcs(room.RoomType).Where(n=>n.Variant!=0 || n.Role!=7))
            {
                if(npc.Variant!=0){Draw(true,npc.Slot,npc.Avatar,Matrix4x4.Identity,ambientNpc:npc);continue;}
                // 00634140 attaches NPCs to the room's avatar_%02d slot.
                string name=$"AVATAR{npc.Slot:00}";
                if(room.Resource.Hierarchies.Single().Pivots.Any(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))
                    Draw(true,npc.Slot,npc.Avatar,room.PivotTransform(name));
            }
        // 63CA50/63D1F0 consume run/teleport flags, not a bit-4 visibility gate.
        // Removal is a separate 00A0 -> 63CF30 -> 63E770 path.
        foreach(var actor in _stationPopulation.Ordered)
        {
            var player=actor.Pose;
            if(player.Avatar is not null)
                Draw(false,player.AvatarId,player.Avatar,actor.Motion.Transform,player.ActionFlag,player:player,roomActor:actor);
        }
        foreach(var key in _stationPeople.Keys.Where(k=>!seen.Contains(k)).ToArray())
        {
            var person=_stationPeople[key];
            ReleaseStationDraws(gl,person.Draws,false);
            person.CollisionRoom=null;person.Collision=null;person.Motion=new();
            person.Teleport.Stop();
            _stationPeople.Remove(key);
        }
        PublishStationPlayerPicks(StationCamera(room,width,height));
        RenderStationRemoteBeams(gl,fb,width,height,room,now,delta,elapsed);
        var portraitActors=new HashSet<uint>();
        foreach(var portrait in StationPortraitsAccessor?.Invoke() ?? [])
        {
            if(portrait.PortraitStationRemoteId is not uint id || portrait.Portrait is not {} identity || !portraitActors.Add(id))continue;
            // Trade5E2BF0 ->7E3A20 uses the same74C7B0 actor.783B50's
            // +E4 frame guard permits one advance across room/PIP consumers.
            if(seen.Contains((false,id)))continue;
            if(!_stationRemotePeople.TryGetValue(id,out var person) || !SamePortrait(person.Identity,identity))
                _stationRemotePeople[id]=person=new(identity);
            if(person.Failure is not null)continue;
            try
            {
                if(person.Speech is null)
                {
                    person.Speech=new(_retailAssetsAccessor()!,identity,portraitIdle:true);
                    person.Speech.StartIdle(elapsed*.001);
                }
                person.Attached=true;
                ReceiveStationCommands(id,person.Speech,elapsed);
                if(person.Speech.Evaluate(elapsed*.001) is {} pose)
                {
                    person.LastPose=pose;
                    CollectStationAnimationSounds(id,person.Speech,pose,person.World,elapsed);
                }
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {person.Failure=ex.Message;}
        }
        foreach(var (id,person) in _stationRemotePeople)
            if(!seen.Contains((false,id)) && !portraitActors.Contains(id))person.Speech?.Pause(elapsed*.001);
        var labels=new List<StationNameLabel>();
        var picks=new List<NpcPick>();
        var camera=StationCamera(room,width,height);
        AddStationSelfLabels(labels,room,camera,width,height);
        if(scene.StationDefinition is {} names)
            foreach(var npc in names.RoomNpcs(room.RoomType))
                if(_stationPeople.TryGetValue((true,npc.Slot),out var person) && person.Failure is null && person.Asset is not null)
                {
                    // 0063F490 uses avatar head height, 0077D9F0's +1, then +1.5.
                    var anchor=person.World.Translation + new Vector3(0,0,
                        person.Asset!.HeadAnchor.Z-person.Asset.SkeletonReference.Translation.Z+2.5f);
                    var feet=person.World.Translation;
                    // Feet may be outside the viewport during a close conversation.
                    // Project without viewport clipping for label scale, while retaining
                    // the visible-head requirement for the label itself.
                    if(StationLabelServiceOpen)
                    {
                        // 63F490 ->69CC10 hides the retained name immediately;
                        // conversation/trade alone are not service-panel gates.
                        if(person.NameLabel is {} retained)labels.Add(retained with {Hidden=true});
                    }
                    else if(camera.TryProjectWorldPoint(anchor,0,out var labelPoint) &&
                        camera.Projection.TryProjectViewPoint(camera.WorldToCanonicalView(feet),out var footNdc))
                        labels.Add(person.NameLabel=new(npc.Slot,npc.DisplayName,labelPoint.PixelX/width,labelPoint.PixelY/height,labelPoint.Depth,
                            Math.Abs((.5-footNdc.Y*.5)-labelPoint.PixelY/height),
                            Vector3.Distance(_stationLastCameraPose!.Value.Position,anchor),
                            _stationCollision?.OccludesName(_stationLastCameraPose.Value.Position,anchor) == true));
                    //634140 binds the render object to LogoWidget;66C4C0 uses
                    //its geometry even when the feet/head leave the viewport.
                    bool inRange=_stationMovement is {} movement && Vector3.Distance(movement.Position,feet)<12;
                    if(!person.TeleportHidden)
                        picks.Add(new(npc,StationPersonPicks(person,camera),inRange,person.World.Translation));
                }
        var roomActors=_stationPopulation.Ordered.ToDictionary(a=>a.Pose.AvatarId);
        foreach(var entry in _stationPeople.Where(p=>!p.Key.Npc && p.Value.Failure is null && p.Value.Asset is not null))
        {
            var person=entry.Value;
            var speaker=roomActors[entry.Key.Id].Speaker;
            var normalNameColor=EnbStationNamePresentation.RemoteNameColor(scene.Hud?.Group,entry.Key.Id);
            speaker.Advance(delta,StationLabelInteractionOpen,normalNameColor);
            if(StationLabelInteractionOpen)
            {
                // 63AC60 does not run63F490 or refresh guild metadata while
                // 69CCD0 is true. Only the name receives a fade-out request.
                if(person.NameLabel is {} retainedName)labels.Add(retainedName with
                    {Occluded=true,Name=speaker.Text,Color=speaker.Color,Flash=speaker.Flash});
                if(person.GuildLabel is {} retainedGuild)labels.Add(retainedGuild);
                continue;
            }
            person.NameLabel=person.GuildLabel=null;
            var anchor=person.World.Translation+new Vector3(0,0,person.Asset!.HeadAnchor.Z-person.Asset.SkeletonReference.Translation.Z+2.5f);
            var nameColor=normalNameColor;
            var guild=roomActors[entry.Key.Id].Guild;
            guild.Refresh(EnbStationGuildLabel.FromScene(scene,entry.Key.Id),nameColor);
            double distance=Vector3.Distance(_stationLastCameraPose!.Value.Position,anchor);
            bool occluded=_stationCollision?.OccludesName(_stationLastCameraPose.Value.Position,anchor)==true;
            if(camera.TryProjectWorldPoint(anchor,0,out var point))
                labels.Add(person.NameLabel=new(entry.Key.Id,speaker.Text,
                    point.PixelX/width,point.PixelY/height,point.Depth,CameraDistance:distance,Occluded:occluded,Player:true){Color=speaker.Color,Flash=speaker.Flash});
            // 63F490 projects guild text one world unit below the name,
            // sharing the name anchor's scale and occlusion decision.
            if(camera.TryProjectWorldPoint(anchor-Vector3.UnitZ,0,out var guildPoint))
                labels.Add(person.GuildLabel=new(entry.Key.Id,guild.Text,guildPoint.PixelX/width,guildPoint.PixelY/height,guildPoint.Depth,
                    CameraDistance:distance,Occluded:occluded,Player:true){Guild=true,Color=guild.Color});
        }
        Volatile.Write(ref _stationNameLabels,labels.ToArray());
        //687D30 pushes bindings to the front;6871D0 activates that order.
        //661700 preserves equal-priority insertion;65F780 takes the first hit.
        Volatile.Write(ref _stationNpcPicks,picks.OrderByDescending(p=>p.Npc.Slot).ToArray());
    }
    private static EnbRetailMeshPick[] StationPersonPicks(StationPerson person,EnbResolvedRenderCamera camera)
    {
        if(person.PickFailure is not null)return [];
        try{return person.LastPose?.CreatePickMeshes?.Invoke(camera,person.World)??[];}
        catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // A query resource belongs to this actor, not to the room's GPU
            // readiness/input lifecycle. Preserve its draw and report the fault.
            person.PickFailure=ex.ToString();return [];
        }
    }
    private void AddStationSelfLabels(List<StationNameLabel> labels,EnbCurrentRetailStationInterior room,EnbResolvedRenderCamera camera,int width,int height)
    {
        if(_stationScene is not {} scene || _stationAvatarAsset is not {} asset || _stationAvatarIdentity is not {} identity ||
            scene.LocalAvatarObjectId is not uint id)return;
        var guildData=EnbStationGuildLabel.FromScene(scene,scene.Objects.FirstOrDefault(o=>o.IsLocalShip)?.GameId??id);
        _stationSelfGuild ??=new(guildData);
        bool hidden=!StationShowSelfName || !StationArrivalComplete || _stationDepartureStarted is not null;
        if(!hidden)
        {
            //634140 uses cached first name and blue, independently of group.
            var color=new EnbNext.Assets.EnbRgb(.33f,.33f,.99f);
            _stationSelfGuild.Refresh(guildData,color);
            if(!StationLabelServiceOpen)
            {
                var world=_stationMovement?.Transform??room.PivotTransform(room.SpawnPivot);
                var anchor=world.Translation+new Vector3(0,0,asset.HeadAnchor.Z-asset.SkeletonReference.Translation.Z+2.5f);
                double distance=Vector3.Distance(_stationLastCameraPose!.Value.Position,anchor);
                bool occluded=_stationCollision?.OccludesName(_stationLastCameraPose.Value.Position,anchor)==true;
                _stationSelfNameLabel=camera.TryProjectWorldPoint(anchor,0,out var namePoint)?
                    new(id,identity.FirstName,namePoint.PixelX/width,namePoint.PixelY/height,namePoint.Depth,
                        CameraDistance:distance,Occluded:occluded,Player:true){Color=color}:null;
                _stationSelfGuildLabel=camera.TryProjectWorldPoint(anchor-Vector3.UnitZ,0,out var guildPoint)?
                    new(id,_stationSelfGuild.Text,guildPoint.PixelX/width,guildPoint.PixelY/height,guildPoint.Depth,
                        CameraDistance:distance,Occluded:occluded,Player:true){Guild=true,Color=_stationSelfGuild.Color}:null;
            }
            else if(_stationSelfGuildLabel is {} retainedGuild)
                _stationSelfGuildLabel=retainedGuild with {Name=_stationSelfGuild.Text,Color=_stationSelfGuild.Color};
        }
        if(_stationSelfNameLabel is {} name)labels.Add(name with {Hidden=hidden||StationLabelServiceOpen});
        if(_stationSelfGuildLabel is {} guild)labels.Add(guild with {Hidden=hidden});
    }
}
