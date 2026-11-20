using System.Numerics;
using System.Diagnostics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public delegate (float Fraction, bool StartInside) EnbDazzleRayCast(
    Vector3 start, Vector3 end, float fraction, bool startInside);

/// <summary>An ordinary scene root, in scene iterator order. Children remain
/// behind the root's collision callback; draw packets are not scene roots.</summary>
public sealed record EnbDazzleCollisionRoot(Vector4 Sphere, bool SceneVisible,
    bool IsTerrain, EnbDazzleRayCast Cast)
{
    public string DiagnosticName { get; init; } = "scene root";
    //Some +F0 getters advance retained state. Resolve only inside the actual
    //visibility query, after the scene-visible gate. Null contains bad assets.
    public Func<Vector4?>? ResolveSphere { get; init; }
    // Optional query-task preparation. Called only when an admitted sample
    // reaches this root; the owner may reuse its prepared leaves for the view.
    public Action? PrepareCast { get; init; }

}

/// <summary>CPU wall timings; deliberately does not flush or wait for the GPU.
/// Reused by one renderer and reset once per frame.</summary>
public sealed class EnbDazzleVisibilityProfile
{
    [ThreadStatic] public static EnbDazzleVisibilityProfile? Active;
    public int MeshCasts, TriangleTests, TreeNodes, LiteralCasts, CachedMeshQueries;
    public long MeshTicks;
    private readonly Dictionary<string,long> _meshTicks=[];
    public void Mesh(string name,long ticks,int triangles,int nodes,bool literal)
    {
        MeshCasts++;MeshTicks+=ticks;TriangleTests+=triangles;TreeNodes+=nodes;if(literal)LiteralCasts++;
        _meshTicks[name]=_meshTicks.GetValueOrDefault(name)+ticks;
    }
    public long AdmissionTicks, SphereTicks, CastTicks;
    public int Queries, Roots, Candidates, Samples, Casts;
    public long SlowestSphereTicks, SlowestCastTicks;
    public string SlowestSphere = "", SlowestCast = "";
    public void Reset()
    {
        AdmissionTicks=SphereTicks=CastTicks=SlowestSphereTicks=SlowestCastTicks=0;
        Queries=Roots=Candidates=Samples=Casts=0;SlowestSphere=SlowestCast="";
        MeshCasts=TriangleTests=TreeNodes=LiteralCasts=CachedMeshQueries=0;MeshTicks=0;_meshTicks.Clear();
    }
    public override string ToString() =>
        $"visibility queries {Queries} roots {Roots} candidates {Candidates} samples {Samples} casts {Casts}; CPU admission {Ms(AdmissionTicks):0.00}ms (sphere {Ms(SphereTicks):0.00}ms, slowest {SlowestSphere}/{Ms(SlowestSphereTicks):0.00}ms), cast {Ms(CastTicks):0.00}ms (slowest {SlowestCast}/{Ms(SlowestCastTicks):0.00}ms); mesh {MeshCasts}/{Ms(MeshTicks):0.00}ms triangles {TriangleTests} nodes {TreeNodes} literal {LiteralCasts} cached {CachedMeshQueries} top [{string.Join("; ",_meshTicks.OrderByDescending(p=>p.Value).Take(3).Select(p=>$"{p.Key}/{Ms(p.Value):0.00}ms"))}]";
    private static double Ms(long ticks)=>ticks*1000d/Stopwatch.Frequency;
}

/// <summary>0075DEE0 scene collision provider; 0075DD00/009D3D40 samples.
/// Halo depth testing is a separate render consumer.</summary>
public static class EnbRetailDazzleVisibility
{
    private static readonly Vector3[] samples = CreateSamples();

    // 0096EC80 resolves the dazzle's owning scene through its parents.
    // 00966010 casts one center ray against that scene, with mask 4. The
    // caller supplies collision leaves configured for that mask in iterator
    // order; neither viewport clipping nor world-provider admission applies.
    public static float EvaluateScene(Vector3 camera, Vector3 center,
        IReadOnlyList<EnbDazzleRayCast> scene)
    {
        (float Fraction, bool StartInside) hit = (1, false);
        foreach (var cast in scene)
            hit = cast(camera, center, hit.Fraction, hit.StartInside);
        return hit.Fraction == 1 ? 1 : 0;
    }

    public static float Evaluate(Vector3 center, float radius, float orbitalVisibility,
        EnbResolvedRenderCamera camera, IReadOnlyList<EnbDazzleCollisionRoot> scene, EnbDazzleVisibilityProfile? profile=null, EnbDazzleQueryStorage? storage=null)
    {
        if (orbitalVisibility == 0) return 0;
        long admissionStart=profile is null?0:Stopwatch.GetTimestamp();
        if(profile is not null){profile.Queries++;profile.Roots+=scene.Count;}
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 delta = center - camera.Position;
        float square = (float)((R)delta.Y * delta.Y + (R)delta.Z * delta.Z + (R)delta.X * delta.X);
        if (square == 0) return 0;
        float ratio = (float)((R)radius * radius / square);
        float sine = (float)R.Sqrt(ratio), cosine = (float)R.Sqrt((R)1 - ratio);
        R inverse = EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
        delta = new((float)((R)delta.X * inverse), (float)((R)delta.Y * inverse), (float)((R)delta.Z * inverse));
        using var query=(storage??new EnbDazzleQueryStorage()).Acquire();
        var candidates=query.Candidates;
        for(int rootIndex=0;rootIndex<scene.Count;rootIndex++)
        {
            var root=scene[rootIndex];
            if (root.IsTerrain) { candidates.Add((root, 0)); continue; }
            if (!root.SceneVisible) continue;
            long sphereStart=profile is null?0:Stopwatch.GetTimestamp();
            Vector4? resolved = root.ResolveSphere is {} resolve ? resolve() : root.Sphere;
            if(profile is not null)
            {
                long elapsed=Stopwatch.GetTimestamp()-sphereStart;profile.SphereTicks+=elapsed;
                if(elapsed>profile.SlowestSphereTicks){profile.SlowestSphereTicks=elapsed;profile.SlowestSphere=root.DiagnosticName;}
            }
            if (resolved is not {} sphere) continue;
            Vector3 offset = new Vector3(sphere.X, sphere.Y, sphere.Z) - camera.Position;
            float r2 = (float)((R)sphere.W * sphere.W);
            float d2 = (float)((R)offset.Z * offset.Z + (R)offset.Y * offset.Y + (R)offset.X * offset.X);
            if (r2 < d2)
            {
                float dot = (float)((R)offset.Z * delta.Z + (R)offset.Y * delta.Y + (R)offset.X * delta.X);
                if ((R)dot * dot < (R)d2 * ratio || dot <= 0)
                {
                    float lateral = (float)R.Sqrt(MathF.Abs((float)((R)d2 - (R)dot * dot)));
                    R f = (R)sine * dot + (R)lateral * cosine;
                    if (f * f - (R)r2 * d2 < 0 || f < 0) continue;
                }
            }
            candidates.Add((root, d2));
        }
        // An empty native candidate vector bypasses sample projection entirely.
        if(profile is not null){profile.AdmissionTicks+=Stopwatch.GetTimestamp()-admissionStart;profile.Candidates+=candidates.Count;}
        if (candidates.Count == 0) return orbitalVisibility;
        Sort(candidates, 0, candidates.Count);
        for (int i = 1; i < candidates.Count; i++)
        {
            var value = candidates[i]; int j = i;
            while (j > 0 && value.Distance < candidates[j - 1].Distance)
            { candidates[j] = candidates[j - 1]; j--; }
            candidates[j] = value;
        }
        var projection = query.Projection(camera);
        int visible = 0;
        foreach (var unit in samples)
        {
            Vector3 sample = new((float)((R)radius * unit.X + center.X),
                (float)((R)radius * unit.Y + center.Y), (float)((R)radius * unit.Z + center.Z));
            if (!projection.Contains(sample)) continue;
            if(profile is not null)profile.Samples++;
            (float Fraction, bool StartInside) hit = (1, false);
            foreach (var candidate in candidates)
            {
                long castStart=profile is null?0:Stopwatch.GetTimestamp();
                if (candidate.Root.PrepareCast is {} prepare)
                {
                    // Scene preparation used the normal arithmetic domain before
                    // entering this provider. Keep it separate from its PC=00 rays.
                    using var preparation = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Double53);
                    prepare();
                }
                hit = candidate.Root.Cast(camera.Position, sample, hit.Fraction, hit.StartInside);
                if(profile is not null)
                {
                    long elapsed=Stopwatch.GetTimestamp()-castStart;profile.CastTicks+=elapsed;profile.Casts++;
                    if(elapsed>profile.SlowestCastTicks){profile.SlowestCastTicks=elapsed;profile.SlowestCast=candidate.Root.DiagnosticName;}
                }
                if (hit.Fraction < 1) break;
            }
            if (!(hit.Fraction < 1)) visible++;
        }
        return (float)((R)visible / 16 * orbitalVisibility);
    }

    private static Vector3[] CreateSamples()
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var result = new Vector3[16];
        for (int i = 0; i < result.Length; i++)
        {
            float u = Halton(i, 2), v = Halton(i, 3);
            float angle = (float)((R)u * (2 * MathF.PI));
            float radial = (float)R.Sqrt((float)(((R)1 - v) * v));
            radial = (float)((R)radial + radial);
            result[i] = new((float)((R)(float)EnbRetailExtendedX87Register.Cosine(angle) * radial),
                (float)((R)(float)EnbRetailExtendedX87Register.Sine(angle) * radial), (float)((R)1 - ((R)v + v)));
        }
        return result;
    }

    private static float Halton(int index, int basis)
    {
        float sum = 0, fraction = 1;
        for (; index != 0; index /= basis)
        { fraction = (float)((R)fraction / basis); sum = (float)((R)sum + (R)(index % basis) * fraction); }
        return sum;
    }

    // 0075EA60 partitions only ranges larger than sixteen, then 0075EC20
    // insertion-sorts. Preserve equal-distance swaps: StartInside is shared.
    private static void Sort(List<(EnbDazzleCollisionRoot Root, float Distance)> values, int first, int end)
    {
        while (end - first > 16)
        {
            float a = values[first].Distance, b = values[first + (end - first) / 2].Distance, c = values[end - 1].Distance;
            float pivot = b <= a ? c <= a ? b < c ? c : b : a : b < c ? b : c <= a ? a : c;
            int left = first, right = end;
            while (true)
            {
                while (values[left].Distance < pivot) left++;
                do { right--; } while (pivot < values[right].Distance);
                if (right <= left) break;
                (values[left], values[right]) = (values[right], values[left]); left++;
            }
            if (left - first < end - left) { Sort(values, first, left); first = left; }
            else { Sort(values, left, end); end = left; }
        }
    }
}
