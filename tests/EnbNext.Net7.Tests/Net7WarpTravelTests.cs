using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Net7.Timing;
using EnbNext.Rendering;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7WarpTravelTests
{
    [Fact]
    public async Task FreeWarpSendsEmptyRouteAfterPositionAndCanDisengage()
    {
        await using var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"},clock:new Clock());
        Ready(session);
        var (transport,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        var frames=new List<ServerUdpFrame>();
        channel.FrameSent+=(frame,_)=>frames.Add(frame);
        var master=await AttachMaster(session,transport,receiver);
        master.FrameSent+=frame=>frames.Add(frame);
        Assert.True(await session.RequestWarpAsync(7,Array.Empty<uint>()));
        Assert.Equal(new ushort[]{0x1004,0x9B},frames.Select(f=>f.Opcode));
        Assert.Equal(new byte[]{7,0,0,0,0,0},frames[^1].Payload.ToArray());
        Assert.Empty(session.Gameplay.WarpRoute!.ObjectIds);
        Assert.Equal(0,Net7WarpStatus.Read(session.World.Objects.FindFirst(7)!.AuxData,0).State);
        Set(session,38,2);frames.Clear();
        Assert.True(await session.RequestWarpAsync(7,Array.Empty<uint>()));
        Assert.Equal(0x9B,frames[^1].Opcode);
        Set(session,38,9);frames.Clear();
        Assert.False(await session.RequestWarpAsync(7,Array.Empty<uint>()));
        Assert.Empty(frames);
    }
    [Fact]
    public void DecodedConstructorDefaultAndRetainedWarpFieldsRemainUsable()
    {
        var schema=Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec=new Net7AuxDataCodec();
        Assert.False(Net7WarpStatus.Read(schema,0).CanActivate); // Truly unmaterialized.
        using var stream=new MemoryStream();
        using var writer=new BinaryWriter(stream);
        writer.Write((byte)0); // Default Aux selector, explicit values then state-two list.
        writer.Write(2u);
        writer.Write(20u); writer.Write(525.8667f);
        writer.Write(40u); writer.Write(2);
        writer.Write(4u);
        foreach(uint id in new uint[]{23,28,38,41}) writer.Write(id);
        writer.Write(0u);
        codec.Apply(schema,stream.ToArray());
        Assert.Equal(2,schema.Resolve(38).State);
        Assert.Null(schema.Resolve(38).Value); // The actual live entry form.
        var ready=Net7WarpStatus.Read(schema,0);
        Assert.Equal(0,ready.State);
        Assert.True(ready.CanActivate);
        Assert.Equal("WARP",ready.TooltipKey);
        Assert.Equal(0,ready.TriggerTime);
        // After an explicit idle value, state-zero is a callback reset, not deletion.
        stream.SetLength(0); stream.Position=0;
        writer.Write((byte)0); writer.Write(1u); writer.Write(38u); writer.Write(0);
        writer.Write(0u); writer.Write(0u);
        codec.Apply(schema,stream.ToArray());
        stream.SetLength(0); stream.Position=0;
        writer.Write((byte)0); writer.Write(0u); writer.Write(0u); writer.Write(3u);
        writer.Write(20u); writer.Write(38u); writer.Write(40u);
        codec.Apply(schema,stream.ToArray());
        Assert.Equal(0,schema.Resolve(40).State);
        Assert.True(Net7WarpStatus.Read(schema,0).CanActivate);
    }

    [Theory]
    [InlineData(0,2,true)] [InlineData(0,1,false)] [InlineData(0,0,false)]
    [InlineData(1,2,true)] [InlineData(2,2,true)] [InlineData(3,2,false)]
    [InlineData(4,2,false)] [InlineData(5,2,false)] [InlineData(6,2,false)]
    [InlineData(7,2,false)] [InlineData(8,2,false)] [InlineData(9,2,false)]
    [InlineData(10,2,false)] [InlineData(11,2,false)]
    public void RetailStatePolicy(int state, int available, bool enabled)
        => Assert.Equal(enabled, new Net7WarpStatus(state, available, 5000, false, false, 150, 0).CanActivate);

    [Fact]
    public void ChargeAndRecoveryUseCorrectedDeadlineAndDoNotTriggerTravelLocally()
    {
        var charge = new Net7WarpStatus(1, 2, 5000, false, false, 150, 0);
        Assert.Equal(0, charge.RemainingSeconds(6000));
        Assert.Equal(1, charge.State); // Reaching the deadline does not invent active warp.
        var recovery = charge with { State = 8, TriggerTime = 10000 };
    }

    [Fact]
    public async Task LaunchThenDisengageSendsWarpWithoutExtraControlPackets()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host="127.0.0.1" }, clock: new Clock());
        Ready(session);
        var (transport, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var frames = new List<ServerUdpFrame>();
        channel.FrameSent += (frame, _) => frames.Add(frame);
        await session.UpdateFlightInputAsync(new(1,1,1));
        frames.Clear();
        var master = await AttachMaster(session,transport,receiver);
        master.FrameSent += frame => frames.Add(frame);
        Assert.NotNull(new Net7MovementFeedbackController(new Clock()).Capture(session.World));
        Assert.Equal(7u,session.FixedSessionId);
        Assert.True(await session.RequestWarpAsync(7, new uint[] { 91,72,63 }));
        var capturedRoute=Assert.IsType<Net7WarpRouteSnapshot>(session.Gameplay.WarpRoute);
        Assert.Equal(new ushort[] { 0x1004,0x9B }, frames.Select(f=>f.Opcode));
        Assert.Equal(Net7GameplayWire.BuildWarp(7, new uint[] {91,72,63}), frames[^1].Payload.ToArray());
        frames.Clear();
        Assert.True(await session.UpdateFlightInputAsync(default));
        Assert.Empty(frames); // UI reset cannot send Move=4 after the launch.
        Set(session,38,2);
        Assert.True(await session.RequestWarpAsync(7, Array.Empty<uint>()));
        Assert.Same(capturedRoute,session.Gameplay.WarpRoute);
        Assert.Equal(0x9B, frames[^1].Opcode); // Same command, valid empty rebuilt route for exit.
        Assert.DoesNotContain(frames,f=>f.Opcode is 0x12 or 0x13 or 0x14);
        Set(session,38,6);
        Assert.False(await session.RequestWarpAsync(7,new uint[] {63}));
        Assert.Equal("WARP_WAIT",Net7WarpStatus.Read(session.World.Objects.FindFirst(7)!.AuxData,0).TooltipKey);
    }

    [Fact]
    public async Task HandoffDuringSnapshotCannotSendWarpToReplacementRoute()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host="127.0.0.1" }, clock: new Clock());
        Ready(session);
        var (transport, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var opcodes = new List<ushort>();
        var master = await AttachMaster(session,transport,receiver);
        channel.FrameSent += (frame, _) => opcodes.Add(frame.Opcode);
        master.FrameSent += frame => { opcodes.Add(frame.Opcode); transport.InvalidateGameplayRoute(); };
        Assert.False(await session.RequestWarpAsync(7,new uint[] {63}));
        Assert.DoesNotContain((ushort)0x9B,opcodes);
        Assert.Null(session.Gameplay.WarpRoute);
    }

    [Fact]
    public async Task QueuedDisengageCannotBecomeLaunchAfterArrival()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host="127.0.0.1" }, clock: new Clock());
        Ready(session); Set(session,38,2);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var opcodes = new List<ushort>();
        channel.FrameSent += (frame, _) => opcodes.Add(frame.Opcode);
        var gate=(SemaphoreSlim)typeof(Net7Session).GetField("_flightGate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
        await gate.WaitAsync();
        Task<bool> request;
        try
        {
            request=session.RequestWarpAsync(7,new uint[] {63});
            Assert.False(request.IsCompleted);
            Set(session,38,0);
        }
        finally { gate.Release(); }
        Assert.False(await request);
        Assert.Empty(opcodes);
        Assert.Null(session.Gameplay.WarpRoute);
    }

    [Fact]
    public void ActivationRequiresOrdinarySpaceAndUsableIdleEngineButAlwaysAllowsEngagedExit()
    {
        var ready=new Net7WarpStatus(0,2,null,false,false,150,0);
        Assert.False((ready with { Incapacitated=true }).CanActivate);
        Assert.False((ready with { SpeedLocked=true }).CanActivate);
        Assert.False((ready with { MaximumSpeed=0 }).CanActivate);
        Assert.False((ready with { MaximumSpeed=null }).CanActivate);
        Assert.False((ready with { PresentationMode=null }).CanActivate);
        Assert.False((ready with { PresentationMode=1 }).CanActivate);
        Assert.False((ready with { PresentationMode=5 }).CanActivate);
        Assert.True((ready with { State=2,Availability=0,SpeedLocked=true,MaximumSpeed=0 }).CanActivate);
    }

    [Theory]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(11)]
    public void InterruptedOrArrivedShipUsesAuthoritativePositionAndSpeed(int state)
    {
        var world = EnbR11FlightTests.FlightWorld();
        var data=world.Objects.FindFirst(7)!.AuxData!;
        data.Resolve(38).State=3; data.Resolve(38).Value=state;
        // A warp-speed keyframe uses the existing physics path, without an impulse cap.
        var update=new Net7AdvancedPositionUpdate(0x103,7,1000,new(100,0,0),System.Numerics.Quaternion.Identity,
            1,5,5,0,0,-MathF.PI,0,-1000,System.Numerics.Vector3.Zero,0,0,0,100);
        world.MaterializeAdvanced(update,1000);
        world.TrySampleObject(7,3000,out var travelling); // Beyond the existing 2-second reconciliation blend.
        Assert.True(travelling.Position.X > 4000);
        world.MaterializeAdvanced(update with { ServerTimestamp=3000,Position=new(9000,0,0),CurrentSpeed=0,SetSpeed=0 },3000);
        world.TrySampleObject(7,4000,out var stopped);
        Assert.Equal(0,stopped.Speed);
        Assert.Equal(9000,stopped.Position.X);
        Assert.False(Net7WarpStatus.Read(data,0).CanActivate);
    }

    private static void Ready(Net7Session session)
    {
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(Net7Session).GetProperty(nameof(Net7Session.PlayerId))!.SetValue(session,7u);
        typeof(Net7GameplayState).GetProperty(nameof(Net7GameplayState.PresentationMode))!.SetValue(session.Gameplay,0);
        Set(session,40,2);
        var idle=session.World.Objects.FindFirst(7)!.AuxData!.Resolve(38);
        idle.State=2; idle.Value=null; // Retail constructor default, as sent at live entry.
    }
    private static async Task<EnbNext.Net7.Transport.MasterControlUdpChannel> AttachMaster(Net7Session session,
        EnbNext.Net7.Transport.Net7TransportState transport, System.Net.Sockets.UdpClient receiver)
    {
        var master=await EnbNext.Net7.Transport.MasterControlUdpChannel.ConnectAsync(System.Net.IPAddress.Loopback,
            ((System.Net.IPEndPoint)receiver.Client.LocalEndPoint!).Port,transport);
        master.Sequence.AllocateNormalSequence(); // Causal bootstrap precedes fixed-ID gameplay snapshots.
        typeof(Net7Session).GetField("_master",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,master);
        return master;
    }
    private static void Set(Net7Session session,int id,int value)
    { var p=session.World.Objects.FindFirst(7)!.AuxData!.Resolve(id); p.State=3; p.Value=value; }
    private sealed class Clock : INet7RuntimeClock
    {
        public uint GetMilliseconds()=>1000;
        public Task DelayAsync(TimeSpan delay,CancellationToken cancellationToken=default)=>Task.Delay(delay,cancellationToken);
    }
}
