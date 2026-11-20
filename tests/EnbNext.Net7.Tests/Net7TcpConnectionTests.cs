using System.Net;
using System.Net.Sockets;
using System.Reflection;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7TcpConnectionTests
{
    [Fact]
    public async Task ReceiveAsync_ReassemblesArbitrarilyFragmentedTcpFrame()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        await using Net7TcpConnection connection = await Net7TcpConnection.ConnectAsync("127.0.0.1", port);
        using TcpClient peer = await acceptTask;
        listener.Stop();

        Packet expected = Packet.ForOpcode(0x1234, new byte[] { 1, 2, 3, 4, 5 });
        byte[] wire = expected.ToWireBytes();
        NetworkStream peerStream = peer.GetStream();

        Task writer = Task.Run(async () =>
        {
            foreach (byte b in wire)
            {
                await peerStream.WriteAsync(new byte[] { b });
                await Task.Yield();
            }
        });

        Packet actual = await connection.ReceiveAsync();
        await writer;

        Assert.Equal(expected.Header, actual.Header);
        Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());
    }

    [Fact]
    public async Task ReceiveAsync_FrameBelowHeaderSizeEndsTheAccountStream()
    {
        // Safety rule: net7proxy 00403C30 has no defined frame boundary for a
        // size below the 4-byte header (one recv becomes the payload).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        await using Net7TcpConnection connection = await Net7TcpConnection.ConnectAsync("127.0.0.1", port);
        using TcpClient peer = await acceptTask;
        listener.Stop();
        await peer.GetStream().WriteAsync(new byte[] { 2, 0, 0x34, 0x12, 9, 9 });
        await Assert.ThrowsAsync<InvalidDataException>(() => connection.ReceiveAsync());
    }

    [Fact]
    public async Task ConcurrentSends_ArriveAsCompleteSerializedFrames()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        await using Net7TcpConnection connection = await Net7TcpConnection.ConnectAsync("127.0.0.1", port);
        using TcpClient peer = await acceptTask;
        listener.Stop();

        const int count = 40;
        Task[] sends = Enumerable.Range(0, count)
            .Select(i => connection.SendAsync(Packet.ForOpcode(
                (ushort)(0x5000 + i),
                Enumerable.Repeat((byte)i, 257).ToArray())))
            .ToArray();

        Task<List<Packet>> reader = ReadPacketsAsync(peer.GetStream(), count);
        await Task.WhenAll(sends);
        List<Packet> packets = await reader;

        Assert.Equal(count, packets.Count);
        Assert.Equal(count, packets.Select(p => p.Header.Opcode).Distinct().Count());
        foreach (Packet packet in packets)
        {
            int i = packet.Header.Opcode - 0x5000;
            Assert.Equal(257, packet.Payload.Length);
            Assert.All(packet.Payload.ToArray(), b => Assert.Equal((byte)i, b));
        }
    }

    [Fact]
    public async Task AccountPump_EofUnpublishesAndDisposesTcp3807Owner()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        Net7TcpConnection connection = await Net7TcpConnection.ConnectAsync("127.0.0.1", port);
        TcpClient peer = await acceptTask;
        listener.Stop();

        await using var session = new Net7Session(new Net7ServerOptions());
        FieldInfo accountField = typeof(Net7Session).GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(Net7Session), "_account");
        FieldInfo pumpTaskField = typeof(Net7Session).GetField("_accountPumpTask", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(Net7Session), "_accountPumpTask");
        MethodInfo startPump = typeof(Net7Session).GetMethod("StartAccountResponsePump", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Net7Session), "StartAccountResponsePump");

        accountField.SetValue(session, connection);
        startPump.Invoke(session, null);

        peer.Dispose();
        Task pump = (Task)(pumpTaskField.GetValue(session)
            ?? throw new InvalidOperationException("Account pump was not published."));
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(accountField.GetValue(session));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            connection.SendAsync(Packet.ForOpcode(0x1234, ReadOnlyMemory<byte>.Empty)));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        Net7TcpConnection connection = await Net7TcpConnection.ConnectAsync("127.0.0.1", port);
        using TcpClient peer = await acceptTask;
        listener.Stop();

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            connection.SendAsync(Packet.ForOpcode(0x1234, ReadOnlyMemory<byte>.Empty)));
    }

    private static async Task<List<Packet>> ReadPacketsAsync(NetworkStream stream, int count)
    {
        var result = new List<Packet>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] headerBytes = await ReadExactAsync(stream, PacketHeader.WireSize);
            PacketHeader header = PacketHeader.Read(headerBytes);
            byte[] payload = await ReadExactAsync(stream, header.PayloadLength);
            result.Add(new Packet(header, payload));
        }

        return result;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }

        return buffer;
    }
}
