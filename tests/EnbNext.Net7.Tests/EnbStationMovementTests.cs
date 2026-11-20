using System.Numerics;
using System.Reflection;
using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;
public sealed class EnbStationMovementTests
{
    [Fact]
    public void CameraYawIsIndependentUnboundedAndAdvancesOnCappedLongFrames()
    {
        var motion=new EnbStationMovement(Matrix4x4.Identity){CameraTurn=.5f,Look=.5f};
        Assert.False(motion.Advance(.1,(p,d)=>p+d));
        Assert.Equal(.1f,motion.CameraYaw,6);Assert.Equal(.05f,motion.CameraPitch,6);
        motion.Advance(1,(p,d)=>p+d);
        Assert.Equal(.35f,motion.CameraYaw,6);Assert.Equal(.05f,motion.CameraPitch,6);
        Assert.Equal(0,motion.Heading);Assert.Equal(Vector3.Zero,motion.Position);
        motion.CameraTurn=1;
        for(int i=0;i<40;i++)motion.Advance(.1,(p,d)=>p+d);
        Assert.True(motion.CameraYaw>MathF.Tau);
        motion.RestoreCamera();Assert.Equal(0,motion.CameraYaw);Assert.Equal(0,motion.CameraPitch);
        motion.Stop();motion.Advance(.1,(p,d)=>p+d);Assert.Equal(0,motion.CameraYaw);
    }
    [Fact]
    public void CameraLookUsesRetailRateLimitsAndLongFrameRule()
    {
        var motion=new EnbStationMovement(Matrix4x4.Identity){Look=-.5f};
        Assert.False(motion.Advance(.1,(p,d)=>p+d));
        Assert.Equal(-.05f,motion.CameraPitch,5);
        motion.Advance(1,(p,d)=>p+d);
        Assert.Equal(-.05f,motion.CameraPitch,5);
        motion.Look=-1;
        for(int i=0;i<20;i++)motion.Advance(.1,(p,d)=>p+d);
        Assert.Equal(-.8f,motion.CameraPitch);
        motion.Look=1;
        for(int i=0;i<20;i++)motion.Advance(.1,(p,d)=>p+d);
        Assert.Equal(.4f,motion.CameraPitch);
        motion.Stop();motion.Advance(.1,(p,d)=>p+d);
        Assert.Equal(.4f,motion.CameraPitch);
        Assert.Equal(Vector3.Zero,motion.Position);
    }
    [Fact]
    public void PositionSendsAtOnceThenEveryHalfSecondWhileMovingAndRearmsWhenIdle()
    {
        // 0063FF80: idle movement state zeroes the timer; while moving the timer counts down
        // from 0 and each underflow re-arms 0.5 s and sends.
        var motion=new EnbStationMovement(Matrix4x4.Identity){Thrust=1};
        bool[] sends=Enumerable.Range(0,5).Select(_=>motion.Advance(.2,(p,d)=>p+d)).ToArray();
        Assert.Equal(new[]{true,false,false,true,false},sends);
        motion.Thrust=0;Assert.False(motion.Advance(.2,(p,d)=>p+d));
        motion.Turn=1;Assert.True(motion.Advance(.2,(p,d)=>p+d));
    }
    [Fact]
    public void CrossingKeepsRunningSpeedAndHeldInput()
    {
        var motion=new EnbStationMovement(Matrix4x4.Identity){Thrust=1,Run=true};
        for(int i=0;i<60;i++)motion.Advance(1d/60,(p,d)=>p+d);
        motion.Relocate(Matrix4x4.CreateTranslation(100,0,0));
        motion.Advance(1d/60,(p,d)=>p+d);
        Assert.Equal(25,motion.Speed);
        Assert.InRange(motion.Position.X,100.416f,100.417f);
        Assert.Equal(1,motion.Thrust);
    }
    [Fact]
    public void RetailRunDefaultAndSavedWalkOptionControlSpeed()
    {
        Assert.True(EnbNext.Assets.EnbRetailPlayerOptionsCatalog.ParseText("").StarbaseDefaultRun);
        Assert.False(EnbNext.Assets.EnbRetailPlayerOptionsCatalog.ParseText("[BOOL-1]\nOptionName=Starbase_Default_Run\nOptionValue=no").StarbaseDefaultRun);
        var movement=new EnbStationMovement(Matrix4x4.Identity){Thrust=1,Run=true};
        for(int i=0;i<60;i++)movement.Advance(1d/60,(p,d)=>p+d);
        Assert.Equal(25,movement.Speed);
        movement.Thrust=0;
        for(int i=0;i<60;i++)movement.Advance(1d/60,(p,d)=>p+d);
        Assert.Equal(0,movement.Speed);
    }
    [Fact]
    public void WalkingAcceleratesTurnsAndDeceleratesWithoutOvershoot()
    {
        var movement=new EnbStationMovement(Matrix4x4.Identity){Thrust=1};
        for(int i=0;i<10;i++)movement.Advance(.1,(p,d)=>p+d);
        Assert.Equal(10,movement.Speed);
        Assert.InRange(movement.Position.X,8.79f,8.81f);
        movement.Thrust=0;
        for(int i=0;i<4;i++)movement.Advance(.1,(p,d)=>p+d);
        Assert.Equal(0,movement.Speed);
        var stopped=movement.Position;
        movement.Advance(.1,(p,d)=>p+d);
        Assert.Equal(stopped,movement.Position);
        movement.Turn=1;movement.Advance(.1,(p,d)=>p+d);
        Assert.InRange(movement.Heading,-.201f,-.199f);
        movement.Stop();Assert.Equal(0,movement.Turn);
    }
    [Fact]
    public void MovementUpdatesAreBoundedAndIdleDoesNotEmit()
    {
        var movement=new EnbStationMovement(Matrix4x4.Identity){Thrust=1};
        int count=0;
        for(int i=0;i<100;i++)if(movement.Advance(.016,(p,d)=>p+d))count++;
        Assert.Equal(4,count);
        movement.Stop();
        Assert.False(movement.Advance(1,(p,d)=>p+d));
    }
    [Fact]
    public async Task AvatarPoseUsesRoomZeroAndRejectsRetiredHandoffs()
    {
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        typeof(EnbClient).GetField("_net7",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        session.World.SetLocalPlayerId(7);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,7u);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        var packets=new List<byte[]>();
        channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x009D)packets.Add(frame.Payload.ToArray());};
        var request=new EnbStationMovementRequest(10651,session.World.PresentationHandoffVersion,new(1.5f,-2,3),.25f,false);
        Assert.False(await client.MoveStationAvatarAsync(request));
        session.PlayerSemantics.ObserveStarbaseSet(new(10651,0,0));
        Assert.False(await client.MoveStationAvatarAsync(request with{HandoffVersion=request.HandoffVersion+1}));
        Assert.False(await client.MoveStationAvatarAsync(request with{StarbaseId=10652}));
        Assert.True(await client.MoveStationAvatarAsync(request));
        Assert.Equal(Convert.FromHexString("07000000000000000000803E0000C03F000000C00000404000000000"),Assert.Single(packets));
        Assert.False(await client.MoveStationAvatarAsync(request with { Interaction=EnbStationInteractionPose.Terminal, HandoffVersion=request.HandoffVersion+1 }));
        Assert.True(await client.MoveStationAvatarAsync(request with { Interaction=EnbStationInteractionPose.Terminal, RoomType=3 }));
        Assert.Equal(Convert.FromHexString("07000000030000000000803E0000C03F000000C00000404010000000"),packets[^1]);
        Assert.True(await client.MoveStationAvatarAsync(request with { Interaction=EnbStationInteractionPose.Terminal, RoomType=3, Run=true }));
        Assert.Equal(Convert.FromHexString("07000000030000000000803E0000C03F000000C00000404011000000"),packets[^1]);
        Assert.True(await client.MoveStationAvatarAsync(request with { Interaction=EnbStationInteractionPose.Npc, RoomType=1 }));
        Assert.Equal(Convert.FromHexString("07000000010000000000803E0000C03F000000C00000404020000000"),packets[^1]);
        Assert.True(await client.MoveStationAvatarAsync(request with { Interaction=EnbStationInteractionPose.Npc, RoomType=1, Run=true }));
        Assert.Equal(Convert.FromHexString("07000000010000000000803E0000C03F000000C00000404021000000"),packets[^1]);
        Assert.True(await client.MoveStationAvatarAsync(request));
        Assert.Equal(Convert.FromHexString("07000000000000000000803E0000C03F000000C00000404000000000"),packets[^1]);
        var departure=new List<(ushort Opcode,byte[] Payload)>();
        channel.FrameSent+=(frame,_)=>{if(frame.Opcode is 0x009D or 0x009F)departure.Add((frame.Opcode,frame.Payload.ToArray()));};
        Assert.False(await client.BeginStarbaseDepartureAsync(request with{HandoffVersion=request.HandoffVersion+1}));
        Assert.Empty(departure);
        Assert.True(await client.BeginStarbaseDepartureAsync(request with{Run=true}));
        Assert.Equal(new ushort[]{0x009D,0x009F},departure.Select(p=>p.Opcode));
        Assert.Equal(Convert.FromHexString("07000000000000000000803E0000C03F000000C00000404005000000"),departure[0].Payload);
        Assert.Equal(Convert.FromHexString("07000000FFFFFFFF00000000"),departure[1].Payload);
        session.PlayerSemantics.ObserveStarbaseSet(new(10651,1,0));
        Assert.False(await client.MoveStationAvatarAsync(request));
        Assert.False(await client.BeginStarbaseDepartureAsync(request));
        Assert.Equal(7,packets.Count);
    }
}
