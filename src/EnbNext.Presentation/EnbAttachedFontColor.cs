using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>Model-attached FA_CYCLE color state (006CD180/006CA0C0 case 0x12).
/// Each label owns its hover clock; gadget selection is a separate input.</summary>
public struct EnbAttachedFontColor
{
    private bool hovered;
    private long entered;
    public EnbRgb Update(EnbFontEffectPalette palette, IReadOnlyDictionary<string, string> properties,
        bool inside, bool selected, long milliseconds)
    {
        if (inside && !hovered) entered = milliseconds;
        hovered = inside;
        return palette.ResolveSelected(properties, inside, selected, milliseconds - entered);
    }

    public static bool Contains(EnbUiRect clip, int width, int height, float x, float y)
    {
        // FSTP stores the scale and centered cursor as binary32; bound products
        // stay extended through FCOMP. Integer display halves matter at odd sizes.
        float sx = (float)((double)width * (1f / 640));
        float sy = (float)((double)height * (1f / 480));
        float px = (float)((double)width * x - width / 2);
        float py = (float)(height / 2 - (double)height * y);
        return (clip.X - 320) * sx < px && px < (clip.Right - 320) * sx
            && (240 - clip.Bottom) * sy < py && py < (240 - clip.Y) * sy;
    }
}
