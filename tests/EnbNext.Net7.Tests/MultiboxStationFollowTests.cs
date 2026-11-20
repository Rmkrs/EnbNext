using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using EnbNext.App;
using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class MultiboxStationFollowTests
{
    [Fact]
    public void SteeringTurnsTowardTheGoalWalksWhenFacingItAndStopsBehindTheLeader()
    {
        Assert.Equal((1,0f,true,false),EnbStationFollow.Steer(Vector3.Zero,0,new(100,0,0),EnbStationFollow.FollowDistance,false));
        Assert.Equal((1,0f,false,false),EnbStationFollow.Steer(Vector3.Zero,0,new(20,0,0),EnbStationFollow.FollowDistance,false));
        Assert.True(EnbStationFollow.Steer(Vector3.Zero,0,new(20,0,0),EnbStationFollow.FollowDistance,true).Run);
        Assert.True(EnbStationFollow.Steer(Vector3.Zero,0,new(10,5,30),EnbStationFollow.FollowDistance,false).Arrived);
        // A door goal is never "reached": the room change takes over at the exit.
        Assert.False(EnbStationFollow.Steer(Vector3.Zero,0,new(.5f,0,0),0,false).Arrived);
        var side=EnbStationFollow.Steer(Vector3.Zero,0,new(0,100,0),EnbStationFollow.FollowDistance,false);
        Assert.Equal(0,side.Thrust);
        // The sign matches EnbStationMovement: following the turn raises the heading toward +Y.
        var movement=new EnbStationMovement(Matrix4x4.Identity){Turn=side.Turn};
        movement.Advance(.1,(p,d)=>p+d);
        Assert.True(movement.Heading>0);
    }

    [Fact]
    public void NextRoomFollowsTheRetailDoorLayout()
    {
        static EnbSceneStationDefinition Station(params uint[] rooms)
            => new(0,rooms.Select(r=>new EnbSceneStationRoom(r,0,[])).ToImmutableArray());
        var all=Station(0,1,2,3);
        Assert.Equal(1u,EnbStationFollow.NextRoom(0,2,all));
        Assert.Equal(1u,EnbStationFollow.NextRoom(2,3,all));
        Assert.Equal(0u,EnbStationFollow.NextRoom(1,0,all));
        Assert.Equal(3u,EnbStationFollow.NextRoom(1,3,all));
        Assert.Null(EnbStationFollow.NextRoom(0,0,all));
        Assert.Null(EnbStationFollow.NextRoom(0,2,Station(0,2,3)));
    }

    [Fact]
    public void StationPlaceAndSpawnDrawCrossTheLocalBus()
    {
        var state=new MultiboxState(Guid.NewGuid(),"test",Guid.NewGuid(),1,"Pilot",8,null,false,true,[7],"Sector",null,false)
            {StationPose=new(10651,2,1.5f,-2,3,true),StationSpawn=new(10651,4,17)};
        var input=new EnbMultiboxInput(Guid.NewGuid(),"dock",99,new(1,8,8,12,4,3)){SpawnDraw=.375};
        var message=new MultiboxMessage(MultiboxPeer.Version,"command",state.Instance,input.Id,"dock",state,input);
        var copy=JsonSerializer.Deserialize<MultiboxMessage>(JsonSerializer.SerializeToUtf8Bytes(message))!;
        Assert.Equal(state.StationPose,copy.State!.StationPose);
        Assert.Equal(state.StationSpawn,copy.State.StationSpawn);
        Assert.Equal(.375,copy.Command!.SpawnDraw);
    }
}
