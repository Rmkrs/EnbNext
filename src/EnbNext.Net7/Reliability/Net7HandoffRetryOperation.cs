using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Reliability;

/// <summary>
/// Clean immutable implementation fixed-header handoff
/// retry workers. Each instance owns one logical operation and one acknowledgement
/// latch, eliminating the legacy shared scratch/latch races.
/// </summary>
public sealed class Net7HandoffRetryOperation
{
    private readonly INet7RuntimeClock _clock;
    private readonly Func<ushort, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> _trySendFixedAsync;
    private readonly ushort _opcode;
    private readonly byte[] _payload;
    private readonly TimeSpan _interval;
    private readonly int _maximumSends;
    private readonly Func<bool> _ownerIsCurrent;
    private int _handoffAccepted;
    private int _started;

    private Net7HandoffRetryOperation(
        INet7RuntimeClock clock,
        Func<ushort, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> trySendFixedAsync,
        ushort opcode,
        ReadOnlySpan<byte> payload,
        TimeSpan interval,
        int maximumSends,
        Func<bool>? ownerIsCurrent = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _trySendFixedAsync = trySendFixedAsync ?? throw new ArgumentNullException(nameof(trySendFixedAsync));
        _opcode = opcode;
        _payload = payload.ToArray();
        _interval = interval;
        _maximumSends = maximumSends;
        _ownerIsCurrent = ownerIsCurrent ?? (() => true);
    }

    public static Net7HandoffRetryOperation CreateAction(
        INet7RuntimeClock clock,
        Func<ushort, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> trySendFixedAsync,
        ReadOnlySpan<byte> actionPayload,
        Func<bool>? ownerIsCurrent = null)
    {
        if (actionPayload.Length != 16)
            throw new ArgumentException("Action handoff retry payload must be exactly 16 bytes.", nameof(actionPayload));
        return new Net7HandoffRetryOperation(
            clock, trySendFixedAsync, 0x002C, actionPayload, TimeSpan.FromMilliseconds(1500), 5, ownerIsCurrent);
    }

    public static Net7HandoffRetryOperation CreateLeaveStarbase(
        INet7RuntimeClock clock,
        Func<ushort, ReadOnlyMemory<byte>, CancellationToken, Task<bool>> trySendFixedAsync,
        ReadOnlySpan<byte> leavePayload,
        Func<bool>? ownerIsCurrent = null)
    {
        if (leavePayload.Length != 9)
            throw new ArgumentException("Leave-starbase handoff retry payload must be exactly 9 bytes.", nameof(leavePayload));
        return new Net7HandoffRetryOperation(
            clock, trySendFixedAsync, 0x004E, leavePayload, TimeSpan.FromMilliseconds(1000), 10, ownerIsCurrent);
    }

    public static bool ActionSubtypeUsesRetry(byte subtype)
        => subtype is 0x07 or 0x08 or 0x13;

    public void ObserveServerHandoff()
        => Volatile.Write(ref _handoffAccepted, 1);

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A handoff retry operation can run only once.");

        int sends = 0;
        await _clock.DelayAsync(_interval, cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _handoffAccepted) != 0 || !_ownerIsCurrent())
            return 0;

        while (sends < _maximumSends)
        {
            if (!_ownerIsCurrent())
                break;
            try
            {
                _ = await _trySendFixedAsync(_opcode, _payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                // The legacy void send chain still consumes this retry attempt.
                // A clean caller may trace the exception separately.
            }

            sends++;
            await _clock.DelayAsync(_interval, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _handoffAccepted) != 0 || !_ownerIsCurrent())
                break;
        }

        return sends;
    }
}
