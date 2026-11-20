using System.Buffers.Binary;

namespace EnbNext.Net7;

/// <summary>A moment's reading of the connection (Next: the Modern UI connection indicator).</summary>
/// <param name="LatencyMilliseconds">The last round trip of the client's own time request, or null before the first.</param>
/// <param name="LatencyAgeMilliseconds">How long ago that round trip completed.</param>
/// <param name="PendingMilliseconds">How long the latest time request has waited for its answer, or null when none waits.</param>
/// <param name="LastReceivedAgeMilliseconds">How long ago the server's last frame arrived, or null before the first.</param>
/// <param name="BytesReceived">Frame bytes received so far (header included).</param>
/// <param name="BytesSent">Frame bytes sent so far (header included).</param>
/// <param name="Awaited">The server answer the client is waiting for (dock, gate, conversation...), if any.</param>
/// <param name="AwaitedMilliseconds">How long that answer has been awaited.</param>
/// <param name="ServerAnswersWhileAwaiting">True when the server answered a time request after the wait began
/// (the connection works), false when such a request went unanswered for <see cref="Net7ConnectionMeter.ProbeTimeout"/>
/// (nothing gets through), null when not known yet.</param>
public readonly record struct Net7ConnectionSample(long? LatencyMilliseconds, long LatencyAgeMilliseconds, long? PendingMilliseconds,
    long? LastReceivedAgeMilliseconds, long BytesReceived, long BytesSent,
    Net7AwaitedAnswer Awaited = Net7AwaitedAnswer.None, long AwaitedMilliseconds = 0, bool? ServerAnswersWhileAwaiting = null);

/// <summary>A server answer the client waits for after its own request.</summary>
public enum Net7AwaitedAnswer { None, Dock, Landing, Gate, Launch, Conversation }

/// <summary>
/// Next: measures the connection from the traffic the client exchanges anyway; it sends nothing of its own.
/// Latency is the round trip of retail's own Request_Time (0x0044), which the server answers with
/// Set_Client_Time (0x0034) echoing the request's send time as its first timestamp. Only an answer that echoes
/// the latest request's send time counts, so other Set_Client_Time packets (login's, a previous sector's) never do.
/// Both times come from the session's client-time clock.
/// </summary>
public sealed class Net7ConnectionMeter
{
    private readonly object _sync = new();
    private long _bytesReceived, _bytesSent, _lastReceivedTick = -1;
    private uint _pendingSent; private long _pendingTick = -1;
    private long _latency = -1, _latencyTick;
    private Net7AwaitedAnswer _awaited; private long _awaitedTick; private bool _probed;
    /// <summary>An awaited answer this late makes the session probe the connection with one time request.</summary>
    public const long ProbeAfter = 5000;
    /// <summary>A probe unanswered this long means nothing gets through.</summary>
    public const long ProbeTimeout = 5000;
    /// <summary>A wait this old is forgotten: the request was refused or the player moved on.</summary>
    public const long AwaitExpiry = 120000;

    /// <summary>The client sent a request the server must answer. A repeat of the same request keeps the first start.</summary>
    public void Await(Net7AwaitedAnswer answer)
    {
        lock (_sync)
        {
            if (_awaited == answer) return;
            _awaited = answer; _awaitedTick = Environment.TickCount64; _probed = false;
        }
    }

    /// <summary>The server handed the client over to another sector or station.</summary>
    public void HandoffArrived()
    {
        lock (_sync) _awaited = Net7AwaitedAnswer.None;
    }

    /// <summary>A gameplay packet arrived; a talk tree or talk action answers a conversation request.</summary>
    public void PacketArrived(ushort opcode)
    {
        if (opcode is not (0x0054 or 0x0056)) return;
        lock (_sync) if (_awaited == Net7AwaitedAnswer.Conversation) _awaited = Net7AwaitedAnswer.None;
    }

    /// <summary>True once per wait when the awaited answer is late and no time request is out: the caller then sends one.</summary>
    public bool ShouldProbe()
    {
        lock (_sync)
        {
            if (_awaited == Net7AwaitedAnswer.None || _probed || _pendingTick >= 0) return false;
            if (Environment.TickCount64 - _awaitedTick < ProbeAfter) return false;
            _probed = true; return true;
        }
    }

    public void Received(int bytes)
    {
        lock (_sync) { _bytesReceived += bytes; _lastReceivedTick = Environment.TickCount64; }
    }

    /// <summary>A frame went out; a Request_Time frame starts a measurement.</summary>
    public void Sent(ushort opcode, ReadOnlySpan<byte> payload, int bytes)
    {
        lock (_sync)
        {
            _bytesSent += bytes;
            if (opcode == 0x0044 && payload.Length == 4) { _pendingSent = BinaryPrimitives.ReadUInt32LittleEndian(payload); _pendingTick = Environment.TickCount64; }
        }
    }

    /// <summary>A Set_Client_Time payload applied at <paramref name="clientReceivedTime"/> on the session clock.</summary>
    public void TimeAnswered(ReadOnlySpan<byte> payload, uint clientReceivedTime)
    {
        if (payload.Length < 4) return;
        uint sent = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        lock (_sync)
        {
            if (_pendingTick < 0 || sent != _pendingSent) return;
            _latency = unchecked((int)(clientReceivedTime - sent));
            _latencyTick = Environment.TickCount64; _pendingTick = -1;
            if (_latency < 0) _latency = -1; // A clock that ran backwards gives no reading.
        }
    }

    public Net7ConnectionSample Sample()
    {
        long now = Environment.TickCount64;
        lock (_sync)
        {
            bool? answers = null;
            if (_awaited != Net7AwaitedAnswer.None && now - _awaitedTick >= AwaitExpiry) _awaited = Net7AwaitedAnswer.None;
            if (_awaited != Net7AwaitedAnswer.None)
            {
                if (_latency >= 0 && _latencyTick >= _awaitedTick) answers = true;
                else if (_pendingTick >= _awaitedTick && now - _pendingTick >= ProbeTimeout) answers = false;
            }
            return new(_latency < 0 ? null : _latency, now - _latencyTick, _pendingTick < 0 ? null : now - _pendingTick,
                _lastReceivedTick < 0 ? null : now - _lastReceivedTick, _bytesReceived, _bytesSent,
                _awaited, _awaited == Net7AwaitedAnswer.None ? 0 : now - _awaitedTick, answers);
        }
    }
}
