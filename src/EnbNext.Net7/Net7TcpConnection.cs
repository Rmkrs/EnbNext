using System.Net;
using System.Net.Sockets;
using EnbNext.Protocol.Net;
using EnbNext.Net7.Diagnostics;

namespace EnbNext.Net7;

/// <summary>
/// Plain server-facing TCP connection used by the current account/login path.
/// This leg does not use the retail Westwood RSA/RC4 handshake.
///
/// TCP is a byte stream: writes and reads are explicitly serialized so callers
/// cannot interleave frame bytes or race independent receive cursors.
/// </summary>
public sealed class Net7TcpConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly Net7WireCapture? _wireCapture;
    private int _disposed;

    private Net7TcpConnection(TcpClient client, IPEndPoint remoteEndPoint, Net7WireCapture? wireCapture)
    {
        _client = client;
        _stream = client.GetStream();
        RemoteEndPoint = remoteEndPoint;
        _wireCapture = wireCapture;
    }

    public IPEndPoint RemoteEndPoint { get; }
    public IPEndPoint LocalEndPoint => (IPEndPoint)(_client.Client.LocalEndPoint
        ?? throw new ObjectDisposedException(nameof(Net7TcpConnection)));

    public event Action<Packet>? PacketSent;
    public event Action<Packet>? PacketReceived;

    public static async Task<Net7TcpConnection> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default,
        Net7WireCapture? wireCapture = null)
    {
        IPAddress address = await ResolveIpv4Async(host, cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(address, port, cancellationToken, wireCapture).ConfigureAwait(false);
    }

    public static async Task<Net7TcpConnection> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken = default,
        Net7WireCapture? wireCapture = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException($"Current direct Net7 transport requires IPv4; got {address}.");

        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };

        try
        {
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            return new Net7TcpConnection(client, new IPEndPoint(address, port), wireCapture);
        }
        catch
        {
            // Clean ownership rule: failed connect owns and disposes the
            // socket immediately; no unpublished connection/socket leak survives.
            client.Dispose();
            throw;
        }
    }

    public async Task SendAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ThrowIfDisposed();

        byte[] wire = packet.ToWireBytes();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _wireCapture?.RecordFrame(
                Net7WireDirection.Outbound,
                Net7WireTransport.Tcp,
                "account-tcp",
                LocalEndPoint,
                RemoteEndPoint,
                wire);

            // NetworkStream.WriteAsync completes the requested buffer or throws.
            // A complete encoded frame is therefore one serialized write request;
            // we do not reproduce Net7Proxy's one-send/ignore-short-result defect.
            await _stream.WriteAsync(wire, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            PacketSent?.Invoke(packet);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<Packet> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            byte[] headerBytes = new byte[PacketHeader.WireSize];
            await ReadExactAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            PacketHeader header = PacketHeader.Read(headerBytes);

            // Safety rule: net7proxy 00403C30 computes (size-4)&0xFFFF; below 4 that is
            // >= 25000, so the payload becomes whatever one recv returns (transport
            // chunking, up to an overflow of its 50,400-byte buffer). No defined frame
            // boundary follows, so the account stream ends here.
            if (header.Size < PacketHeader.WireSize)
                throw new InvalidDataException($"Net7 TCP frame length {header.Size} is smaller than the header.");

            byte[] payload = new byte[header.PayloadLength];
            if (payload.Length != 0)
                await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);

            byte[] wire = new byte[header.Size];
            headerBytes.CopyTo(wire, 0);
            payload.CopyTo(wire, PacketHeader.WireSize);
            _wireCapture?.RecordFrame(
                Net7WireDirection.Inbound,
                Net7WireTransport.Tcp,
                "account-tcp",
                LocalEndPoint,
                RemoteEndPoint,
                wire);

            var packet = new Packet(header, payload);
            PacketReceived?.Invoke(packet);
            return packet;
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stream.Dispose();
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task ReadExactAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await _stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Net7 TCP connection closed with {destination.Length - offset} bytes still expected.");
            }

            offset += read;
        }
    }

    private static async Task<IPAddress> ResolveIpv4Async(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException($"Current direct Net7 transport requires IPv4; got {parsed}.");
            return parsed;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"Host '{host}' did not resolve to IPv4.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(Net7TcpConnection));
    }
}
