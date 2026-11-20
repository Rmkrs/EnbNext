using System.Text;
using System.Text.RegularExpressions;

namespace EnbNext.Engine;

// Shared by session ingress and UI sampling. Stores reported problems and
// their bounded gameplay failure receipts, not the general login transcript.
internal sealed class LiveIssueLog
{
    private readonly object _sync=new();
    private readonly object _saveSync=new();
    private readonly string? _directory;
    private readonly Timer? _saveTimer;
    private bool _dirty, _saveScheduled;
    private string? _reportPath, _saveError;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EnbNext","Logs");
    // Probes and linked-source tests stay in memory unless explicitly given a directory.
    public LiveIssueLog(string? directory=null)
    {
        _directory=directory;
        if(directory is not null)_saveTimer=new Timer(_=>SavePending(),null,Timeout.Infinite,Timeout.Infinite);
    }
    public string? ReportPath {get {lock(_sync)return _reportPath;}}
    public string? SaveError {get {lock(_sync)return _saveError;}}
    private readonly Dictionary<string,Entry> _entries=[];
    private int _omitted;
    private sealed record Entry(DateTimeOffset First,DateTimeOffset Last,string Source,string Detail,int Count);
    public int Count {get {lock(_sync)return _entries.Count;}}
    public void Clear()
    {
        lock(_saveSync)
        {
            SavePending(force:true);
            lock(_sync){_entries.Clear();_omitted=0;_reportPath=null;_dirty=false;}
        }
    }
    public void Flush()=>SavePending(force:true);
    private void QueueSave()
    {
        if(_saveTimer is null)return;
        _reportPath??=Path.Combine(_directory!,$"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.issues.txt");
        _dirty=true;
        if(!_saveScheduled){_saveScheduled=true;_saveTimer.Change(500,Timeout.Infinite);}
    }
    private void SavePending(bool force=false)
    {
        lock(_saveSync)
        {
            string report,path;
            lock(_sync)
            {
                _saveTimer?.Change(Timeout.Infinite,Timeout.Infinite);
                _saveScheduled=false;
                if(_reportPath is null || (!_dirty && !force))return;
                path=_reportPath;report=Describe(includeSaveError:false);_dirty=false;
            }
            try
            {
                Directory.CreateDirectory(_directory!);
                // Replace atomically so an interrupted save leaves the previous complete report.
                string temporary=path+".tmp";
                File.WriteAllText(temporary,report,Encoding.UTF8);
                File.Move(temporary,EnbNext.Installation.EnbPathCase.Resolve(path),overwrite:true);
                lock(_sync)_saveError=null;
                foreach(var old in new DirectoryInfo(EnbNext.Installation.EnbPathCase.Resolve(_directory!)).EnumerateFiles("session-*.issues.txt")
                    .OrderByDescending(f=>f.LastWriteTimeUtc).Skip(30))
                    if(!string.Equals(old.FullName,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))
                        try{old.Delete();}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                lock(_sync)_saveError=ex.Message;
            }
        }
    }
    public static bool IsProblem(string text) =>
        // These transport-owned retry reports describe ordinary connectivity loss.
        // Keep their full details in the transcript, not the defect history.
        !text.StartsWith("login/control fatal: Net7IncompatibleVersionException: ", StringComparison.Ordinal) &&
        !text.StartsWith("bootstrap send failed: SocketException: ", StringComparison.Ordinal) &&
        !text.StartsWith("bootstrap send failed: IOException: ", StringComparison.Ordinal) &&
        !text.StartsWith("master/control UDP receive failed; retrying: ", StringComparison.Ordinal) &&
        Regex.IsMatch(text,
        @"\b(unsupported|unexpected|unhandled|malformed|mismatch|exception|rejected|error)\b|\b(?:render|load|decode|asset|protocol) failure\b|\bfailed\b",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex CounterGroups = new(
        @"\b[a-z][a-z0-9-]*(?:/[a-z][a-z0-9-]*)+ \d+(?:/\d+)+\b",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static readonly Regex PresentationProblems = new(
        @"render failure|requires missing|not implemented|not the bounded|sky renderer:.*unavailable|Interior unavailable|\brejected\b(?!\s+0\b)|\b(?:fail|blocked|unresolved|recovered) [1-9]\d*\b",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    public static bool IsPresentationProblem(string text)
    {ArgumentNullException.ThrowIfNull(text);return IsPresentationProblem(text.AsSpan());}
    public static bool IsPresentationProblem(ReadOnlySpan<char> text)
    {
        // The old normalized string was a temporary publication to one consumer.
        // Preserve its exact contents in reusable scratch, including malformed
        // label/value groups which must remain unchanged.
        var matches=CounterGroups.EnumerateMatches(text);
        if(!matches.MoveNext())return PresentationProblems.IsMatch(text);
        char[]? rented=null;
        int capacity=checked(text.Length*2);
        Span<char> normalized=capacity<=1024?stackalloc char[capacity]:
            (rented=System.Buffers.ArrayPool<char>.Shared.Rent(capacity));
        try
        {
            int consumed=0,written=0;
            do
            {
                var match=matches.Current;
                text[consumed..match.Index].CopyTo(normalized[written..]);written+=match.Index-consumed;
                var group=text.Slice(match.Index,match.Length);int space=group.IndexOf(' ');
                var labels=group[..space];var values=group[(space+1)..];
                int labelCount=1,valueCount=1;
                foreach(char c in labels)if(c=='/')labelCount++;
                foreach(char c in values)if(c=='/')valueCount++;
                if(labelCount!=valueCount){group.CopyTo(normalized[written..]);written+=group.Length;}
                else
                {
                    for(int i=0;i<labelCount;i++)
                    {
                        if(i!=0){normalized[written++]=';';normalized[written++]=' ';}
                        int a=labels.IndexOf('/');if(a<0)a=labels.Length;
                        int v=values.IndexOf('/');if(v<0)v=values.Length;
                        labels[..a].CopyTo(normalized[written..]);written+=a;normalized[written++]=' ';
                        values[..v].CopyTo(normalized[written..]);written+=v;
                        labels=a==labels.Length?[]:labels[(a+1)..];values=v==values.Length?[]:values[(v+1)..];
                    }
                }
                consumed=match.Index+match.Length;
            }while(matches.MoveNext());
            text[consumed..].CopyTo(normalized[written..]);written+=text.Length-consumed;
            return PresentationProblems.IsMatch(normalized[..written]);
        }
        finally{if(rented is not null)System.Buffers.ArrayPool<char>.Shared.Return(rented);}
    }
    public void ObserveDiagnostic(string message)
    {
        if(IsProblem(message))Report("Session",Regex.Replace(message,@"\b\d{5,}\b","#"),message,true);
    }
    public void Report(string source,string key,string detail,bool occurrence=false)
    {
        if(string.IsNullOrWhiteSpace(detail))return;
        lock(_sync)
        {
            detail=detail.Length>8000?detail[..8000]+" [truncated]":detail;
            key=key.Length>256?key[..256]:key;
            key=source+"/"+key;
            var now=DateTimeOffset.Now;
            if(_entries.TryGetValue(key,out var old))
            {
                _entries[key]=old with {Last=now,Detail=detail,Count=old.Count+(occurrence?1:0)};
                if(occurrence || old.Detail!=detail || _saveError is not null)QueueSave();
                return;
            }
            if(_entries.Count==100){_entries.Remove(_entries.First().Key);_omitted++;}
            _entries[key]=new(now,now,source,detail,1);
            QueueSave();
        }
    }
    public string Describe(bool includeSaveError=true)
    {
        lock(_sync)
        {
            var text=new StringBuilder("ENBNEXT · SESSION ISSUES\nRecorded history; entries may no longer be active.\n");
            text.AppendLine($"Snapshot: {DateTimeOffset.Now:O}");
            text.AppendLine($"Build: {typeof(LiveIssueLog).Assembly.GetName().Version}; module {typeof(LiveIssueLog).Assembly.ManifestModule.ModuleVersionId}");
            if(_reportPath is not null)text.AppendLine($"Automatic report: {_reportPath}");
            if(includeSaveError && _saveError is not null)text.AppendLine($"Automatic save failed: {_saveError}");
            if(_omitted>0)text.AppendLine($"{_omitted} older entries omitted (100-entry limit).");
            foreach(var e in _entries.Values)
                text.AppendLine($"\n[{e.First:HH:mm:ss}–{e.Last:HH:mm:ss}] {e.Source} · {e.Count} report(s)\n{e.Detail}");
            return text.ToString();
        }
    }
}
