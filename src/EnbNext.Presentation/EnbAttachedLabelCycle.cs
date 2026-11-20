using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>006CA0C0 case18: integer channel midpoints and signed half-ranges.</summary>
public static class EnbAttachedLabelCycle
{
    public static EnbRgb Color(EnbRgb current, EnbRgb alternate, long elapsedMilliseconds, float speed)
    {
        double wave = Math.Sin(Math.Max(0, elapsedMilliseconds) * (double)speed * BitConverter.Int32BitsToSingle(0x3bcde277));
        float Channel(float a, float b)
        {
            int first = (int)MathF.Round(a * 255), second = (int)MathF.Round(b * 255);
            return (float)(((second - first) / 2 * wave + (second + first) / 2) * BitConverter.Int32BitsToSingle(0x3b80806e));
        }
        return new(Channel(current.R, alternate.R), Channel(current.G, alternate.G), Channel(current.B, alternate.B));
    }
}
