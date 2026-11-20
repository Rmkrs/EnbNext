using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Timing;
using EnbNext.Net7.Transport;

namespace EnbNext.Net7.Tests;

public sealed class Net7R11FlightWireTests
{
    [Fact]
    public async Task IncapacitationOnsetQueuesTheRetailReleaseOnceAndLeavesFlightGatedOnlyByLockOrient()
    {
        // 00581670 onset: Move 4, Turn 0, Tilt 0 once per observation (latch
        // +13D). Steering stays available until LockOrient (0057D6C0).
        var clock = new FlightClock { Now=1000 };
        await using var session = new Net7Session(new Net7ServerOptions {Host="127.0.0.1"},clock:clock);
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await AttachLoopback(session);
        using var lifetime=receiver;
        var sent=new List<ServerUdpFrame>();channel.FrameSent+=(frame,_)=>sent.Add(frame);
        Assert.True(await session.UpdateFlightInputAsync(new(1,1,1)));
        Assert.Equal(3,sent.Count);
        var aux=session.World.Objects.FindFirst(7)!.AuxData!;
        var incapacitated=aux.Resolve(28);
        incapacitated.Value=(byte)1;incapacitated.State=1;
        clock.Now=1400;
        await session.ReconcileIncapacitatedFlightAsync(default);
        Assert.Equal(new ushort[]{0x14,0x12,0x13},sent.Skip(3).Select(f=>f.Opcode));
        Assert.Equal((byte)4,sent[3].Payload.Span[4]);
        Assert.Equal(0f,BinaryPrimitives.ReadSingleLittleEndian(sent[4].Payload.Span[4..]));
        Assert.Equal(0f,BinaryPrimitives.ReadSingleLittleEndian(sent[5].Payload.Span[4..]));
        await session.ReconcileIncapacitatedFlightAsync(default);
        Assert.Equal(6,sent.Count);
        clock.Now=1700;
        Assert.True(await session.UpdateFlightInputAsync(new(-1,0,0)));
        Assert.Equal(new ushort[]{0x12},sent.Skip(6).Select(f=>f.Opcode));
        var lockOrient=aux.Resolve(24);
        lockOrient.Value=(byte)1;lockOrient.State=1;
        clock.Now=2100;
        Assert.True(await session.UpdateFlightInputAsync(new(1,1,1)));
        Assert.Equal(new ushort[]{0x14},sent.Skip(7).Select(f=>f.Opcode));
        Assert.False(await session.SendHorizontalSteeringAsync(1));
        Assert.False(await session.SendVerticalSteeringAsync(1));
        Assert.Equal(8,sent.Count);
        incapacitated.Value=(byte)0;
        await session.ReconcileIncapacitatedFlightAsync(default);
        Assert.Equal(8,sent.Count);
        incapacitated.Value=(byte)1;
        clock.Now=2500;
        await session.ReconcileIncapacitatedFlightAsync(default);
        Assert.Equal(new ushort[]{0x14,0x12,0x13},sent.Skip(8).Select(f=>f.Opcode));
    }

    [Fact]
    public async Task LockOrientDuringFirstSendSuppressesTheRemainingSteeringAxis()
    {
        await using var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"},clock:new FlightClock{Now=1000});
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await AttachLoopback(session);using var lifetime=receiver;
        var sent=new List<ushort>();
        channel.FrameSent+=(frame,_)=>{sent.Add(frame.Opcode);var lockOrient=session.World.Objects.FindFirst(7)!.AuxData!.Resolve(24);lockOrient.Value=(byte)1;lockOrient.State=1;};
        Assert.True(await session.UpdateFlightInputAsync(new(1,1,1)));
        Assert.Equal(new ushort[]{0x12,0x14},sent);
    }

    [Fact]
    public async Task CockpitLatchesCoalesceAndStopWithoutChangingTheWireEncoder()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" }, clock: new FlightClock { Now = 1000 });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await AttachLoopback(session);
        using var receiverLifetime = receiver;
        var sent = new List<byte>();
        channel.FrameSent += (frame, _) =>
        {
            Assert.Equal((ushort)0x14, frame.Opcode);
            sent.Add(frame.Payload.Span[4]);
        };
        foreach (var input in new[] {
            new Net7FlightInput(0,0,1) { ThrustLocked = true },
            new Net7FlightInput(0,0,1) { ThrustLocked = true },
            new Net7FlightInput(0,0,1), default,
            new Net7FlightInput(0,0,-1) { ThrustLocked = true }, default })
            Assert.True(await session.UpdateFlightInputAsync(input));
        Assert.Equal(new byte[] { 2,0,4,3,4 }, sent);
    }

    [Fact]
    public async Task InputCoalescesHeldStateAndPreservesIndependentThrottlesAndImmediateRelease()
    {
        var clock = new FlightClock { Now = 1000 };
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" }, clock: clock);
        EnbR11FlightTests.FlightWorld(session.World);
        var (transport, channel, receiver) = await AttachLoopback(session);
        using var receiverLifetime = receiver;
        var sent = new List<ServerUdpFrame>();
        channel.FrameSent += (frame, _) => sent.Add(frame);
        Assert.True(await session.UpdateFlightInputAsync(new(1,-1,1)));
        Assert.Equal(new ushort[] {0x12,0x13,0x14},sent.Select(p=>p.Opcode));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(sent[0].Payload.Span[4..]));
        Assert.Equal(-1f, BinaryPrimitives.ReadSingleLittleEndian(sent[1].Payload.Span[4..]));
        Assert.Equal((byte)0, sent[2].Payload.Span[4]);
        for (int i=0;i<20;i++) await session.UpdateFlightInputAsync(new(1,-1,1));
        Assert.Equal(3,sent.Count);
        clock.Now = 1010;
        await session.UpdateFlightInputAsync(new(-1,0,0));
        Assert.Equal(new ushort[] {0x13,0x14},sent.Skip(3).Select(p=>p.Opcode));
        Assert.Equal((byte)4,sent[^1].Payload.Span[4]);
        clock.Now = 1250;
        await session.UpdateFlightInputAsync(new(-1,0,0));
        Assert.Equal(5,sent.Count); // exact +250 remains suppressed.
        clock.Now = 1251;
        await session.UpdateFlightInputAsync(new(-1,0,0));
        Assert.Equal(6,sent.Count);
        Assert.Equal(-1f,BinaryPrimitives.ReadSingleLittleEndian(sent[^1].Payload.Span[4..]));
        clock.Now = 1252;
        await session.UpdateFlightInputAsync(default);
        Assert.Equal(7,sent.Count);
        Assert.Equal(0f,BinaryPrimitives.ReadSingleLittleEndian(sent[^1].Payload.Span[4..]));
        Assert.All(sent,p=>Assert.Contains(p.Opcode,new ushort[] {0x12,0x13,0x14}));
        transport.InvalidateGameplayRoute();
        Assert.False(await session.UpdateFlightInputAsync(new(1,1,1)));
        Assert.Equal(7,sent.Count);
    }

    [Fact]
    public async Task RouteLossDuringFirstSendCannotLeakOtherControlsIntoANewRoute()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" }, clock: new FlightClock { Now=1000 });
        EnbR11FlightTests.FlightWorld(session.World);
        var (transport, channel, receiver) = await AttachLoopback(session);
        using var receiverLifetime = receiver;
        int count=0;
        channel.FrameSent += (_,_) => { count++; transport.InvalidateGameplayRoute(); };
        Assert.False(await session.UpdateFlightInputAsync(new(1,1,1)));
        Assert.Equal(1,count);
        Assert.False(await session.UpdateFlightInputAsync(default));
        Assert.Equal(1,count);
    }

    [Fact]
    public async Task RecordedSteeringTimesSurviveBatchedDeliveryWithoutChangingLivePrediction()
    {
        var liveClock=new FlightClock();
        await using var live=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"},clock:liveClock);
        await using var replay=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"},clock:new FlightClock {Now=2000});
        EnbR11FlightTests.FlightWorld(live.World);EnbR11FlightTests.FlightWorld(replay.World);
        var (_,_,livePeer)=await AttachLoopback(live);using var liveLifetime=livePeer;
        var (_,_,replayPeer)=await AttachLoopback(replay);using var replayLifetime=replayPeer;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var method=typeof(Net7Session).GetMethod("UpdateRecordedFlightInputAsync",flags)!;
        var error=Assert.Throws<TargetInvocationException>(()=>method.Invoke(live,[default(Net7FlightInput),(uint)1000,CancellationToken.None]));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        typeof(Net7Session).GetField("_recordedReplay",flags)!.SetValue(replay,true);
        foreach(var (time,input) in new (uint,Net7FlightInput)[] {(1000,new(1,0,0)),(1250,new(.5f,0,0)),(1500,default)})
        {
            liveClock.Now=time;
            Assert.True(await live.UpdateFlightInputAsync(input));
            Assert.True(await (Task<bool>)method.Invoke(replay,[input,time,CancellationToken.None])!);
        }
        Assert.True(live.World.TrySampleObject(7,2500,out var expected));
        Assert.True(replay.World.TrySampleObject(7,2500,out var actual));
        Assert.NotEqual(System.Numerics.Quaternion.Identity,expected.Orientation);
        Assert.Equal(expected.Orientation,actual.Orientation);
        Assert.Equal(expected.Position,actual.Position);
    }

    internal static async Task<(Net7TransportState,RoutedSectorUdpChannel,UdpClient)> AttachLoopback(Net7Session session)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var transport=(Net7TransportState)typeof(Net7Session).GetField("_transportState",flags)!.GetValue(session)!;
        transport.CommitValidatedFixedSessionId(7);
        UdpClient? receiver = null;
        for (int port=Net7ServerOptions.CurrentMinGameplaySectorUdpPort; port<=Net7ServerOptions.CurrentMaxGameplaySectorUdpPort; port++)
        {
            try { receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback,port)); break; }
            catch (SocketException) { }
        }
        Assert.NotNull(receiver);
        transport.CommitGameplayPort(((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
        transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);
        var channel=await RoutedSectorUdpChannel.CreateAsync(IPAddress.Loopback,transport);
        typeof(Net7Session).GetField("_routed",flags)!.SetValue(session,channel);
        return (transport,channel,receiver);
    }

    private sealed class FlightClock : INet7RuntimeClock
    {
        public uint Now;
        public uint GetMilliseconds()=>Now;
        public Task DelayAsync(TimeSpan delay,CancellationToken cancellationToken=default)=>Task.Delay(delay,cancellationToken);
    }
}
