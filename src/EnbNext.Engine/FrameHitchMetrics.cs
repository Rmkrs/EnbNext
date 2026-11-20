namespace EnbNext.Engine;

// Receipt-time only: no sorting or additional allocation on the frame path.
internal static class FrameHitchMetrics
{
    internal static object Summarize(IEnumerable<double> intervals)
    {
        var values=intervals.Where(v=>double.IsFinite(v)&&v>=0).Order().ToArray();
        double observedMs=values.Sum();
        double? Percentile(double p)=>values.Length==0?null:values[(int)Math.Ceiling(p*values.Length)-1];
        return new {
            IntervalCount=values.Length, ObservedIntervalMs=observedMs,
            P50Ms=Percentile(.50),P95Ms=Percentile(.95),P99Ms=Percentile(.99),
            MaxMs=values.Length==0?(double?)null:values[^1],
            Hitches=new[]{16.6666667,25d,33.3333333,50d,100d}.Select(t=>new {
                ThresholdMs=t,Count=values.Count(v=>v>t),
                PerMinute=observedMs>0?values.Count(v=>v>t)*60000/observedMs:(double?)null }),
            Accounting="Nearest-rank percentiles of callback-start intervals, including pauses and waits; not GPU presentation intervals. Hitch counts use strictly greater than threshold. Rates use observed interval duration, not receipt wall time. No samples gives null percentiles."
        };
    }
}
