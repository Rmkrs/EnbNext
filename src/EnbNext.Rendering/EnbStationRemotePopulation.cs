using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.Presentation;
using EnbNext.Presentation;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>63E5C0 ingress, 63CF30/63E770 deferred removal and 63AAF0 hash walk.</summary>
public sealed class EnbStationRemotePopulation
{
    public sealed class Actor(EnbSceneStationAvatarSnapshot pose)
    {
        internal ulong AttachmentOrdinal;
        public EnbSceneStationAvatarSnapshot Pose=pose;
        public EnbStationRemoteMotion Motion=new();
        public EnbRetailAvatarTeleportClock Teleport=new();
        public int RequestedTeleport;
        public bool Departing;
        public float DepartureSeconds;
        public bool ReplaceCachedActor;
        public EnbStationGuildLabel Guild=new(null);
        public EnbStationSpeakerLabel Speaker = new(pose.Avatar?.FirstName ?? "",
            pose.Avatar is {} avatar ? EnbStationNamePresentation.FullName(avatar) : "");
    }
    private readonly Dictionary<uint,Actor> actors=[];
    private readonly HashSet<uint> absentDepartures=[];
    private Net7GalaxyMapOrder order=Net7GalaxyMapOrder.Empty;
    private Guid stream;
    private ulong sequence;
    private ulong attachmentOrdinal;
    public IReadOnlyList<Actor> Ordered=>order.Keys.Select(id=>actors[id]).ToArray();
    //63CA50 prepends a widget, then reactivates the collection (687D30/6871D0).
    //661700 preserves equal-priority insertion;65F780 selects the first hit.
    //This differs from63AAF0's hash-ordered actor updates.
    public IReadOnlyList<Actor> InteractionOrdered=>actors.Values.OrderByDescending(a=>a.AttachmentOrdinal).ToArray();
    public uint BucketCount=>order.BucketCount;
    public void Reset()
    {actors.Clear();absentDepartures.Clear();order=Net7GalaxyMapOrder.Empty;stream=default;sequence=0;attachmentOrdinal=0;}
    public void ChangeRoom(IEnbSceneFrame? previous)
    {
        actors.Clear();absentDepartures.Clear();order=order.Retain([]);
        if(previous is not null)
        {stream=previous.StationAvatarStreamId;sequence=previous.StationAvatarEvents.LastOrDefault()?.Sequence??sequence;}
    }
    private void Remove(uint id)
    {actors.Remove(id);order=order.Retain(order.Keys.Where(k=>k!=id));}
    private void Attach(EnbSceneStationAvatarSnapshot pose,double time,Net7StationGuildState? guild=null)
    {
        var actor=new Actor(pose){Guild=new(guild),AttachmentOrdinal=++attachmentOrdinal};actors[pose.AvatarId]=actor;
        actor.Motion.Receive(pose.Position,pose.Orientation,(pose.ActionFlag&1)!=0,time,pose.MotionRevision);
        if((pose.ActionFlag&0x40)!=0)
        {actor.Teleport.StartStation(false);actor.RequestedTeleport=2;}
        order=order.Retain(order.Keys.Append(pose.AvatarId));
    }
    public void Receive(IEnbSceneFrame scene,uint room,double time,Action<Actor>? attached=null,Func<byte,EnbNext.Assets.EnbRgb>? chatColor=null,
        Action<Actor>? teleportRequested=null)
    {
        if(stream!=scene.StationAvatarStreamId)
        {Reset();stream=scene.StationAvatarStreamId;}
        if(scene.StationAvatarEvents.Count==0)
        {
            // Saved/offline scenes predate the event stream. Production 009E
            // always appends an event, even for unchanged coordinates.
            var present=scene.StationAvatars.Where(a=>a.RoomType==room && a.AvatarId!=scene.LocalAvatarObjectId).ToArray();
            foreach(uint id in actors.Keys.Except(present.Select(a=>a.AvatarId)).ToArray())Remove(id);
            foreach(var pose in present)
            {
                if(!actors.TryGetValue(pose.AvatarId,out var actor)){Attach(pose,time,EnbStationGuildLabel.FromScene(scene,pose.AvatarId));attached?.Invoke(actors[pose.AvatarId]);if(actors[pose.AvatarId].RequestedTeleport!=0)teleportRequested?.Invoke(actors[pose.AvatarId]);continue;}
                actor.Pose=pose;
                if(!pose.MotionHistory.IsDefaultOrEmpty)
                    foreach(var sample in pose.MotionHistory)actor.Motion.Receive(sample.Position,sample.Orientation,(sample.ActionFlag&1)!=0,time,sample.Revision);
                else actor.Motion.Receive(pose.Position,pose.Orientation,(pose.ActionFlag&1)!=0,time,pose.MotionRevision);
            }
            return;
        }
        foreach(var input in scene.StationAvatarEvents.Where(e=>e.Sequence>sequence))
        {
            sequence=input.Sequence;
            if(input.Speaker is {} speaker)
            {
                var actor=Ordered.FirstOrDefault(a=>string.Equals(a.Speaker.FirstName,speaker.Identity,StringComparison.OrdinalIgnoreCase));
                actor?.Speaker.Speak(speaker.Color is {} captured?new(captured.X,captured.Y,captured.Z):
                    chatColor?.Invoke(speaker.Type)??EnbNext.Assets.EnbRetailChatColors.Default(speaker.Type));
                continue;
            }
            if(input.Pose is {} received)
            {
                if(received.RoomType!=room || received.AvatarId==scene.LocalAvatarObjectId)continue;
                if(absentDepartures.Contains(received.AvatarId))continue;
                var pose=new EnbSceneStationAvatarSnapshot(received.AvatarId,received.RoomType,received.Orientation,received.Position,received.ActionFlag)
                {Avatar=EnbPresentationSnapshotBuilder.BuildAvatar(input.Avatar),MotionRevision=received.MotionRevision};
                if(!actors.TryGetValue(received.AvatarId,out var actor))
                {if(input.Avatar is not null){Attach(pose,time,input.Guild);attached?.Invoke(actors[pose.AvatarId]);if(actors[pose.AvatarId].RequestedTeleport!=0)teleportRequested?.Invoke(actors[pose.AvatarId]);}continue;}
                if(actor.Departing)continue; //63E5C0 checks the removal list first.
                actor.Pose=pose with {Avatar=actor.Pose.Avatar};
                actor.Motion.Receive(pose.Position,pose.Orientation,(pose.ActionFlag&1)!=0,time,pose.MotionRevision);
            }
            else if(input.Departure is {} departure)
            {
                if(!actors.TryGetValue(departure.AvatarId,out var actor)){absentDepartures.Add(departure.AvatarId);continue;}
                if(actor.Departing)continue;
                actor.Departing=true;actor.DepartureSeconds=0;
                if(room==0 && departure.NewRoom==uint.MaxValue)
                {actor.Teleport.StartStation(true);actor.RequestedTeleport=3;teleportRequested?.Invoke(actor);}
            }
            else if(input.ReplacedAvatarId is uint id && actors.TryGetValue(id,out var replaced))
            {
                //6437A0 removes/reinserts the room record at its current transform.
                var transform=replaced.Motion.Transform;
                var pose=replaced.Pose with {Position=transform.Translation,Orientation=MathF.Atan2(transform.M12,transform.M11),
                    Avatar=EnbPresentationSnapshotBuilder.BuildAvatar(input.Avatar)};
                bool departing=replaced.Departing;float age=replaced.DepartureSeconds;
                Remove(id);Attach(pose,time,input.Guild);
                actors[id].Departing=departing;actors[id].DepartureSeconds=age;
                actors[id].ReplaceCachedActor=true;
                attached?.Invoke(actors[id]);
                if(actors[id].RequestedTeleport!=0)teleportRequested?.Invoke(actors[id]);
            }
        }
    }
    public void RemoveCompleted(uint milliseconds)
    {
        absentDepartures.Clear();
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float seconds=(float)((R)milliseconds*(R).001f);
        foreach(var actor in actors.Values.Where(a=>a.Departing).ToArray())
        {
            actor.DepartureSeconds=(float)((R)actor.DepartureSeconds+(R)seconds);
            if(actor.DepartureSeconds>10 || (actor.Motion.Remaining==0 && !actor.Teleport.Active))Remove(actor.Pose.AvatarId);
        }
    }
}
