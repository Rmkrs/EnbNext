using System.Globalization;
using System.Text;
using System.Text.Json;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI chat history: the panel's own copy of the chat lines with the time each arrived. It keeps up to
/// <see cref="ModernHistoryCapacity"/> lines; with Keep chat history on, it is kept per character between sessions,
/// and with Save chat logs on, each session is also written in full to a text file named after its start and the
/// character.
/// </summary>
internal sealed partial class EnbChatUi
{
    private sealed class ModernLine(EnbChatLine line, DateTime time, DateTime session, long id)
    {
        public readonly EnbChatLine Line = line;
        public readonly DateTime Time = time, Session = session;
        /// <summary>Increasing in history order; lines read from a kept history are below zero.</summary>
        public readonly long Id = id;
        public string? Plain;
        public (double Width, float Pixels, bool Times) WrapKey;
        public List<string>? Wrapped;
    }
    private sealed record ModernHistoryRow(DateTime T, DateTime S, byte Y, string X, string? P, string? C);
    internal const int ModernHistoryCapacity = 2000;
    private readonly List<ModernLine> _modernHistory = [];
    private long _modernLineId;
    private Guid _absorbStream;
    private long _absorbSequence;
    private DateTime _modernSession = DateTime.Now;
    private uint? _historyOwner;
    private bool _historyLoaded, _historyConfigured, _modernKeepHistory;
    private long _historySavedId = long.MinValue;
    private string? _localName;
    private string? _chatLogPath;
    private Guid _chatLogStream;
    private long _chatLogWrittenId;
    private bool _chatLogFailed;

    /// <summary>Modern UI: the time each message arrived, before it.</summary>
    internal bool ModernTimes { get => _modernTimes; set { if (_modernTimes == value) return; _modernTimes = value; Publish(); } }
    private bool _modernTimes;
    /// <summary>Modern UI: the chat history is kept per character between sessions (off removes the kept history).</summary>
    internal bool ModernKeepHistory
    {
        get => _modernKeepHistory;
        set
        {
            bool was = _modernKeepHistory, first = !_historyConfigured;
            _historyConfigured = true; _modernKeepHistory = value;
            if (!value && (was || first) && _historyOwner is uint character) DeleteModernHistory(character);
        }
    }
    /// <summary>Modern UI: every session's chat is written to a text file in <see cref="ModernChatLogDirectory"/>.</summary>
    internal bool ModernSaveLogs { get; set; }
    /// <summary>Where chat logs are written (a folder the player can open).</summary>
    internal string? ModernChatLogDirectory { get; set; }

    /// <summary>Probe view: the history's lines with their arrival times, oldest first.</summary>
    internal IEnumerable<(DateTime Time, DateTime Session, string Text)> ModernHistoryLines => _modernHistory.Select(l => (l.Time, l.Session, l.Line.Text));

    private string? ModernHistoryFile(uint character) => ModernTabsPath?.Invoke(character) is { } tabs ? Path.ChangeExtension(tabs, ".history.jsonl") : null;

    /// <summary>New chat lines join the history; a new character starts from its kept history (or none).</summary>
    private void ModernAbsorb(EnbSceneChatSnapshot data)
    {
        uint? owner = _session();
        if (owner != _historyOwner)
        {
            _historyOwner = owner; _modernHistory.Clear(); _historyLoaded = false; _historySavedId = long.MinValue;
            _absorbSequence = 0; _chatLogPath = null; _chatLogFailed = false;
        }
        if (owner is uint character && !_historyLoaded && _historyConfigured) { _historyLoaded = true; LoadModernHistory(character); }
        if (data.StreamId != _absorbStream)
        {
            // A new login: without a kept history the panel starts empty, as the retail chat does.
            if (_absorbStream != Guid.Empty && !_modernKeepHistory) _modernHistory.RemoveAll(l => l.Id > 0);
            _absorbStream = data.StreamId; _absorbSequence = 0; _modernSession = DateTime.Now;
        }
        var lines = data.Lines;
        if (lines.Length > 0 && lines[^1].Sequence > _absorbSequence)
        {
            int start = lines.Length;
            while (start > 0 && lines[start - 1].Sequence > _absorbSequence) start--;
            var now = DateTime.Now;
            for (int i = start; i < lines.Length; i++) _modernHistory.Add(new(lines[i], now, _modernSession, ++_modernLineId));
            _absorbSequence = lines[^1].Sequence;
            if (_modernHistory.Count > ModernHistoryCapacity + 100) _modernHistory.RemoveRange(0, _modernHistory.Count - ModernHistoryCapacity);
        }
        if (!_modernChat) return;
        if (_modernKeepHistory && _historyLoaded && owner is uint keeper) SaveModernHistory(keeper);
        if (ModernSaveLogs) WriteModernChatLog();
    }

    private void LoadModernHistory(uint character)
    {
        if (ModernHistoryFile(character) is not { } path) return;
        if (!_modernKeepHistory) { DeleteModernHistory(character); return; }
        _historySavedId = 0;
        try
        {
            if (!EnbNext.Installation.EnbPathCase.FileExists(path)) return;
            var rows = new List<ModernHistoryRow>();
            foreach (string text in File.ReadLines(EnbNext.Installation.EnbPathCase.Resolve(path)))
            {
                try { if (text.Length > 0 && JsonSerializer.Deserialize<ModernHistoryRow>(text) is { X: not null } row) rows.Add(row); }
                catch (JsonException) { } // A line cut short (the game closed while writing) is left out.
            }
            int drop = Math.Max(0, rows.Count - ModernHistoryCapacity);
            long id = -(rows.Count - drop);
            _modernHistory.InsertRange(0, rows.Skip(drop).Select(r => new ModernLine(new(0, r.Y, r.X, r.P, r.C), r.T, r.S, id++)));
            // The file keeps growing while playing; it is cut back to the kept lines when read.
            if (rows.Count > ModernHistoryCapacity + 500) WriteModernHistory(path, rows.Skip(drop));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "Could not read the chat history: " + ex.Message; }
    }
    private static void WriteModernHistory(string path, IEnumerable<ModernHistoryRow> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows) text.Append(JsonSerializer.Serialize(row)).Append('\n');
        File.WriteAllText(path + ".tmp", text.ToString(), new UTF8Encoding(false));
        File.Move(path + ".tmp", EnbNext.Installation.EnbPathCase.Resolve(path), true);
    }
    private static ModernHistoryRow HistoryRow(ModernLine l) => new(l.Time, l.Session, l.Line.Type, l.Line.Text, l.Line.Peer, l.Line.Channel);
    private void SaveModernHistory(uint character)
    {
        if (_modernHistory.Count == 0 || _modernHistory[^1].Id <= _historySavedId || ModernHistoryFile(character) is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (_historySavedId == long.MinValue) WriteModernHistory(path, _modernHistory.Select(HistoryRow));
            else
            {
                var text = new StringBuilder();
                for (int i = _modernHistory.Count - 1; i >= 0 && _modernHistory[i].Id > _historySavedId; i--) text.Insert(0, JsonSerializer.Serialize(HistoryRow(_modernHistory[i])) + "\n");
                File.AppendAllText(EnbNext.Installation.EnbPathCase.Resolve(path), text.ToString(), new UTF8Encoding(false));
            }
            _historySavedId = _modernHistory[^1].Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "Could not save the chat history: " + ex.Message; _historySavedId = _modernHistory[^1].Id; }
    }
    private void DeleteModernHistory(uint character)
    {
        _historySavedId = long.MinValue;
        if (ModernHistoryFile(character) is not { } path) return;
        try { if (EnbNext.Installation.EnbPathCase.FileExists(path)) File.Delete(EnbNext.Installation.EnbPathCase.Resolve(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "Could not remove the chat history: " + ex.Message; }
    }

    private static readonly string[] ModernKindNames = ["Say", "OOC", "Local", "Broadcast", "Private", "Group", "Guild", "Combat", "Loot", "Notice", "Tip", "Error", "Warning", "Echo"];
    /// <summary>The session's lines (sent and received, every type) appended to its chat log, one line each.</summary>
    private void WriteModernChatLog()
    {
        if (_chatLogFailed || ModernChatLogDirectory is not { } directory || _localName is not { Length: > 0 } name) return;
        if (_chatLogPath is null || _chatLogStream != _absorbStream)
        {
            string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            _chatLogStream = _absorbStream; _chatLogWrittenId = 0;
            _chatLogPath = Path.Combine(directory, _modernSession.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + " " + safe + ".txt");
        }
        var text = new StringBuilder();
        long written = _chatLogWrittenId;
        int first = _modernHistory.Count;
        while (first > 0 && _modernHistory[first - 1].Id > written && _modernHistory[first - 1].Session == _modernSession) first--;
        if (first == _modernHistory.Count) return;
        try
        {
            Directory.CreateDirectory(directory);
            if (!File.Exists(_chatLogPath))
                text.Append(CultureInfo.InvariantCulture, $"Earth & Beyond chat log: {name}, {_modernSession:yyyy-MM-dd HH:mm:ss}\n\n");
            for (int i = first; i < _modernHistory.Count; i++)
            {
                var l = _modernHistory[i];
                string kind = l.Line.Channel ?? ModernKindNames[(int)ModernClassify(l.Line)];
                text.Append(CultureInfo.InvariantCulture, $"[{l.Time:HH:mm:ss}] [{kind}] {ModernPlain(l.Line.Text)}\n");
            }
            File.AppendAllText(_chatLogPath, text.ToString(), new UTF8Encoding(false));
            _chatLogWrittenId = _modernHistory[^1].Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _chatLogFailed = true; Error = "Could not save the chat log: " + ex.Message; }
    }
}
