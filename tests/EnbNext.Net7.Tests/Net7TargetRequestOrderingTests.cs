using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using EnbNext.Net7.Transport;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7TargetRequestOrderingTests
{
    [Fact]
    public async Task ServerCorpseSelectionSubscribesVerbsWithoutEchoingTargetRequest()
    {
        using UdpClient server=BindGameplayTestServer(out int port);
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        session.World.SetLocalPlayerId(1);
        session.World.MaterializeCreate(new EnbNext.Net7.World.Net7CreateUpdate(1,1,0,1,0,0,0));
        session.Gameplay.SetLocalSelectedTarget(43);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var transport=(Net7TransportState)typeof(Net7Session).GetField("_transportState",flags)!.GetValue(session)!;
        transport.CommitValidatedFixedSessionId(99);transport.CommitGameplayPort(port);transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);
        var routed=await RoutedSectorUdpChannel.CreateAsync(IPAddress.Loopback,transport);
        typeof(Net7Session).GetField("_routed",flags)!.SetValue(session,routed);
        var result=new EnbNext.Net7.World.Net7WorldApplyResult {TargetSelectionChange=new(42,43)};
        await (Task)typeof(Net7Session).GetMethod("ApplyWorldPostMaterializationAsync",flags)!.Invoke(session,
            [EnbNext.Protocol.Net.Packet.ForOpcode(0x001B,ReadOnlyMemory<byte>.Empty),result,CancellationToken.None])!;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AssertVerb(ServerUdpFrame.Parse((await server.ReceiveAsync(timeout.Token)).Buffer),0,1,42,2);
        AssertVerb(ServerUdpFrame.Parse((await server.ReceiveAsync(timeout.Token)).Buffer),1,1,43,1);
        Assert.Equal(43u,session.Gameplay.LocalSelectedTargetId);
    }

    [Fact]
    public async Task RapidTargetRequestsPublishLatestLocallyAndKeepWireTransactionsWhole()
    {
        const uint fixedSessionId = 0xAABBCCDD;
        const uint localShipId = 0x11111111;
        const uint initialTargetId = 0x22222222;
        const uint firstTargetId = 0x33333333;
        const uint secondTargetId = 0x44444444;

        using UdpClient server = BindGameplayTestServer(out int serverPort);
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        session.World.SetLocalPlayerId(localShipId);
        session.Gameplay.SetLocalSelectedTarget(initialTargetId);

        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo transportField = typeof(Net7Session).GetField("_transportState", PrivateInstance)!;
        var transport = Assert.IsType<Net7TransportState>(transportField.GetValue(session));
        transport.CommitValidatedFixedSessionId(fixedSessionId);
        transport.CommitGameplayPort(serverPort);
        transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);

        RoutedSectorUdpChannel routed = await RoutedSectorUdpChannel.CreateAsync(IPAddress.Loopback, transport);
        typeof(Net7Session).GetField("_routed", PrivateInstance)!.SetValue(session, routed);

        // Hold the per-datagram transport gate so the first request owns the
        // target transaction gate while both UI requests have already published
        // their local state. Without the target gate, their three packets
        // alternate as detach O, detach A, target A, target B, attach A, attach B.
        var transportSendGate = Assert.IsType<SemaphoreSlim>(
            typeof(RoutedSectorUdpChannel).GetField("_sendGate", PrivateInstance)!.GetValue(routed));
        await transportSendGate.WaitAsync();
        Task<bool>? first = null;
        Task<bool>? second = null;
        try
        {
            first = session.RequestTargetAsync(firstTargetId);
            second = session.RequestTargetAsync(secondTargetId);

            Assert.Equal(secondTargetId, session.Gameplay.LocalSelectedTargetId);
            Assert.Null(session.World.CurrentTargetId);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            transportSendGate.Release();
        }

        Assert.True(await first!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second!.WaitAsync(TimeSpan.FromSeconds(5)));

        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ServerUdpFrame[] frames = new ServerUdpFrame[6];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = ServerUdpFrame.Parse((await server.ReceiveAsync(receiveTimeout.Token)).Buffer);

        AssertVerb(frames[0], sequence: 0, localShipId, initialTargetId, action: 2);
        AssertTarget(frames[1], sequence: 1, localShipId, firstTargetId);
        AssertVerb(frames[2], sequence: 2, localShipId, firstTargetId, action: 1);
        AssertVerb(frames[3], sequence: 3, localShipId, firstTargetId, action: 2);
        AssertTarget(frames[4], sequence: 4, localShipId, secondTargetId);
        AssertVerb(frames[5], sequence: 5, localShipId, secondTargetId, action: 1);
    }

    private static void AssertTarget(
        ServerUdpFrame frame,
        uint sequence,
        uint expectedSubjectId,
        uint expectedTargetId)
    {
        Assert.Equal(0x0017, frame.Opcode);
        Assert.Equal(sequence, frame.Sequence);
        Assert.Equal(expectedSubjectId, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[..4]));
        Assert.Equal(expectedTargetId, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[4..8]));
    }

    private static void AssertVerb(
        ServerUdpFrame frame,
        uint sequence,
        uint expectedSubjectId,
        uint expectedTargetId,
        uint action)
    {
        Assert.Equal(0x005A, frame.Opcode);
        Assert.Equal(sequence, frame.Sequence);
        Assert.Equal(expectedSubjectId, BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.Span[..4]));
        Assert.Equal(expectedTargetId, BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.Span[4..8]));
        Assert.Equal(action, BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[8..12]));
    }

    private static UdpClient BindGameplayTestServer(out int port)
    {
        for (int candidate = Net7ServerOptions.CurrentMinGameplaySectorUdpPort;
             candidate <= Net7ServerOptions.CurrentMaxGameplaySectorUdpPort;
             candidate++)
        {
            try
            {
                var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, candidate));
                port = candidate;
                return server;
            }
            catch (SocketException)
            {
                // Try the next authoritative gameplay-sector port.
            }
        }

        throw new InvalidOperationException("No free UDP port exists in the authoritative gameplay-sector range for this test.");
    }
}
