using EnbNext.Presentation;

namespace EnbNext.App;

// Multibox: station follow and shared arrival spawn.
internal sealed partial class SdlApplication
{
    /// <summary>The arrival spawn draw of the last group dock this client led or followed.</summary>
    private (double Draw,long At) stationSpawnDraw;
    private static readonly long StationSpawnDrawLifetime=120_000;
    private Func<uint,int,int?>? stationSharedSpawn;
    private readonly object stationSpawnSync=new();
    private volatile MultiboxState? stationSelf;
    private void SetStationSpawnDraw(double draw){lock(stationSpawnSync)stationSpawnDraw=(draw,Environment.TickCount64);}

    private (MultiboxStationPose? Pose,MultiboxStationSpawn? Spawn) StationMultiboxPlace()
    {
        if(worldScreen?.Owner is not {} world)return (null,null);
        var pose=world.StationFollowSource is {} p?new MultiboxStationPose(p.StarbaseId,p.Room,p.Position.X,p.Position.Y,p.Position.Z,p.Run):null;
        var spawn=world.StationArrivalSpawn is {} s?new MultiboxStationSpawn(s.StarbaseId,s.Index,s.Max):null;
        return (pose,spawn);
    }

    private MultiboxState? StationLeader(MultiboxState self)
        => multibox?.Peers.FirstOrDefault(p=>p.Leader&&p.Instance!=self.Instance&&p.Server==self.Server&&self.GroupMembers.Contains(p.Avatar));

    private void ApplyStationMultibox(MultiboxState self)
    {
        stationSelf=self;
        if(worldScreen?.Owner is not {} world)return;
        var leader=self.Leader?null:StationLeader(self);
        world.StationFollowTarget=multiboxOptions.FollowLeaderStation&&leader?.StationPose is {} pose&&self.StationPose?.Starbase==pose.Starbase
            ? new EnbStationFollowTarget(pose.Starbase,pose.Room,new(pose.X,pose.Y,pose.Z),pose.Run) : null;
        world.StationFollowDistance=multiboxOptions.StationFollowDistance;
        world.StationSharedSpawn=stationSharedSpawn??=SharedStationSpawn;
    }

    /// <summary>A dock this client led or followed fixes the draw; otherwise an opted-in alt
    /// takes the spawn its leader already arrived at in the same starbase.</summary>
    private int? SharedStationSpawn(uint starbase,int max)
    {
        lock(stationSpawnSync)
            if(stationSpawnDraw.Draw is >0 and <1 && Environment.TickCount64-stationSpawnDraw.At<StationSpawnDrawLifetime)
            {
                double draw=stationSpawnDraw.Draw;stationSpawnDraw=default;
                return Math.Min(max,(int)(draw*(max+1)));
            }
        if(!multiboxOptions.Enabled||!multiboxOptions.SharedStationSpawn||stationSelf is not {} self||self.Leader)return null;
        return StationLeader(self)?.StationSpawn is {} spawn&&spawn.Starbase==starbase&&spawn.Max==max?spawn.Index:null;
    }

    private void StopStationMultibox()
    {
        stationSelf=null;
        if(worldScreen?.Owner is {} world){world.StationFollowTarget=null;world.StationSharedSpawn=null;}
    }
}
