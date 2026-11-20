using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail's orthonormal check before RenderObject::SetTransform, and the cross-product
/// repair it applies when the check fails.</summary>
public static class EnbRetailSpatialBasis
{
    public readonly record struct Result(bool WasOrthonormal, Matrix4x4 Transform);
    private const float Epsilon = 0.0001f;

    // Rows of the retail 3x4 matrix are the columns of the System.Numerics matrix.
    public static Result Validate(Matrix4x4 m)
    {
        if (IsOrthonormal(m)) return new(true, m);
        Vector3 a = new(m.M11, m.M21, m.M31), b = new(m.M12, m.M22, m.M32);
        Vector3 c = new(b.Z * a.Y - b.Y * a.Z, b.X * a.Z - b.Z * a.X, b.Y * a.X - b.X * a.Y);
        Vector3 d = new(c.Y * a.Z - c.Z * a.Y, c.Z * a.X - c.X * a.Z, c.X * a.Y - c.Y * a.X);
        if (!Normalize(ref a) || !Normalize(ref d) || !Normalize(ref c)) return new(false, Matrix4x4.Identity);
        m.M11 = a.X; m.M21 = a.Y; m.M31 = a.Z;
        m.M12 = d.X; m.M22 = d.Y; m.M32 = d.Z;
        m.M13 = c.X; m.M23 = c.Y; m.M33 = c.Z;
        return new(false, m);
    }

    private static bool Normalize(ref Vector3 v)
    {
        float length = MathF.Sqrt((v.Z * v.Z + v.Y * v.Y) + v.X * v.X);
        if (!(length >= Epsilon)) return false;
        float inverse = 1f / length;
        v = new(inverse * v.X, inverse * v.Y, inverse * v.Z);
        return true;
    }

    // Dot products at or below the limit pass (retail does not take their absolute value).
    private static bool IsOrthonormal(Matrix4x4 m)
    {
        Vector3 a = new(m.M11, m.M21, m.M31), b = new(m.M12, m.M22, m.M32), c = new(m.M13, m.M23, m.M33);
        if ((b.Z * a.Z + b.Y * a.Y) + b.X * a.X > Epsilon) return false;
        if ((c.Z * b.Z + c.Y * b.Y) + c.X * b.X > Epsilon) return false;
        if ((c.Z * a.Z + c.Y * a.Y) + c.X * a.X > Epsilon) return false;
        return !(Unit(a) > Epsilon) && !(Unit(b) > Epsilon) && !(Unit(c) > Epsilon);
        static float Unit(Vector3 v) => MathF.Abs(((v.Z * v.Z + v.Y * v.Y) + v.X * v.X) - 1f);
    }
}
