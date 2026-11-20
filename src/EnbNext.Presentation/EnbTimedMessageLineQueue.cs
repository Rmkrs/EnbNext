using EnbNext.Net7.Presentation;

namespace EnbNext.Presentation;

/// <summary>Retail 005A7690 / 005A69B0 timed message ordering. Smaller priorities
/// run first, equals remain FIFO, and interruption preserves remaining time.</summary>
public sealed class EnbTimedMessageLineQueue
{
    private sealed class Entry(Net7MessageLineUpdate value, uint priority)
    {
        public Net7MessageLineUpdate Value { get; } = value;
        public uint Priority { get; } = priority;
        public long Remaining { get; set; } = value.TimeMilliseconds;
    }
    private readonly List<Entry> _pending = [];
    private readonly List<Entry> _history = [];
    private int _historyIndex = -1;
    private bool _replaying;
    private Entry? _current;
    private long? _lastTime;
    public Net7MessageLineUpdate? Current => _current?.Value;
    public bool Replaying => _replaying;
    public string? DisplayText => _current is not {} current ? null : _replaying
        ? FormattableString.Invariant($"({_history.Count - _historyIndex}) {current.Value.Text}")
        : current.Value.Text;

    public void Clear()
    {
        _pending.Clear(); _history.Clear(); _historyIndex = -1;
        _current = null; _replaying = false; _lastTime = null;
    }

    // Retail 005A7DE0 archives the cleared queue; changing character instead
    // uses Clear above. History is session memory, never a private saved file.
    public void ClearPending()
    {
        _history.AddRange(_pending); _pending.Clear();
        _current = null; _replaying = false;
    }

    public void NavigateHistory(bool previous)
    {
        // 005A70B0: Previous enters at newest and walks past oldest back to
        // the queue. Next exits past newest; outside recall it skips a line.
        if (previous)
        {
            if (_historyIndex == -1)
            {
                if (_current?.Remaining != 0) _current = null;
                _historyIndex = _history.Count - 1;
            }
            else _historyIndex--;
        }
        else if (_historyIndex == -1)
        {
            if (_current is { Remaining: > 0 } active) active.Remaining = 1;
            return;
        }
        else if (++_historyIndex >= _history.Count) _historyIndex = -1;
        if (_replaying) _current = null;
        _replaying = false;
    }

    public void Enqueue(Net7MessageLineUpdate value)
    {
        if (string.IsNullOrEmpty(value.Text)) return;
        uint priority = value.Kind switch { Net7MessageLineKind.Queue => 7, Net7MessageLineKind.Push => 3, _ => value.DisplayPriority };
        int index = _pending.FindIndex(e => e.Priority > priority);
        _pending.Insert(index < 0 ? _pending.Count : index, new(value, priority));
    }

    public bool Advance(long now, bool effectsComplete = false)
    {
        var before = _current;
        long elapsed = _lastTime is {} last ? Math.Max(0, now - last) : 0;
        _lastTime = now;
        if (_current is {} active)
        {
            bool first = _pending.Count > 0 && ReferenceEquals(_pending[0], active);
            bool effectDriven = active.Remaining == 0 && !_replaying;
            // 005A69B0: an effect-driven active line holds the display even
            // when priority insertion changes the head of the pending queue.
            if (effectDriven && !effectsComplete) return false;
            if (effectDriven || elapsed >= active.Remaining)
            {
                if (_replaying) _historyIndex = -1;
                else if (first)
                {
                    if (!effectDriven) while (_history.Count >= 30) _history.RemoveAt(0);
                    _history.Add(active); _pending.RemoveAt(0);
                }
                _current = null; _replaying = false;
            }
            else if (_replaying || first) active.Remaining -= elapsed;
            else _current = null;
            // A newly inserted priority line interrupts before this frame's
            // decrement, as in 005A69B0. The displaced entry remains queued.
        }
        if (_current is null)
        {
            _current = _pending.FirstOrDefault();
            if (_historyIndex >= 0 && _current?.Remaining != 0)
            {
                _current = _history[_historyIndex];
                _current.Remaining = 5000;
                _replaying = true;
            }
        }
        return !ReferenceEquals(before, _current);
    }
}
