using System.Numerics;
using EnbNext.Presentation;
using X87 = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>006FEC40: transform the stored local sphere center using the physical getter.</summary>
public static class EnbRetailCameraTargetCenter
{
    public static Vector3 Evaluate(IEnbRenderTransform transform, Vector3 center)
    {
        var matrix = transform.ProviderTransform ?? EnbRetailCameraQuaternionBasis.Create(transform.Orientation);
        if (transform.ProviderTransform is null) matrix.Translation = transform.Position;
        return Evaluate(matrix, center);
    }

    public static Vector3 Evaluate(Matrix4x4 matrix, Vector3 center)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        X87 x = center.X, y = center.Y, z = center.Z;
        // Native order differs for the first row. Do not use SIMD reassociation
        // or normalize/repair the provider's matrix before this getter.
        return new(
            (float)(((X87)matrix.M21 * y + (X87)matrix.M31 * z) + x * (X87)matrix.M11 + (X87)matrix.M41),
            (float)(((X87)matrix.M22 * y + (X87)matrix.M12 * x) + (X87)matrix.M32 * z + (X87)matrix.M42),
            (float)(((X87)matrix.M23 * y + (X87)matrix.M13 * x) + (X87)matrix.M33 * z + (X87)matrix.M43));
    }
}
