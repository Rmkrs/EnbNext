namespace EnbNext.Presentation;

/// <summary>0065CA10/0065CCD0: ten circular sent-input slots, separate from
/// received chat history. Opening or cancelling the editor preserves recall.</summary>
public sealed class EnbChatInputHistory
{
    public const int InputLimit = 255;
    private readonly string?[] _slots = new string?[10];
    private int _write, _recall;
    private bool _recalled;

    public void Clear() { Array.Clear(_slots); _write = _recall = 0; _recalled = false; }

    public void Submit(string text)
    {
        if (text.Length != 0 && !string.Equals(text, _slots[_recall], StringComparison.Ordinal))
        {
            _slots[_write] = text;
            _recall = _write;
            _write = (_write + 1) % _slots.Length;
        }
        _recalled = false;
    }

    public string? Recall(bool previous)
    {
        if (_slots[_recall] is null) return null;
        if (!previous || _recalled)
            do { _recall = (_recall + (previous ? 9 : 1)) % _slots.Length; }
            while (_slots[_recall] is null);
        _recalled = true;
        return _slots[_recall];
    }
}
