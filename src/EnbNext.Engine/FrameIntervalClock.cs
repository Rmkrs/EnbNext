using System.Diagnostics;

namespace EnbNext.Engine;

// The world callback's start-to-start QPC clock, shared by replay receipts and
// the optional title counter. Scanner and overlay callbacks do not publish here.
internal static class FrameIntervalClock
{
    internal static event Action<long>? FrameStarted;
    internal static bool IsObserved => FrameStarted is not null;
    internal static long BeginFrame()
    {
        long timestamp=Stopwatch.GetTimestamp();
        FrameStarted?.Invoke(timestamp);
        return timestamp;
    }
}

internal sealed class FrameTitleAverage
{
    private long start;
    private int intervals;
    internal void Reset() { start=0; intervals=0; }
    internal bool Add(long timestamp, out double milliseconds)
    {
        milliseconds=0;
        if(start==0) { start=timestamp; return false; }
        intervals++;
        long elapsed=timestamp-start;
        if(elapsed<Stopwatch.Frequency/2)return false;
        milliseconds=elapsed*1000d/Stopwatch.Frequency/intervals;
        start=timestamp; intervals=0;
        return true;
    }
}
