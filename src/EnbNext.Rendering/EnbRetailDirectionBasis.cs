using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Basis whose X axis is the (normalized) direction, Y the horizontal side (-dy, dx, 0)
/// and Z their cross product, placed at the origin. A vertical direction leaves the side axis
/// zero, as retail does.</summary>
public static class EnbRetailDirectionBasis
{
    public static Matrix4x4 Create(Vector3 direction, Vector3 origin)
    {
        float x = direction.X, y = direction.Y, z = direction.Z;
        float xx = x * x;
        if ((y * y + z * z) + xx != 0)
        {
            float r = EnbRetailFloat.ReciprocalSquareRoot((z * z + y * y) + xx);
            x *= r; y *= r; z *= r;
        }
        float sx = 0f - y, sy = x - 0f, sz = 0f;
        float sxx = sx * sx;
        if ((sy * sy + 0f * 0f) + sxx != 0)
        {
            float r = EnbRetailFloat.ReciprocalSquareRoot((0f * 0f + sy * sy) + sxx);
            sx *= r; sy *= r; sz = r * 0f;
        }
        float ux = sz * y - sy * z, uy = z * sx - sz * x, uz = sy * x - y * sx;
        return new(x, y, z, 0, sx, sy, sz, 0, ux, uy, uz, 0, origin.X, origin.Y, origin.Z, 1);
    }
}
