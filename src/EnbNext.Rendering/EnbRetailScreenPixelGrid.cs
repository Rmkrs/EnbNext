using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>491550 logical 640x480 coordinates, native A26D75 floor.</summary>
public static class EnbRetailScreenPixelGrid
{
    public static Vector2 Snap(Vector2 value,int width,int height)
    {
        float sx=width*.0015625f,sy=height*.0020833334f;
        return new(MathF.Floor(value.X*sx)/sx,MathF.Floor(value.Y*sy)/sy);
    }
}
