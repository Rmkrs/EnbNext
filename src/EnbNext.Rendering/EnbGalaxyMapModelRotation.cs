using System.Numerics;
using X87 = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Retained galaxy model transforms, transposed from retail's 3x4 layout.</summary>
public static class EnbGalaxyMapModelRotation
{
    // 007AB8B0 reads double 00B05968; 007ABC60/007A7070 read 00AF3F00.
    private static readonly double InitialAngle = BitConverter.UInt64BitsToDouble(0x3fd0c15240000000);
    private static readonly double StepAngle = BitConverter.UInt64BitsToDouble(0x3f847ae140000000);

    public static Matrix4x4 InitialSector()
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        X87 c = X87.Cosine(InitialAngle), s = X87.Sine(InitialAngle);
        return new((float)(c + (X87)0), (float)(s + (X87)0), 0, 0,
            (float)((X87)0 - s), (float)(c - (X87)0), 0, 0,
            0, 0, 1, 0, 0, 0, 0, 1);
    }

    public static Matrix4x4 Advance(Matrix4x4 basis)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        X87 c = X87.Cosine(StepAngle), s = X87.Sine(StepAngle);
        // Each native pair uses the old values, with a float store only after
        // both products and their sum/difference. Never reconstruct from angle.
        (float X, float Y) Pair(float x, float y) =>
            ((float)(c * (X87)x + (X87)y * s), (float)((X87)y * c - (X87)x * s));
        (basis.M11, basis.M21) = Pair(basis.M11, basis.M21);
        (basis.M12, basis.M22) = Pair(basis.M12, basis.M22);
        (basis.M13, basis.M23) = Pair(basis.M13, basis.M23);
        return basis;
    }
}
