using EnbNext.Assets;
namespace EnbNext.Rendering;

/// <summary>Retained per-pane message groups, independent of the shared replay history.</summary>
public sealed class EnbRetailChatHistory(int capacity = 100)
{
    public sealed record Row(string Text, byte Type, long Sequence)
    {
        public EnbRgb? Color { get; init; }
        public bool IsContinuation { get; init; }
        public bool HasHistoryMarker { get; init; }
        public string Format(string continuationPrefix) => (HasHistoryMarker?"^":" ")+
            (IsContinuation?continuationPrefix:"")+Text.TrimStart(' ');
    }
    private sealed record Message(string Text, byte Type, long Sequence, string[] Rows, EnbRgb? Color);
    private readonly List<Message> _messages = [];
    private readonly List<Row> _rows = [];
    public IReadOnlyList<Row> Rows => _rows;

    public void Clear() { _messages.Clear(); _rows.Clear(); }

    public int Append(string text, byte type, long sequence, int visible, Func<string, string[]> wrap, EnbRgb? color = null)
    {
        // 006821F0: empty input does not insert, and eviction removes whole
        // messages through their end flag, using max(capacity, visible rows).
        if (text.Length == 0) return 0;
        string[] rows = wrap(text);
        if (rows.Length == 0) return 0;
        _messages.Add(new(text, type, sequence, rows, color));
        for (int i=0;i<rows.Length;i++)
        {
            _rows.Add(new(rows[i], type, sequence) { IsContinuation=i>0, Color=color });
            // 006821F0 marks the row just beyond the current window for
            // every appended row, before whole-message eviction.
            int marker = _rows.Count - visible - 1;
            if (marker >= 0) _rows[marker] = _rows[marker] with { HasHistoryMarker=true };
        }
        int limit = Math.Max(capacity, visible);
        while (_rows.Count > limit && _messages.Count > 0)
        {
            _rows.RemoveRange(0, _messages[0].Rows.Length);
            _messages.RemoveAt(0);
        }
        return rows.Length;
    }

    public void Rewrap(Func<string, string[]> wrap)
    {
        // 006817E0/006818C0: reflow is not an insertion and does not evict.
        // Keep original text: retail's 00681DB0 concatenation loses spaces at
        // old wrap boundaries. That string corruption is not a required behavior.
        // A different viewport/font metric can still produce identical rows.
        // Such a refresh must not turn the visible messages into history.
        var wrapped = new string[_messages.Count][];
        bool changed = false;
        for (int i = 0; i < _messages.Count; i++)
        {
            wrapped[i] = wrap(_messages[i].Text);
            changed |= !_messages[i].Rows.AsSpan().SequenceEqual(wrapped[i]);
        }
        if (!changed) return;
        _rows.Clear();
        for (int i = 0; i < _messages.Count; i++)
        {
            var message = _messages[i];
            var rows = wrapped[i];
            _messages[i] = message with { Rows = rows };
            for (int row=0;row<rows.Length;row++)
                _rows.Add(new(rows[row], message.Type, message.Sequence) { IsContinuation=row>0, HasHistoryMarker=true, Color=message.Color });
        }
    }
}
