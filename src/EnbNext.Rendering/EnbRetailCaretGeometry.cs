using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>00685AB0's rectangle in the retail 640x480, Y-up UI coordinates.</summary>
public static class EnbRetailCaretGeometry
{
    public static Vector4 Resolve(Vector2 anchor, float textWidth, float prefixWidth,
        float characterAdvance, float fontHeight, bool centered, bool verticallyCentered, bool insert)
    {
        float x = anchor.X - (centered ? (int)textWidth / 2 : 0) + (int)prefixWidth;
        float y = anchor.Y + (verticallyCentered ? fontHeight * .5f : 0);
        if (insert) y -= fontHeight - 2;
        return new(x, y, Math.Max(2, (int)characterAdvance + 1), insert ? 2 : fontHeight - 1);
    }
}
