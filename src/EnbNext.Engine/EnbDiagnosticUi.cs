using System.Diagnostics;
using System.Text.Json;
using System.Text;
using EnbNext.Presentation;

namespace EnbNext.Engine;

// These developer controls have no retail counterpart. Their state belongs to
// the game thread; the host supplies only clipboard and frame-cap operations.
internal sealed class EnbDiagnosticUi(Func<string> report,Action<string> clipboard,
    Func<(int Foreground,int Background)> readCaps,Action<int,int> writeCaps) : IDisposable
{
    internal bool Captured=>pressed!=-1;
    internal (int Foreground,int Background) Caps=>readCaps();
    internal EnbUiSize Size {get;private set;}
    internal bool PerformanceVisible {get;private set;}
    internal LivePerformanceSnapshot? Performance {get;private set;}
    internal bool Details {get;private set;}
    internal bool IssuesOpen {get;private set;}
    internal int IssueCount {get;set;}
    internal string IssueText {get;private set;}="";
    internal string Status {get;private set;}="";
    internal bool DeveloperOpen {get;private set;}
    internal string DeveloperText {get;private set;}="Waiting for world frames...";
    internal double IssueScroll {get;private set;}
    internal double Scroll {get;private set;}
    internal EnbUiPoint PerformancePosition {get;private set;}=new(12,260);
    private EnbUiPoint? drag;
    private EnbUiPoint dragOrigin;
    private int pressed=-1;
    private FullClientBenchmarkMeter? meter;
    private string? receipt;
    private long nextSample,recordingStart;
    private (int Foreground,int Background) savedCaps;
    internal event Action<bool>? CaptureRequested;
    internal event Action? GameplaySuspended;
    private double? performanceHeight;
    internal void MeasurePerformanceBody(double height){performanceHeight=height+116;ClampPerformance();}
    internal EnbUiRect PerformanceBounds=>new(PerformancePosition.X,PerformancePosition.Y,Math.Min(390,Math.Max(100,Size.Width-24)),performanceHeight??(Details?470:190));
    internal EnbUiRect IssueBadge=>new(Math.Max(0,(Size.Width-80)/2),80,80,28);
    internal EnbUiRect IssueBounds=>new(Math.Max(0,(Size.Width-520)/2),113,Math.Min(520,Size.Width),Math.Min(400,Math.Max(100,Size.Height-125)));
    internal EnbUiRect DeveloperBounds=>new(Math.Max(0,(Size.Width-660)/2),12,Math.Min(660,Size.Width),Math.Max(100,Size.Height-24));
    internal void Resize(EnbUiSize size){Size=size;ClampPerformance();}
    internal void UpdatePerformance(bool visible,LivePerformanceSnapshot? snapshot){PerformanceVisible=visible;Performance=snapshot;ClampPerformance();}
    private void ClampPerformance(){var r=PerformanceBounds;PerformancePosition=new(Math.Clamp(r.X,0,Math.Max(0,Size.Width-r.Width)),Math.Clamp(r.Y,0,Math.Max(0,Size.Height-r.Height)));}
    internal string PerformanceText=>Performance is not {} s?"Waiting for frame measurements…":Details?s.Describe():
        $"{s.Fps:0.0} FPS   •   target ≥60 FPS / 16.7 ms\nCPU {s.CpuAverage:0.0} ms   •   frame p95 {s.P95:0.0} ms\n"+
        (s.Stages.Length==7?(s.StageScene=="Station"?$"Ship {s.Stages[3]:0.0}   Room / actors {s.Stages[4]:0.0} ms":$"Objects {s.Stages[2]:0.0}   Prepare {s.Stages[3]:0.0}   Submit {s.Stages[4]:0.0} ms")+"\n":"")+
        $"Allocated {s.AllocationKiB/1024:0.0} MiB/frame   •   GC0 {s.Gen0}";
    internal EnbUiRect ActionBounds(int id)
    {
        var p=PerformanceBounds;var i=IssueBounds;var d=DeveloperBounds;
        return id switch
        {
            0=>new(p.X,p.Y,Math.Max(0,p.Width-90),32),1=>new(p.Right-90,p.Y,90,32),2=>IssueBadge,
            3=>new(i.X+12,i.Bottom-36,155,26),4=>new(i.Right-85,i.Bottom-36,73,26),
            5=>new(d.Right-80,d.Y+8,68,26),
            >=10 and <=18=>new(d.X+14,d.Y+90+(id-10)*26-Scroll,d.Width-28,24),
            20=>new(d.X+14,d.Y+330-Scroll,30,26),21=>new(d.X+210,d.Y+330-Scroll,30,26),
            22=>new(d.X+14,d.Y+360-Scroll,30,26),23=>new(d.X+210,d.Y+360-Scroll,30,26),
            24=>new(d.X+14,d.Y+396-Scroll,190,28),25=>new(d.X+214,d.Y+396-Scroll,210,28),
            _=>default,
        };
    }
    internal bool Blocks(EnbUiPoint point)=>DeveloperOpen&&DeveloperBounds.Contains(point)||IssueCount>0&&(IssueBadge.Contains(point)||IssuesOpen&&IssueBounds.Contains(point));
    internal int Hit(EnbUiPoint point)
    {
        if(DeveloperOpen&&DeveloperBounds.Contains(point))
        {
            if(ActionBounds(5).Contains(point))return 5;
            if(point.Y<DeveloperBounds.Y+70)return -2;
            for(int n=10;n<=25;n++)if(n!=19&&ActionBounds(n).Contains(point))return n;
            return -2;
        }
        if(IssueCount>0)
        {
            if(IssueBadge.Contains(point))return 2;
            if(IssuesOpen&&IssueBounds.Contains(point)){if(ActionBounds(3).Contains(point))return 3;if(ActionBounds(4).Contains(point))return 4;return -2;}
        }
        if(PerformanceVisible){if(ActionBounds(1).Contains(point))return 1;if(ActionBounds(0).Contains(point))return 0;}
        return -1;
    }
    internal bool Press(EnbUiPoint point)
    {
        int hit=Hit(point);if(hit==-1)return false;pressed=hit;
        if(hit==0){drag=point;dragOrigin=PerformancePosition;}
        GameplaySuspended?.Invoke();CaptureRequested?.Invoke(true);return true;
    }
    internal bool Move(EnbUiPoint point)
    {
        if(drag is {} start){PerformancePosition=new(dragOrigin.X+point.X-start.X,dragOrigin.Y+point.Y-start.Y);ClampPerformance();return true;}
        return pressed!=-1||Blocks(point);
    }
    internal bool Release(EnbUiPoint point)
    {
        int action=pressed;if(action==-1)return false;bool activate=Hit(point)==action;Cancel();if(activate)Activate(action);return true;
    }
    internal void Cancel(){pressed=-1;drag=null;CaptureRequested?.Invoke(false);}
    internal bool Wheel(EnbUiPoint point,double delta)
    {
        if(IssuesOpen&&IssueBounds.Contains(point)){IssueScroll=Math.Clamp(IssueScroll-delta*32,0,Math.Max(0,IssueText.Length*8/Math.Max(1,IssueBounds.Width-24)*20-IssueBounds.Height+80));return true;}
        if(!DeveloperOpen||!DeveloperBounds.Contains(point))return false;
        Scroll=Math.Clamp(Scroll-delta*32,0,Math.Max(0,435+DeveloperText.Count(c=>c=='\n')*20+100-DeveloperBounds.Height));return true;
    }
    private void Activate(int action)
    {
        switch(action)
        {
            case 1:Details=!Details;performanceHeight=null;ClampPerformance();break;
            case 2:IssuesOpen=!IssuesOpen;if(IssuesOpen){IssueScroll=0;IssueText=report();Status="";}break;
            case 3:try{clipboard(report());Status="Diagnostics copied.";}catch(Exception e){Status="Could not copy: "+e.Message;}break;
            case 4:IssuesOpen=false;break;
            case 5:CloseDeveloper();break;
            case >=10 and <=18:SetSwitch(action-10,!Switch(action-10));DeveloperRenderDiagnostics.NotifyChanged();break;
            case >=20 and <=23:
                var caps=readCaps();int step=action%2==0?-1:1;
                if(action<22)caps.Foreground=Math.Clamp(caps.Foreground+step,1,1000);else caps.Background=Math.Clamp(caps.Background+step,0,1000);
                writeCaps(caps.Foreground,caps.Background);DeveloperRenderDiagnostics.FocusedCap=caps.Foreground;DeveloperRenderDiagnostics.BackgroundCap=caps.Background;DeveloperRenderDiagnostics.NotifyChanged();break;
            case 24:if(receipt is not null)SaveReceipt(receipt);break;
            case 25:if(meter is not null&&ReferenceEquals(FullClientBenchmarkMeter.Active,meter)){receipt=null;StartMeter();recordingStart=Stopwatch.GetTimestamp();Status="Recording 10 seconds; keep settings and view unchanged...";}break;
        }
    }
    internal static bool Switch(int index)=>index switch{0=>DeveloperRenderDiagnostics.Dazzles,1=>DeveloperRenderDiagnostics.Particles,2=>DeveloperRenderDiagnostics.Effects,3=>DeveloperRenderDiagnostics.Scanner,4=>DeveloperRenderDiagnostics.Hud,5=>DeveloperRenderDiagnostics.Backdrop,6=>DeveloperRenderDiagnostics.Shadows,7=>DeveloperRenderDiagnostics.Gutter,_=>DeveloperRenderDiagnostics.Audio};
    private static void SetSwitch(int index,bool value)
    {
        switch(index){case 0:DeveloperRenderDiagnostics.Dazzles=value;break;case 1:DeveloperRenderDiagnostics.Particles=value;break;case 2:DeveloperRenderDiagnostics.Effects=value;break;case 3:DeveloperRenderDiagnostics.Scanner=value;break;case 4:DeveloperRenderDiagnostics.Hud=value;break;case 5:DeveloperRenderDiagnostics.Backdrop=value;break;case 6:DeveloperRenderDiagnostics.Shadows=value;break;case 7:DeveloperRenderDiagnostics.Gutter=value;break;case 8:DeveloperRenderDiagnostics.Audio=value;break;}
    }
    internal bool OpenDeveloper()
    {
        if(DeveloperOpen)return true;
        if(!DeveloperRenderDiagnostics.Available||FullClientBenchmarkMeter.Active is not null||!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENBNEXT_FULL_CLIENT_BENCHMARK")))return false;
        savedCaps=readCaps();DeveloperRenderDiagnostics.FocusedCap=savedCaps.Foreground;DeveloperRenderDiagnostics.BackgroundCap=savedCaps.Background;
        DeveloperOpen=true;Scroll=0;Status="";DeveloperRenderDiagnostics.OpenSession();StartMeter();DeveloperRenderDiagnostics.Changed+=ResetSample;GameplaySuspended?.Invoke();return true;
    }
    internal void CloseDeveloper()
    {
        if(!DeveloperOpen)return;DeveloperOpen=false;Cancel();DeveloperRenderDiagnostics.Changed-=ResetSample;
        if(ReferenceEquals(FullClientBenchmarkMeter.Active,meter))FullClientBenchmarkMeter.Active=null;
        meter=null;recordingStart=0;writeCaps(savedCaps.Foreground,savedCaps.Background);DeveloperRenderDiagnostics.FocusedCap=savedCaps.Foreground;DeveloperRenderDiagnostics.BackgroundCap=savedCaps.Background;DeveloperRenderDiagnostics.CloseSession();
    }
    private void StartMeter(){meter=new();FullClientBenchmarkMeter.Active=meter;}
    private void ResetSample()
    {
        if(meter is null||!ReferenceEquals(FullClientBenchmarkMeter.Active,meter))return;
        if(recordingStart!=0){recordingStart=0;Status="Recording cancelled: settings changed. Start again for an unmixed 10-second sample.";}
        receipt=null;StartMeter();DeveloperText="Settings changed; waiting for the next sample...";
    }
    internal void Tick()
    {
        if(!DeveloperOpen||Environment.TickCount64<nextSample)return;nextSample=Environment.TickCount64+500;
        if(meter is null||!ReferenceEquals(FullClientBenchmarkMeter.Active,meter)){DeveloperText="Another collector owns the meter; close this panel before benchmarking.";return;}
        if(recordingStart!=0&&Stopwatch.GetElapsedTime(recordingStart).TotalSeconds<10){Status=$"Recording {Stopwatch.GetElapsedTime(recordingStart).TotalSeconds:F1} / 10 seconds...";return;}
        receipt=JsonSerializer.Serialize(meter.Finish());if(recordingStart!=0){recordingStart=0;SaveReceipt(receipt);}StartMeter();
        var result=JsonSerializer.Deserialize<JsonElement>(receipt);
        double interval=result.GetProperty("FrameIntervalMs").GetDouble();
        var text=new StringBuilder();
        text.AppendLine($"{(interval>0?1000/interval:0):F1} FPS | {interval:F2} ms (callback interval)");
        text.AppendLine($"Process CPU {result.GetProperty("ProcessCpuMsPerFrame").GetDouble():F2} ms/frame");
        var hitches=result.GetProperty("FrameHitches");
        text.AppendLine($"p95 {hitches.GetProperty("P95Ms")} | p99 {hitches.GetProperty("P99Ms")} | max {hitches.GetProperty("MaxMs")} ms");
        text.AppendLine($"GC pause total {result.GetProperty("GcPauseMs").GetDouble():F2} ms / {result.GetProperty("DurationMs").GetDouble()/1000:F1} s (individual pauses need GC trace)");
        var components=result.GetProperty("InclusiveComponents");
        if(components.TryGetProperty("world",out var world)&&world.GetProperty("ThreadIds").GetArrayLength()>0)
        {
            int ui=world.GetProperty("ThreadIds")[0].GetInt32();
            if(result.GetProperty("Threads").TryGetProperty(ui.ToString(),out var thread))
                text.AppendLine($"UI-thread sampled-window CPU {thread.GetProperty("CpuMsPerFrame").GetDouble():F2} ms/frame");
        }
        foreach(var p in components.EnumerateObject().Where(p=>p.Name.StartsWith("world-phase.") || p.Name is "scanner" or "hud"))
            text.AppendLine($"{p.Name,-34} {p.Value.GetProperty("CpuEstimateMsPerFrame").GetDouble(),6:F2} CPU-est ms");
        foreach(var p in result.GetProperty("ObservedCounts").EnumerateObject())
            text.AppendLine($"{p.Name,-34} {p.Value.GetProperty("PerFrame").GetDouble(),8:F0}/frame");
        var memory=result.GetProperty("Memory");
        text.AppendLine($"RSS {memory.GetProperty("WorkingSetEndBytes").GetDouble()/1048576:F1} MiB | managed estimate {memory.GetProperty("ManagedLiveEstimateEndBytes").GetDouble()/1048576:F1} MiB");
        DeveloperText=text.ToString();
    }
    private void SaveReceipt(string text)
    {
        string directory=Path.Combine(LiveIssueLog.DefaultDirectory,"developer"),path=Path.Combine(directory,$"diagnostic-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.json");
        try{Directory.CreateDirectory(directory);File.WriteAllText(path,text);Status="Saved "+path;}catch(Exception e) when(e is IOException or UnauthorizedAccessException){Status="Save failed: "+e.Message;}
    }
    public void Dispose(){CloseDeveloper();Cancel();}
}
