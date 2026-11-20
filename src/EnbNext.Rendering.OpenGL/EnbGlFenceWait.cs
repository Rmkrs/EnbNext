using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace EnbNext.Rendering.OpenGL;

// Own the deadline: the bundled ANGLE D3D11 implementation truncates a
// sub-second glClientWaitSync timeout to zero. Poll without a driver wait,
// sleeping between attempts; retain the original two-frame queue boundary.
internal static class EnbGlFenceWait
{
    [ThreadStatic] private static Sleeper? sleeper;
    [ThreadStatic] internal static int LastPolls;
    [ThreadStatic] internal static long LastTimeoutTicks;
    private static readonly Func<long> clock=Stopwatch.GetTimestamp;
    private static readonly Action pause=()=> (sleeper??=new()).Wait();
    internal static uint Wait(nint sync,Func<nint,uint,uint> poll) =>
        Wait(sync,poll,clock,pause,Stopwatch.Frequency/20);
    internal static uint Wait(nint sync,Func<nint,uint,uint> poll,Func<long> timestamp,Action sleep,long budget)
    {
        long start=timestamp();LastPolls=0;LastTimeoutTicks=0;
        while(true)
        {
            uint result=poll(sync,LastPolls++==0?1u:0u);
            if(result!=0x911B)return result;
            long elapsed=timestamp()-start;
            if(elapsed>=budget){LastTimeoutTicks=elapsed;return result;}
            sleep();
        }
    }
    private sealed class Sleeper
    {
        private readonly EventWaitHandle? timer;
        internal Sleeper()
        {
            if(!OperatingSystem.IsWindows())return;
            var handle=CreateWaitableTimerExW(0,null,2,0x1F0003);
            if(handle.IsInvalid){handle.Dispose();return;}
            timer=new(false,EventResetMode.AutoReset){SafeWaitHandle=handle};
        }
        internal void Wait()
        {
            long due=-2500; // 250 microseconds, sleeping in the kernel.
            if(timer is not null && SetWaitableTimer(timer.SafeWaitHandle,ref due,0,0,0,false))timer.WaitOne();
            else Thread.Sleep(1);
        }
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer,ref long dueTime,int period,nint callback,nint argument,[MarshalAs(UnmanagedType.Bool)]bool resume);
}
