using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>6F4174..6F41E4: rotate columns1/2 of the retained physical matrix.</summary>
public static class EnbRetailShipBankTransform
{
    public static Matrix4x4 Apply(Matrix4x4 matrix, float angle,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(precision, true);
        float sine = (float)R.Sine((R)angle);
        R cosine = R.Cosine((R)angle);
        (matrix.M21, matrix.M31) = Rotate(matrix.M21, matrix.M31);
        (matrix.M22, matrix.M32) = Rotate(matrix.M22, matrix.M32);
        (matrix.M23, matrix.M33) = Rotate(matrix.M23, matrix.M33);
        return matrix;

        (float, float) Rotate(float a, float b)
            => ((float)((R)a * cosine + (R)b * sine),
                (float)((R)b * cosine - (R)a * sine));
    }
}
