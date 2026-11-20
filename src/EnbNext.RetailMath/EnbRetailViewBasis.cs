using System;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail view basis between two points: starts from the camera axes (X back, Y right,
/// Z up), yaws toward the horizontal direction, pitches toward the elevation and rolls about the
/// view axis; translation is the origin.</summary>
public static class EnbRetailViewBasis
{
    public static Matrix4x4 BetweenPoints(Vector3 origin, Vector3 target, float roll = 0)
    {
        float dx = target.X - origin.X, dy = target.Y - origin.Y, dz = target.Z - origin.Z;
        float horizontalSquare = dy * dy + dx * dx, horizontal = MathF.Sqrt(horizontalSquare);
        float sinYaw = 0, cosYaw = 1;
        if (horizontalSquare != 0) { float inverse = 1f / horizontal; sinYaw = inverse * dy; cosYaw = inverse * dx; }
        float square = dz * dz + horizontalSquare, sinPitch = 0, cosPitch = 1;
        if (square != 0)
        {
            float r = EnbRetailFloat.ReciprocalSquareRoot(square);
            sinPitch = r * dz; cosPitch = horizontal * r;
        }
        Span<float> m = stackalloc float[12] { 0, 0, -1, origin.X, -1, 0, 0, origin.Y, 0, 1, 0, origin.Z };
        for (int row = 0; row < 12; row += 4)
        {
            float a = m[row], b = m[row + 2];
            m[row] = a * cosYaw - b * sinYaw;
            m[row + 2] = b * cosYaw + a * sinYaw;
        }
        for (int row = 0; row < 12; row += 4)
        {
            float a = m[row + 1], b = m[row + 2];
            m[row + 1] = a * cosPitch + b * sinPitch;
            m[row + 2] = b * cosPitch - a * sinPitch;
        }
        double s = Math.Sin(-roll);
        float c = (float)Math.Cos(-roll);
        for (int row = 0; row < 12; row += 4)
        {
            float a = m[row], b = m[row + 1];
            m[row] = (float)(b * s) + a * c;
            m[row + 1] = b * c - (float)(a * s);
        }
        return new(m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1);
    }
}
