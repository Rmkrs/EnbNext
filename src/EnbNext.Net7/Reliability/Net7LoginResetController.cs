using System.Buffers.Binary;
using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Reliability;

/// <summary>
/// Clean transaction-correlated form optional
/// 0x3006 -> 0x3007 reset/recovery primitive. The ordinary current watchdog
/// path does not invoke this transaction because its stock communal ready byte
/// is already set; this controller exists for the proven active-reset branch
/// without reproducing stale-mailbox or cross-transport false completion.
/// </summary>
public sealed class Net7LoginResetController
{
    private readonly INet7RuntimeClock _clock;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>> _trySend3006Async;
    private readonly int _attempts;
    private readonly int _pollsPerAttempt;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string>? _trace;
    private int _active;
    private int _acknowledged;

    public Net7LoginResetController(
        INet7RuntimeClock clock,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>> trySend3006Async,
        Action<string>? trace = null)
        : this(
            clock,
            trySend3006Async,
            Net7ServerOptions.CurrentGenericRequestAttempts,
            Net7ServerOptions.CurrentGenericResponsePollsPerAttempt,
            Net7ServerOptions.CurrentGenericResponsePollInterval,
            trace)
    {
    }

    internal Net7LoginResetController(
        INet7RuntimeClock clock,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>> trySend3006Async,
        int attempts,
        int pollsPerAttempt,
        TimeSpan pollInterval,
        Action<string>? trace = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _trySend3006Async = trySend3006Async ?? throw new ArgumentNullException(nameof(trySend3006Async));
        if (attempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempts));
        if (pollsPerAttempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(pollsPerAttempt));
        _attempts = attempts;
        _pollsPerAttempt = pollsPerAttempt;
        _pollInterval = pollInterval;
        if (_pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _trace = trace;
    }

    public bool TransactionActive => Volatile.Read(ref _active) != 0;

    /// <summary>
    /// Direct outer routed-context 0x3007 has no payload. It can shorten only
    /// the currently active reset transaction; late/stale acknowledgements are
    /// deliberately not retained for a future transaction.
    /// </summary>
    public void ObserveDirectOuter3007()
    {
        if (Volatile.Read(ref _active) != 0)
            Volatile.Write(ref _acknowledged, 1);
    }

    public async Task<bool> RunAsync(uint fixedSessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _acknowledged, 0);
            Volatile.Write(ref _active, 1);

            byte[] payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, fixedSessionId);

            for (int attempt = 0; attempt < _attempts; attempt++)
            {
                if (Volatile.Read(ref _acknowledged) != 0)
                    return true;

                try
                {
                    _ = await _trySend3006Async(payload, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
                {
                    // Current void send chain treats local send failure exactly as
                    // an unanswered attempt. Clean EnbNext keeps that wire/timing
                    // consequence without hiding the failure from diagnostics.
                    _trace?.Invoke($"0x3006 reset attempt failed/suppressed: {ex.Message}");
                }

                for (int poll = 0; poll < _pollsPerAttempt; poll++)
                {
                    if (Volatile.Read(ref _acknowledged) != 0)
                        return true;
                    await _clock.DelayAsync(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
            }

            // No extra final acknowledgement check: the current helper has none
            // after the fourth sleep of its twentieth/final send. Cleanup is
            // ACK-independent in the owning failure path anyway.
            return false;
        }
        finally
        {
            Volatile.Write(ref _active, 0);
            Volatile.Write(ref _acknowledged, 0);
            _gate.Release();
        }
    }
}
