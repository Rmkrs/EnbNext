using System.Net;
using System.Net.Sockets;
using EnbNext.Net7.Diagnostics;

namespace EnbNext.Net7.Transport;

/// <summary>
/// Current Net7 routed UDP channel. One unconnected socket addresses server UDP
/// 3808 during Master handoff and the learned dynamic gameplay-sector port later.
/// </summary>
public sealed class RoutedSectorUdpChannel : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly Net7TransportState _transportState;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly Net7WireCapture? _wireCapture;
    private int _disposed;

    private RoutedSectorUdpChannel(
        Socket socket,
        IPAddress serverAddress,
        Net7TransportState transportState,
        Action<string>? trace,
        Net7WireCapture? wireCapture)
    {
        _socket = socket;
        ServerAddress = serverAddress;
        _transportState = transportState;
        Trace = trace;
        _wireCapture = wireCapture;
    }

    public IPAddress ServerAddress { get; }
    public IPEndPoint LocalEndPoint => (IPEndPoint)(_socket.LocalEndPoint
        ?? throw new ObjectDisposedException(nameof(RoutedSectorUdpChannel)));
    public Action<string>? Trace { get; }
    public Net7UdpSequenceState Sequence { get; } = new();
    public int? ActiveGameplayPort => _transportState.ConfirmedGameplayPort;
    public bool GameplayForwardingActive => _transportState.GameplayForwardingActive;

    public event Action<ServerUdpFrame, IPEndPoint>? FrameSent;
    public event Action<ServerUdpFrame, IPEndPoint>? FrameReceived;

    public static async Task<RoutedSectorUdpChannel> CreateAsync(
        string host,
        Net7TransportState transportState,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default,
        Net7WireCapture? wireCapture = null)
    {
        IPAddress address = await Net7AddressResolver.ResolveIpv4Async(host, cancellationToken).ConfigureAwait(false);
        return await CreateAsync(address, transportState, trace, cancellationToken, wireCapture).ConfigureAwait(false);
    }

    public static Task<RoutedSectorUdpChannel> CreateAsync(
        IPAddress address,
        Net7TransportState transportState,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default,
        Net7WireCapture? wireCapture = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(transportState);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException($"Current direct Net7 transport requires IPv4; got {address}.");

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            trace?.Invoke($"routed UDP local -> {socket.LocalEndPoint}");
            return Task.FromResult(new RoutedSectorUdpChannel(socket, address, transportState, trace, wireCapture));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void CommitGameplayPort(int port)
    {
        ThrowIfDisposed();
        _transportState.CommitGameplayPort(port);
        Trace?.Invoke($"confirmed gameplay-sector UDP -> {ServerAddress}:{port}");
    }

    public void SetGameplayForwardingActive(bool active)
    {
        ThrowIfDisposed();
        _transportState.SetGameplayForwardingActive(active);
        Trace?.Invoke(active ? "gameplay-sector UDP forwarding active" : "gameplay-sector UDP forwarding inactive");
    }

    public async Task SendNormalAsync(
        int destinationPort,
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (destinationPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destinationPort));

        ThrowIfDisposed();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            uint sequence = Sequence.AllocateNormalSequence();
            uint fixedSessionId = _transportState.FixedSessionId;
            var frame = new ServerUdpFrame(opcode, fixedSessionId, sequence, payload);
            await SendFrameCoreAsync(destinationPort, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task SendToActiveAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_transportState.TryGetActiveGameplayRoute(out uint fixedSessionId, out int port))
            {
                throw new InvalidOperationException(
                    "No active gameplay-sector route exists. A retained port alone does not authorize forwarding.");
            }

            uint sequence = Sequence.AllocateNormalSequence();
            var frame = new ServerUdpFrame(opcode, fixedSessionId, sequence, payload);
            await SendFrameCoreAsync(port, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Compatibility send. The current gameplay-login handler cannot
    /// observe route-helper suppression or socket send failure, but a normal
    /// sequence is still consumed once frame construction begins. Cancellation
    /// remains an owned local control and is therefore not swallowed.
    /// </summary>
    public async Task SendToActiveBestEffortAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_transportState.TryGetActiveGameplayRoute(out uint fixedSessionId, out int port))
                return;

            uint sequence = Sequence.AllocateNormalSequence();
            var frame = new ServerUdpFrame(opcode, fixedSessionId, sequence, payload);
            try
            {
                await SendFrameCoreAsync(port, frame, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Trace?.Invoke($"routed UDP best-effort send failed for 0x{opcode:X4}: {ex.Message}");
                return;
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                Trace?.Invoke($"routed UDP best-effort short/failed send for 0x{opcode:X4}: {ex.Message}");
                return;
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<(ServerUdpFrame Frame, IPEndPoint Sender)> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            byte[] buffer = new byte[ushort.MaxValue];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _socket.ReceiveFromAsync(
                        buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Current raw receive failure is retryable and must
                    // not dispatch or become an admitted semantic stop.
                    Trace?.Invoke($"routed UDP receive failed; retrying: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.ReceivedBytes <= 0)
                {
                    Trace?.Invoke("routed UDP receive returned no data; retrying");
                    continue;
                }

                var sender = (IPEndPoint)result.RemoteEndPoint;
                _wireCapture?.RecordFrame(
                    Net7WireDirection.Inbound,
                    Net7WireTransport.Udp,
                    "routed-udp",
                    LocalEndPoint,
                    sender,
                    buffer.AsSpan(0, result.ReceivedBytes));
                if (!sender.Address.Equals(ServerAddress))
                {
                    Trace?.Invoke($"routed UDP dropped datagram from unexpected address {sender.Address}; expected {ServerAddress}");
                    continue;
                }

                ServerUdpFrame frame;
                try
                {
                    frame = ServerUdpFrame.Parse(buffer.AsSpan(0, result.ReceivedBytes));
                }
                catch (InvalidDataException ex)
                {
                    Trace?.Invoke($"routed UDP dropped malformed datagram from {sender}: {ex.Message}");
                    continue;
                }

                // Compare both UDP channels against the single authoritative
                // master/control identity, not a channel-local copy.
                if (!_transportState.IsIncomingFixedSessionCompatible(frame.FixedSessionId))
                {
                    Trace?.Invoke(
                        $"routed UDP dropped fixed/session mismatch from {sender.Port}: " +
                        $"expected 0x{_transportState.FixedSessionId:X8}, got 0x{frame.FixedSessionId:X8} " +
                        $"on 0x{frame.Opcode:X4}");
                    continue;
                }

                FrameReceived?.Invoke(frame, sender);
                return (frame, sender);
            }
        }
        finally
        {
            _receiveGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SendFrameCoreAsync(
        int destinationPort,
        ServerUdpFrame frame,
        CancellationToken cancellationToken)
    {
        var endpoint = new IPEndPoint(ServerAddress, destinationPort);
        byte[] wire = frame.ToWireBytes();
        _wireCapture?.RecordFrame(
            Net7WireDirection.Outbound,
            Net7WireTransport.Udp,
            "routed-udp",
            LocalEndPoint,
            endpoint,
            wire);
        int sent = await _socket.SendToAsync(wire, SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false);
        if (sent != wire.Length)
            throw new IOException($"Net7 routed UDP send wrote {sent}/{wire.Length} bytes.");
        FrameSent?.Invoke(frame, endpoint);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RoutedSectorUdpChannel));
    }
}
