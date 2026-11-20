using EnbNext.Engine;
using System.Text;
using System.Text.Json;

namespace EnbNext.App;

// Incremental transcript survives navigation and failed login attempts. The latest
// presentation receipt is replaced separately, so frame sampling cannot flood it.
internal sealed class SessionDiagnosticLog : IDisposable
{
    private readonly object _sync = new();
    private readonly StringBuilder _pending = new();
    private readonly Timer _timer;
    private string? _presentation;
    private bool _disposed;
    private SpacePerformanceScene? _performanceScene;
    private DateTimeOffset _lastSceneWrite;
    private int _sceneSlot;
    private static readonly JsonSerializerOptions SceneOptions=new(){IncludeFields=true,
        NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals};
    private static readonly Lazy<JsonSerializerOptions> DiagnosticSceneOptions=new(()=>
    {
        var resolver=new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info=>
        {
            if(info.Type!=typeof(SpacePerformanceScene))return;
            var property=info.CreateJsonPropertyInfo(typeof(object),"DeveloperDiagnostics");
            property.Get=_=>DeveloperRenderDiagnostics.Receipt(); info.Properties.Add(property);
        });
        return new JsonSerializerOptions(SceneOptions) { TypeInfoResolver=resolver };
    });
    /// <summary>The transcript file, or null when diagnostic files are not written (no --logs).</summary>
    public string? Path { get; }
    public string? Error { get; private set; }

    public SessionDiagnosticLog(string? directory)
    {
        Path = directory is null ? null : System.IO.Path.Combine(directory, $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.log");
        _timer = new(_ => Flush(), null, 1000, 1000);
    }
    public void Append(string line) { lock (_sync) if (!_disposed) _pending.Append(line); }
    public void Presentation(string text) { lock (_sync) if (!_disposed) _presentation = text; }
    private DateTimeOffset _lastSceneCapture;
    public void PerformanceScene(SpacePerformanceState? scene)
    {
        lock(_sync)
        {
            if(_disposed)return;
            if(scene is null){_performanceScene=null;return;}
            if(scene.CapturedUtc-_lastSceneCapture<TimeSpan.FromSeconds(10))return;
            _performanceScene=scene.Capture();_lastSceneCapture=scene.CapturedUtc;
        }
    }
    public void Flush()
    {
        // Only the timer writes scene snapshots. UI-triggered transcript flushes
        // must not serialize the scene or wait for its disk write.
        SpacePerformanceScene? scene=null;
        int slot=0;
        lock (_sync)
        {
            if (_disposed || (_pending.Length == 0 && _presentation is null && _performanceScene is null)) return;
            // Without --logs nothing is kept: the transcript is dropped, not stored.
            if (Path is null) { _pending.Clear(); _presentation = null; _performanceScene = null; return; }
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (_pending.Length != 0) { File.AppendAllText(Path, _pending.ToString(), Encoding.UTF8); _pending.Clear(); }
                if (_presentation is {} receipt)
                {
                    File.WriteAllText(Path + ".presentation.tmp", receipt, Encoding.UTF8);
                    File.Move(Path + ".presentation.tmp", Path + ".presentation.txt", true);
                    // Docking replaces the full receipt. Retain compact frame
                    // timing separately so a later station/login cannot erase
                    // the space hitch which prompted the investigation.
                    var timing=receipt.Split('\n').Where(line=>
                        line.Contains("DiagnosticSession",StringComparison.Ordinal) ||
                        line.Contains("frame loop:",StringComparison.Ordinal) ||
                        line.Contains("ui scene pump",StringComparison.Ordinal) ||
                        line.Contains("render profile:",StringComparison.Ordinal) ||
                        line.Contains("slow frames (",StringComparison.Ordinal)).ToArray();
                    if(timing.Length!=0)
                    {
                        string history=Path+".frames.txt";
                        if(File.Exists(history) && new FileInfo(history).Length>=8*1024*1024)
                            File.Move(history,history+".previous",true);
                        File.AppendAllText(history,$"{DateTimeOffset.UtcNow:O}\n{string.Join('\n',timing)}\n",Encoding.UTF8);
                    }
                    _presentation = null;
                }
                Error = null;
                if(Thread.CurrentThread.IsThreadPoolThread && _performanceScene is {} pending &&
                    pending.CapturedUtc-_lastSceneWrite>=TimeSpan.FromSeconds(10))
                {
                    scene=pending;_lastSceneWrite=pending.CapturedUtc;slot=_sceneSlot++%12;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
                if (_pending.Length > 1024 * 1024) _pending.Remove(0, _pending.Length - 1024 * 1024);
            }
        }
        if(scene is not null)
        {
            try
            {
                string destination=Path+$".scene-{slot:D2}.json";
                // Captures can be tens of MiB. Serialize directly to the file
                // instead of allocating a complete UTF-16 JSON string and a
                // second UTF-8 buffer, which stalls the renderer during GC.
                using (var stream = File.Create(destination+".tmp"))
                {
                    JsonSerializer.Serialize(stream,scene,DeveloperRenderDiagnostics.SessionUsed?DiagnosticSceneOptions.Value:SceneOptions);
                }
                File.Move(destination+".tmp",destination,true);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
            { lock(_sync) Error=ex.Message; }
        }
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; Flush(); _disposed = true; }
        _timer.Dispose();
    }
}
