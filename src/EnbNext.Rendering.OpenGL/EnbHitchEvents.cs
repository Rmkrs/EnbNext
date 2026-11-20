using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace EnbNext.Rendering.OpenGL;

// Attribution-only. Disabled in normal play and untraced timing controls.
[EventSource(Name="EnbNext-Hitch")]
public sealed class EnbHitchEvents : EventSource
{
    public static readonly bool Enabled=Environment.GetEnvironmentVariable("ENB_DIAG_HITCH_EVENTS")=="1";
    private static readonly EnbHitchEvents Log=new();
    public static Scope Measure(string operation)=>Enabled?new(operation):default;
    public static void Completed(string operation,long start,long end)
    { if(Enabled&&Log.IsEnabled())Log.WorkCompleted(operation,start,end,Stopwatch.Frequency); }
    [Event(1,Level=EventLevel.Informational)]
    public void WorkCompleted(string operation,long start,long end,long frequency)=>WriteEvent(1,operation,start,end,frequency);
    public static void CompletedScope(string operation,long start,long end,long cpu100ns,ulong cycles)
    { if(Enabled&&Log.IsEnabled())Log.ScopeCompleted(operation,start,end,Stopwatch.Frequency,cpu100ns,cycles); }
    [Event(2,Level=EventLevel.Informational)]
    public void ScopeCompleted(string operation,long start,long end,long frequency,long cpu100ns,ulong cycles)
        =>WriteEvent(2,operation,start,end,frequency,cpu100ns,cycles);
    public readonly struct Scope : IDisposable
    {
        private readonly string? operation;
        private readonly long start;
        internal Scope(string operation){this.operation=operation;start=Stopwatch.GetTimestamp();}
        public void Dispose(){if(operation is not null)Completed(operation,start,Stopwatch.GetTimestamp());}
    }
}
