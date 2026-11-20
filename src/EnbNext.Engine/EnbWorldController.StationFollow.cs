using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

// Multibox (no retail counterpart): an alt walks after its
// leader inside a station and can arrive at the leader's spawn point.
internal sealed partial class EnbWorldController
{
    /// <summary>The leader to walk after, or null. Set by the multibox host each frame.</summary>
    public EnbStationFollowTarget? StationFollowTarget { get; set; }
    /// <summary>How far behind the leader a follower stops (an option, 1..30).</summary>
    public float StationFollowDistance { get; set; } = EnbStationFollow.FollowDistance;
    /// <summary>Chooses the arrival spawn index (0..max) for this starbase, or null for retail's random draw.</summary>
    public Func<uint,int,int?>? StationSharedSpawn { get; set; }
    /// <summary>This avatar's place for followers once it stands in the station.</summary>
    public EnbStationFollowTarget? StationFollowSource =>
        _stationKey is {} key && _stationArrivalPublished && !_stationRoomChanging && _w3dWorld.StationAvatarPose is {} pose
            ? new(key.Id,_stationRoom,pose.Position,pose.Run) : null;
    /// <summary>The arrival spawn this avatar received in this station: (starbase, index, max).</summary>
    public (uint StarbaseId,int Index,int Max)? StationArrivalSpawn =>
        _stationKey is {} key && _w3dWorld.StationArrivalSpawn is {} spawn ? (key.Id,spawn.Index,spawn.Max) : null;

    private long _stationManualUntil;
    private bool _stationFollowing;
    private const long ManualFollowPause = 2000;

    // Player input always wins: any thrust/turn pauses following; the idle clear of an
    // unfocused window does not stop an active follow.
    private bool AcceptStationInput(int thrust,float turn)
    {
        if(thrust!=0 || turn!=0){_stationManualUntil=Environment.TickCount64+ManualFollowPause;_stationFollowing=false;_stationWalk=null;_stationWalking=false;return true;}
        return !_stationFollowing && !_stationWalking;
    }

    private (Func<uint,int,int?>? Source,uint Starbase) _stationSpawnChoice;
    private void UpdateStationFollow(EnbSceneStationDefinition? definition)
    {
        var choice=(StationSharedSpawn,_stationKey?.Id??0);
        if(choice!=_stationSpawnChoice)
        {
            _stationSpawnChoice=choice;
            _w3dWorld.StationArrivalSpawnChoice=choice.Item1 is {} shared && choice.Item2!=0 ? max=>shared(choice.Item2,max) : null;
        }
        if(_stationWalk is not null){_stationFollowing=false;return;} // A Local walk leads while it lasts.
        if(StationFollowTarget is not {} target || _stationKey is not {} key || target.StarbaseId!=key.Id ||
            definition is null || _stationLoadedRoom is not {} room || !CanControlStation ||
            Environment.TickCount64<_stationManualUntil || _w3dWorld.StationAvatarPose is not {} pose)
        {
            if(_stationFollowing){_stationFollowing=false;_w3dWorld.StopStationMovement();}
            return;
        }
        System.Numerics.Vector3 goal;float stop;
        if(target.Room==_stationRoom){goal=target.Position;stop=StationFollowDistance;}
        else if(EnbStationFollow.NextRoom(_stationRoom,target.Room,definition) is uint next &&
            EnbStationRooms.ExitPosition(room,next) is {} exit){goal=exit;stop=0;}
        else
        {
            if(_stationFollowing){_stationFollowing=false;_w3dWorld.StopStationMovement();}
            return;
        }
        var steer=EnbStationFollow.Steer(pose.Position,pose.Heading,goal,stop,target.Run);
        if(steer.Arrived)
        {
            if(_stationFollowing){_stationFollowing=false;_w3dWorld.StopStationMovement();}
            return;
        }
        _stationFollowing=true;
        _w3dWorld.SetStationMovement(steer.Thrust,steer.Turn,steer.Run);
    }
}
