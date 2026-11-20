using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly EnbRetailStationCameraGeometry _stationCameraGeometry=new();
    private EnbCurrentRetailStationInterior? _stationCameraRoom;
    private Vector3? _stationCameraPrevious;
    private readonly Dictionary<uint,Vector3> _stationCameraRetainedRooms=[];
    private Matrix4x4? _stationCameraFrameTarget;
    private string? _stationCameraCollisionDiagnostic;

    private void PrepareStationCameraFollow(EnbCurrentRetailStationInterior room)
    {
        _stationCameraFrameTarget=null;
        if(!ReferenceEquals(_stationCameraRoom,room))
        {_stationCameraRoom=room;_stationCameraPrevious=_stationCameraRetainedRooms.TryGetValue(room.RoomType,out var retained)?retained:null;}
        if(_stationAvatarAsset is not {} avatar || !StationArrivalComplete || _stationDepartureStarted is not null)return;
        var desired=StationAvatarCameraTransform(room,avatar);
        _stationCameraFrameTarget=desired;
        //0063B310's +1FC suppresses the sweep during camera transitions;
        //the retained follow state is independent of the displayed NPC/terminal camera.
        if(!StationThirdPersonMode || StationConversationCameraMoving || StationTerminalCameraMoving ||
            room.PlayDocking && _stationElapsedMilliseconds<room.ArrivalDurationMilliseconds+1000)return;
        _stationCameraGeometry.Clear();_stationCameraCollisionDiagnostic=null;
        Append(room.Resource,Matrix4x4.Identity,room.Arrival is {} arrival?new(arrival.Name,room.ArrivalFrame(_stationElapsedMilliseconds)):null);
        //6333E0 attaches ExtraBones through98E0D0 -> virtual84, bone zero.
        //634140 scales its hangar ship-extents leaf by.6 before use.
        if(room.SupplementalResource is {} extra)Append(extra,Matrix4x4.Identity,scaleShipProxy:room.RoomType==0);
        foreach(var door in room.Doorways)
            Append(door.Resource,door.Transform,door.Animation is {} animation && _stationDoorFrames.TryGetValue(door.Pivot,out var state)?new(animation.Name,state.Frame):null);
        foreach(var fixture in StationFixturePlacements(room))
        {
            try
            {
                var npc=_stationScene!.StationDefinition!.RoomNpcs(room.RoomType).FirstOrDefault(n=>fixture.Key==$"AVATAR{n.Slot:00}/booth/{n.Role}");
                var sample=npc is not null && _stationBooths.TryGetValue((room.RoomType,npc.Slot),out var booth)?booth.Sample(ReadMapperClock()):null;
                Append(StationFixtureAsset(fixture.Name),fixture.World,sample);
            }
            catch(InvalidDataException error){_stationCameraCollisionDiagnostic=error.Message;}
        }
        foreach(var machine in room.Machinery)Append(machine.Resource,Matrix4x4.Identity,machine.Sample(ReadMapperClock()));
        foreach(var person in _stationPeople.Where(p=>p.Key.Npc && p.Value.Ambient?.Prop is not null))
            Append(person.Value.Ambient!.Prop!,person.Value.World,person.Value.Ambient.Sample(ReadMapperClock()));
        var position=(_stationMovement?.Transform??room.AvatarSpawn??room.PivotTransform(room.SpawnPivot)).Translation;
        var origin=EnbRetailStationCameraFollow.SweepOrigin(position,avatar.HeadAnchor.Z-avatar.SkeletonReference.Translation.Z);
        float fraction=EnbShipCameraCollision.SweepStationRoom(origin,desired.Translation-origin,_stationCameraGeometry.Frame);
        //6333E0 leaves the first retained matrix uninitialized. Do not reproduce
        //allocator contents: seed its translation from the already working camera target.
        var result=EnbRetailStationCameraFollow.Apply(desired,origin,_stationCameraPrevious??desired.Translation,fraction<1,fraction);
        _stationCameraPrevious=result.Translation;_stationCameraFrameTarget=result;
        _stationCameraRetainedRooms[room.RoomType]=result.Translation;
        void Append(EnbW3dRenderResource resource,Matrix4x4 world,EnbW3dAnimationSample? sample=null,bool scaleShipProxy=false)
        {
            try{_stationCameraGeometry.Append(resource,world,sample,scaleShipProxy);}
            catch(InvalidDataException error){_stationCameraCollisionDiagnostic=resource.LogicalFileName+": "+error.Message;}
        }
    }
}
