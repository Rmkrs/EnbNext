using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace EnbNext.Rendering;

/// <summary>Opt-in observation only. Never supplies a renderer/cache decision.</summary>
public static class EnbWorkReuseDiagnostics
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("ENB_WORK_REUSE_OUTPUT");
    private static readonly ConditionalWeakTable<object, History> Histories = new();
    private static readonly Channel<object> Queue = Channel.CreateBounded<object>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static long dropped;
    private static int nextView;
    [ThreadStatic] private static Frame? current;
    public static bool Active => current?.Detailed == true;
    public static bool Enabled => !string.IsNullOrWhiteSpace(Output);
    public static void Context(object value) { if(current is {} frame)frame.Metadata=value; }
    static EnbWorkReuseDiagnostics()
    {
        if (Enabled) _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Output!))!);
                await using var writer = new StreamWriter(Output!, append: true);
                await foreach (var item in Queue.Reader.ReadAllAsync())
                { await writer.WriteLineAsync(JsonSerializer.Serialize(item)); await writer.FlushAsync(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Interlocked.Increment(ref dropped); /* Diagnostics must not take down the client. */ }
        });
    }
    internal sealed class History
    {
        public readonly int Id = Interlocked.Increment(ref nextView);
        public long Number;
        public Dictionary<(string, object), object?[]> Previous = [];
    }
    private readonly record struct ReferenceValue(object? Value)
    {
        public bool Equals(ReferenceValue other) => ReferenceEquals(Value, other.Value);
        public override int GetHashCode() => Value is null ? 0 : RuntimeHelpers.GetHashCode(Value);
    }
    public static object Reference(object? value) => new ReferenceValue(value);
    public static void ObservePacket(string domain, EnbW3dDrawTask packet)
    {
        if (!Active) return;
        object key=(packet.RenderIdentity,packet.OrderKey,packet.RenderInstancePath);
        Observe(domain+".topology",key,Reference(packet.Mesh),Reference(packet.Material.Source),
            Reference(packet.CurrentRetailMaterialMappers),Reference(packet.CurrentRetailSharedMaterialMappers),
            packet.VisualGeneration,packet.RenderObjectName);
        Observe(domain+".pose",key,packet.WorldTransform,packet.CurrentRetailMeshModelScale);
        Add(domain+".vertices",packet.Mesh.Positions.Count);
        Add(domain+".indices",packet.Mesh.Indices.Count);
        if(packet.DeformedGeometry is not null)Add(domain+".deformedPackets");
        if(packet.CurrentRetailViewPositions is not null)Add(domain+".viewGeneratedPackets");
    }
    public static Frame? Begin(object view, string kind, long clientTime)
    {
        if (!Enabled) return null;
        var history = Histories.GetOrCreateValue(view);
        // Hard bound on diagnostic recording; a new process starts a new capture.
        if (history.Number >= 30000) { history.Previous.Clear(); return null; }
        var frame = new Frame(history, current, kind, clientTime);
        current = frame;
        return frame;
    }
    public static bool Observe(string domain, object key, params object?[] state)
    {
        if (current is not { Detailed: true } frame) return false;
        long start = Stopwatch.GetTimestamp();
        var identity = (domain, key);
        bool repeated = frame.Now.ContainsKey(identity);
        var source = repeated ? frame.Now : frame.History.Previous;
        bool known = source.TryGetValue(identity, out var previous);
        bool same = known && previous!.AsSpan().SequenceEqual(state);
        frame.Now[identity] = state;
        Add(domain + (repeated ? ".repeat" : "") + (same ? ".unchanged" : known ? ".changed" : ".added"));
        frame.ObserverTicks += Stopwatch.GetTimestamp() - start;
        return same;
    }
    public static void Add(string name, long count = 1)
    {
        if (current is {} frame) frame.Counts[name] = frame.Counts.GetValueOrDefault(name) + count;
    }
    public static Timing Measure(string name) => new(current, name);
    public static (long Clock,long Overhead) Stamp() => current is {} frame ? (Stopwatch.GetTimestamp(),frame.ObserverTicks) : default;
    public static void Elapsed(string name,(long Clock,long Overhead) stamp)
    {
        if(current is {} frame)frame.Ticks[name]=frame.Ticks.GetValueOrDefault(name)+
            Math.Max(0,Stopwatch.GetTimestamp()-stamp.Clock-(frame.ObserverTicks-stamp.Overhead));
    }
    public static Inspection Inspect() => new(current);
    public readonly struct Inspection : IDisposable
    {
        private readonly Frame? frame;
        private readonly long start, previous;
        internal Inspection(Frame? value)
        { frame=value; start=value is null?0:Stopwatch.GetTimestamp(); previous=value?.ObserverTicks??0; }
        public void Dispose()
        { if(frame is not null) frame.ObserverTicks=previous+Stopwatch.GetTimestamp()-start; }
    }
    public readonly struct Timing : IDisposable
    {
        private readonly Frame? frame;
        private readonly string name;
        private readonly long start, overhead;
        internal Timing(Frame? value, string label)
        { frame = value; name = label; start = value is null ? 0 : Stopwatch.GetTimestamp(); overhead = value?.ObserverTicks ?? 0; }
        public void Dispose()
        {
            if (frame is null) return;
            long elapsed = Math.Max(0, Stopwatch.GetTimestamp() - start - (frame.ObserverTicks - overhead));
            frame.Ticks[name] = frame.Ticks.GetValueOrDefault(name) + elapsed;
        }
    }
    public sealed class Frame : IDisposable
    {
        private readonly Frame? parent;
        private readonly string kind;
        private readonly long clientTime, started = Stopwatch.GetTimestamp();
        private readonly long allocated = GC.GetAllocatedBytesForCurrentThread();
        private readonly History history;
        internal History History => history;
        public bool Detailed {get;}
        internal Dictionary<(string, object), object?[]> Now = [];
        internal Dictionary<string, long> Counts = [], Ticks = [];
        internal long ObserverTicks;
        internal object? Metadata;
        internal Frame(History history, Frame? parent, string kind, long time)
        {
            this.history = history; this.parent = parent; this.kind = kind; clientTime = time;
            // Four consecutive frames per sixty. The other frames expose observation bias.
            Detailed=history.Number%60<4;
        }
        public void Dispose()
        {
            long observation = Stopwatch.GetTimestamp();
            if(Detailed) foreach (var key in history.Previous.Keys)
                if (!Now.ContainsKey(key)) Add(key.Item1 + ".notObservedThisFrame");
            if(Detailed)
            {
                var camera=Now.Keys.Where(k=>k.Item1=="collision.camera.visited").Select(k=>k.Item2).ToHashSet();
                var dazzle=Now.Keys.Where(k=>k.Item1=="collision.dazzle.visited").Select(k=>k.Item2).ToHashSet();
                Add("collision.visitedBothQueries",camera.Count(dazzle.Contains));
                Add("collision.cameraOnly",camera.Count(k=>!dazzle.Contains(k)));
                Add("collision.dazzleOnly",dazzle.Count(k=>!camera.Contains(k)));
                foreach(var pair in Now.Where(p=>p.Key.Item1=="roots.prePackets.structure"))
                {
                    if(!history.Previous.TryGetValue(pair.Key,out var old) || !old.AsSpan().SequenceEqual(pair.Value))continue;
                    Add("roots.jointStructureStable");
                    foreach(var suffix in new[]{"pose","selection","wrapper"})
                    {
                        var key=("roots.prePackets."+suffix,pair.Key.Item2);
                        if(Now.TryGetValue(key,out var next) && history.Previous.TryGetValue(key,out var last) && !last.AsSpan().SequenceEqual(next))
                            Add("roots.structureStableButChanged."+suffix);
                    }
                }
            }
            history.Previous = Now; // Retain only one frame of diagnostic comparison state.
            long number = ++history.Number;
            var item = new { Version = 1, Utc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
                View = history.Id, Kind = kind, Frame = number, ClientTime = clientTime, Detailed, Metadata,
                WallMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency,
                ObserverMs = (ObserverTicks + Stopwatch.GetTimestamp() - observation) * 1000d / Stopwatch.Frequency,
                AllocatedBytesIncludingObserver = GC.GetAllocatedBytesForCurrentThread() - allocated,
                DroppedRecords = Interlocked.Read(ref dropped), Counts,
                ScopeMs = Ticks.ToDictionary(x => x.Key, x => x.Value * 1000d / Stopwatch.Frequency) };
            if (!Queue.Writer.TryWrite(item)) Interlocked.Increment(ref dropped);
            current = parent;
        }
    }
}
