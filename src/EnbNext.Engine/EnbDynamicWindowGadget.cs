using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>006689C0/00668C80 dynamic-window gadget color and inclusive hit query.
/// Capture/selection remain in the shared gadget manager.</summary>
internal struct EnbDynamicWindowGadget(bool hoverBrightness)
{
    public float Emissive { get; private set; } = hoverBrightness ? .6f : 1;
    public void Reset() => Emissive = hoverBrightness ? .6f : 1;

    public bool Query(EnbUiRect bounds, float normalizedX, float normalizedY)
    {
        if (hoverBrightness) Emissive = .6f;
        // Native retains X on the x87 stack, but stores Y to float at00668D1B.
        double x = (double)normalizedX * 640 - 320;
        float y = (float)(240 - (double)normalizedY * 480);
        float cx = (float)(bounds.Center.X - 320), cy = (float)(240 - bounds.Center.Y);
        float hx = (float)(bounds.Width / 2), hy = (float)(bounds.Height / 2);
        bool hit = x >= (double)cx - hx && x <= (double)cx + hx &&
            y >= (double)cy - hy && y <= (double)cy + hy;
        if (hit && hoverBrightness) Emissive = 1;
        return hit;
    }

    public void SelectionChanged(bool before, bool after)
    {
        if (before != after) Emissive = after ? .35f : 1;
    }
}
