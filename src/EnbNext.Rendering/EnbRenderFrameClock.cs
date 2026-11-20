namespace EnbNext.Rendering;

/// <summary>
/// Bridges an immutable client-time sample to a continuously rendered frame.
/// The network/presentation snapshot remains immutable while visual-only time
/// advances from the same monotonic clock used to capture the sample.
/// </summary>
public static class EnbRenderFrameClock
{
    public static uint AdvanceClientTime(
        uint anchorClientTime,
        long anchorTimestamp,
        long currentTimestamp,
        long timestampFrequency)
    {
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        if (currentTimestamp <= anchorTimestamp)
            return anchorClientTime;

        ulong elapsedTicks = unchecked((ulong)(currentTimestamp - anchorTimestamp));
        ulong frequency = unchecked((ulong)timestampFrequency);
        ulong wholeSeconds = elapsedTicks / frequency;
        ulong remainder = elapsedTicks % frequency;
        ulong elapsedMilliseconds = checked((wholeSeconds * 1000UL) + ((remainder * 1000UL) / frequency));
        return unchecked(anchorClientTime + (uint)elapsedMilliseconds);
    }
}
