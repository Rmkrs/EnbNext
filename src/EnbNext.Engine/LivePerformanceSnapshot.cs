namespace EnbNext.Engine;

internal sealed record LivePerformanceSnapshot(double Fps,double CpuAverage,double CpuWorst,
    double P95,double Maximum,double Gap,double AllocationKiB,int Gen0,int Resources,
    double[] Stages,double[] Intervals,LiveFrameStallHistory.Sample? Worst,string StageScene)
{
    public string Describe()
    {
        string stages=Stages.Length==0?"Stage breakdown unavailable in this scene":StageScene=="Station"?
            $"Station CPU stages — mean ms\nAssets / population {Stages[0]:0.0}   Pose / collision {Stages[1]:0.0}\nRoom preparation {Stages[2]:0.0}   Ship {Stages[3]:0.0}\nRoom / actors {Stages[4]:0.0}   Audio {Stages[5]:0.0}\nNames / preloading {Stages[6]:0.0}":
            $"CPU stages — mean ms\nSetup {Stages[0]:0.0}   Animation {Stages[1]:0.0}   Objects {Stages[2]:0.0}\n"+
            $"Draw preparation {Stages[3]:0.0}\nSubmission + picking {Stages[4]:0.0}\nAudio {Stages[5]:0.0}   Diagnostics {Stages[6]:0.0}";
        string worst=Worst is {} w?$"Worst recent stall: {w.IntervalMs:0.0} ms\n  CPU {w.CpuMs:0.0} + outside callback {w.GapMs:0.0} ms":"No slow frames in the last 30 seconds";
        string uploads=StageScene=="Space"?$"   New world mesh resources: {Resources}":"";
        return $"PERFORMANCE\n{Fps:0.0} render FPS   |   60 FPS budget: 16.7 ms\n"+
            $"Frame interval p95 / max: {P95:0.0} / {Maximum:0.0} ms\nRender callback CPU avg / max: {CpuAverage:0.0} / {CpuWorst:0.0} ms\n"+
            $"Outside callback, latest: {Gap:0.0} ms\n\n{stages}\n"+
            $"\nLast frame: {AllocationKiB:0} KiB allocated\nGC0 since prior frame: {Gen0}{uploads}\n\n{worst}\n"+
            "\nGraph: frame intervals; white line = 16.7 ms\nCPU timings include driver waits; GPU time is not measured.\nOutside callback includes pacing, UI and scheduling.\nRender FPS is callback cadence, not monitor presentation.\nToggle with your configured Toggle Ping binding.";
    }
}
