namespace EnbNext.Rendering;

/// <summary>Ordinary tooltip delay: client 006AB930 / N7CM delay observation.</summary>
public sealed class EnbCurrentRetailTooltipDelay
{
    private int? _key;
    private long _enteredAt;
    private bool _ready, _immediate, _forced;

    /// <summary>VK_MENU: force the current hover and every new hover while held.</summary>
    public bool Immediate
    {
        get => _immediate;
        set { _immediate = value; if (value && _key.HasValue) _ready = _forced = true; }
    }

    public static int Milliseconds(float percent)
        => (int)(Math.Clamp(float.IsFinite(percent) ? percent : 50f, 0f, 100f) * 1200f * 0.01f);

    public bool IsReady(int key, long now, int delayMilliseconds, bool captured = false)
    {
        if (_key != key)
        {
            _key = key;
            _enteredAt = now;
            _ready = false;
            _forced = _immediate;
        }
        // 006AB5B0 postpones an ordinary tooltip while its gadget is captured;
        // the explicit force flag survives Alt release until the gadget changes.
        if (captured && !_forced) { _enteredAt = now; _ready = false; }
        return _ready |= _forced || now - _enteredAt >= delayMilliseconds;
    }

    public void Reset() { _key = null; _ready = _forced = false; }
}
