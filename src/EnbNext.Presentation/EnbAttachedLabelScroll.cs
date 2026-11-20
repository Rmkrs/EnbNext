namespace EnbNext.Presentation;

/// <summary>Retail 006CA0C0 SCROLL/LEFT for a mesh-attached, unwrapped label.</summary>
public sealed class EnbAttachedLabelScroll
{
    private string? _text;
    private long _tick;
    private float _travel;
    public float Offset { get; private set; }

    public void Pause(long tick) => _tick = tick;

    public void Clear()
    {
        _text = null;
        _travel = Offset = 0;
    }

    public float Update(string text, long tick, float textWidth, float meshHalfWidth, float speed)
    {
        if (!string.Equals(_text, text, StringComparison.Ordinal))
        {
            // SetTextWidgetText (006CDDBC/006CDDF9) resets both offsets.
            _text = text;
            _tick = tick;
            _travel = Offset = 0;
        }
        long elapsed = Math.Max(0, tick - _tick);
        _tick = tick;
        int limit = (int)(meshHalfWidth * 1.9f);
        if (textWidth > limit)
        {
            float distance = elapsed * speed * .02f;
            Offset -= distance;
            _travel += distance;
            // One wrap per update, strict >, retaining the offset's overshoot.
            // 006CAED6..006CAEF4 resets the accumulator to -limit.
            if (_travel > textWidth)
            {
                Offset += textWidth + limit;
                _travel = -limit;
            }
        }
        return Offset;
    }
}
