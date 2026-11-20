using System.Numerics;

namespace EnbNext.Rendering;

// Original acceleration of the existing PC24 rejection predicate, not a new
// collision tree. Retained model data is packed once; accepted lanes still run
// the unchanged scalar triangle test in the original order. No RNG is used.
internal sealed class EnbRetailTrianglePlaneBlocks
{
    private readonly record struct Block(Vector<float> X,Vector<float> Y,Vector<float> Z,
        Vector<float> Plane,Vector<int> Eligible,Vector<int> PlaneEligible,Vector<int> UisY,Vector<int> VisY,
        Vector<float> Au,Vector<float> Av,Vector<float> Bu,Vector<float> Bv,Vector<float> Cu,Vector<float> Cv,
        Vector<float> ABu,Vector<float> ABv,Vector<float> BCu,Vector<float> BCv,Vector<float> CAu,Vector<float> CAv);
    private readonly Block[] _blocks;
    internal int Count=>_blocks.Length;
    internal static int Width=>Vector<float>.Count;
    internal EnbRetailTrianglePlaneBlocks(ReadOnlySpan<EnbRetailDazzleCollision.Triangle> triangles)
    {
        _blocks=new Block[(triangles.Length+Width-1)/Width];
        Span<float> x=stackalloc float[Width],y=stackalloc float[Width],z=stackalloc float[Width],plane=stackalloc float[Width];
        Span<int> eligible=stackalloc int[Width],planeEligible=stackalloc int[Width];
        Span<int> uisY=stackalloc int[Width],visY=stackalloc int[Width];
        Span<float> au=stackalloc float[Width],av=stackalloc float[Width],bu=stackalloc float[Width],bv=stackalloc float[Width],cu=stackalloc float[Width],cv=stackalloc float[Width];
        Span<float> abu=stackalloc float[Width],abv=stackalloc float[Width],bcu=stackalloc float[Width],bcv=stackalloc float[Width],cau=stackalloc float[Width],cav=stackalloc float[Width];
        for(int block=0;block<_blocks.Length;block++)
        {
            x.Clear();y.Clear();z.Clear();plane.Clear();eligible.Clear();planeEligible.Clear();uisY.Clear();visY.Clear();
            au.Clear();av.Clear();bu.Clear();bv.Clear();cu.Clear();cv.Clear();abu.Clear();abv.Clear();bcu.Clear();bcv.Clear();cau.Clear();cav.Clear();
            for(int lane=0;lane<Width && block*Width+lane<triangles.Length;lane++)
            {
                bool safe=triangles[block*Width+lane].TryGetSinglePlane(out var normal,out var offset);
                x[lane]=normal.X;y[lane]=normal.Y;z[lane]=normal.Z;plane[lane]=offset;
                eligible[lane]=safe?-1:0;planeEligible[lane]=triangles[block*Width+lane].PlaneRangeSafe?-1:0;
                var abs=Vector3.Abs(normal);int axis=abs.X<abs.Y?1:0;if(MathF.Max(abs.X,abs.Y)<abs.Z)axis=2;
                int u=axis==0?1:0,v=axis==2?1:2;uisY[lane]=u==1?-1:0;visY[lane]=v==1?-1:0;
                var (a,b,c)=triangles[block*Width+lane].Vertices;
                au[lane]=a[u];av[lane]=a[v];bu[lane]=b[u];bv[lane]=b[v];cu[lane]=c[u];cv[lane]=c[v];
                abu[lane]=b[u]-a[u];abv[lane]=b[v]-a[v];bcu[lane]=c[u]-b[u];bcv[lane]=c[v]-b[v];cau[lane]=a[u]-c[u];cav[lane]=a[v]-c[v];
            }
            _blocks[block]=new(new(x),new(y),new(z),new(plane),new(eligible),new(planeEligible),new(uisY),new(visY),
                new(au),new(av),new(bu),new(bv),new(cu),new(cv),new(abu),new(abv),new(bcu),new(bcv),new(cau),new(cav));
        }
    }
    internal Vector<int> Candidates(int index,Vector3 origin,Vector3 delta,bool expandedPlaneRange=false,bool edgesSafe=true)
    {
        ref readonly var b=ref _blocks[index];
        // Preserve the scalar Z,Y,X operation/rounding order. Do not use Dot,
        // FusedMultiplyAdd, reciprocal estimates or a near-parallel epsilon.
        var denominator=Vector.Add(Vector.Add(Vector.Multiply(b.Z,new Vector<float>(delta.Z)),
            Vector.Multiply(b.Y,new Vector<float>(delta.Y))),Vector.Multiply(b.X,new Vector<float>(delta.X)));
        var distance=Vector.Add(Vector.Add(Vector.Multiply(b.Z,new Vector<float>(origin.Z)),
            Vector.Multiply(b.Y,new Vector<float>(origin.Y))),Vector.Multiply(b.X,new Vector<float>(origin.X)));
        var numerator=Vector.Negate(Vector.Subtract(distance,b.Plane));
        var t=Vector.Divide(numerator,denominator);
        var candidates=~Vector.Equals(denominator,Vector<float>.Zero) &
            Vector.GreaterThanOrEqual(t,Vector<float>.Zero) & Vector.LessThanOrEqual(t,Vector<float>.One);
        // With positions/rays >=2^-50 and normals >=2^-30, rounded dot
        // products and their cancellation have quantum >=2^-103. The upper
        // bound is below2^102. They stay normal binary32 or exact zero even
        // when the stricter edge proof fails. Surviving lanes still refine
        // through the unchanged scalar/literal predicate.
        var planeCandidates=candidates | ~(expandedPlaneRange?b.PlaneEligible:b.Eligible);
        if(!edgesSafe)return planeCandidates;
        if(Vector.EqualsAll(candidates & b.Eligible,Vector<int>.Zero))return ~b.Eligible & planeCandidates;
        // Tiny nonzero fractions require the literal extended-exponent path.
        // They remain candidates even when binary32 projected edges disagree.
        var safePoint=Vector.GreaterThanOrEqual(t,new Vector<float>(1f/1048576f)) |
            (Vector.Equals(t,Vector<float>.Zero)&Vector.Equals(numerator,Vector<float>.Zero));
        var pointU=Vector.Add(Vector.Multiply(t,Vector.ConditionalSelect(b.UisY,new Vector<float>(delta.Y),new Vector<float>(delta.X))),
            Vector.ConditionalSelect(b.UisY,new Vector<float>(origin.Y),new Vector<float>(origin.X)));
        var pointV=Vector.Add(Vector.Multiply(t,Vector.ConditionalSelect(b.VisY,new Vector<float>(delta.Y),new Vector<float>(delta.Z))),
            Vector.ConditionalSelect(b.VisY,new Vector<float>(origin.Y),new Vector<float>(origin.Z)));
        var ab=Side(pointU,pointV,b.Au,b.Av,b.ABu,b.ABv);
        var bc=Side(pointU,pointV,b.Bu,b.Bv,b.BCu,b.BCv);
        var ca=Side(pointU,pointV,b.Cu,b.Cv,b.CAu,b.CAv);
        var inside=Vector.Equals(ab,bc)&Vector.Equals(ab,ca);
        // Inputs outside the existing proof always reach the literal/scalar
        // path. Accepted lanes are still refined in original triangle order.
        return ((candidates & (inside | ~safePoint)) | ~b.Eligible) & planeCandidates;
    }
    private static Vector<int> Side(Vector<float> u,Vector<float> v,Vector<float> aU,Vector<float> aV,Vector<float> deltaU,Vector<float> deltaV)
    {
        var edge=Vector.Subtract(Vector.Multiply(Vector.Subtract(v,aV),deltaU),Vector.Multiply(Vector.Subtract(u,aU),deltaV));
        return Vector.GreaterThanOrEqual(edge,Vector<float>.Zero);
    }
}
