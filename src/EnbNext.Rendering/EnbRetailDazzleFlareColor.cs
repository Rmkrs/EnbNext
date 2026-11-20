using System.Numerics;

namespace EnbNext.Rendering;

public static class EnbRetailDazzleFlareColor
{
    // 0096A4C0..0096A596: multiply authored RGB, cap above one, truncate
    // to packed D3DCOLOR bytes before the texture/blend consumer.
    public static Vector3 Evaluate(Vector3 color, float intensity)
    {
        color *= intensity;
        uint r=unchecked((uint)(int)(MathF.Min(color.X,1)*255));
        uint g=unchecked((uint)(int)(MathF.Min(color.Y,1)*255));
        uint b=unchecked((uint)(int)(MathF.Min(color.Z,1)*255));
        uint packed=0xff000000u | (r<<16) | (g<<8) | b;
        return new((packed>>16&255)/255f,(packed>>8&255)/255f,(packed&255)/255f);
    }
}
