using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Assets;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>007633E0's projector and 009B7250/009B8A10's retained geometry.
/// Direction is supplied by AuxData; retail does not normalize its basis.</summary>
public readonly record struct EnbRetailDecalProjection(Vector3 X, Vector3 Y, Vector3 Z,
    Vector3 Origin, Vector3 Center, Vector3 Extent)
{
    public static EnbRetailDecalProjection Create(Vector3 direction, float scale,
        EnbProjectedDecalDefinition definition, EnbW3dWorldBounds sphere)
    {
        var x = new Vector3(-direction.Y, direction.X, 0);
        var y = new Vector3(-direction.X * direction.Z, -direction.Y * direction.Z,
            direction.X * direction.X + direction.Y * direction.Y);
        var origin = sphere.Center + 2 * sphere.Radius * direction;
        // 763593..7635C6: far=2r, near=r/2; 965130 keeps these bounds
        // independently of the projection matrix's fixed 0.1 near plane.
        return new(x, y, direction, origin, origin - direction * (1.25f * sphere.Radius),
            new(scale * definition.BaseSize * .5f, scale * definition.BaseSize * .5f, .75f * sphere.Radius));
    }

    public EnbRetailDecalProjection Transform(Matrix4x4 worldToMesh) => new(
        Vector3.TransformNormal(X, worldToMesh), Vector3.TransformNormal(Y, worldToMesh),
        Vector3.TransformNormal(Z, worldToMesh), Vector3.Transform(Origin, worldToMesh),
        Vector3.Transform(Center, worldToMesh), Extent);

    public Vector2 Uv(Vector3 worldPosition, int textureWidth)
    {
        // 981490 -> 97F810/97F9E0. The UV rows use the transposed basis,
        // with (width-1)/(2*width), not a generic inverse or half-texel offset.
        var relative = worldPosition - Origin;
        float factor = (textureWidth - 1f) * .5f / textureWidth;
        return new((Vector3.Dot(relative, X) / Extent.X + 1) * factor,
            (1 - Vector3.Dot(relative, Y) / Extent.Y) * factor);
    }
}

public sealed record EnbRetailDecalGeometry(Vector3[] Positions, Vector3[] Normals,
    Vector2[] Uvs, uint[] Indices, int[] SourceVertices);

public static class EnbRetailProjectedDecal
{
    private readonly record struct Vertex(Vector3 Position, Vector3 Normal);

    public static EnbW3dDeformedGeometry UpdateSkin(EnbRetailDecalGeometry retained, EnbW3dDeformedGeometry current)
        => new(retained.SourceVertices.Select(i => current.Positions[i]).ToImmutableArray(),
            retained.SourceVertices.Select(i => current.Normals[i]).ToImmutableArray());

    public static bool Admits(EnbW3dRenderMesh mesh)
        => !mesh.GeometrySort.Requested &&
            mesh.MaterialPasses.FirstOrDefault(p => p.Ordinal == 0)?.Shaders.Any(s =>
                (s.FixedFunction.PackedRetailState & 0x400e0) != 0) != true;

    /// <summary>0093F020 selects candidate triangles in mesh space for rigid
    /// meshes and deformed world space for skins. 009B8A10 keeps skin indices
    /// and initial UVs; it does not clip polygons or reject back-facing faces.</summary>
    public static EnbRetailDecalGeometry Build(EnbRetailDecalProjection worldProjection,
        EnbW3dDrawTask packet, int textureWidth)
    {
        bool skin = packet.DeformedGeometry is not null;
        IReadOnlyList<Vector3> source = packet.DeformedGeometry is {} deformed ? deformed.Positions
            : packet.Mesh.Positions.Select(p => p * packet.CurrentRetailMeshModelScale).ToArray();
        var normals = packet.DeformedGeometry?.Normals ?? packet.Mesh.Normals;
        // Native 8C2BD0 is the rigid transpose inverse. Mesh scaling has
        // already been applied to the model at the caller boundary.
        var world = MeshWorld(packet);
        var inverse = RigidInverse(world);
        var projection = skin ? worldProjection : worldProjection.Transform(inverse);
        var positions = new List<Vector3>(); var outputNormals = new List<Vector3>();
        var uvs = new List<Vector2>(); var indices = new List<uint>(); var sourceVertices = new List<int>();
        for (int triangle = 0; triangle + 2 < packet.Mesh.Indices.Count; triangle += 3)
        {
            int ia = (int)packet.Mesh.Indices[triangle], ib = (int)packet.Mesh.Indices[triangle + 1],
                ic = (int)packet.Mesh.Indices[triangle + 2];
            var a = source[ia]; var b = source[ib]; var c = source[ic];
            if (!Intersects(projection, a, b, c)) continue;
            if (skin)
            {
                foreach (int index in new[] { ia, ib, ic })
                {
                    indices.Add((uint)positions.Count); positions.Add(source[index]);
                    outputNormals.Add(normals[index]); uvs.Add(worldProjection.Uv(source[index], textureWidth));
                    sourceVertices.Add(index);
                }
                continue;
            }
            var normal = Vector3.Cross(b - a, c - a);
            if (normal == Vector3.Zero) normal = Vector3.UnitZ; //0092C7B0 degenerate plane.
            if (!(Vector3.Dot(Normalize(normal), projection.Z) > 0)) continue;
            List<Vertex> polygon = [new(a, normals[ia]), new(b, normals[ib]), new(c, normals[ic])];
            foreach (var (axis, extent) in new[] { (projection.X, projection.Extent.X), (projection.Y, projection.Extent.Y) })
            {
                polygon = Clip(polygon, -axis, Vector3.Dot(-axis, projection.Center + axis * extent));
                polygon = Clip(polygon, axis, Vector3.Dot(axis, projection.Center - axis * extent));
            }
            if (polygon.Count < 3) continue;
            uint first = (uint)positions.Count;
            for (int i = 1; i + 1 < polygon.Count; i++)
            { indices.Add(first); indices.Add(first + (uint)i); indices.Add(first + (uint)i + 1); }
            foreach (var vertex in polygon)
            {
                positions.Add(vertex.Position);
                outputNormals.Add(Normalize(vertex.Normal));
                uvs.Add(worldProjection.Uv(Vector3.Transform(vertex.Position, world), textureWidth));
            }
        }
        return new(positions.ToArray(), outputNormals.ToArray(), uvs.ToArray(), indices.ToArray(), sourceVertices.ToArray());
    }

    public static Matrix4x4 MeshWorld(EnbW3dDrawTask packet)
    {
        if (packet.DeformedGeometry is not null) return Matrix4x4.Identity;
        if (packet.CurrentRetailUnscaledWorldTransform is {} retained) return retained;
        var world = packet.WorldTransform; float scale = packet.CurrentRetailMeshModelScale;
        if (scale != 0)
        {
            world.M11 /= scale; world.M12 /= scale; world.M13 /= scale;
            world.M21 /= scale; world.M22 /= scale; world.M23 /= scale;
            world.M31 /= scale; world.M32 /= scale; world.M33 /= scale;
        }
        return world;
    }

    private static Vector3 Normalize(Vector3 value)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float square = (float)((R)value.X * value.X + (R)value.Y * value.Y + (R)value.Z * value.Z);
        if (square == 0) return value;
        R inverse = EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
        return new((float)((R)value.X * inverse), (float)((R)value.Y * inverse), (float)((R)value.Z * inverse));
    }

    public static Matrix4x4 RigidInverse(Matrix4x4 world)
    {
        var result = Matrix4x4.Transpose(world);
        result.M14 = result.M24 = result.M34 = 0;
        result.Translation = -Vector3.TransformNormal(world.Translation, result);
        result.M44 = 1;
        return result;
    }

    // 8D0740: triangle normal, three box axes, then nine edge/box cross axes.
    public static bool Intersects(EnbRetailDecalProjection box, Vector3 a, Vector3 b, Vector3 c)
    {
        a -= box.Center; b -= box.Center; c -= box.Center;
        var edges = new[] { b - a, c - a, c - b };
        bool Separated(Vector3 axis, float radius)
        {
            float first = Vector3.Dot(a, axis), second = Vector3.Dot(b, axis), third = Vector3.Dot(c, axis);
            //8D1BA0 and inlined tail: native B0C64C=0.001 shrinks the
            // admissible interval. Touching is rejected; this is not padding.
            float limit = radius - axis.Length() * .001f;
            return MathF.Min(first, MathF.Min(second, third)) > limit ||
                MathF.Max(first, MathF.Max(second, third)) < -limit;
        }
        float Radius(Vector3 axis) => MathF.Abs(Vector3.Dot(box.X, axis)) * box.Extent.X +
            MathF.Abs(Vector3.Dot(box.Y, axis)) * box.Extent.Y + MathF.Abs(Vector3.Dot(box.Z, axis)) * box.Extent.Z;
        var normal = Vector3.Cross(edges[0], edges[1]);
        if (Separated(normal, Radius(normal))) return false;
        foreach (var (axis, extent) in new[] { (box.X, box.Extent.X), (box.Y, box.Extent.Y), (box.Z, box.Extent.Z) })
        {
            if (Separated(axis, extent)) return false;
            foreach (var edge in edges)
            {
                var cross = Vector3.Cross(axis, edge);
                //B0C624=1e-8; degenerate cross axes are not tested.
                if (cross.LengthSquared() > 1e-8f && Separated(cross, Radius(cross))) return false;
            }
        }
        return true;
    }

    private static List<Vertex> Clip(List<Vertex> polygon, Vector3 axis, float distance)
    {
        var result = new List<Vertex>();
        if (polygon.Count < 3) return result;
        var previous = polygon[^1]; float previousDistance = Vector3.Dot(previous.Position, axis) - distance;
        foreach (var current in polygon)
        {
            float currentDistance = Vector3.Dot(current.Position, axis) - distance;
            bool inside = currentDistance > 0, previousInside = previousDistance > 0;
            if (inside != previousInside)
            {
                float t = -previousDistance / Vector3.Dot(current.Position - previous.Position, axis);
                result.Add(new(previous.Position + (current.Position - previous.Position) * t,
                    previous.Normal + (current.Normal - previous.Normal) * t));
            }
            if (inside) result.Add(current);
            previous = current; previousDistance = currentDistance;
        }
        return result;
    }
}
