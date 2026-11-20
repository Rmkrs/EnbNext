using System.Diagnostics;

namespace EnbNext.Net7.Timing;

/// <summary>
/// Process-local millisecond clock and delay seam. The current retail timer lazily
/// establishes one process-wide origin and returns a wrapping u32 delta from it.
/// Custom clocks remain injectable so retry/watchdog timing stays deterministic in tests.
/// </summary>
public interface INet7RuntimeClock
{
    uint GetMilliseconds();
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemNet7RuntimeClock : INet7RuntimeClock
{
    private static long s_origin = long.MinValue;

    public uint GetMilliseconds()
    {
        // One process-relative epoch for packets, prediction and presentation.
        // TickCount64 advances in 15/16 ms chunks on Windows, creating 31 ms
        // spatial steps even with a perfect 60 Hz renderer.
        long now = Stopwatch.GetTimestamp();
        long origin = Volatile.Read(ref s_origin);
        if (origin == long.MinValue)
        {
            long observed = Interlocked.CompareExchange(ref s_origin, now, long.MinValue);
            origin = observed == long.MinValue ? now : observed;
        }

        // A racing first caller may have established an origin after 'now'.
        long elapsed = Math.Max(0, now - origin);
        ulong milliseconds = (ulong)(elapsed / Stopwatch.Frequency) * 1000UL +
            (ulong)(elapsed % Stopwatch.Frequency) * 1000UL / (ulong)Stopwatch.Frequency;
        return unchecked((uint)milliseconds);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}
