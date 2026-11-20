using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail look-at basis between two points: yaw from the horizontal direction, pitch from
/// the elevation, then roll about the forward (X) axis; translation is the origin.</summary>
public static class EnbRetailCameraLookAt
{
    public static Matrix4x4 BetweenPoints(Vector3 origin, Vector3 target, float roll = 0)
    {
        float dx = target.X - origin.X, dy = target.Y - origin.Y, dz = target.Z - origin.Z;
        float xx = dx * dx, yy = dy * dy;
        float length = MathF.Sqrt((dz * dz + xx) + yy), horizontal = MathF.Sqrt(xx + yy);
        float sinPitch = 0, cosPitch = 1;
        if (length != 0) { sinPitch = dz / length; cosPitch = horizontal / length; }
        float cosYaw = 1, sinYaw = 0;
        if (horizontal != 0) { sinYaw = dy / horizontal; cosYaw = dx / horizontal; }
        // Rows of the retail 3x4 matrix, starting from identity.
        Span<float> m = stackalloc float[12] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
        m[3] = ((origin.X * m[0] + origin.Z * m[2]) + origin.Y * m[1]) + m[3];
        m[7] = ((origin.Z * m[6] + origin.Y * m[5]) + m[4] * origin.X) + m[7];
        m[11] = ((m[8] * origin.X + origin.Z * m[10]) + origin.Y * m[9]) + m[11];
        for (int r = 0; r < 12; r += 4)
        {
            float a = m[r], b = m[r + 1];
            m[r] = a * cosYaw + b * sinYaw;
            m[r + 1] = b * cosYaw - a * sinYaw;
        }
        float negative = -sinPitch;
        for (int r = 0; r < 12; r += 4)
        {
            float a = m[r], b = m[r + 2];
            m[r] = a * cosPitch - b * negative;
            m[r + 2] = a * negative + b * cosPitch;
        }
        ApplyRoll(m, roll);
        return new(m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1);
    }

    /// <summary>Rotation about the local X axis (retail rows' Y/Z entries).</summary>
    public static void ApplyRoll(Span<float> rows, float roll)
    {
        double s = Math.Sin(roll), c = Math.Cos(roll);
        for (int r = 0; r < 12; r += 4)
        {
            float a = rows[r + 1], b = rows[r + 2];
            rows[r + 1] = (float)((float)(a * c) + (float)(b * s));
            rows[r + 2] = (float)((float)(b * c) - (float)(a * s));
        }
    }
}
