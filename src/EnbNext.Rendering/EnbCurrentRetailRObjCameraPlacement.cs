using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>006EF297..006EF38D: front displacement with00447C40's
/// reciprocal-square-root and the caller's x87 operation/spill order.</summary>
public static class EnbCurrentRetailRObjCameraPlacement
{
    public static Vector3 MoveInFront(Vector3 position, Vector3 camera, float radius,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision, true);
        R dx = (R)camera.X - position.X, dy = (R)camera.Y - position.Y, dz = (R)camera.Z - position.Z;
        float x = (float)dx, y = (float)dy, z = (float)dz;
        R square = dx * dx + (dy * dy + dz * dz);
        if (square > (R)0 || square < (R)0)
        {
            R inverse = EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
            x = (float)((R)x * inverse);
            y = (float)((R)y * inverse);
            z = (float)((R)z * inverse);
        }
        // X and Y products spill before addition; Z stays in an x87 register.
        float rx = (float)((R)x * radius), ry = (float)((R)y * radius);
        return new((float)((R)rx + position.X), (float)((R)ry + position.Y),
            (float)((R)z * radius + position.Z));
    }
}
