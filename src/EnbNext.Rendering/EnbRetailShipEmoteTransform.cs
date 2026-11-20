using System.Buffers.Binary;
using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>7E70A0 local gesture and 7E6E40 physical-provider composition.</summary>
public static class EnbRetailShipEmoteTransform
{

    public static Matrix4x4 Sample(Matrix4x4 physical, uint emote, float time, float radius,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
        => EnbRetailPointGroupTransform.Compose(Local(emote, time, radius, precision), physical, precision);

    public static Matrix4x4 Local(uint emote, float time, float radius,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(precision, true);
        float tau = BitConverter.UInt32BitsToSingle(0x40c90fdb);
        float fourPi = BitConverter.UInt32BitsToSingle(0x41490fdb);
        Matrix4x4 matrix = Matrix4x4.Identity;
        if (emote is 1 or 2)
        {
            float extent = (float)((R)radius * 2);
            float phase = (float)((emote == 1 ? (R)time - .25f : (R).25f - time) * tau);
            float sine = (float)R.Sine((R)phase), cosine = (float)R.Cosine((R)phase);
            matrix.M41 = (float)((R)cosine * extent);
            matrix.M43 = (float)((emote == 1 ? (R)sine + 1 : (R)sine - 1) * extent);
            float angle = (float)((R)time * (emote == 1 ? tau : -tau));
            sine = (float)R.Sine((R)angle); cosine = (float)R.Cosine((R)angle);
            float squaredCosine = (float)((R)cosine * cosine);
            R squared = (R)sine * sine + squaredCosine;
            if ((float)squared != 0)
            {
                R inverse = Normalize((float)squared, precision);
                cosine = (float)(inverse * cosine); sine = (float)(inverse * sine);
            }
            float sideX = (float)((R)0 - sine), sideY = 0;
            R sideZ = (R)cosine - 0;
            float storedSideZ = (float)sideZ;
            float squaredSideX = (float)((R)sideX * sideX);
            squared = sideZ * sideZ + (R)0 * 0 + squaredSideX;
            if ((float)squared != 0)
            {
                R inverse = Normalize((float)squared, precision);
                sideX = (float)(inverse * sideX); sideY = (float)((R)sideY * inverse);
                sideZ = inverse * storedSideZ;
            }
            matrix.M11 = cosine; matrix.M13 = sine;
            matrix.M31 = sideX; matrix.M32 = sideY; matrix.M33 = (float)sideZ;
            return matrix;
        }
        R s, c;
        if (emote == 0)
        {
            float phase = (float)((R)time * fourPi);
            float wave = (float)R.Sine((R)phase);
            R angle = (R)wave * .25f;
            s = (R)(float)R.Sine(angle); c = R.Cosine(angle);
        }
        else if (emote == 3)
        {
            R angle = (R)time * tau;
            s = R.Sine(angle); c = R.Cosine((R)(float)angle);
        }
        else if (emote is 4 or 5 or 6)
        {
            float angle = (float)((R)time * (emote == 4 ? -tau : emote == 5 ? -fourPi : fourPi));
            s = R.Sine((R)angle); c = R.Cosine((R)angle);
        }
        else return matrix;
        if (emote is 5 or 6)
        {
            (matrix.M11, matrix.M21) = Rotate(1, 0);
            (matrix.M12, matrix.M22) = Rotate(0, 1);
            (matrix.M13, matrix.M23) = Rotate(0, 0);
        }
        else
        {
            (matrix.M21, matrix.M31) = Rotate(0, 0);
            (matrix.M22, matrix.M32) = Rotate(1, 0);
            (matrix.M23, matrix.M33) = Rotate(0, 1);
        }
        return matrix;

        (float, float) Rotate(float a, float b)
            => ((float)((R)a * c + (R)b * s), (float)((R)b * c - (R)a * s));
    }

    private static R Normalize(float squared, EnbRetailLinePrecision precision)
    {
        return R.FromFloatBits(BitConverter.SingleToUInt32Bits(EnbRetailFloat.ReciprocalSquareRoot(squared)));
    }
}
