using System.Buffers.Binary;
using EnbNext.Net7.Protocol;
using System.Net;
using System.Net.Sockets;
using EnbNext.Net7.Transport;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class TransportChannelTests
{
    [Fact]
    public async Task ControlIdentityClearPreservesRoutedAdmissionAndOutboundIdentity()
    {
        using var controlServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var routedServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var state = new Net7TransportState();
        state.CommitValidatedFixedSessionId(0x11223344);
        await using var control = await MasterControlUdpChannel.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)controlServer.Client.LocalEndPoint!).Port, state);
        await using var routed = await RoutedSectorUdpChannel.CreateAsync("127.0.0.1", state);
        state.ApplyFixedSessionControl(0, controlChannel: true);
        await control.SendNormalAsync(0x200F, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(0u, ServerUdpFrame.Parse((await controlServer.ReceiveAsync()).Buffer).FixedSessionId);
        int routedPort = ((IPEndPoint)routedServer.Client.LocalEndPoint!).Port;
        await routed.SendNormalAsync(routedPort, 0x2008, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(0x11223344u, ServerUdpFrame.Parse((await routedServer.ReceiveAsync()).Buffer).FixedSessionId);
        var oldIdentity = new ServerUdpFrame(0x3005, 0x11223344, 1, ReadOnlyMemory<byte>.Empty);
        await controlServer.SendAsync(oldIdentity.ToWireBytes(), control.LocalEndPoint);
        await controlServer.SendAsync(new ServerUdpFrame(0x1001, 0, 2, ReadOnlyMemory<byte>.Empty).ToWireBytes(), control.LocalEndPoint);
        await routedServer.SendAsync(oldIdentity.ToWireBytes(), new IPEndPoint(IPAddress.Loopback, routed.LocalEndPoint.Port));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.Equal(0x1001, (await control.ReceiveAsync(cts.Token)).Opcode);
        Assert.Equal(0x3005, (await routed.ReceiveAsync(cts.Token)).Frame.Opcode);
    }

    [Fact]
    public async Task MasterChannel_NormalAndFixedSendsPreserveCausalSequence()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();

        await using var channel = await MasterControlUdpChannel.ConnectAsync(
            "127.0.0.1", serverPort, state);

        await channel.SendNormalAsync(0x1000, new byte[] { 0xF6, 0, 0, 0 });
        ServerUdpFrame first = ServerUdpFrame.Parse((await server.ReceiveAsync()).Buffer);
        Assert.Equal(0u, first.FixedSessionId);
        Assert.Equal(0u, first.Sequence);

        state.CommitValidatedFixedSessionId(0x11223344);
        await channel.SendNormalAsync(0x200F, ReadOnlyMemory<byte>.Empty);
        ServerUdpFrame second = ServerUdpFrame.Parse((await server.ReceiveAsync()).Buffer);
        Assert.Equal(0x11223344u, second.FixedSessionId);
        Assert.Equal(1u, second.Sequence);

        await channel.SendFixedAsync(0x3005, 0x11223344, ReadOnlyMemory<byte>.Empty);
        ServerUdpFrame fixedFrame = ServerUdpFrame.Parse((await server.ReceiveAsync()).Buffer);
        Assert.Equal(0x11223344u, fixedFrame.FixedSessionId);
        Assert.Equal(1u, fixedFrame.Sequence);
        Assert.Equal(2u, channel.Sequence.NextNormalSequence);

        await channel.SendNormalAsync(0x200F, ReadOnlyMemory<byte>.Empty);
        ServerUdpFrame third = ServerUdpFrame.Parse((await server.ReceiveAsync()).Buffer);
        Assert.Equal(2u, third.Sequence);
    }

    [Fact]
    public async Task MasterChannel_DropsNonzeroFixedIdWhileAuthorityIsZero()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();

        await using var channel = await MasterControlUdpChannel.ConnectAsync(
            "127.0.0.1", serverPort, state);

        var bad = new ServerUdpFrame(0x2005, 0xDEADBEEF, 77, ReadOnlyMemory<byte>.Empty);
        var good = new ServerUdpFrame(0x1001, 0, 78, ReadOnlyMemory<byte>.Empty);
        await server.SendAsync(bad.ToWireBytes(), channel.LocalEndPoint);
        await server.SendAsync(good.ToWireBytes(), channel.LocalEndPoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ServerUdpFrame received = await channel.ReceiveAsync(cts.Token);

        Assert.Equal(0x1001, received.Opcode);
        Assert.Equal(0u, received.FixedSessionId);
    }

    [Fact]
    public async Task RoutedChannel_AcceptsArbitrarySenderPortButRequiresConfiguredAddress()
    {
        var state = new Net7TransportState();
        await using var channel = await RoutedSectorUdpChannel.CreateAsync("127.0.0.1", state);
        var target = new IPEndPoint(IPAddress.Loopback, channel.LocalEndPoint.Port);

        using var wrongAddress = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        using var goodA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var goodB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await wrongAddress.SendAsync(
            new ServerUdpFrame(0x1111, 0, 1, ReadOnlyMemory<byte>.Empty).ToWireBytes(), target);
        await goodA.SendAsync(
            new ServerUdpFrame(0x2222, 0, 2, ReadOnlyMemory<byte>.Empty).ToWireBytes(), target);

        using var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        (ServerUdpFrame first, IPEndPoint senderA) = await channel.ReceiveAsync(ctsA.Token);
        Assert.Equal(0x2222, first.Opcode);
        Assert.Equal(((IPEndPoint)goodA.Client.LocalEndPoint!).Port, senderA.Port);

        // A different source UDP port on the same configured IPv4 is still
        // admitted at the application-level gate.
        await goodB.SendAsync(
            new ServerUdpFrame(0x3333, 0, 3, ReadOnlyMemory<byte>.Empty).ToWireBytes(), target);

        using var ctsB = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        (ServerUdpFrame second, IPEndPoint senderB) = await channel.ReceiveAsync(ctsB.Token);
        Assert.Equal(0x3333, second.Opcode);
        Assert.NotEqual(senderA.Port, senderB.Port);
    }

    [Fact]
    public async Task RoutedChannel_ActiveSendRequiresIdentityPortAndForwardingGate()
    {
        using var server = BindGameplayTestServer(out int serverPort);
        var state = new Net7TransportState();
        await using var channel = await RoutedSectorUdpChannel.CreateAsync("127.0.0.1", state);

        channel.CommitGameplayPort(serverPort);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendToActiveAsync(0x0044, ReadOnlyMemory<byte>.Empty));

        state.CommitValidatedFixedSessionId(0xAABBCCDD);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendToActiveAsync(0x0044, ReadOnlyMemory<byte>.Empty));

        channel.SetGameplayForwardingActive(true);
        await channel.SendToActiveAsync(0x0044, ReadOnlyMemory<byte>.Empty);

        ServerUdpFrame frame = ServerUdpFrame.Parse((await server.ReceiveAsync()).Buffer);
        Assert.Equal(0x0044, frame.Opcode);
        Assert.Equal(0xAABBCCDDu, frame.FixedSessionId);
        Assert.Equal(0u, frame.Sequence);

        channel.SetGameplayForwardingActive(false);
        Assert.Equal(serverPort, state.ConfirmedGameplayPort);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendToActiveAsync(0x0044, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task ConcurrentMasterSendsRemainWholeFramesAndAdvanceSequenceOnceEach()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync(
            "127.0.0.1", serverPort, state);

        const int count = 32;
        Task[] sends = Enumerable.Range(0, count)
            .Select(i => channel.SendNormalAsync(
                (ushort)(0x4000 + i),
                BitConverter.GetBytes(i)))
            .ToArray();

        await Task.WhenAll(sends);

        var seenSequences = new HashSet<uint>();
        var seenOpcodes = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            ServerUdpFrame frame = ServerUdpFrame.Parse((await server.ReceiveAsync()).Buffer);
            seenSequences.Add(frame.Sequence);
            seenOpcodes.Add(frame.Opcode);
            Assert.Equal(4, frame.Payload.Length);
        }

        Assert.Equal(count, seenSequences.Count);
        Assert.Equal(count, seenOpcodes.Count);
        Assert.Equal((uint)count, channel.Sequence.NextNormalSequence);
        Assert.Equal(Enumerable.Range(0, count).Select(i => (uint)i).OrderBy(x => x), seenSequences.OrderBy(x => x));
    }


    [Fact]
    public async Task Bootstrap_ProcessesFrequencyControlButOnly1001SatisfiesAck()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync("127.0.0.1", serverPort, state);

        Task serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await server.ReceiveAsync();
            ServerUdpFrame bootstrap = ServerUdpFrame.Parse(request.Buffer);
            Assert.Equal(0x1000, bootstrap.Opcode);
            Assert.Equal(new byte[] { 0xF6, 0, 0, 0 }, bootstrap.Payload.ToArray());

            byte[] multiplier = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(multiplier, 5);
            await server.SendAsync(new ServerUdpFrame(0x1007, 0, 42, multiplier).ToWireBytes(), request.RemoteEndPoint);
            await server.SendAsync(new ServerUdpFrame(0x1001, 0, 999, new byte[] { 1, 2, 3 }).ToWireBytes(), request.RemoteEndPoint);
        });

        ServerUdpFrame ack = await channel.BootstrapAsync(1, TimeSpan.FromMilliseconds(250));
        await serverTask;

        Assert.Equal(0x1001, ack.Opcode);
        Assert.Equal(999u, ack.Sequence); // sequence is deliberately not constrained for ACK
        Assert.Equal(5, channel.ServerFrequencyMultiplier);
        Assert.Equal(1u, channel.Sequence.NextNormalSequence);
    }

    [Fact]
    public async Task Bootstrap_ShortControlFramesAreIgnoredAndTheAckStillAdmits()
    {
        // 0040C910 passes 0040EED0/0040EB30/00414430 a receive-buffer pointer
        // without a payload-length check: short fields are stale bytes.
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync("127.0.0.1", serverPort, state);

        Task serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await server.ReceiveAsync();
            foreach (ushort opcode in new ushort[] { 0x1007, 0x100A, 0x200E })
                await server.SendAsync(new ServerUdpFrame(opcode, 0, 1, new byte[] { 9, 9 }).ToWireBytes(), request.RemoteEndPoint);
            await server.SendAsync(new ServerUdpFrame(0x1001, 0, 2, ReadOnlyMemory<byte>.Empty).ToWireBytes(), request.RemoteEndPoint);
        });

        ServerUdpFrame ack = await channel.BootstrapAsync(1, TimeSpan.FromMilliseconds(250));
        await serverTask;
        Assert.Equal(0x1001, ack.Opcode);
        Assert.Equal(20, channel.ServerFrequencyMultiplier);
    }

    [Fact]
    public async Task Bootstrap_InvalidFrequencyNormalizesToTwo()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync("127.0.0.1", serverPort, state);

        Task serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await server.ReceiveAsync();
            byte[] multiplier = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(multiplier, 99);
            await server.SendAsync(new ServerUdpFrame(0x1007, 0, 1, multiplier).ToWireBytes(), request.RemoteEndPoint);
            await server.SendAsync(new ServerUdpFrame(0x1001, 0, 2, ReadOnlyMemory<byte>.Empty).ToWireBytes(), request.RemoteEndPoint);
        });

        await channel.BootstrapAsync(1, TimeSpan.FromMilliseconds(250));
        await serverTask;
        Assert.Equal(2, channel.ServerFrequencyMultiplier);
    }

    [Fact]
    public async Task Bootstrap_RetriesWithFreshNormalSequenceAndFailsAfterBudget()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync("127.0.0.1", serverPort, state);

        Task<ServerUdpFrame[]> collector = Task.Run(async () =>
        {
            var frames = new List<ServerUdpFrame>();
            for (int i = 0; i < 3; i++)
                frames.Add(ServerUdpFrame.Parse((await server.ReceiveAsync()).Buffer));
            return frames.ToArray();
        });

        await Assert.ThrowsAsync<TimeoutException>(() =>
            channel.BootstrapAsync(3, TimeSpan.FromMilliseconds(20)));
        ServerUdpFrame[] requests = await collector;

        Assert.Equal(new uint[] { 0, 1, 2 }, requests.Select(f => f.Sequence));
        Assert.All(requests, f => Assert.Equal(0x1000, f.Opcode));
        Assert.Equal(3u, channel.Sequence.NextNormalSequence);
    }

    [Theory]
    [InlineData(0x1009)]
    [InlineData(0x100B)]
    public async Task Bootstrap_TerminalControlsAbortInsteadOfPretendingToBeTimeout(ushort opcode)
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync("127.0.0.1", serverPort, state);

        Task serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await server.ReceiveAsync();
            byte[] payload = opcode == 0x1009 ? new byte[4] : Array.Empty<byte>();
            if (opcode == 0x1009)
                BinaryPrimitives.WriteInt32LittleEndian(payload, 545);
            await server.SendAsync(new ServerUdpFrame(opcode, 0, 0, payload).ToWireBytes(), request.RemoteEndPoint);
        });

        if (opcode == 0x1009)
        {
            Net7IncompatibleVersionException ex = await Assert.ThrowsAsync<Net7IncompatibleVersionException>(() =>
                channel.BootstrapAsync(1, TimeSpan.FromMilliseconds(250)));
            Assert.Equal(545, ex.VersionHundredths);
            Assert.Equal(5.45d, ex.LatestVersion!.Value, 10);
        }
        else
        {
            await Assert.ThrowsAsync<Net7ServerStartingException>(() => channel.BootstrapAsync(1, TimeSpan.FromMilliseconds(250)));
        }
        await serverTask;
    }

    [Fact]
    public async Task Bootstrap_Short1009StillEndsAsIncompatibleVersionWithoutAVersion()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync("127.0.0.1", serverPort, state);

        Task serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await server.ReceiveAsync();
            await server.SendAsync(new ServerUdpFrame(0x1009, 0, 0, new byte[3]).ToWireBytes(), request.RemoteEndPoint);
        });

        // Net7Proxy 0040BE70 formats the version from stale buffer bytes; the
        // frame is terminal regardless, so no version value is reported.
        var ex = await Assert.ThrowsAsync<Net7IncompatibleVersionException>(() =>
            channel.BootstrapAsync(1, TimeSpan.FromMilliseconds(250)));
        Assert.Null(ex.VersionHundredths);
        await serverTask;
    }

    [Fact]
    public async Task Bootstrap_OuterGlobalErrorAbortsWithMappedError()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var state = new Net7TransportState();
        await using var channel = await MasterControlUdpChannel.ConnectAsync("127.0.0.1", serverPort, state);

        Task serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await server.ReceiveAsync();
            byte[] selector = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(selector, 7);
            await server.SendAsync(new ServerUdpFrame(0x200E, 0, 0, selector).ToWireBytes(), request.RemoteEndPoint);
        });

        Net7GlobalErrorException ex = await Assert.ThrowsAsync<Net7GlobalErrorException>(() =>
            channel.BootstrapAsync(1, TimeSpan.FromMilliseconds(250)));
        await serverTask;
        Assert.Equal(14u, ex.Error.Code);
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

    [Fact]
    public async Task RoutedChannel_Entry01BestEffortSuppressesMissingRouteWithoutSequence()
    {
        var state = new Net7TransportState();
        await using var channel = await RoutedSectorUdpChannel.CreateAsync("127.0.0.1", state);

        await channel.SendToActiveBestEffortAsync(0x0002, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(0u, channel.Sequence.NextNormalSequence);

        state.CommitValidatedFixedSessionId(0xAABBCCDD);
        await channel.SendToActiveBestEffortAsync(0x0002, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(0u, channel.Sequence.NextNormalSequence);

        channel.CommitGameplayPort(Net7ServerOptions.CurrentMinGameplaySectorUdpPort);
        await channel.SendToActiveBestEffortAsync(0x0002, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(0u, channel.Sequence.NextNormalSequence);
    }

}
