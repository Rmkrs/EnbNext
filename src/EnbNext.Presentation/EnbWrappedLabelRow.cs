namespace EnbNext.Presentation;

/// <summary>The delayed wrapped-row clock and UP/REVERT path of 6CF930/6D60C0/6CA0C0.</summary>
public sealed class EnbWrappedLabelRow(int delayMilliseconds)
{
    private bool _started;
    private int _clock, _delay = delayMilliseconds;
    private float _offset;
    public float Offset => _offset;
    public int Clock => _clock;
    public bool Ellipsis { get; private set; }

    // 6D64C0 samples the position before applying this frame's effects.
    public (bool Visible, float Offset) Advance(int delta, bool hovered, float limit, float speed)
    {
        int previous = _clock;
        int elapsed = 0;
        if (!_started) _started = true;
        else
        {
            _clock += delta;
            if (_clock < _delay) return (false, _offset);
            elapsed = _clock - previous;
            if (_delay > 0) { _clock -= _delay; _delay = 0; }
        }
        float rendered = _offset;
        _offset = hovered ? Math.Min(limit, _offset + elapsed * speed * .02f) : 0;
        return (true, rendered);
    }

    // 6D66xx retains the suffix until a non-final row moves above the
    // bottom glyph-height band. Equality and clipped-out rows retain state.
    public bool UpdateEllipsis(float y, float bottom, int height, bool last)
    {
        if (!last && y > bottom)
        {
            if (y < bottom + height) Ellipsis = true;
            else if (y > bottom + height) Ellipsis = false;
        }
        return Ellipsis;
    }
}
