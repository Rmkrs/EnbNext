using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EnbNext.Rendering;

/// <summary>Geometry preparation for D3D8's cubic-position, linear-normal
/// N-patches. Lighting, texture mapping and rasterization remain on the GPU.
/// Retail sets four segments at 00442D96; 00917600 admits one through eight.</summary>
public sealed record EnbW3dNPatchGeometry(
    ImmutableArray<Vector3> Positions, ImmutableArray<Vector3> Normals,
    ImmutableArray<uint> Indices, ImmutableArray<EnbW3dIndexedSubmissionRun> Runs,
    ImmutableArray<EnbW3dNPatchSample> Samples, int Segments)
{
    public ImmutableArray<Vector2> Interpolate(IReadOnlyList<Vector2> source)
        => Samples.Select(s => source[s.A] * s.Weights.X + source[s.B] * s.Weights.Y + source[s.C] * s.Weights.Z).ToImmutableArray();
    public ImmutableArray<Vector4> Interpolate(IReadOnlyList<Vector4> source)
        => Samples.Select(s => source[s.A] * s.Weights.X + source[s.B] * s.Weights.Y + source[s.C] * s.Weights.Z).ToImmutableArray();
}

public readonly record struct EnbW3dNPatchSample(int A, int B, int C, Vector3 Weights);
public readonly record struct EnbW3dNPatchSeam(int SourceTriangle, int A, int B, int C);

public static class EnbW3dNPatchCompiler
{
    public const int RetailSegments = 4;

    public static int NormalizeSegments(uint segments) => (int)Math.Clamp(segments, 1u, 8u);

    public static int PreparedIndexCount(EnbW3dRenderMesh mesh, bool capability, int segments)
        => checked(mesh.Indices.Count + (capability && segments > 1 && mesh.NPatch.Requested
            ? OwnerCache.Get(ref mesh.DerivedNPatchSeams, mesh, static m => CompileSeams(m.Positions, m.Indices)).Length * 3 : 0));

    public static bool IsEligible(EnbW3dDrawTask packet, bool capability, int segments)
        => capability && segments > 1 && packet.Mesh.CurrentRetailUsesMeshRenderer &&
           packet.Mesh.NPatch.Requested && packet.CurrentRetailAdditionalMeshPassFlags is null &&
           EnbCurrentRetailMeshBufferLayout.RequiresAuthoredNormals(packet.Mesh.Attributes,
               packet.CurrentRetailMeshInvocation is {} invocation ? invocation.Appearance.Passes : packet.Mesh.MaterialPasses,
               true, capability, (uint)segments);

    // PN-triangle control points: Vlachos/Peters/Boyd/Mitchell (I3D 2001).
    // D3D8 defaults to cubic positions and LINEAR normals, not the optional
    // quadratic normal field in that paper. Do not normalize the interpolant
    // here: the existing GPU normal/lighting state owns that operation.
    public static Vector3 EvaluatePosition(Vector3 a, Vector3 b, Vector3 c,
        Vector3 na, Vector3 nb, Vector3 nc, Vector3 weights)
    {
        static Vector3 Edge(Vector3 p, Vector3 q, Vector3 n)
            => (2 * p + q - Vector3.Dot(q - p, n) * n) / 3;
        var ab = Edge(a, b, na); var ac = Edge(a, c, na);
        var ba = Edge(b, a, nb); var bc = Edge(b, c, nb);
        var ca = Edge(c, a, nc); var cb = Edge(c, b, nc);
        var e = (ab + ac + ba + bc + ca + cb) / 6;
        var center = e + (e - (a + b + c) / 3) / 2;
        float u = weights.X, v = weights.Y, w = weights.Z;
        return a * (u * u * u) + b * (v * v * v) + c * (w * w * w) +
            3 * (ab * (u * u * v) + ac * (u * u * w) + ba * (u * v * v) +
                 bc * (v * v * w) + ca * (u * w * w) + cb * (v * w * w)) +
            center * (6 * u * v * w);
    }

    /// <summary>0095A200 adds two triangles between disjoint index edges whose
    /// endpoints have duplicate positions. Each bridge inherits the indicated
    /// original triangle's material/texture/shader (00959EC0).</summary>
    public static EnbW3dNPatchSeam[] CompileSeams(IReadOnlyList<Vector3> positions, IReadOnlyList<uint> indices)
    {
        var duplicates = positions.GroupBy(p => p).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var edges = new Dictionary<((int, int, int), (int, int, int)), (int A, int B, int Triangle)>();
        var result = new List<EnbW3dNPatchSeam>();
        for (int triangle = 0; triangle < indices.Count / 3; triangle++)
            for (int edge = 0; edge < 3; edge++)
            {
                int a = (int)indices[triangle * 3 + edge], b = (int)indices[triangle * 3 + (edge + 1) % 3];
                var pa = positions[a]; var pb = positions[b];
                if (!duplicates.Contains(pa) || !duplicates.Contains(pb)) continue;
                // Preserve the native endpoint ordering, including hash ties.
                static (int X, int Y, int Z) Bits(Vector3 p) => (BitConverter.SingleToInt32Bits(p.X), BitConverter.SingleToInt32Bits(p.Y), BitConverter.SingleToInt32Bits(p.Z));
                static int Hash(Vector3 p) { var bits = Bits(p); return unchecked((bits.X * 37 + bits.Y) * 37 + bits.Z); }
                var key = Hash(pa) < Hash(pb) ? (Bits(pa), Bits(pb)) : (Bits(pb), Bits(pa));
                if (!edges.TryGetValue(key, out var prior)) { edges.Add(key, (a, b, triangle)); continue; }
                if (a == prior.A || a == prior.B || b == prior.A || b == prior.B) continue;
                result.Add(new(triangle, b, prior.B, prior.A));
                result.Add(new(prior.Triangle, a, prior.B, b));
            }
        return result.ToArray();
    }

    public static EnbW3dNPatchGeometry Compile(EnbW3dIndexedSubmissionPlan source, int segments)
    {
        if (segments is < 2 or > 8) throw new ArgumentOutOfRangeException(nameof(segments));
        var positions = ImmutableArray.CreateBuilder<Vector3>();
        var normals = ImmutableArray.CreateBuilder<Vector3>();
        var indices = ImmutableArray.CreateBuilder<uint>();
        var samples = ImmutableArray.CreateBuilder<EnbW3dNPatchSample>();
        var runs = ImmutableArray.CreateBuilder<EnbW3dIndexedSubmissionRun>();
        var seams = OwnerCache.Get(ref source.Packet.Mesh.DerivedNPatchSeams, source.Packet.Mesh, static m => CompileSeams(m.Positions, m.Indices));
        foreach (var run in source.Runs)
        {
            int firstIndex = indices.Count, firstVertex = positions.Count;
            for (int index = run.StartIndex; index < run.StartIndex + run.IndexCount; index += 3)
                Triangle((int)source.Indices[index], (int)source.Indices[index + 1], (int)source.Indices[index + 2]);
            foreach (var seam in seams)
                if (seam.SourceTriangle * 3 >= run.StartIndex && seam.SourceTriangle * 3 < run.StartIndex + run.IndexCount)
                    Triangle(seam.A, seam.B, seam.C);
            int count = indices.Count - firstIndex;
            runs.Add(run with { FirstTriangleOrdinal = firstIndex / 3, TriangleCount = count / 3,
                StartIndex = firstIndex, IndexCount = count, MinVertexIndex = (uint)firstVertex,
                MaxVertexIndex = (uint)(positions.Count - 1), VertexCount = (uint)(positions.Count - firstVertex) });
        }
        return new(positions.ToImmutable(), normals.ToImmutable(), indices.ToImmutable(), runs.ToImmutable(), samples.ToImmutable(), segments);

        void Triangle(int a, int b, int c)
        {
            int start = positions.Count;
            int Row(int i) => start + i * (segments + 1) - i * (i - 1) / 2;
            for (int i = 0; i <= segments; i++)
                for (int j = 0; j <= segments - i; j++)
                {
                    var weights = new Vector3(segments - i - j, j, i) / segments;
                    positions.Add(EvaluatePosition(source.Positions[a], source.Positions[b], source.Positions[c],
                        source.Normals[a], source.Normals[b], source.Normals[c], weights));
                    normals.Add(source.Normals[a] * weights.X + source.Normals[b] * weights.Y + source.Normals[c] * weights.Z);
                    samples.Add(new(a, b, c, weights));
                    if (i == segments || j == segments - i) continue;
                    indices.Add((uint)(Row(i) + j)); indices.Add((uint)(Row(i) + j + 1)); indices.Add((uint)(Row(i + 1) + j));
                    if (j < segments - i - 1)
                    {
                        indices.Add((uint)(Row(i) + j + 1)); indices.Add((uint)(Row(i + 1) + j + 1)); indices.Add((uint)(Row(i + 1) + j));
                    }
                }
        }
    }
}
