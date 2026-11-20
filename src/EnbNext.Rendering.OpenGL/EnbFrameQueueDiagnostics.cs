using System.Diagnostics;
using System.Runtime.InteropServices;
namespace EnbNext.Rendering.OpenGL;

public static class EnbFrameQueueDiagnostics
{
    public static readonly bool Enabled=Environment.GetEnvironmentVariable("ENB_DIAG_FRAME_QUEUE")=="1";
    public static readonly bool LegacyWait=Environment.GetEnvironmentVariable("ENB_DIAG_LEGACY_FRAME_WAIT")=="1";
    private static readonly object gate=new();
    private static readonly Dictionary<(int Queue,string Role),Totals> totals=[];
    private static readonly Queue<object> events=new();
    private static int nextId;
    internal static int NewId()=>Interlocked.Increment(ref nextId);
    private sealed class Totals
    {
        public long Waits,Timeouts,WaitTicks,CpuTicks,Polls,Flushes,FlushTicks,MaxWaitTicks,MaxFlushTicks;
    }
    public static object Snapshot()
    {
        lock(gate)return new {Mode=LegacyWait?"ANGLE 50ms argument (legacy)":"Monotonic 50ms deadline, zero-time driver polls, sleeping retries",
            CapturedUtc=DateTimeOffset.UtcNow,Queues=totals.Select(p=>new {p.Key.Queue,p.Key.Role,p.Value.Waits,p.Value.Timeouts,p.Value.Polls,
                WaitMs=p.Value.WaitTicks*1000d/Stopwatch.Frequency,CpuMs=p.Value.CpuTicks/10000d,
                MaxWaitMs=p.Value.MaxWaitTicks*1000d/Stopwatch.Frequency,p.Value.Flushes,
                FlushMs=p.Value.FlushTicks*1000d/Stopwatch.Frequency,MaxFlushMs=p.Value.MaxFlushTicks*1000d/Stopwatch.Frequency}).ToArray(),RecentEvents=events.ToArray()};
    }
    internal static long Cpu()
    {
        if(!OperatingSystem.IsWindows())return 0;
        return GetThreadTimes(new(-2),out _,out _,out long kernel,out long user)?kernel+user:0;
    }
    internal static void Wait(int id,string role,long start,long cpu,long age,uint result,int polls)
    {
        long end=Stopwatch.GetTimestamp(),elapsed=end-start,cpuTicks=Cpu()-cpu;
        if(elapsed>Stopwatch.Frequency/1000)EnbHitchEvents.Completed("fence.wait."+role,start,end);
        lock(gate)
        {
            var key=(id,role);if(!totals.TryGetValue(key,out var value))totals[key]=value=new();
            value.Waits++;value.WaitTicks+=elapsed;value.CpuTicks+=cpuTicks;value.Polls+=polls;
            value.MaxWaitTicks=Math.Max(value.MaxWaitTicks,elapsed);if(result==0x911B)value.Timeouts++;
            if(result==0x911B || elapsed>Stopwatch.Frequency/1000)
            {
                if(events.Count==128)events.Dequeue();
                events.Enqueue(new {Utc=DateTimeOffset.UtcNow,Queue=id,Role=role,Thread=Environment.CurrentManagedThreadId,
                    Result=result,WaitMs=elapsed*1000d/Stopwatch.Frequency,CpuMs=cpuTicks/10000d,
                    FenceAgeMs=age*1000d/Stopwatch.Frequency,Polls=polls});
            }
        }
    }
    internal static void Flush(int id,string role,long start)
    {
        long end=Stopwatch.GetTimestamp(),elapsed=end-start;
        if(elapsed>Stopwatch.Frequency/1000)EnbHitchEvents.Completed("fence.flush."+role,start,end);
        lock(gate){var key=(id,role);if(!totals.TryGetValue(key,out var value))totals[key]=value=new();value.Flushes++;value.FlushTicks+=elapsed;value.MaxFlushTicks=Math.Max(value.MaxFlushTicks,elapsed);}
    }
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread,out long creation,out long exit,out long kernel,out long user);
}
