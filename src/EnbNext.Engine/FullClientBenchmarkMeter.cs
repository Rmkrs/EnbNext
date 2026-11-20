using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EnbNext.Engine;

// Opt-in only. Cycles are retained as cycles, never converted to elapsed time.
// Short-scope GetThreadTimes deltas alias scheduler accounting ticks. Attribute
// each thread's whole-segment CPU proportionally to measured cycles instead;
// label that attribution as an estimate and retain both raw observations.
internal sealed class FullClientBenchmarkMeter
{
    internal static volatile FullClientBenchmarkMeter? Active;
    internal static string? StartupSignalPath;
    private static int _startupFrames;
    internal static void ResetStartupSignal(string path)
    {
        File.Delete(path);
        _startupFrames=0;
        StartupSignalPath=path;
    }
    internal static void RenderCompleted()
    {
        if(StartupSignalPath is not {} path)return;
        if(Interlocked.Increment(ref _startupFrames)!=30)return;
        StartupSignalPath=null;File.WriteAllText(path,"30");
    }
    [ThreadStatic] private static int _depth;
    private sealed class ThreadCounter(long cpu,ulong cycles,long wall,uint nativeId)
    {
        internal readonly uint NativeThreadId=nativeId;
        internal readonly long FirstCpu=cpu,FirstWall=wall;
        internal readonly ulong FirstCycles=cycles;
        internal long LastCpu=cpu,LastWall=wall;
        internal ulong LastCycles=cycles,OuterCycles;
        internal double CpuMs=>(LastCpu-FirstCpu)/10000d;
        internal double Attribute(ulong cycles)=>LastCycles>FirstCycles?CpuMs*(cycles/(double)(LastCycles-FirstCycles)):0;
    }
    private readonly object _sync=new();
    private readonly Dictionary<int,ThreadCounter> _threads=new();
    private readonly Dictionary<(string Name,int Thread), (long Calls,long Wall,long Cpu,long Bytes,ulong Cycles)> _totals=new();
    private readonly Dictionary<string,long> _observedWallTicks=new();
    private readonly Dictionary<string,long> _observedCounts=new();
    internal static void RecordObservedCount(string name,long count)
    {
        if(Active is not {} meter)return;
        lock(meter._sync)meter._observedCounts[name]=meter._observedCounts.GetValueOrDefault(name)+count;
    }
    internal static void RecordObservedWallTicks(string name,long ticks)
    {
        if(Active is not {} meter)return;
        lock(meter._sync)meter._observedWallTicks[name]=meter._observedWallTicks.GetValueOrDefault(name)+ticks;
    }
    internal static void RecordObservedMilliseconds(string name,double milliseconds) =>
        RecordObservedWallTicks(name,checked((long)Math.Round(milliseconds*Stopwatch.Frequency/1000d)));
    private readonly List<double> _intervals=new();
    private readonly List<double> _intervalEndOffsetsMs=new();
    private long _previousFrame;
    private readonly DateTimeOffset _startedUtc=DateTimeOffset.UtcNow;
    private readonly long _start=Stopwatch.GetTimestamp();
    private readonly long _fenceTimeoutStart=EnbNext.Rendering.OpenGL.EnbOpenGlFrameQueue.TimeoutCount;
    private readonly object? _queueStart=EnbNext.Rendering.OpenGL.EnbFrameQueueDiagnostics.Enabled?EnbNext.Rendering.OpenGL.EnbFrameQueueDiagnostics.Snapshot():null;
    private readonly long _workingSetStart=Process.GetCurrentProcess().WorkingSet64;
    private readonly long _managedStart=GC.GetTotalMemory(false);
    private readonly TimeSpan _processStart=Process.GetCurrentProcess().TotalProcessorTime;
    private readonly long _bytesStart=GC.GetTotalAllocatedBytes(false);
    private readonly TimeSpan _gcPauseStart=GC.GetTotalPauseDuration();
    private readonly TimeSpan _jitStart=System.Runtime.JitInfo.GetCompilationTime();
    private readonly long _jitMethodsStart=System.Runtime.JitInfo.GetCompiledMethodCount();
    private readonly int[] _gcStart=[GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)];
    internal static Scope Enter(string name,bool frame=false)
    {
        var meter=Active;
        long timestamp=frame && (meter is not null || FrameIntervalClock.IsObserved) ? FrameIntervalClock.BeginFrame() : 0;
        return meter is not null ? new(meter,name,frame,timestamp) : default;
    }
    // Diagnostic-only sequential scopes. Unlike sampled status text, these
    // cover every rendered frame in the measured segment. Their costs remain
    // inside the enclosing world scope; the unassigned remainder is reported.
    private static readonly bool ConfiguredWorldStages = Environment.GetEnvironmentVariable("ENB_DIAG_WORLD_STAGES") == "1";
    internal static bool WorldStagesEnabled => ConfiguredWorldStages || (DeveloperRenderDiagnostics.Available && DeveloperRenderDiagnostics.Open);
    internal static StageSequence? BeginStages(bool enabled,string first) =>
        enabled && Active is {} meter ? new(meter,first) : null;
    internal sealed class StageSequence : IDisposable
    {
        private readonly FullClientBenchmarkMeter _meter;
        private Scope _scope;
        private bool _disposed;
        internal StageSequence(FullClientBenchmarkMeter meter,string first)
        { _meter=meter;_scope=new(meter,first,false); }
        internal void Next(string name)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(StageSequence));
            _scope.Dispose();
            _scope=new(_meter,name,false);
        }
        public void Dispose()
        {
            if(_disposed)return;
            _disposed=true;_scope.Dispose();
        }
    }
    internal readonly struct Scope:IDisposable
    {
        private readonly FullClientBenchmarkMeter? _owner;
        private readonly string? _name;
        private readonly int _thread;
        private readonly long _wall,_cpu,_bytes;
        private readonly ulong _cycles;
        private readonly bool _outer;
        internal Scope(FullClientBenchmarkMeter owner,string name,bool frame,long frameTimestamp=0)
        {
            _owner=owner;_name=name;_thread=Environment.CurrentManagedThreadId;
            _wall=frame ? frameTimestamp : Stopwatch.GetTimestamp();_cpu=ThreadCpu();_cycles=ThreadCycles();
            _bytes=GC.GetAllocatedBytesForCurrentThread();_outer=_depth++==0;
            lock(owner._sync)
            {
                if(!owner._threads.ContainsKey(_thread))owner._threads.Add(_thread,new(_cpu,_cycles,_wall,GetCurrentThreadId()));
                if(frame)
                {
                    if(owner._previousFrame!=0)
                    {
                        owner._intervals.Add(Stopwatch.GetElapsedTime(owner._previousFrame,_wall).TotalMilliseconds);
                        owner._intervalEndOffsetsMs.Add(Stopwatch.GetElapsedTime(owner._start,_wall).TotalMilliseconds);
                    }
                    owner._previousFrame=_wall;
                }
            }
        }
        public void Dispose()
        {
            if(_owner is null)return;
            long endCpu=ThreadCpu(),endWall=Stopwatch.GetTimestamp();ulong endCycles=ThreadCycles();
            long bytes=GC.GetAllocatedBytesForCurrentThread()-_bytes;_depth--;
            if(EnbNext.Rendering.OpenGL.EnbHitchEvents.Enabled && endWall-_wall>=Stopwatch.Frequency/100)
                EnbNext.Rendering.OpenGL.EnbHitchEvents.CompletedScope("scope."+_name,_wall,endWall,endCpu-_cpu,endCycles-_cycles);
            lock(_owner._sync)
            {
                var key=(_name!,_thread);var prior=_owner._totals.GetValueOrDefault(key);
                _owner._totals[key]=(prior.Calls+1,prior.Wall+endWall-_wall,prior.Cpu+endCpu-_cpu,prior.Bytes+bytes,prior.Cycles+endCycles-_cycles);
                var thread=_owner._threads[_thread];thread.LastCpu=endCpu;thread.LastCycles=endCycles;thread.LastWall=endWall;
                if(_outer)thread.OuterCycles+=endCycles-_cycles;
            }
        }
    }
    internal object Finish()
    {
        lock(_sync)
        {
            double processMs=(Process.GetCurrentProcess().TotalProcessorTime-_processStart).TotalMilliseconds;
            double gcPauseMs=(GC.GetTotalPauseDuration()-_gcPauseStart).TotalMilliseconds;
            long frames=_totals.Where(p=>p.Key.Name=="world").Sum(p=>p.Value.Calls);
            long denominator=Math.Max(1,frames);
            double durationMs=Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            double attributed=_threads.Values.Sum(t=>t.Attribute(t.OuterCycles));
            return new {
                DeveloperDiagnostics=DeveloperRenderDiagnostics.Receipt(),
                Calibrated=false,StartedUtc=_startedUtc,EndedUtc=DateTimeOffset.UtcNow,Frames=frames,DurationMs=durationMs,
                FrameIntervalMs=_intervals.Count==0?0:_intervals.Average(),FrameIntervalsMs=_intervals.ToArray(),
                FrameHitches=FrameHitchMetrics.Summarize(_intervals),
                FrameIntervalEndOffsetsMs=_intervalEndOffsetsMs.ToArray(),
                FenceTimeoutsInSegment=EnbNext.Rendering.OpenGL.EnbOpenGlFrameQueue.TimeoutCount-_fenceTimeoutStart,
                FenceQueueStart=_queueStart,FenceQueueEnd=EnbNext.Rendering.OpenGL.EnbFrameQueueDiagnostics.Enabled?EnbNext.Rendering.OpenGL.EnbFrameQueueDiagnostics.Snapshot():null,
                ProcessCpuMs=processMs,ProcessCpuMsPerFrame=processMs/denominator,
                Memory=new {WorkingSetStartBytes=_workingSetStart,WorkingSetEndBytes=Process.GetCurrentProcess().WorkingSet64,
                    ProcessLifetimePeakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64,
                    ManagedLiveEstimateStartBytes=_managedStart,ManagedLiveEstimateEndBytes=GC.GetTotalMemory(false),
                    ManagedHeapAtLastGcBytes=GC.GetGCMemoryInfo().HeapSizeBytes,
                    Accounting="Working set is resident process memory. Managed estimate does not force GC; heap size is from the last GC. Peak working set covers process lifetime, not just this segment."},
                JitCompilationMs=(System.Runtime.JitInfo.GetCompilationTime()-_jitStart).TotalMilliseconds,
                JitCompiledMethods=System.Runtime.JitInfo.GetCompiledMethodCount()-_jitMethodsStart,
                ProcessJitBeforeSegmentMs=_jitStart.TotalMilliseconds,
                ProcessJitThroughSegmentMs=System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds,
                InstrumentedCpuEstimateMsPerFrame=attributed/denominator,
                UnattributedProcessCpuEstimateMsPerFrame=(processMs-attributed)/denominator,
                TotalAllocatedBytesPerFrame=(GC.GetTotalAllocatedBytes(false)-_bytesStart)/(double)denominator,
                GcCollections=Enumerable.Range(0,3).Select(i=>GC.CollectionCount(i)-_gcStart[i]).ToArray(),
                GcPauseMs=gcPauseMs,GcPauseMsPerFrame=gcPauseMs/denominator,
                GcPauseMsPerMinute=durationMs>0?gcPauseMs*60000/durationMs:0,
                GcCollectionAccounting="Collection counts are inclusive: gen0 includes gen1/gen2. They are not suspension counts. Individual pause count/duration and generation attribution require the GC event companion trace.",
                GcConfiguration=new {Server=System.Runtime.GCSettings.IsServerGC,LatencyMode=System.Runtime.GCSettings.LatencyMode.ToString(),
                    Settings=GC.GetConfigurationVariables()},
                GcPauseAccounting="Process-wide stop-the-world elapsed time. Overlaps frame intervals and enclosing wall scopes; not additive CPU or attributed to a rendering subsystem.",
                Threads=_threads.ToDictionary(p=>p.Key,p=>(object)new {p.Value.NativeThreadId,CpuMsPerFrame=p.Value.CpuMs/denominator,
                    ElapsedMsPerFrame=Stopwatch.GetElapsedTime(p.Value.FirstWall,p.Value.LastWall).TotalMilliseconds/denominator,
                    Cycles=p.Value.LastCycles-p.Value.FirstCycles}),
                InclusiveComponents=_totals.GroupBy(p=>p.Key.Name).ToDictionary(g=>g.Key,g=>(object)new {
                    Calls=g.Sum(p=>p.Value.Calls),ThreadIds=g.Select(p=>p.Key.Thread).ToArray(),
                    WallMsPerFrame=g.Sum(p=>p.Value.Wall)*1000d/Stopwatch.Frequency/denominator,
                    CpuEstimateMsPerFrame=g.Sum(p=>_threads[p.Key.Thread].Attribute(p.Value.Cycles))/denominator,
                    CoarseThreadTimeMsPerFrame=g.Sum(p=>p.Value.Cpu)/10000d/denominator,
                    CyclesPerFrame=g.Sum(p=>(double)p.Value.Cycles)/denominator,
                    BytesPerFrame=g.Sum(p=>p.Value.Bytes)/(double)denominator}),
                ObservedCounts=_observedCounts.ToDictionary(p=>p.Key,p=>(object)new {Total=p.Value,PerFrame=p.Value/(double)denominator}),
                ObservedWallOnlyMsPerFrame=_observedWallTicks.ToDictionary(p=>p.Key,p=>p.Value*1000d/Stopwatch.Frequency/denominator),
                Accounting="Component wall time and raw thread cycles are observed. Component CPU is a cycle-weighted allocation of each thread's whole-segment CPU, an estimate. Coarse short-scope thread-time deltas are retained only to diagnose accounting aliasing. Scanner is inclusive within world. Outer scopes count once. Process CPU includes parallel threads and must not be subtracted from frame interval to claim idle time."
            };
        }
    }
    private static uint GetCurrentThreadId()=>OperatingSystem.IsWindows()?WindowsThreadId():(uint)Environment.CurrentManagedThreadId;
    [DllImport("kernel32.dll",EntryPoint="GetCurrentThreadId")] private static extern uint WindowsThreadId();
    // Elsewhere the thread's CPU clock stands in for both CPU time (100 ns units)
    // and the cycle counter (nanoseconds); both are per-thread and monotonic.
    [StructLayout(LayoutKind.Sequential)] private struct TimeSpec { public nint Seconds,Nanoseconds; }
    [DllImport("libc",EntryPoint="clock_gettime",SetLastError=true)] private static extern int ClockGetTime(int clock,out TimeSpec time);
    private static ulong ThreadClockNanoseconds()
    {
        if(ClockGetTime(3,out var time)!=0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); // CLOCK_THREAD_CPUTIME_ID
        return (ulong)time.Seconds*1_000_000_000UL+(ulong)time.Nanoseconds;
    }
    private static ulong ThreadCycles()
    {
        if(!OperatingSystem.IsWindows())return ThreadClockNanoseconds();
        if(!QueryThreadCycleTime(new IntPtr(-2),out ulong cycles))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return cycles;
    }
    private static long ThreadCpu()
    {
        if(!OperatingSystem.IsWindows())return (long)(ThreadClockNanoseconds()/100);
        if(!GetThreadTimes(new IntPtr(-2),out _,out _,out long kernel,out long user))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return kernel+user;
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread,out ulong cycles);
}
