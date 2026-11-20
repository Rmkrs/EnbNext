using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Reliability;

/// <summary>
/// Periodic keepalive and MVAS-loop cadence state. The controller keeps
/// the 30-second attempt baseline independent from inbound 0x3005 reception and
/// models the current proxy's mutable frequency multiplier exactly.
/// </summary>
public sealed class Net7KeepaliveController
{
    private readonly INet7RuntimeClock _clock;
    private readonly Func<CancellationToken, Task<bool>> _trySendSectorAsync;
    private readonly Func<CancellationToken, Task<bool>> _trySendMasterAsync;
    private readonly Func<bool> _eligible;
    private readonly Action<string>? _trace;
    private uint _baseline;
    private uint? _activityStartedAt;
    private int _loopMultiplier;

    public Net7KeepaliveController(
        INet7RuntimeClock clock,
        Func<CancellationToken, Task<bool>> trySendSectorAsync,
        Func<CancellationToken, Task<bool>> trySendMasterAsync,
        Func<bool> eligible,
        int initialLoopMultiplier = 20,
        Action<string>? trace = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _trySendSectorAsync = trySendSectorAsync ?? throw new ArgumentNullException(nameof(trySendSectorAsync));
        _trySendMasterAsync = trySendMasterAsync ?? throw new ArgumentNullException(nameof(trySendMasterAsync));
        _eligible = eligible ?? throw new ArgumentNullException(nameof(eligible));
        _loopMultiplier = NormalizeServerMultiplier(initialLoopMultiplier);
        _trace = trace;
    }

    public uint Baseline => _baseline;

    /// <summary>
    /// Current MVAS-loop multiplier. One unit is 250 ms. This value is state,
    /// not a transient calculation: an activity-window expiry overwrites a
    /// previously server-selected multiplier with 2 until a later 0x1007.
    /// </summary>
    public int CurrentLoopMultiplier => ResolveLoopMultiplier();

    public void ObserveServerFrequency(uint requested)
    {
        _loopMultiplier = requested is >= 1 and <= 20 ? checked((int)requested) : 2;
    }

    public void MarkActiveForTwoSeconds()
    {
        // This explicitly permits a clean implementation to avoid the
        // legacy GetTickCount deadline-wrap bug. Keep the full wrapping u32
        // start tick and compare elapsed time instead of masking the high bit
        // or comparing an absolute wrapped deadline.
        _activityStartedAt = _clock.GetMilliseconds();
        _loopMultiplier = 1;
    }

    public int ResolveLoopMultiplier()
    {
        if (_activityStartedAt is uint startedAt)
        {
            uint now = _clock.GetMilliseconds();
            uint elapsed = unchecked(now - startedAt);
            if (elapsed <= 2000U)
            {
                _loopMultiplier = 1;
                return 1;
            }

            // Current Net7Proxy writes the global multiplier to 2 at expiry.
            // It does not remember/restore the server's earlier selection.
            _activityStartedAt = null;
            _loopMultiplier = 2;
        }

        return _loopMultiplier;
    }

    public async Task<bool> EvaluateOnceAsync(CancellationToken cancellationToken = default)
    {
        uint now = _clock.GetMilliseconds() & 0x7FFFFFFF;
        if (now < _baseline)
        {
            _baseline = now;
            return false;
        }

        if (!_eligible())
            return false;

        if (now <= unchecked(_baseline + 30000U))
            return false;

        // Current ordering is sector first, then master. Failure of either send
        // does not create a keepalive retry transaction and does not prevent the
        // second attempt or baseline advancement.
        try { await _trySendSectorAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"sector keepalive attempt failed/suppressed: {ex.Message}");
        }

        try { await _trySendMasterAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _trace?.Invoke($"master keepalive attempt failed/suppressed: {ex.Message}");
        }

        _baseline = now;
        return true;
    }

    /// <summary>Inbound admitted 0x3005 is explicitly a no-op for periodic state.</summary>
    public void ObserveInboundKeepalive()
    {
    }

    private static int NormalizeServerMultiplier(int value)
        => value is >= 1 and <= 20 ? value : 2;
}
