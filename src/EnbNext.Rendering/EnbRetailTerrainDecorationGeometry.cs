using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Decoration geometry as the retail client bakes it: rotation from the placement
/// quaternion, the shadow tilt onto the terrain normal, and the per-instance vertex/index bake.</summary>
public static class EnbRetailTerrainDecorationGeometry
{
    // Retail constants: shadow lift along the normal, the parallel-axis limit and the half-angle factor.
    private const float ShadowLift = 3f;
    private const float ParallelLimit = 0.0001f;

    /// <summary>Matrix columns are the local axes; row r of the retail 3x4 matrix is (M1r, M2r, M3r, M4r).</summary>
    public static Matrix4x4 Rotation(Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float zz = z * z, yy = y * y, xx = x * x;
        float xy = x * y, wz = w * z, wy = w * y, xz = x * z, yz = z * y, wx = w * x;
        float m0 = 1f - (yy + zz) * 2f, m1 = (xy - wz) * 2f, m2 = (xz + wy) * 2f;
        float m4 = (wz + xy) * 2f, m5 = 1f - (xx + zz) * 2f, m6 = (yz - wx) * 2f;
        float m8 = (xz - wy) * 2f, m9 = (wx + yz) * 2f, m10 = 1f - (xx + yy) * 2f;
        return new(m0, m4, m8, 0, m1, m5, m9, 0, m2, m6, m10, 0, 0, 0, 0, 1);
    }

    /// <summary>Lifts the decoration along the terrain normal and tilts its local Z axis onto the
    /// normal (rotation about Z×N by the angle between them).</summary>
    public static Matrix4x4 ShadowTransform(Matrix4x4 m, Vector3 n)
    {
        m.M41 += n.X * ShadowLift; m.M42 += n.Y * ShadowLift; m.M43 += n.Z * ShadowLift;
        float zx = m.M31, zy = m.M32, zz = m.M33;
        float c0 = zy * n.Z - zz * n.Y, c1 = zz * n.X - zx * n.Z, c2 = zx * n.Y - zy * n.X;
        if (!(c2 * c2 + c1 * c1 + c0 * c0 > ParallelLimit)) return m;
        float dot = zy * n.Y + zz * n.Z + zx * n.X;
        float half = (float)Math.Acos(dot) * 0.5f;
        float s = (float)Math.Sin(half), w = (float)Math.Cos(half);
        float qx = c0 * s, qy = c1 * s, qz = c2 * s;
        var x = Rotate(m.M11, m.M12, m.M13);
        var y = Rotate(m.M21, m.M22, m.M23);
        var zAxis = Rotate(zx, zy, zz);
        m.M11 = x.X; m.M12 = x.Y; m.M13 = x.Z;
        m.M21 = y.X; m.M22 = y.Y; m.M23 = y.Z;
        m.M31 = zAxis.X; m.M32 = zAxis.Y; m.M33 = zAxis.Z;
        return m;

        // q * v * conj(q), in the retail evaluation order.
        Vector3 Rotate(float vx, float vy, float vz)
        {
            float px = (vz * qy - vy * qz) + w * vx;
            float py = vy * w - (vz * qx - vx * qz);
            float pz = (vy * qx - vx * qy) + vz * w;
            float pw = -((vz * qz + vy * qy) + vx * qx);
            float bx = -qx, by = -qy, bz = -qz;
            return new(((py * bz - pz * by) + w * px) + pw * bx,
                (w * py + by * pw) - (bz * px - pz * bx),
                ((by * px - py * bx) + w * pz) + bz * pw);
        }
    }

    public sealed record Result(Vector3[] Positions, Vector3[] Normals, Vector2[] TextureCoordinates, ushort[] Indices, ushort MaximumIndex);

    /// <summary>Transforms one decoration instance: positions scaled then placed by the matrix, normals
    /// rotated (not renormalized), UVs copied, indices offset by the batch's first vertex. Returns the
    /// largest source index so the caller can advance the next instance's first vertex.</summary>
    public static Result Bake(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> textureCoordinates, ReadOnlySpan<ushort> indices, Matrix4x4 m, float scale, ushort firstVertex = 0)
    {
        if (indices.Length % 3 != 0 || (normals.Length != 0 && normals.Length != positions.Length) ||
            (textureCoordinates.Length != 0 && textureCoordinates.Length != positions.Length))
            throw new ArgumentException("Decoration arrays must describe complete triangles and matching vertices.");
        var outPositions = new Vector3[positions.Length];
        var outNormals = new Vector3[positions.Length];
        var outCoordinates = new Vector2[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            float sx = scale * positions[i].X, sy = scale * positions[i].Y, sz = scale * positions[i].Z;
            outPositions[i] = new(((sz * m.M31 + sy * m.M21) + sx * m.M11) + m.M41,
                ((sx * m.M12 + sz * m.M32) + sy * m.M22) + m.M42,
                ((sx * m.M13 + sz * m.M33) + sy * m.M23) + m.M43);
            if (normals.Length != 0)
            {
                var v = normals[i];
                outNormals[i] = new((m.M31 * v.Z + m.M21 * v.Y) + v.X * m.M11,
                    (m.M32 * v.Z + m.M12 * v.X) + m.M22 * v.Y,
                    (m.M33 * v.Z + m.M13 * v.X) + m.M23 * v.Y);
            }
            if (textureCoordinates.Length != 0) outCoordinates[i] = textureCoordinates[i];
        }
        var outIndices = new ushort[indices.Length];
        ushort maximum = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            maximum = Math.Max(maximum, indices[i]);
            outIndices[i] = unchecked((ushort)(indices[i] + firstVertex));
        }
        return new(outPositions, outNormals, outCoordinates, outIndices, maximum);
    }
}
