using System.Globalization;

namespace EnbNext.Engine;

// Render-thread samples; UI lifecycle resets can also clear the history.
// Keep the worst eight samples for thirty seconds so a
// settled one-second cadence window cannot hide the hitch being investigated.
internal sealed class LiveFrameStallHistory
{
    private readonly List<Sample> _samples = new(8);
    private readonly object _gate = new();

    internal readonly record struct Sample(double EndSeconds, double CpuMs, double GapMs,
        long AllocatedBytes, int Gen0Collections, int WorldGpuCreates, uint? ClientTime, string Profile)
    {
        public double IntervalMs => CpuMs + GapMs;
        // Publication is the UI's immutable world-frame handoff, not GPU presentation.
        public double? SceneAgeAtCallbackMs { get; init; }
        public string PublicationTiming => SceneAgeAtCallbackMs is not double age ? "" :
            age <= GapMs
                ? string.Create(CultureInfo.InvariantCulture, $" · gap before/after scene publish {Math.Max(0, GapMs-age):0.00}/{age:0.00}ms")
                : string.Create(CultureInfo.InvariantCulture, $" · no new scene during gap (scene age {age:0.00}ms)");
    }

    public void Clear() { lock (_gate) _samples.Clear(); }
    public Sample? Worst(double nowSeconds)
    {
        lock(_gate)
        {
            Expire(nowSeconds);
            return _samples.Count==0?null:_samples.MaxBy(s=>s.IntervalMs);
        }
    }

    public void Record(Sample sample)
    {
        lock (_gate) RecordCore(sample);
    }

    private void RecordCore(Sample sample)
    {
        Expire(sample.EndSeconds);
        if (sample.IntervalMs < 25 && sample.CpuMs < 1000.0 / 60) return;
        if (_samples.Count == 8)
        {
            int smallest = 0;
            for (int i = 1; i < _samples.Count; i++)
                if (_samples[i].IntervalMs < _samples[smallest].IntervalMs) smallest = i;
            if (sample.IntervalMs < _samples[smallest].IntervalMs) return;
            _samples.RemoveAt(smallest);
        }
        _samples.Add(sample);
    }

    public string Describe(double nowSeconds)
    {
        lock (_gate) return DescribeCore(nowSeconds);
    }

    private string DescribeCore(double nowSeconds)
    {
        Expire(nowSeconds);
        string header = "slow frames (worst 8 / last 30s; CPU includes scanner, gap is outside render callback; GC is process-wide since previous frame):";
        if (_samples.Count == 0) return header + " none";
        return header + "\n" + string.Join("\n", _samples.OrderByDescending(s => s.IntervalMs).Select(s =>
            string.Create(CultureInfo.InvariantCulture,
                $"  age {Math.Max(0, nowSeconds-s.EndSeconds):0.0}s · client {s.ClientTime}ms · interval {s.IntervalMs:0.00}ms = CPU {s.CpuMs:0.00} + gap {s.GapMs:0.00}{s.PublicationTiming} · render alloc {s.AllocatedBytes/1024.0:0}KiB · GC0 {s.Gen0Collections} · world GPU new {s.WorldGpuCreates} · {s.Profile}")));
    }

    private void Expire(double nowSeconds)
    {
        for (int i = _samples.Count-1; i >= 0; i--)
            if (nowSeconds-_samples[i].EndSeconds > 30) _samples.RemoveAt(i);
    }
}
