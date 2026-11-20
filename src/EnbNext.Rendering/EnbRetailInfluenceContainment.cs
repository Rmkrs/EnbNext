using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail point-in-mesh test for influence volumes: rays along the six axis directions
/// count triangle crossings; a ray that only grazes an edge counts a tenth. The point is inside
/// when odd counts outweigh even ones, or when it lies on the surface.</summary>
public sealed class EnbRetailInfluenceContainment
{
    private readonly Vector3[] positions;
    private readonly ushort[] indices;
    private readonly Vector4[] planes;
    private readonly EnbCurrentRetailMeshAabTree? tree;
    // Per direction: tested axis, the two projection axes, and whether the ray runs toward +axis.
    private static readonly int[] Axis = [0, 0, 1, 1, 2, 2], U = [1, 1, 2, 2, 0, 0], V = [2, 2, 0, 0, 1, 1];
    private static readonly bool[] Positive = [true, false, true, false, true, false];
    private const float EdgeWeight = 0.1f;

    public EnbRetailInfluenceContainment(EnbCurrentRetailMeshGeometryState geometry)
    {
        positions = geometry.Positions.ToArray();
        indices = geometry.TriangleIndices.ToArray();
        tree = geometry.Tree;
        planes = new Vector4[indices.Length / 3];
        for (int i = 0; i < planes.Length; i++)
            planes[i] = EnbCurrentRetailMeshGeometryRuntime.CalculateTrianglePlane(
                positions[indices[i * 3]], positions[indices[i * 3 + 1]], positions[indices[i * 3 + 2]]);
    }

    /// <summary>The camera in the object's local space (transposed rigid basis), then the local test.</summary>
    public bool Contains(Matrix4x4 m, Vector3 camera)
    {
        float dx = camera.X - m.M41, dy = camera.Y - m.M42, dz = camera.Z - m.M43;
        var local = new Vector3((dz * m.M13 + dy * m.M12) + dx * m.M11,
            (dz * m.M23 + dx * m.M21) + dy * m.M22,
            (dz * m.M33 + dx * m.M31) + dy * m.M32);
        return ContainsLocal(local);
    }

    public bool ContainsLocal(Vector3 point)
    {
        float odd = 0, even = 0;
        for (int direction = 0; direction < 6; direction++)
        {
            var (crossings, flags) = CountDirection(point, (uint)direction);
            if ((flags & 2) != 0) return true;
            float weight = (flags & 1) != 0 ? EdgeWeight : 1f;
            if ((crossings & 1) != 0) odd = weight + odd; else even = weight + even;
        }
        return odd > even;
    }

    internal (uint Crossings, byte Flags) CountDirection(Vector3 point, uint direction)
    {
        int d = (int)direction;
        byte flags = 0;
        uint count = tree is { Nodes.Count: > 0 }
            ? CountTree(0, point, Axis[d], U[d], V[d], Positive[d], ref flags)
            : CountTriangles(0, indices.Length / 3, null, point, Axis[d], U[d], V[d], Positive[d], ref flags);
        return (count, flags);
    }

    private uint CountTree(int index, Vector3 p, int axis, int u, int v, bool positive, ref byte flags)
    {
        // Malformed authored trees fail closed (no crossings) instead of crashing.
        if ((uint)index >= (uint)tree!.Nodes.Count) return 0;
        var node = tree.Nodes[index];
        if (!(p[u] >= node.Minimum[u]) || !(p[v] >= node.Minimum[v]) || p[u] > node.Maximum[u] || p[v] > node.Maximum[v]) return 0;
        if (positive ? p[axis] > node.Maximum[axis] : -p[axis] > -node.Minimum[axis]) return 0;
        if ((node.First & 0x80000000) != 0)
            return CountTriangles((int)(node.First & 0x7fffffff), (int)node.Second, tree.PolygonIndices, p, axis, u, v, positive, ref flags);
        uint first = CountTree((int)node.First, p, axis, u, v, positive, ref flags);
        return CountTree((int)node.Second, p, axis, u, v, positive, ref flags) + first;
    }

    private uint CountTriangles(int first, int count, IReadOnlyList<uint>? order, Vector3 p, int axis, int u, int v,
        bool positive, ref byte flags)
    {
        uint crossings = 0;
        for (int i = 0; i < count; i++)
        {
            if (order is not null && (uint)(first + i) >= (uint)order.Count) break;
            int triangle = order is null ? first + i : (int)order[first + i];
            if ((uint)triangle >= (uint)planes.Length) continue;
            Vector3 a = positions[indices[triangle * 3]], b = positions[indices[triangle * 3 + 1]], c = positions[indices[triangle * 3 + 2]];
            byte edge = 0;
            if (!ProjectedTriangle(a, b, c, p, u, v, ref edge)) continue;
            var plane = planes[triangle];
            float side = (((plane.Z * p.Z + p.Y * plane.Y) + plane.X * p.X) + plane.W) * (positive ? 1f : -1f) * plane[axis];
            if (!(side >= 0)) { crossings++; flags |= (byte)(edge & 1); }
            else if (side == 0)
            {
                if (plane[axis] != 0 && !float.IsNaN(plane[axis])) { crossings++; flags |= (byte)((edge & 1) | 2); }
                else if (ContainsTrianglePoint(new(plane.X, plane.Y, plane.Z), a, b, c, p)) flags |= 2;
            }
        }
        return crossings;
    }

    /// <summary>2-D containment in the (u, v) projection; sets bit 0 when the point lies on an edge.</summary>
    private static bool ProjectedTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 p, int u, int v, ref byte edge)
    {
        float e0u = b[u] - a[u], e0v = b[v] - a[v], e1u = c[u] - b[u], e1v = c[v] - b[v], e2u = a[u] - c[u], e2v = a[v] - c[v];
        float area = (c[u] - a[u]) * e0v - (c[v] - a[v]) * e0u;
        if (area != 0 && !float.IsNaN(area))
        {
            float sign = area > 0 ? 1f : -1f;
            float w0 = e0v * (p[u] - a[u]) - (p[v] - a[v]) * e0u;
            if (!(w0 * sign >= 0)) return false;
            float w1 = e1v * (p[u] - b[u]) - (p[v] - b[v]) * e1u;
            if (!(w1 * sign >= 0)) return false;
            float w2 = e2v * (p[u] - c[u]) - (p[v] - c[v]) * e2u;
            if (!(w2 * sign >= 0)) return false;
            if (!(w0 != 0) || !(w1 != 0) || !(w2 != 0)) edge |= 1;
            return true;
        }
        // Degenerate projection: test the point against the longer of the first two edges
        // (retail compares the first edge with the second twice and otherwise uses the third).
        float l0 = e0v * e0v + e0u * e0u, l1 = e1v * e1v + e1u * e1u;
        float eu, ev, du, dv, length;
        if (l0 > l1) { eu = e0u; ev = e0v; du = p[u] - a[u]; dv = p[v] - a[v]; length = l0; }
        else { eu = e2u; ev = e2v; du = p[u] - c[u]; dv = p[v] - c[v]; length = l1; }
        if (length == 0 || float.IsNaN(length))
        {
            float square = dv * dv + du * du;
            if (square != 0 && !float.IsNaN(square)) return false;
            edge |= 1; return true;
        }
        float cross = ev * du - dv * eu;
        if (cross != 0 && !float.IsNaN(cross)) return false;
        float qu = du - eu, qv = dv - ev;
        if (dv * dv + du * du > length) return false;
        if (qv * qv + qu * qu > length) return false;
        edge |= 1; return true;
    }

    /// <summary>Point-in-triangle in the plane projection that drops the dominant normal axis.</summary>
    private static bool ContainsTrianglePoint(Vector3 normal, Vector3 a, Vector3 b, Vector3 c, Vector3 p)
    {
        float x = MathF.Abs(normal.X), y = MathF.Abs(normal.Y), z = MathF.Abs(normal.Z);
        int dominant = 0; float largest = x;
        if (y > largest) { dominant = 1; largest = y; }
        if (z > largest) dominant = 2;
        int u = dominant == 0 ? 1 : 0, v = dominant == 2 ? 1 : 2;
        Span<bool> sides = stackalloc bool[3];
        Vector3[] vertices = [a, b, c];
        for (int k = 0; k < 3; k++)
        {
            Vector3 current = vertices[k], next = vertices[(k + 1) % 3];
            float eu = next[u] - current[u], ev = next[v] - current[v], pu = p[u] - current[u], pv = p[v] - current[v];
            sides[k] = pv * eu - pu * ev >= 0;
        }
        return sides[0] == sides[1] && sides[1] == sides[2];
    }
}
