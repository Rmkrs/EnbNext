using System.Diagnostics;
using System.Text.Json;
using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Diagnostics;

/// <summary>A bounded window of a complete recording. Every trial starts from
/// the capture prefix; a live session is never rewound or allowed to age past End.</summary>
public sealed class Net7WireReplayRecording
{
    private Net7WireReplayRecording(Net7WireCaptureFrame[] frames, long end)
    { Frames = Array.AsReadOnly(frames); EndMicroseconds = end; }
    public IReadOnlyList<Net7WireCaptureFrame> Frames { get; }
    public long EndMicroseconds { get; }

    public static Net7WireReplayRecording Load(string path)
    {
        bool header = false;
        long? end = null;
        foreach (string line in File.ReadLines(EnbNext.Installation.EnbPathCase.Resolve(path)))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            string? type = root.GetProperty("type").GetString();
            if (type == "header")
            {
                if (header || root.GetProperty("format").GetString() != "enbnext-wire-capture" ||
                    root.GetProperty("version").GetInt32() != Net7WireCapture.CurrentFormatVersion)
                    throw new InvalidDataException("Unsupported or duplicate capture header.");
                header = true;
            }
            else if (type == "marker" && root.GetProperty("name").GetString() == "capture-stop")
            {
                if (end.HasValue) throw new InvalidDataException("Duplicate capture-stop marker.");
                end = root.GetProperty("elapsedMicroseconds").GetInt64();
            }
        }
        if (!header || end is not >= 0)
            throw new InvalidDataException("A benchmark requires a complete capture with a stop marker.");
        var frames = Net7WireCapture.ReadFrames(path).ToArray();
        long priorIndex = -1;
        foreach (var frame in frames)
        {
            if (frame.Index <= priorIndex || frame.ElapsedMicroseconds < 0 || frame.ElapsedMicroseconds > end)
                throw new InvalidDataException("Invalid capture index or frame outside the recorded duration.");
            priorIndex = frame.Index;
        }
        return new(frames, end.Value);
    }

    public Net7WireReplayCursor CreateTrial(long measurementStartMicroseconds, long measurementEndMicroseconds)
    {
        if (measurementStartMicroseconds < 0 || measurementEndMicroseconds <= measurementStartMicroseconds ||
            measurementEndMicroseconds > EndMicroseconds)
            throw new ArgumentOutOfRangeException(nameof(measurementEndMicroseconds),
                "The fixed measurement segment must be wholly inside the complete recording.");
        return new(Frames, measurementStartMicroseconds, measurementEndMicroseconds);
    }
}

/// <summary>Clock-driven ingress cursor, independent of rendering FPS. Consume
/// outbound records as recorded client inputs/observations, never as inbound data.
/// Delivery follows the capture's global index even if concurrent timestamps regress.</summary>
public sealed class Net7WireReplayCursor
{
    private readonly IReadOnlyList<Net7WireCaptureFrame> _frames;
    private int _next;
    private long _lastElapsed = -1;
    private long _scheduled;
    internal Net7WireReplayCursor(IReadOnlyList<Net7WireCaptureFrame> frames, long start, long end)
    { _frames = frames; MeasurementStartMicroseconds = start; MeasurementEndMicroseconds = end; }
    public long MeasurementStartMicroseconds { get; }
    public long MeasurementEndMicroseconds { get; }
    public bool IsMeasuring => _lastElapsed >= MeasurementStartMicroseconds && _lastElapsed < MeasurementEndMicroseconds;
    public bool IsComplete => _lastElapsed >= MeasurementEndMicroseconds;
    public int DeliveredFrames => _next;
    public long MaximumDeliveryLatenessMicroseconds { get; private set; }

    public async ValueTask AdvanceAsync(long elapsedMicroseconds,
        Func<Net7WireCaptureFrame, CancellationToken, ValueTask> deliver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        if (elapsedMicroseconds < _lastElapsed)
            throw new InvalidOperationException("A replay cannot rewind retained session state; create a fresh trial.");
        _lastElapsed = elapsedMicroseconds;
        long limit = Math.Min(elapsedMicroseconds, MeasurementEndMicroseconds);
        while (_next < _frames.Count)
        {
            var frame = _frames[_next];
            long due = Math.Max(_scheduled, frame.ElapsedMicroseconds);
            // The fixed segment is half-open. No tail event is allowed to age it.
            if (due > limit || due >= MeasurementEndMicroseconds) break;
            cancellationToken.ThrowIfCancellationRequested();
            await deliver(frame, cancellationToken).ConfigureAwait(false);
            _scheduled = due;
            _next++;
            MaximumDeliveryLatenessMicroseconds = Math.Max(MaximumDeliveryLatenessMicroseconds, elapsedMicroseconds - due);
        }
    }
}

/// <summary>One process-local trial clock at real-time (1x) tempo. Render speed
/// never advances simulation time. Instantiate a new clock for each fresh session.</summary>
public sealed class Net7WireReplayClock(uint recordedClientOrigin, long originAdvanceMicroseconds=0) : INet7RuntimeClock
{
    private long _origin;
    private long _pendingInputTime = long.MaxValue;
    // A late dispatcher wake must not let a presentation getter advance a
    // retained command keyframe past a control that has not been delivered.
    // The ingress cursor still uses uncapped wall time; this cannot deadlock it.
    public void LimitSimulationTimeUntilInput(uint? clientTime)
        => Volatile.Write(ref _pendingInputTime, clientTime is uint time ? time : long.MaxValue);
    public void Start()
    { if(Interlocked.CompareExchange(ref _origin,Stopwatch.GetTimestamp(),0)!=0)throw new InvalidOperationException("A replay clock cannot restart."); }
    public long ElapsedMicroseconds => Volatile.Read(ref _origin) is var origin && origin != 0 ? (long)(Stopwatch.GetElapsedTime(origin).TotalMilliseconds * 1000) : 0;
    public uint GetMilliseconds() => (uint)Math.Min(
        unchecked(recordedClientOrigin + (uint)((originAdvanceMicroseconds + ElapsedMicroseconds) / 1000)),
        Volatile.Read(ref _pendingInputTime));
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}
