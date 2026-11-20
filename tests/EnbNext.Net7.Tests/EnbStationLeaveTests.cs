using System.Reflection;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationLeaveTests
{
    [Theory]
    [InlineData(false, 0x40u)]
    [InlineData(true, 0x41u)]
    public async Task ArrivalUsesInitialRoomPairAndRejectsStaleStationIdentity(bool run, uint expectedFlags)
    {
        await using var client = new EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbClient).GetField("_net7", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client, ClientLifecycleState.InWorld);
        session.World.SetLocalPlayerId(7);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, 7u);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var requests = new List<byte[]>();
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x009F) requests.Add(frame.Payload.ToArray()); };
        ulong version = session.World.PresentationHandoffVersion;
        var arrival = new EnbStationMovementRequest(10651, version, new(1.5f, -2, 3), .25f, run);
        var poses = new List<byte[]>();
        var order = new List<ushort>();
        channel.FrameSent += (frame, _) => { order.Add(frame.Opcode); if(frame.Opcode == 0x009D) poses.Add(frame.Payload.ToArray()); };
        Assert.False(await client.CompleteStarbaseArrivalAsync(arrival));
        session.PlayerSemantics.ObserveStarbaseSet(new(10651, 0, 0));
        Assert.False(await client.CompleteStarbaseArrivalAsync(arrival with { StarbaseId = 10652 }));
        Assert.False(await client.CompleteStarbaseArrivalAsync(arrival with { HandoffVersion = version + 1 }));
        Assert.Empty(requests);
        Assert.True(await client.CompleteStarbaseArrivalAsync(arrival));
        Assert.Equal(Convert.FromHexString("0700000000000000FFFFFFFF"), Assert.Single(requests));
        session.PlayerSemantics.ObserveStarbaseSet(new(10651, 1, 0));
        Assert.False(await client.CompleteStarbaseArrivalAsync(arrival));
        Assert.Single(requests);
        Assert.Equal(new ushort[] { 0x009F, 0x009D }, order);
        var expected = Convert.FromHexString("07000000000000000000803E0000C03F000000C00000404000000000");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(24), expectedFlags);
        Assert.Equal(expected, Assert.Single(poses));
    }

    [Fact]
    public async Task StationOpenAndClosePublishTheirUpdatedIdentityImmediately()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        var observed = new List<uint?>();
        session.PresentationStateChanged += _ => observed.Add(session.PlayerSemantics.ActiveStarbaseId);
        session.WorldStateChanged += _ => observed.Add(session.PlayerSemantics.ActiveStarbaseId);
        var dispatch = typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (string bytes in new[] { "9B2900000000", "9B2900000100" })
            await (Task)dispatch.Invoke(session, [EnbNext.Protocol.Net.Packet.ForOpcode(0x004F, Convert.FromHexString(bytes)),
                "offline loopback", CancellationToken.None])!;
        Assert.Equal(new uint?[] { 10651, null }, observed);
    }

    [Fact]
    public async Task LeaveUsesActiveStationAndStartIdentityAndRejectsStaleRequests()
    {
        await using var client = new EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbClient).GetField("_net7", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client, ClientLifecycleState.InWorld);
        session.World.SetLocalPlayerId(7);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, 7u);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var requests = new List<byte[]>();
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x004E) requests.Add(frame.Payload.ToArray()); };
        ulong version = session.World.PresentationHandoffVersion;
        Assert.False(await client.LeaveStarbaseAsync(10651, version));
        session.PlayerSemantics.ObserveStarbaseSet(new(10651, 0, 0));
        Assert.False(await client.LeaveStarbaseAsync(10652, version));
        Assert.False(await client.LeaveStarbaseAsync(10651, version + 1));
        Assert.Empty(requests);
        Assert.True(await client.LeaveStarbaseAsync(10651, version));
        Assert.Equal(Net7GameplayWire.BuildStarbaseRequest(7, 10651, 1), Assert.Single(requests));
        session.PlayerSemantics.ObserveStarbaseSet(new(10651, 1, 0));
        Assert.False(await client.LeaveStarbaseAsync(10651, version));
        Assert.Single(requests);
    }

    [Fact]
    public void PublishedStationIdentityDoesNotRequireSpaceEnvironmentOrAvatars()
    {
        var world = new EnbNext.Net7.World.Net7WorldState();
        var publisher = new EnbPresentationSnapshotPublisher();
        var station = publisher.Publish(world, null, null, 10, activeStarbaseId: 10651);
        Assert.Equal(10651u, station.ActiveStarbaseId);
        Assert.Null(station.Environment);
        Assert.Empty(station.StationAvatars);
        var next = publisher.Publish(world, null, null, 11);
        Assert.Null(next.ActiveStarbaseId);
        Assert.Equal(10651u, station.ActiveStarbaseId);
    }
}
