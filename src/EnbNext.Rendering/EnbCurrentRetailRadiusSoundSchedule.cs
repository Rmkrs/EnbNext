using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Silent effect-side deadline seam from current retail 006E6C9D..006E6CD6.
/// The caller supplies the evidenced camera-distance result and shared RNG state.
/// This does not admit a sound definition or manage backend voices.
/// </summary>
public sealed class EnbCurrentRetailRadiusSoundSchedule
{
    private readonly int _firstIntervalMilliseconds;
    private readonly int _secondIntervalMilliseconds;

    public EnbCurrentRetailRadiusSoundSchedule(uint initialDeadline,
        int firstIntervalMilliseconds, int secondIntervalMilliseconds)
    {
        // This seam covers the established nonnegative interval domain only.
        ArgumentOutOfRangeException.ThrowIfNegative(firstIntervalMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(secondIntervalMilliseconds);
        NextDeadline = initialDeadline;
        _firstIntervalMilliseconds = firstIntervalMilliseconds;
        _secondIntervalMilliseconds = secondIntervalMilliseconds;
    }

    public uint NextDeadline { get; private set; }
    public ulong AttemptCount { get; private set; }
    public bool IsRetired { get; private set; }

    /// <summary>
    /// Executes at most one attempt, then samples the next interval, including
    /// when the callback declines playback or selects NULL. The callback must
    /// handle recoverable backend failures and return normally. Invoke once per
    /// effect update; zero intervals intentionally remain due at the same clock.
    /// Clip completion is independent of this owner and is not an input here.
    /// </summary>
    public bool TryAttempt(uint currentClientTime, bool insideRadius,
        Action attempt, ref uint randomState)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (IsRetired || !insideRadius || unchecked(currentClientTime-NextDeadline)>=0x80000000u)return false;
        attempt();
        int interval=EnbCurrentRetailIntegerRandom.Next(ref randomState,_firstIntervalMilliseconds,_secondIntervalMilliseconds);
        NextDeadline=unchecked(currentClientTime+(uint)interval);AttemptCount++;
        return true;
    }
    public bool TryAttempt(uint currentClientTime,bool insideRadius,Action attempt,Func<int,int,int> random)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (IsRetired || !insideRadius ||
            unchecked(currentClientTime - NextDeadline) >= 0x80000000u)
            return false;

        attempt();
        int interval = random(_firstIntervalMilliseconds, _secondIntervalMilliseconds);
        NextDeadline = unchecked(currentClientTime + (uint)interval);
        AttemptCount++;
        return true;
    }

    /// <summary>Owner expiry/removal is terminal; a new owner needs a new schedule.</summary>
    public void Retire() => IsRetired = true;
}
