using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Net7;
using EnbNext.Net7.LocalData;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbClientSectorEntryTests
{
    [Fact]
    public void EmbeddedCurrentLocalDataIsPinnedAndReadyWithoutExternalPaths()
    {
        Assert.Equal(19, Net7LocalDataAssets.CurrentGalaxyMap.Systems.Count);
        Assert.Equal(86, Net7LocalDataAssets.CurrentGalaxyMap.Sectors.Count);
        Assert.Equal(199, Net7LocalDataAssets.CurrentGalaxyMap.Gates.Count);
        Assert.Equal(5_530, Net7LocalDataAssets.CurrentCData.RecordCount);
    }

    [Fact]
    public async Task NormalClientFlow_SelectThenEnterGame_ReachesInWorldAndRemainsConnected()
    {
        await using var harness = await ClientEntryHarness.CreateAsync();
        string captureDirectory = Path.Combine(Path.GetTempPath(), "enbnext-client-entry-" + Guid.NewGuid().ToString("N"));
        await using var client = new EnbClient(harness.CreateClientOptions(
            captureDirectory,
            TimeSpan.FromMilliseconds(500)));

        Task accountServer = harness.RunAccountThroughSelectionAsync();
        IReadOnlyList<CharacterSummary> roster = await client.LoginAsync("pilot", "password");
        CharacterSummary character = Assert.Single(roster, x => !x.IsEmpty);
        _ = await client.SelectCharacterAsync(character);
        await accountServer;

        Assert.Equal(ClientLifecycleState.CharacterSelected, client.State);

        Task<SectorRouteResult> entering = client.EnterGameAsync(character);

        UdpReceiveResult master200F = await harness.MasterUdp.ReceiveAsync();
        ServerUdpFrame p200F = ServerUdpFrame.Parse(master200F.Buffer);
        Assert.Equal(0x200F, p200F.Opcode);
        Assert.Equal(0xAABBCCDDu, p200F.FixedSessionId);

        UdpReceiveResult master2008 = await harness.HandoffUdp.ReceiveAsync();
        ServerUdpFrame p2008 = ServerUdpFrame.Parse(master2008.Buffer);
        Assert.Equal(0x2008, p2008.Opcode);
        Assert.Equal(1079u, BinaryPrimitives.ReadUInt32LittleEndian(p2008.Payload.Span[..4]));

        byte[] masterReply = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(masterReply.AsSpan(0, 4), 0x12345678);
        BinaryPrimitives.WriteUInt16LittleEndian(masterReply.AsSpan(4, 2), checked((ushort)harness.GameplayPort));
        await harness.HandoffUdp.SendAsync(
            new ServerUdpFrame(0x2009, 0xAABBCCDD, 90, masterReply).ToWireBytes(),
            master2008.RemoteEndPoint);

        UdpReceiveResult sectorLoginDatagram = await harness.GameplayUdp.ReceiveAsync();
        ServerUdpFrame sectorLogin = ServerUdpFrame.Parse(sectorLoginDatagram.Buffer);
        Assert.Equal(0x0002, sectorLogin.Opcode);
        Assert.Equal(0xAABBCCDDu, sectorLogin.FixedSessionId);

        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x2011, 0xAABBCCDD, 91, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
            sectorLoginDatagram.RemoteEndPoint);
        byte[] startPayload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(startPayload, 0xCAFEBABE);
        await harness.GameplayUdp.SendAsync(
            new ServerUdpFrame(0x0005, 0xAABBCCDD, 92, startPayload).ToWireBytes(),
            sectorLoginDatagram.RemoteEndPoint);

        ServerUdpFrame requestTime = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        ServerUdpFrame startAck = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        ServerUdpFrame synthetic = ServerUdpFrame.Parse((await harness.GameplayUdp.ReceiveAsync()).Buffer);
        Assert.Equal(new ushort[] { 0x0044, 0x0006, 0x3004 }, new ushort[] { requestTime.Opcode, startAck.Opcode, synthetic.Opcode });

        SectorRouteResult route = await entering.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(harness.GameplayPort, route.GameplaySectorEndPoint.Port);
        Assert.Equal(0x12345678u, route.RetainedMasterResponseDword);
        Assert.Equal(ClientLifecycleState.InWorld, client.State);
        Assert.NotNull(client.World);
        Assert.Equal(0xCAFEBABEu, client.World!.LocalPlayerId);
        Assert.NotNull(client.LastWireCapturePath);
        Assert.DoesNotContain("\"capture-stop\"", ReadCaptureText(client.LastWireCapturePath!));

        byte[] timePayload=new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(timePayload.AsSpan(4),50000);
        BinaryPrimitives.WriteUInt32LittleEndian(timePayload.AsSpan(8),50000);
        await harness.GameplayUdp.SendAsync(new ServerUdpFrame(0x0034,0xAABBCCDD,93,timePayload).ToWireBytes(),sectorLoginDatagram.RemoteEndPoint);
        using(var waitTime=new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            while(client.World!.Time.CorrectionMilliseconds==0)await Task.Delay(5,waitTime.Token);
        long recordedCorrection=client.World!.Time.CorrectionMilliseconds;
        string captureText = await WaitForCaptureStopAsync(client.LastWireCapturePath!);
        Assert.Contains("replay-time-synchronization-v2",captureText);
        Assert.Contains("\"name\":\"sector-entry-inworld\"", captureText);
        Assert.Contains("\"name\":\"sector-entry-post-inworld-window\"", captureText);
        Assert.Contains("\"value\":\"sector-entry-post-inworld-window\"", captureText);

        // Success is a real connected game session now. The Client facade is
        // intentionally not disposed by EnterGameAsync or by the application success path.
        Assert.NotEqual(ClientLifecycleState.Disconnected, client.State);
        // The recorded bootstrap and raw datagrams must enter a fresh production
        // session through its socket pumps, not through a manufactured snapshot.
        string replayPath=client.LastWireCapturePath!;
        await client.DisposeAsync();
        var recording=EnbNext.Net7.Diagnostics.Net7WireReplayRecording.Load(replayPath);
        for(int trial=0;trial<2;trial++)
        {
            await using var replayClient=new EnbClient(new EnbClientOptions { ServerHost="127.0.0.1",WireCaptureDirectory=null });
            var replayProblems=new System.Collections.Concurrent.ConcurrentQueue<string>();
            replayClient.Diagnostic+=message=>{if(message.Contains("previously constructed normal UDP packet"))replayProblems.Enqueue(message);};
            await using var replay=await replayClient.PrepareRecordedReplayAsync(replayPath,
                recording.EndMicroseconds-100_000,recording.EndMicroseconds);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task playback=replay.RunAsync(timeout.Token);
            await replayClient.CompleteRecordedReplayEntryAsync(timeout.Token);
            Assert.Equal(ClientLifecycleState.InWorld,replayClient.State);
            Assert.Equal(0xCAFEBABEu,replayClient.World!.LocalPlayerId);
            Assert.True(System.Net.IPAddress.IsLoopback(replayClient.Route!.GameplaySectorEndPoint.Address));
            await playback;
            Assert.Equal(recordedCorrection,replayClient.World!.Time.CorrectionMilliseconds);
            Assert.True(replay.InboundDatagrams>=3);
            Assert.True(replay.OutboundDatagrams>=3);
            Assert.Empty(replayProblems);
        }
    }

    private sealed class ClientEntryHarness : IAsyncDisposable
    {
        private readonly TcpListener _accountListener;

        private ClientEntryHarness(UdpClient masterUdp, UdpClient handoffUdp, UdpClient gameplayUdp, TcpListener accountListener)
        {
            MasterUdp = masterUdp;
            HandoffUdp = handoffUdp;
            GameplayUdp = gameplayUdp;
            _accountListener = accountListener;
        }

        public UdpClient MasterUdp { get; }
        public UdpClient HandoffUdp { get; }
        public UdpClient GameplayUdp { get; }
        public int GameplayPort => ((IPEndPoint)GameplayUdp.Client.LocalEndPoint!).Port;

        public static Task<ClientEntryHarness> CreateAsync()
        {
            var master = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var handoff = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var gameplay = BindGameplayPort();
            var account = new TcpListener(IPAddress.Loopback, 0);
            account.Start();
            return Task.FromResult(new ClientEntryHarness(master, handoff, gameplay, account));
        }

        public EnbClientOptions CreateClientOptions(
            string? wireCaptureDirectory = null,
            TimeSpan? postInWorldCaptureDuration = null)
            => new()
            {
                ServerHost = "127.0.0.1",
                MasterControlUdpPort = ((IPEndPoint)MasterUdp.Client.LocalEndPoint!).Port,
                AccountTcpPort = ((IPEndPoint)_accountListener.LocalEndpoint).Port,
                MasterHandoffUdpPort = ((IPEndPoint)HandoffUdp.Client.LocalEndPoint!).Port,
                WireCaptureDirectory = wireCaptureDirectory,
                SectorEntryPostInWorldCaptureDuration = postInWorldCaptureDuration ?? TimeSpan.FromSeconds(30),
            };

        public async Task RunAccountThroughSelectionAsync()
        {
            UdpReceiveResult bootstrapRequest = await MasterUdp.ReceiveAsync();
            ServerUdpFrame bootstrap = ServerUdpFrame.Parse(bootstrapRequest.Buffer);
            Assert.Equal(0x1000, bootstrap.Opcode);
            await MasterUdp.SendAsync(
                new ServerUdpFrame(0x1001, 0, 123, ReadOnlyMemory<byte>.Empty).ToWireBytes(),
                bootstrapRequest.RemoteEndPoint);

            using TcpClient accountClient = await _accountListener.AcceptTcpClientAsync();
            using NetworkStream stream = accountClient.GetStream();

            Assert.Equal(0x3002, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2010, (await ReadPacketAsync(stream)).Header.Opcode);
            Assert.Equal(0x2000, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x2001, Encoding.Latin1.GetBytes("Valid=TRUE\nTicket=pilot-token\n"));

            Assert.Equal(0x2002, (await ReadPacketAsync(stream)).Header.Opcode);
            await WritePacketAsync(stream, 0x0070, TestProtocolFixtures.BuildRoster());

            Packet selection = await ReadPacketAsync(stream);
            Assert.Equal(0x2004, selection.Header.Opcode);
            await WritePacketAsync(stream, 0x006F, TestProtocolFixtures.BuildAvatarSelectionPayload(0, 0xAABBCCDD));
        }

        public ValueTask DisposeAsync()
        {
            _accountListener.Stop();
            MasterUdp.Dispose();
            HandoffUdp.Dispose();
            GameplayUdp.Dispose();
            return ValueTask.CompletedTask;
        }

        private static UdpClient BindGameplayPort()
        {
            for (int port = 3501; port <= 4000; port++)
            {
                try { return new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); }
                catch (SocketException) { }
            }
            throw new InvalidOperationException("No loopback gameplay port available in 3501..4000.");
        }

        private static async Task<Packet> ReadPacketAsync(NetworkStream stream)
        {
            byte[] headerBytes = new byte[PacketHeader.WireSize];
            await ReadExactAsync(stream, headerBytes);
            PacketHeader header = PacketHeader.Read(headerBytes);
            byte[] payload = new byte[header.PayloadLength];
            await ReadExactAsync(stream, payload);
            return new Packet(header, payload);
        }

        private static Task WritePacketAsync(NetworkStream stream, ushort opcode, ReadOnlyMemory<byte> payload)
            => stream.WriteAsync(Packet.ForOpcode(opcode, payload).ToWireBytes()).AsTask();

        private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> destination)
        {
            int offset = 0;
            while (offset < destination.Length)
            {
                int read = await stream.ReadAsync(destination[offset..]);
                if (read == 0)
                    throw new EndOfStreamException("Loopback account client disconnected unexpectedly.");
                offset += read;
            }
        }
    }

    private static async Task<string> WaitForCaptureStopAsync(string path)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            string text = ReadCaptureText(path);
            if (text.Contains("\"capture-stop\"", StringComparison.Ordinal))
                return text;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Capture did not stop within the expected test window: {path}");
    }

    private static string ReadCaptureText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
