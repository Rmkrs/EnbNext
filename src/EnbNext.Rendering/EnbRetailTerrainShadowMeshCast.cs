using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Mesh ray cast of a SHADOW occluder (retail 940530 / 92AE10): the world segment
/// in the mesh's local space against its triangles (through the authored tree when it has one),
/// keeping the nearest fraction; the contact point goes back to world space.</summary>
internal sealed class EnbRetailTerrainShadowMeshCast
{
    private readonly EnbRetailDazzleCollision.Triangle[] triangles;
    private readonly EnbCurrentRetailMeshAabTree? tree;
    private readonly (Vector3 Center, Vector3 Extent)[] treeBounds;
    private readonly uint facingFlags;
    private readonly byte[] surfaces;
    private readonly Stack<int> pending = new();

    public EnbRetailTerrainShadowMeshCast(EnbCurrentRetailMeshGeometryState geometry, byte[] surfaces)
    {
        this.surfaces = surfaces;
        var positions = geometry.Positions;
        var indices = geometry.TriangleIndices;
        triangles = new EnbRetailDazzleCollision.Triangle[indices.Length / 3];
        for (int i = 0; i < triangles.Length; i++)
        {
            Vector3 a = positions[indices[i * 3]], b = positions[indices[i * 3 + 1]], c = positions[indices[i * 3 + 2]];
            var plane = EnbCurrentRetailMeshGeometryRuntime.CalculateTrianglePlane(a, b, c);
            triangles[i] = new(a, b, c, new(plane.X, plane.Y, plane.Z));
        }
        tree = geometry.Tree is { Nodes.Count: > 0 } authored ? authored : null;
        treeBounds = tree is null ? [] : new (Vector3, Vector3)[tree.Nodes.Count];
        if (tree is not null)
        {
            using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            for (int i = 0; i < treeBounds.Length; i++)
            {
                var node = tree.Nodes[i]; Vector3 center = default, extent = default;
                for (int axis = 0; axis < 3; axis++)
                {
                    center[axis] = (float)(((R)node.Maximum[axis] + node.Minimum[axis]) * .5f);
                    extent[axis] = (float)(((R)node.Maximum[axis] - node.Minimum[axis]) * .5f);
                }
                treeBounds[i] = (center, extent);
            }
        }
        facingFlags = geometry.Flags & 0xa00;
    }

    /// <summary>Casts the segment start..end (unit direction <paramref name="direction"/>) against
    /// the mesh placed by the retail 3x4 row matrix <paramref name="rows"/>. True when a triangle
    /// is nearer than <paramref name="fraction"/>, which then holds the new fraction; the hit
    /// normal and contact come back in world space with the triangle's surface byte.</summary>
    public bool Cast(ReadOnlySpan<float> rows, Vector3 start, Vector3 end, Vector3 direction, ref float fraction,
        out Vector3 normal, out uint surface, out Vector3 contact)
    {
        normal = contact = default; surface = 0;
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Span<float> m = stackalloc float[12];
        rows.CopyTo(m);
        if (facingFlags != 0)
        {
            // 0200 faces along the ray, 0800 toward its start, both from the owner translation.
            var position = new Vector3(m[3], m[7], m[11]);
            var facing = EnbRetailCameraLookAt.BetweenPoints(position, (facingFlags & 0x200) != 0 ? position - direction : start);
            m[0] = facing.M11; m[1] = facing.M21; m[2] = facing.M31; m[3] = facing.M41;
            m[4] = facing.M12; m[5] = facing.M22; m[6] = facing.M32; m[7] = facing.M42;
            m[8] = facing.M13; m[9] = facing.M23; m[10] = facing.M33; m[11] = facing.M43;
        }
        var world = new Matrix4x4(m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1);
        Span<float> inverse = stackalloc float[12];
        EnbRetailDazzleMeshRay.InvertRigid(world, inverse);
        EnbRetailDazzleMeshRay.TransformRay(inverse, start, end, direction, out var origin, out var endpoint, out var delta, out var normalized);
        bool singleRay = EnbRetailDazzleCollision.SingleTriangleRange(origin) && EnbRetailDazzleCollision.SingleTriangleRange(delta);
        int hit = -1;
        if (tree is null)
        {
            for (int i = 0; i < triangles.Length; i++)
                if (triangles[i].Intersect(origin, delta, singleRay, ref fraction)) hit = i;
        }
        else
        {
            pending.Clear(); pending.Push(0);
            while (pending.TryPop(out int index))
            {
                // Malformed authored trees fail closed instead of crashing.
                if ((uint)index >= (uint)treeBounds.Length) continue;
                var node = tree.Nodes[index];
                var bounds = treeBounds[index];
                if (EnbRetailDazzleCollision.ClassifyRayBounds(bounds.Center, bounds.Extent, origin, endpoint, delta, normalized) == 1) continue;
                if ((node.First & 0x80000000) == 0) { pending.Push((int)node.Second); pending.Push((int)node.First); continue; }
                long first = node.First & 0x7fffffff;
                for (long i = 0; i < node.Second; i++)
                {
                    long polygon = first + i;
                    if (polygon >= tree.PolygonIndices.Count) continue;
                    uint triangle = tree.PolygonIndices[(int)polygon];
                    if (triangle < (uint)triangles.Length && triangles[triangle].Intersect(origin, delta, singleRay, ref fraction)) hit = (int)triangle;
                }
            }
        }
        if (hit < 0) return false;
        surface = (uint)hit < (uint)surfaces.Length ? surfaces[hit] : 0u;
        Vector3 n = triangles[hit].Normal;
        normal = new((m[2] * n.Z + m[1] * n.Y) + m[0] * n.X,
            (m[6] * n.Z + m[5] * n.Y) + m[4] * n.X,
            (m[10] * n.Z + m[9] * n.Y) + m[8] * n.X);
        Vector3 local = new(fraction * delta.X + origin.X, fraction * delta.Y + origin.Y, fraction * delta.Z + origin.Z);
        contact = new(((m[0] * local.X + m[2] * local.Z) + m[1] * local.Y) + m[3],
            ((m[6] * local.Z + m[5] * local.Y) + m[4] * local.X) + m[7],
            ((m[10] * local.Z + m[9] * local.Y) + m[8] * local.X) + m[11]);
        return true;
    }
}
