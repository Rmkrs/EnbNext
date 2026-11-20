using System.Numerics;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>World emitter 008D24E0/008D2580 and particle consumer 0097E810.
/// The native quaternion need not be unit length; its scale survives consumption.</summary>
public static class EnbCurrentRetailParticleBirthTransform
{
    //008D2710 reads the actual3x4 attachment matrix without normalization.
    public static Quaternion ExtractOrientation(Matrix4x4 matrix,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        =>EnbRetailQuaternionConversions.ExtractOrientation(matrix,precision);
    //0097E855..E959 and0097EA62..EB5D have different spills in
    // the first quaternion product. A precomputed float matrix loses them.
    public static Vector3 TransformBirthVector(Quaternion q, Vector3 value, Vector3 addition,
        bool velocity, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        return TransformRetained(q, value.X, value.Y, value.Z, addition, velocity);
    }

    internal static Vector3 TransformRetained(Quaternion q, Register x, Register y, Register z, Vector3 addition, bool velocity)
    {
        Register a = z * q.Y - y * q.Z + x * q.W;
        Register b = y * q.W - (z * q.X - x * q.Z);
        Register c = y * q.X - x * q.Y + z * q.W;
        Register d = -(x * q.X + y * q.Y + z * q.Z);
        if (velocity) { a = (float)a; b = (float)b; c = (float)c; }
        Register nx = -(Register)q.X, ny = -(Register)q.Y, nz = -(Register)q.Z;
        float resultX = (float)(nz * b - ny * c + nx * d + a * q.W);
        float resultY = (float)(ny * d + b * q.W - (nz * a - nx * c));
        Register resultZ = ny * a - nx * b + nz * d + c * q.W;
        return new((float)((Register)resultX + addition.X), (float)((Register)resultY + addition.Y), (float)(resultZ + addition.Z));
    }

    public static Matrix4x4 Create(Quaternion before, Quaternion after, float fraction)
        => Create(Interpolate(before, after, fraction));

    public static Matrix4x4 Create(Quaternion q)
    {
        // Matrix4x4.CreateFromQuaternion assumes unit length on its diagonal.
        // This homogeneous form represents q*v*conjugate(q), including |q|².
        double x=q.X, y=q.Y, z=q.Z, w=q.W;
        return new(
            (float)(w*w+x*x-y*y-z*z), (float)(2*(x*y+z*w)), (float)(2*(x*z-y*w)), 0,
            (float)(2*(x*y-z*w)), (float)(w*w-x*x+y*y-z*z), (float)(2*(y*z+x*w)), 0,
            (float)(2*(x*z+y*w)), (float)(2*(y*z-x*w)), (float)(w*w-x*x-y*y+z*z), 0,
            0, 0, 0, 1);
    }

    public static Quaternion Interpolate(Quaternion before, Quaternion after, float fraction,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        Register dot = (Register)before.W * after.W + (Register)before.Z * after.Z +
            (Register)before.Y * after.Y + (Register)before.X * after.X;
        float magnitude = MathF.Abs((float)dot);
        Register oldWeight = (Register)1d - fraction, newWeight = fraction;
        if ((Register)1d - magnitude >= .001)
        {
            //00A26B58..66: (1+x)*(1-x), FSQRT, FPATAN. The
            // CRT preserves PC while temporarily selecting nearest rounding.
            Register sineSquared = ((Register)1d + magnitude) * ((Register)1d - magnitude);
            float theta = (float)Math.Atan2((double)Register.Sqrt(sineSquared), magnitude);
            float reciprocal = (float)((Register)1d / theta);
            float oldAngle = (float)((Register)theta - (Register)fraction * theta);
            float newAngle = (float)((Register)fraction * theta);
            oldWeight = (float)((Register)(float)Math.Sin(oldAngle) * reciprocal);
            // Native leaves this product in x87 until the component stores.
            newWeight = (Register)(float)Math.Sin(newAngle) * reciprocal;
        }
        if (dot < 0) newWeight = -newWeight;
        return new Quaternion(
            (float)(oldWeight * before.X + newWeight * after.X),
            (float)(oldWeight * before.Y + newWeight * after.Y),
            (float)(oldWeight * before.Z + newWeight * after.Z),
            (float)(oldWeight * before.W + newWeight * after.W));
    }
}
