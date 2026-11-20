using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.Diagnostics;

namespace EnbNext.Net7.Transport;

/// <summary>Connected current-Net7 Master/control UDP channel, normally server UDP 3806.</summary>
public sealed class MasterControlUdpChannel : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly Net7TransportState _transportState;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly Action<string>? _trace;
    private readonly Net7WireCapture? _wireCapture;
    private int _disposed;

    private MasterControlUdpChannel(
        Socket socket,
        IPEndPoint remoteEndPoint,
        Net7TransportState transportState,
        Action<string>? trace,
        Net7WireCapture? wireCapture)
    {
        _socket = socket;
        _transportState = transportState;
        RemoteEndPoint = remoteEndPoint;
        _trace = trace;
        _wireCapture = wireCapture;
    }

    public IPEndPoint RemoteEndPoint { get; }
    public IPEndPoint LocalEndPoint => (IPEndPoint)(_socket.LocalEndPoint
        ?? throw new ObjectDisposedException(nameof(MasterControlUdpChannel)));
    public Net7UdpSequenceState Sequence { get; } = new();
    public int ServerFrequencyMultiplier { get; private set; } = 20;

    public event Action<ServerUdpFrame>? FrameSent;
    public event Action<ServerUdpFrame>? FrameReceived;

    public static async Task<MasterControlUdpChannel> ConnectAsync(
        string host,
        int port,
        Net7TransportState transportState,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default,
        Net7WireCapture? wireCapture = null)
    {
        IPAddress address = await Net7AddressResolver.ResolveIpv4Async(host, cancellationToken).ConfigureAwait(false);
        return await ConnectAsync(address, port, transportState, trace, cancellationToken, wireCapture).ConfigureAwait(false);
    }

    public static async Task<MasterControlUdpChannel> ConnectAsync(
        IPAddress address,
        int port,
        Net7TransportState transportState,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default,
        Net7WireCapture? wireCapture = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(transportState);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException($"Current direct Net7 transport requires IPv4; got {address}.");

        var remote = new IPEndPoint(address, port);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            await socket.ConnectAsync(remote, cancellationToken).ConfigureAwait(false);
            trace?.Invoke($"master/control UDP -> {remote}");
            return new MasterControlUdpChannel(socket, remote, transportState, trace, wireCapture);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<ServerUdpFrame> BootstrapAsync(
        int attempts,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken = default)
    {
        if (attempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempts));
        if (attemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));

        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x000000F6);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _trace?.Invoke($"bootstrap {attempt}/{attempts}: UDP 0x1000 payload=0x000000F6");

            // Construction/sequence advances even if the actual send
            // fails. The current helper then still waits its post-send cadence.
            try
            {
                await SendNormalAsync(0x1000, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                _trace?.Invoke($"bootstrap send failed: {ex.GetType().Name}: {ex.Message}");
            }

            ServerUdpFrame? admittedAck = null;
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = Task.Delay(attemptTimeout, cancellationToken);

            while (!delay.IsCompleted)
            {
                Task<ServerUdpFrame> receive = ReceiveAsync(attemptCts.Token);
                Task completed = await Task.WhenAny(receive, delay).ConfigureAwait(false);
                if (completed == delay)
                {
                    if (receive.IsCompletedSuccessfully)
                    {
                        ServerUdpFrame edgeFrame = await receive.ConfigureAwait(false);
                        ProcessBootstrapFrame(edgeFrame, ref admittedAck);
                    }
                    else
                    {
                        attemptCts.Cancel();
                        try { await receive.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    }
                    break;
                }

                ServerUdpFrame frame = await receive.ConfigureAwait(false);
                ProcessBootstrapFrame(frame, ref admittedAck);
            }

            // The current helper observes the ACK only after the full
            // post-send cadence, including the fifth attempt.
            if (admittedAck is not null)
            {
                _trace?.Invoke("bootstrap <- admitted UDP 0x1001 ACK");
                return admittedAck;
            }

            _trace?.Invoke("bootstrap attempt timed out");
        }

        throw new TimeoutException($"Net7 server did not answer UDP 0x1000 with 0x1001 after {attempts} attempts.");
    }

    private void ProcessBootstrapFrame(ServerUdpFrame frame, ref ServerUdpFrame? admittedAck)
    {
        switch (frame.Opcode)
        {
            case 0x1001:
                // Payload and outer sequence are deliberately ignored.
                admittedAck ??= frame;
                return;

            // Safety rule: Net7Proxy 0040C910 hands 0040EED0/0040EB30/00414430 a
            // pointer into its 0x10000 receive buffer without a payload-length
            // check; a short frame's field is stale bytes. Ignore the frame.
            case 0x1007:
                if (frame.Payload.Length < 4)
                {
                    _trace?.Invoke("bootstrap ignored short UDP 0x1007");
                    return;
                }
                uint requested = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[..4]);
                ServerFrequencyMultiplier = requested is >= 1 and <= 20 ? checked((int)requested) : 2;
                _trace?.Invoke($"bootstrap control <- UDP 0x1007 frequency multiplier {ServerFrequencyMultiplier}");
                return;

            case 0x1009:
                throw Net7IncompatibleVersionException.FromPayload(frame.Payload.Span);

            case 0x100A:
                if (frame.Payload.Length < 4)
                {
                    _trace?.Invoke("bootstrap ignored short UDP 0x100A");
                    return;
                }
                uint shutdownId = BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload.Span[..4]);
                if (shutdownId == _transportState.ControlSessionId)
                    throw new Net7ServerShutdownException();
                _trace?.Invoke($"bootstrap ignored UDP 0x100A for nonmatching ID 0x{shutdownId:X8}");
                return;

            case 0x100B:
                throw new Net7ServerStartingException();

            case 0x200E:
                if (frame.Payload.Length < 4)
                {
                    _trace?.Invoke("bootstrap ignored short UDP 0x200E");
                    return;
                }
                throw new Net7GlobalErrorException(Net7GlobalError.DecodeOuterUdp200E(
                    frame.Payload.Span,
                    Net7GlobalErrorIngress.BootstrapMasterControlUdp,
                    Net7GlobalErrorSessionContext.Bootstrap));

            default:
                _trace?.Invoke($"bootstrap ignored UDP 0x{frame.Opcode:X4} while waiting for 0x1001");
                return;
        }
    }

    public async Task SendNormalAsync(
        ushort opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // Construct/advance sequence before transport success is known.
            uint sequence = Sequence.AllocateNormalSequence();
            uint fixedSessionId = _transportState.ControlSessionId;
            await SendFrameCoreAsync(
                new ServerUdpFrame(opcode, fixedSessionId, sequence, payload),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task SendFixedAsync(
        ushort opcode,
        uint explicitFixedSessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // Clean rule: preserve the causal last-normal +0x08 value,
            // but serialize construction instead of reproducing shared-buffer races.
            uint sequence = Sequence.GetFixedSendSequence();
            await SendFrameCoreAsync(
                new ServerUdpFrame(opcode, explicitFixedSessionId, sequence, payload),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<ServerUdpFrame> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            byte[] buffer = new byte[ushort.MaxValue];
            while (true)
            {
                int received;
                try
                {
                    received = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // The current receive loop treats a transport-level
                    // recvfrom failure as a non-dispatching retry condition. A
                    // bounded backoff avoids a hot loop without promoting the
                    // receive failure into semantic session shutdown.
                    _trace?.Invoke($"master/control UDP receive failed; retrying: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (received <= 0)
                {
                    _trace?.Invoke("master/control UDP receive returned no data; retrying");
                    continue;
                }

                _wireCapture?.RecordFrame(
                    Net7WireDirection.Inbound,
                    Net7WireTransport.Udp,
                    "master-control-udp",
                    LocalEndPoint,
                    RemoteEndPoint,
                    buffer.AsSpan(0, received));

                ServerUdpFrame frame;
                try
                {
                    frame = ServerUdpFrame.Parse(buffer.AsSpan(0, received));
                }
                catch (InvalidDataException ex)
                {
                    _trace?.Invoke($"master/control UDP dropped malformed datagram: {ex.Message}");
                    continue;
                }

                if (!_transportState.IsIncomingFixedSessionCompatible(frame.FixedSessionId, controlChannel: true))
                {
                    _trace?.Invoke(
                        $"master/control UDP dropped fixed/session mismatch: expected 0x{_transportState.ControlSessionId:X8}, " +
                        $"got 0x{frame.FixedSessionId:X8} on 0x{frame.Opcode:X4}");
                    continue;
                }

                FrameReceived?.Invoke(frame);
                return frame;
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

    private async Task SendFrameCoreAsync(ServerUdpFrame frame, CancellationToken cancellationToken)
    {
        byte[] wire = frame.ToWireBytes();
        _wireCapture?.RecordFrame(
            Net7WireDirection.Outbound,
            Net7WireTransport.Udp,
            "master-control-udp",
            LocalEndPoint,
            RemoteEndPoint,
            wire);
        int sent = await _socket.SendAsync(wire, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        if (sent != wire.Length)
            throw new IOException($"Net7 UDP send wrote {sent}/{wire.Length} bytes.");
        FrameSent?.Invoke(frame);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(MasterControlUdpChannel));
    }
}
