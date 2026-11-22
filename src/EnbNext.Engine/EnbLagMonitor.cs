using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EnbNext.Engine;

/// <summary>What a lag report is about.</summary>
internal enum EnbLagKind { None, LowFrameRate, Stutter, Freeze, Manual }

/// <summary>A detected lag problem: its kind and a sentence for the player and the report.</summary>
internal readonly record struct EnbLagDetection(EnbLagKind Kind, string Summary, DateTime DetectedAtUtc);

/// <summary>
/// Next: a flight recorder of the frame loop and a detector of lag the player would notice. It keeps the times of the
/// last <see cref="FrameCapacity"/> frames and a per-second timeline of the last <see cref="SecondCapacity"/> seconds
/// in fixed arrays, so recording a frame allocates nothing. Only frames the owner marks as eligible (in the world,
/// in the foreground, not loading) are judged, and <see cref="Hold"/> keeps a settling period out after a change.
/// It reports one problem per session:
/// a frame of a second or more (a freeze); <see cref="HitchCount"/> hitches within a minute (a hitch is a frame of
/// <see cref="HitchMilliseconds"/> or more, or of four times the usual frame and at least
/// <see cref="RelativeHitchFloorMilliseconds"/>); or <see cref="LowFrameRateSeconds"/> seconds in a row below
/// <see cref="LowFrameRate"/> frames per second while the frame rate is not capped below <see cref="LowFrameRateCapFloor"/>.
/// </summary>
internal sealed class EnbLagMonitor
{
    public const int FrameCapacity = 16384, SecondCapacity = 180;
    public const double FreezeMilliseconds = 1000, HitchMilliseconds = 100, RelativeHitch = 4, RelativeHitchFloorMilliseconds = 50;
    public const int HitchCount = 5, HitchWindowSeconds = 60;
    public const double LowFrameRate = 30;
    public const int LowFrameRateSeconds = 20, LowFrameRateCapFloor = 40;

    private static readonly double TickMilliseconds = 1000.0 / Stopwatch.Frequency;

    // Frames, oldest overwritten: start, length and where the time went.
    private readonly long[] _frameStart = new long[FrameCapacity];
    private readonly float[] _frameTotal = new float[FrameCapacity], _frameInput = new float[FrameCapacity], _frameQueued = new float[FrameCapacity],
        _frameTicks = new float[FrameCapacity], _frameRender = new float[FrameCapacity];
    private readonly bool[] _frameJudged = new bool[FrameCapacity];
    private int _frameNext, _frameCount;

    // Seconds: frames, total and worst frame time, hitches, judged frames and the phases' totals.
    private readonly DateTime[] _secondUtc = new DateTime[SecondCapacity];
    private readonly int[] _secondFrames = new int[SecondCapacity], _secondJudged = new int[SecondCapacity], _secondHitches = new int[SecondCapacity];
    private readonly float[] _secondTotal = new float[SecondCapacity], _secondMax = new float[SecondCapacity], _secondInput = new float[SecondCapacity],
        _secondQueued = new float[SecondCapacity], _secondTicks = new float[SecondCapacity], _secondRender = new float[SecondCapacity];
    private int _secondNext, _secondCount;
    private long _secondIndex = long.MinValue;

    // The usual frame: the median of the last full second, from a histogram of 0.5 ms .. 2 s in quarter-octave bins.
    private const int Bins = 48;
    private readonly int[] _histogram = new int[Bins];
    private int _histogramCount;
    private double _usual;

    private readonly long[] _hitchAt = new long[32];
    private int _hitchNext;
    private int _lowSeconds;

    // The previous frame, completed when the next one starts.
    private long _pendingBegin, _pendingHandled, _pendingDrained, _pendingTicked, _pendingRendered;
    private bool _pendingEligible, _hasPending;
    private long _holdUntil;
    private EnbLagDetection? _detected;
    private bool _taken;

    /// <summary>The foreground frame-rate cap the loop runs at (a low cap is no low frame rate).</summary>
    public int FrameRateCap { get; set; } = 1000;

    /// <summary>The problem found this session, once found.</summary>
    public EnbLagDetection? Detected => _detected;

    /// <summary>Frames starting within this time are not judged (a world entry, a sector change, a changed window).</summary>
    public void Hold(TimeSpan duration)
    {
        long until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        if (until > _holdUntil) _holdUntil = until;
    }

    /// <summary>One frame of the loop, with the times it finished handling input, queued work, ticks and rendering.</summary>
    public void Frame(long begin, long handled, long drained, long ticked, long rendered, bool eligible)
    {
        if (_hasPending && begin > _pendingBegin)
        {
            // The previous frame ends where this one begins (its present and sleep included); it is judged when it was
            // eligible, held by nothing and this frame is eligible too (no pause in between).
            bool judged = _pendingEligible && eligible && _pendingBegin >= _holdUntil;
            Record(_pendingBegin, begin, judged);
        }
        _pendingBegin = begin; _pendingHandled = handled; _pendingDrained = drained; _pendingTicked = ticked; _pendingRendered = rendered;
        _pendingEligible = eligible; _hasPending = true;
    }

    /// <summary>The problem found, the first time it is asked for; null afterwards and until one is found.</summary>
    public EnbLagDetection? TakeDetection()
    {
        if (_detected is null || _taken) return null;
        _taken = true; return _detected;
    }

    private static float Ms(long from, long to) => to > from ? (float)((to - from) * TickMilliseconds) : 0;

    private void Record(long begin, long end, bool judged)
    {
        float total = Ms(begin, end), input = Ms(begin, _pendingHandled), queued = Ms(_pendingHandled, _pendingDrained),
            ticks = Ms(_pendingDrained, _pendingTicked), render = Ms(_pendingTicked, _pendingRendered);
        int f = _frameNext;
        _frameStart[f] = begin; _frameTotal[f] = total; _frameInput[f] = input; _frameQueued[f] = queued; _frameTicks[f] = ticks; _frameRender[f] = render;
        _frameJudged[f] = judged;
        _frameNext = (f + 1) % FrameCapacity; if (_frameCount < FrameCapacity) _frameCount++;

        long second = begin / Stopwatch.Frequency;
        if (second != _secondIndex) StartSecond(second);
        int s = (_secondNext + SecondCapacity - 1) % SecondCapacity;
        _secondFrames[s]++; _secondTotal[s] += total; if (total > _secondMax[s]) _secondMax[s] = total;
        _secondInput[s] += input; _secondQueued[s] += queued; _secondTicks[s] += ticks; _secondRender[s] += render;
        if (!judged) return;
        _secondJudged[s]++;
        int bin = Bin(total); _histogram[bin]++; _histogramCount++;

        bool hitch = total >= HitchMilliseconds || _usual > 0 && total >= RelativeHitch * _usual && total >= RelativeHitchFloorMilliseconds;
        if (hitch)
        {
            _secondHitches[s]++;
            _hitchAt[_hitchNext] = end; _hitchNext = (_hitchNext + 1) % _hitchAt.Length;
        }
        if (_detected is not null) return;
        if (total >= FreezeMilliseconds)
        {
            Detect(EnbLagKind.Freeze, string.Create(CultureInfo.InvariantCulture, $"the game stopped for {total / 1000:0.0} seconds"));
            return;
        }
        if (hitch)
        {
            long since = end - HitchWindowSeconds * Stopwatch.Frequency;
            int count = 0;
            foreach (long at in _hitchAt) if (at > since && at != 0) count++;
            if (count >= HitchCount)
                Detect(EnbLagKind.Stutter, string.Create(CultureInfo.InvariantCulture, $"{count} hitches within a minute, the last one {total:0} ms"));
        }
    }

    private void StartSecond(long second)
    {
        if (_secondIndex != long.MinValue) CloseSecond();
        _secondIndex = second;
        int s = _secondNext;
        _secondUtc[s] = DateTime.UtcNow;
        _secondFrames[s] = _secondJudged[s] = _secondHitches[s] = 0;
        _secondTotal[s] = _secondMax[s] = _secondInput[s] = _secondQueued[s] = _secondTicks[s] = _secondRender[s] = 0;
        _secondNext = (s + 1) % SecondCapacity; if (_secondCount < SecondCapacity) _secondCount++;
    }

    /// <summary>The second just past sets the usual frame and counts towards a low frame rate.</summary>
    private void CloseSecond()
    {
        int s = (_secondNext + SecondCapacity - 1) % SecondCapacity;
        if (_histogramCount > 0)
        {
            int half = (_histogramCount + 1) / 2, seen = 0;
            for (int bin = 0; bin < Bins; bin++) { seen += _histogram[bin]; if (seen >= half) { _usual = BinMilliseconds(bin); break; } }
            Array.Clear(_histogram); _histogramCount = 0;
        }
        bool whole = _secondFrames[s] > 0 && _secondJudged[s] == _secondFrames[s] && _secondTotal[s] >= 900;
        double rate = _secondTotal[s] > 0 ? _secondFrames[s] * 1000 / _secondTotal[s] : 0;
        if (!whole || FrameRateCap < LowFrameRateCapFloor) { _lowSeconds = 0; return; }
        _lowSeconds = rate < LowFrameRate ? _lowSeconds + 1 : 0;
        if (_detected is null && _lowSeconds >= LowFrameRateSeconds)
            Detect(EnbLagKind.LowFrameRate, string.Create(CultureInfo.InvariantCulture, $"about {rate:0} frames per second for {LowFrameRateSeconds} seconds"));
    }

    private void Detect(EnbLagKind kind, string summary) => _detected = new(kind, summary, DateTime.UtcNow);

    // Quarter octaves from 0.5 ms: bin n holds frames up to 0.5 * 2^(n/4) ms.
    private static int Bin(double milliseconds) => milliseconds <= .5 ? 0 : Math.Min(Bins - 1, (int)Math.Ceiling(4 * Math.Log2(milliseconds / .5)));
    private static double BinMilliseconds(int bin) => .5 * Math.Pow(2, bin / 4.0);

    /// <summary>The last minute: frames per second, hitches and the worst frame.</summary>
    public (double FramesPerSecond, int Hitches, double WorstMilliseconds) LastMinute()
    {
        int frames = 0, hitches = 0; double total = 0, worst = 0;
        for (int i = 0; i < Math.Min(60, _secondCount); i++)
        {
            int s = (_secondNext + SecondCapacity - 1 - i) % SecondCapacity;
            frames += _secondFrames[s]; total += _secondTotal[s]; hitches += _secondHitches[s]; worst = Math.Max(worst, _secondMax[s]);
        }
        return (total > 0 ? frames * 1000 / total : 0, hitches, worst);
    }

    /// <summary>The timeline, oldest second first: one row per second of the last three minutes.</summary>
    public string TimelineCsv()
    {
        var text = new StringBuilder("utc,frames,fps,average_ms,worst_ms,hitches,judged_frames,input_ms,queued_ms,ticks_ms,render_ms\n");
        for (int i = _secondCount - 1; i >= 0; i--)
        {
            int s = (_secondNext + SecondCapacity - 1 - i) % SecondCapacity;
            int frames = _secondFrames[s]; double n = Math.Max(1, frames), total = _secondTotal[s];
            text.Append(CultureInfo.InvariantCulture,
                $"{_secondUtc[s]:yyyy-MM-ddTHH:mm:ssZ},{frames},{(total > 0 ? frames * 1000 / total : 0):0.0},{total / n:0.00},{_secondMax[s]:0.00},{_secondHitches[s]},{_secondJudged[s]},{_secondInput[s] / n:0.00},{_secondQueued[s] / n:0.00},{_secondTicks[s] / n:0.00},{_secondRender[s] / n:0.00}\n");
        }
        return text.ToString();
    }

    /// <summary>The recorded frames, oldest first, with their start in milliseconds before the newest one.</summary>
    public string FramesCsv()
    {
        var text = new StringBuilder("start_ms,frame_ms,input_ms,queued_ms,ticks_ms,render_ms,judged\n");
        if (_frameCount == 0) return text.ToString();
        long newest = _frameStart[(_frameNext + FrameCapacity - 1) % FrameCapacity];
        for (int i = _frameCount - 1; i >= 0; i--)
        {
            int f = (_frameNext + FrameCapacity - 1 - i) % FrameCapacity;
            text.Append(CultureInfo.InvariantCulture,
                $"{0 - Ms(_frameStart[f], newest):0.0},{_frameTotal[f]:0.00},{_frameInput[f]:0.00},{_frameQueued[f]:0.00},{_frameTicks[f]:0.00},{_frameRender[f]:0.00},{(_frameJudged[f] ? 1 : 0)}\n");
        }
        return text.ToString();
    }
}
