using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Retail camera-axis look-at (8C2780) and rotating provider (79C590).
/// Matrices retain right/up/back columns through rotation and extraction.</summary>
public static class EnbRetailViewCamera
{
    public static Matrix4x4 BetweenPoints(Vector3 origin,Vector3 target,float roll=0,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        =>EnbRetailViewBasis.BetweenPoints(origin,target,roll);
    /// <summary>5C2920 automatic scanner branch, through matrix submission.
    /// Bound selection and clip-plane retention belong to the caller.</summary>
    public static Matrix4x4 ScannerFit(Vector3 observer,Vector3 center,float radius,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        var m=BetweenPoints(observer,center,0,precision);
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        // AF3AF0 is the double 1.8. Y/Z products spill before addition; X stays in ST.
        float distance=(float)((R)radius*R.FromDoubleBits(0x3ffccccccccccccd,control));
        R x=(R)m.M31*distance;
        float y=(float)((R)m.M32*distance),z=(float)((R)m.M33*distance);
        m.M41=(float)(x+center.X);m.M42=(float)((R)center.Y+y);m.M43=(float)((R)center.Z+z);
        return m;
    }

    /// <summary>Retail rotating camera provider: rotates the basis by elapsed time × rate about one
    /// of its local axes (0, 1 or 2; any other value leaves it) and returns the orientation.</summary>
    public static Quaternion Rotate(Matrix4x4 basis, uint elapsed, float rate, int axis)
    {
        Span<float> m = stackalloc float[12] { basis.M11, basis.M21, basis.M31, basis.M41, basis.M12, basis.M22, basis.M32, basis.M42, basis.M13, basis.M23, basis.M33, basis.M43 };
        float angle = (float)((double)elapsed * rate);
        switch (axis)
        {
            case 2:
            {
                double s = Math.Sin(-angle);
                float c = (float)Math.Cos(-angle);
                for (int r = 0; r < 12; r += 4)
                {
                    float a = m[r], b = m[r + 1];
                    m[r] = (float)(b * s) + c * a;
                    m[r + 1] = b * c - (float)(a * s);
                }
                break;
            }
            case 1:
            {
                double s = Math.Sin(angle), c = Math.Cos(angle);
                for (int r = 0; r < 12; r += 4)
                {
                    float a = m[r + 1], b = m[r + 2];
                    m[r + 1] = (float)(a * c) + (float)(b * s);
                    m[r + 2] = (float)(b * c) - (float)(a * s);
                }
                break;
            }
            case 0:
            {
                double s = Math.Sin(angle), c = Math.Cos(angle);
                for (int r = 0; r < 12; r += 4)
                {
                    float a = m[r], b = m[r + 2];
                    m[r] = (float)(c * a) - (float)(b * s);
                    m[r + 2] = (float)(b * c) + (float)(a * s);
                }
                break;
            }
        }
        return EnbRetailQuaternionConversions.ExtractOrientation(new(m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1));
    }
}
