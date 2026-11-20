using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("EnbNext.Net7.Tests")]

[assembly: InternalsVisibleTo("EnbNext.TestSupport")]
namespace EnbNext.Rendering.OpenGL;

/// <summary>Bounds application GL submissions, not the desktop compositor's presentation queue.</summary>
public static class EnbOpenGlFrameQueue
{
    private static long timeouts;
    private static readonly object timeoutGate=new();
    private static readonly System.Collections.Generic.Queue<TimeoutEvent> timeoutEvents=new();
    public sealed record TimeoutEvent(DateTime Utc, string Role, int ManagedThread, double? WaitMs, int? Polls);
    public static TimeoutEvent[] SnapshotTimeoutEvents(){lock(timeoutGate)return timeoutEvents.ToArray();}
    private static void RecordTimeout(string role,bool sleepingWait)
    {
        var item=new TimeoutEvent(DateTime.UtcNow,role,Environment.CurrentManagedThreadId,
            sleepingWait?EnbGlFenceWait.LastTimeoutTicks*1000.0/Stopwatch.Frequency:null,
            sleepingWait?EnbGlFenceWait.LastPolls:null);
        lock(timeoutGate){if(timeoutEvents.Count==16)timeoutEvents.Dequeue();timeoutEvents.Enqueue(item);}
    }
    public static long TimeoutCount=>Interlocked.Read(ref timeouts);
    public static Scope Enter(GlInterface gl) => new(gl.FrameQueue ??= Create(gl));
    public static bool TryEnter(GlInterface gl,out Scope scope,string role="view")
    {
        var queue=gl.FrameQueue ??= Create(gl);
        queue.SetRole(role);
        if(!queue.TryBegin()){scope=default;return false;}
        scope=new Scope(queue,true);return true;
    }
    public static void Release(GlInterface gl, bool contextLost = false)
    {
        if (gl.FrameQueue is not {} queue) return;
        gl.FrameQueue=null;
        queue.Release(contextLost);
    }
    private static Queue Create(GlInterface gl)
    {
        T Load<T>(string name) where T : Delegate
        {
            nint address = gl.GetProcAddress(name);
            if (address == 0) throw new NotSupportedException("Required frame queue entry point: " + name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
        var fence = Load<Fence>("glFenceSync");
        var wait = Load<Wait>("glClientWaitSync");
        var delete = Load<Delete>("glDeleteSync");
        var flush = Load<Flush>("glFlush");
        Func<nint,uint,uint> poll=(sync,flags)=>wait(sync,flags,0);
        return new(() => fence(0x9117, 0), sync => EnbFrameQueueDiagnostics.LegacyWait
            ? wait(sync,1,50_000_000) : EnbGlFenceWait.Wait(sync,poll), sync => delete(sync), () => flush(), !EnbFrameQueueDiagnostics.LegacyWait);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint Fence(uint condition, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Wait(nint sync, uint flags, ulong timeout);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Delete(nint sync);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Flush();
    public readonly struct Scope : IDisposable
    {
        private readonly Queue? queue;
        internal Scope(Queue queue) { this.queue = queue; queue.Begin(); }
        internal Scope(Queue queue,bool alreadyEntered) {this.queue=queue;}
        public void Dispose() => queue?.End();
    }
    // All calls are made with this context current on its render thread. Nested
    // scanner/host passes share the outer frame and do not insert extra fences.
    internal sealed class Queue(Func<nint> fence, Func<nint, uint> wait, Action<nint> delete, Action flush, bool sleepingWait=false)
    {
        internal string Role="view";
        private readonly int diagnosticId=EnbFrameQueueDiagnostics.Enabled?EnbFrameQueueDiagnostics.NewId():0;
        private readonly long[] submitted=new long[2];
        private readonly nint[] pending = new nint[2];
        private int head, count, depth;
        private bool failed;
        internal void SetRole(string role) { if(depth==0)Role=role; }
        internal void Begin()=>BeginCore(retryTimeout:false);
        internal bool TryBegin()=>BeginCore(retryTimeout:true);
        private bool BeginCore(bool retryTimeout)
        {
            if (failed) throw new InvalidOperationException("GL frame queue failed; recreate the context before submitting more work.");
            if (depth != 0) { depth++; return true; }
            if (count == 2)
            {
                // A finite client wait yields the CPU; never enqueue a server
                // wait, busy-poll, or submit a third frame after a timeout.
                long start=diagnosticId==0?0:Stopwatch.GetTimestamp(),cpu=diagnosticId==0?0:EnbFrameQueueDiagnostics.Cpu();
                uint result = wait(pending[head]);
                if(diagnosticId!=0)EnbFrameQueueDiagnostics.Wait(diagnosticId,Role,start,cpu,start-submitted[head],result,
                    EnbFrameQueueDiagnostics.LegacyWait?1:EnbGlFenceWait.LastPolls);
                if(result==0x911B)
                {
                    Interlocked.Increment(ref timeouts);
                    RecordTimeout(Role,sleepingWait);
                    // A timeout is backpressure, not a failed GL context. Keep
                    // both fences and submit nothing; a later frame may retry.
                    if(retryTimeout)return false;
                }
                if (result is not (0x911A or 0x911C))
                {
                    failed = true;
                    throw new InvalidOperationException($"GL frame queue wait failed or timed out: 0x{result:X}");
                }
                delete(pending[head]); pending[head] = 0;
                head = (head + 1) % 2; count--;
            }
            depth = 1;
            return true;
        }
        internal void End()
        {
            if (--depth != 0) return;
            nint sync = fence();
            if (sync == 0) { failed = true; throw new InvalidOperationException("glFenceSync failed for frame queue."); }
            int slot=(head + count) % 2;
            pending[slot] = sync; count++;
            long start=diagnosticId==0?0:Stopwatch.GetTimestamp();
            submitted[slot]=start;
            flush();
            if(diagnosticId!=0)EnbFrameQueueDiagnostics.Flush(diagnosticId,Role,start);
        }
        internal void Release(bool lost)
        {
            if (!lost) foreach (nint sync in pending) if (sync != 0) delete(sync);
            Array.Clear(pending); count = depth = head = 0; failed = true;
        }
    }
}
