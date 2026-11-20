using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Collision leaves used by 0075DEE0's mask-16 visibility queries.</summary>
public static class EnbRetailDazzleCollision
{
    // Offline same-build comparison; never enabled by the application.
    internal static bool UseLiteralTriangleOracle { get; set; }
    internal static bool UsePreviousNormalRange { get; set; }
    internal static bool SingleTriangleRange(Vector3 v)=>SingleScalar(v.X)&&SingleScalar(v.Y)&&SingleScalar(v.Z);
    private static bool SingleScalar(float v)=>v==0 || MathF.Abs(v)>=
        (UsePreviousNormalRange?1f/1024f:1f/262144f) && MathF.Abs(v)<=1125899906842624f;
    // Plane dots have a weaker range requirement than projected edge tests.
    internal static bool SinglePlaneRange(Vector3 v)=>PlaneScalar(v.X)&&PlaneScalar(v.Y)&&PlaneScalar(v.Z);
    private static bool PlaneScalar(float v)=>v==0 || MathF.Abs(v) is >= 8.881784197001252e-16f and <= 1125899906842624f;
    internal readonly struct Triangle
    {
        private readonly Vector3 a,b,c,normal;
        private readonly float plane;
        private readonly bool single;
        private readonly RetainedLiteralTriangle? literal;
        public Triangle(Vector3 a,Vector3 b,Vector3 c,Vector3 normal,bool retainLiteral=false)
        {
            this.a=a;this.b=b;this.c=c;this.normal=normal;
            single=SingleTriangleRange(a)&&SingleTriangleRange(b)&&SingleTriangleRange(c)&&
                (UsePreviousNormalRange?SingleTriangleRange(normal):NormalRange(normal));
            // This plane is consumed only inside the guarded PC24 path.
            plane=a.Y*normal.Y+a.Z*normal.Z+a.X*normal.X;
            literal=retainLiteral?new(a,b,c,normal):null;
        }
        // Normals participate only in plane/direction dot products, not the
        // projected edge tests. With position inputs >=2^-18 these products
        // are >=2^-48; their PC24 sums/cancellation remain multiples of 2^-71.
        // Near-axis authored normals need not take the extended-exponent path.
        private static bool NormalRange(Vector3 v)=>NormalScalar(v.X)&&NormalScalar(v.Y)&&NormalScalar(v.Z);
        private static bool NormalScalar(float v)=>v==0 || MathF.Abs(v) is >= (1f/1073741824f) and <= 1125899906842624f;
        internal bool PlaneRangeSafe=>SinglePlaneRange(a)&&SinglePlaneRange(b)&&SinglePlaneRange(c)&&NormalRange(normal);
        internal (Vector3 A,Vector3 B,Vector3 C) Vertices => (a,b,c);
        internal Vector3 Normal => normal;
        internal bool TryGetSinglePlane(out Vector3 value,out float offset)
        { value=normal;offset=plane;return single; }
        public bool Intersect(Vector3 origin,Vector3 delta,bool singleRay,ref float fraction)
        {
            if(!UseLiteralTriangleOracle&&single&&singleRay&&TryIntersectTriangleSingle(origin,delta,a,b,c,normal,plane,ref fraction,out bool hit))return hit;
            if(literal is not null && !UseLiteralTriangleOracle)return literal.Intersect(origin,delta,ref fraction);
            return IntersectTriangleLiteral(origin,delta,a,b,c,normal,ref fraction);
        }
    }
    // 008C91E0/008E1730: model-only PC24 operands retain their register
    // precision. No query result, changing ray, or fraction is retained.
    private sealed class RetainedLiteralTriangle
    {
        private readonly Vector3 a,b,c,normal;
        private readonly R plane,abu,abv,bcu,bcv,cau,cav;
        private readonly int u,v;
        internal RetainedLiteralTriangle(Vector3 a,Vector3 b,Vector3 c,Vector3 normal)
        {
            this.a=a;this.b=b;this.c=c;this.normal=normal;
            using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            plane=(R)a.Y*normal.Y+(R)a.Z*normal.Z+(R)a.X*normal.X;
            var absolute=Vector3.Abs(normal);int axis=absolute.X<absolute.Y?1:0;
            if(MathF.Max(absolute.X,absolute.Y)<absolute.Z)axis=2;
            u=axis==0?1:0;v=axis==2?1:2;
            abu=(R)b[u]-a[u];abv=(R)b[v]-a[v];
            bcu=(R)c[u]-b[u];bcv=(R)c[v]-b[v];
            cau=(R)a[u]-c[u];cav=(R)a[v]-c[v];
        }
        internal bool Intersect(Vector3 origin,Vector3 delta,ref float fraction)
        {
            using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            R denominator=(R)normal.Z*delta.Z+(R)normal.Y*delta.Y+(R)normal.X*delta.X;
            if(denominator==0)return false;
            R distance=(R)normal.Z*origin.Z+(R)normal.Y*origin.Y+(R)normal.X*origin.X;
            float t=(float)(-(distance-plane)/denominator);
            if(!(t>=0&&t<=1))return false;
            Vector3 point=new((float)((R)t*delta.X+origin.X),
                (float)((R)(float)((R)t*delta.Y)+origin.Y),
                (float)((R)(float)((R)t*delta.Z)+origin.Z));
            bool Side(Vector3 first,R du,R dv)=>(R)0<=((R)point[v]-first[v])*du-((R)point[u]-first[u])*dv;
            bool first=Side(a,abu,abv);
            if(first!=Side(b,bcu,bcv)||first!=Side(c,cau,cav)||!(t<fraction))return false;
            fraction=t;return true;
        }
    }

    //008E2450: endpoint containment, directed axis separation, then the
    //three cross-product axes. The authored tree consumes only result==1.
    internal static uint ClassifyRayBounds(Vector3 center,Vector3 extent,Vector3 start,Vector3 end,Vector3 delta,Vector3 normalized)
    {
        // These finite binary32 inputs bound every intermediate to normal
        // binary32 (or exact zero): differences need at most 24 extra bits,
        // products at most 48. Native float operations then implement PC24
        // exactly, including each separate rounding; no FMA is used.
        if(Safe(center)&&Safe(extent)&&Safe(start)&&Safe(end)&&Safe(delta)&&Safe(normalized))
            return ClassifyRayBoundsSingle(center,extent,start,end,delta,normalized);
        return ClassifyRayBoundsLiteral(center,extent,start,end,delta,normalized);
        static bool Safe(Vector3 v)=>Scalar(v.X)&&Scalar(v.Y)&&Scalar(v.Z);
        static bool Scalar(float v)=>v==0 || MathF.Abs(v) is >= (1f/1073741824f) and <= 1073741824f;
    }

    private static uint ClassifyRayBoundsSingle(Vector3 center,Vector3 extent,Vector3 start,Vector3 end,Vector3 delta,Vector3 normalized)
    {
        bool Inside(Vector3 point)=>!(MathF.Abs(point.X-center.X)>extent.X)&&
            !(MathF.Abs(point.Y-center.Y)>extent.Y)&&!(MathF.Abs(point.Z-center.Z)>extent.Z);
        int contained=(Inside(start)?1:0)+(Inside(end)?1:0);
        if(contained!=0)return contained==2?2u:8u;
        Vector3 offset=start-center;
        for(int axis=0;axis<3;axis++)
        {
            float movement=delta[axis];
            if(!(offset[axis]>0))
            {
                if(!(movement>0))movement=0;
                if(!(movement+extent[axis]>=-offset[axis]))return 1;
            }
            else
            {
                if(!(movement<0))movement=0;
                if(offset[axis]>extent[axis]-movement)return 1;
            }
        }
        if(!(MathF.Abs(normalized.Y*extent.Z)+MathF.Abs(-normalized.Z*extent.Y)>=
            MathF.Abs(normalized.Y*offset.Z+-normalized.Z*offset.Y)))return 1;
        if(!(MathF.Abs(-normalized.X*extent.Z)+MathF.Abs(normalized.Z*extent.X)>=
            MathF.Abs(-normalized.X*offset.Z+normalized.Z*offset.X)))return 1;
        return MathF.Abs(normalized.X*extent.Y)+MathF.Abs(-normalized.Y*extent.X)>=
            MathF.Abs(normalized.X*offset.Y+-normalized.Y*offset.X)?8u:1u;
    }

    internal static uint ClassifyRayBoundsLiteral(Vector3 center,Vector3 extent,Vector3 start,Vector3 end,Vector3 delta,Vector3 normalized)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        static float Abs(R value)=>MathF.Abs((float)value);
        bool Inside(Vector3 point)=>!(Abs((R)point.X-center.X)>extent.X)&&
            !(Abs((R)point.Y-center.Y)>extent.Y)&&!(Abs((R)point.Z-center.Z)>extent.Z);
        int contained=(Inside(start)?1:0)+(Inside(end)?1:0);
        if(contained!=0)return contained==2?2u:8u;
        Vector3 offset=new((float)((R)start.X-center.X),(float)((R)start.Y-center.Y),(float)((R)start.Z-center.Z));
        for(int axis=0;axis<3;axis++)
        {
            float movement=delta[axis];
            if(!(offset[axis]>0))
            {
                if(!(movement>0))movement=0;
                if(!((R)movement+extent[axis]>=-(R)offset[axis]))return 1;
            }
            else
            {
                if(!(movement<0))movement=0;
                if(offset[axis]>(R)extent[axis]-movement)return 1;
            }
        }
        if(!((R)Abs((R)normalized.Y*extent.Z)+Abs(-(R)normalized.Z*extent.Y)>=
            Abs((R)normalized.Y*offset.Z+-(R)normalized.Z*offset.Y)))return 1;
        if(!((R)Abs(-(R)normalized.X*extent.Z)+Abs((R)normalized.Z*extent.X)>=
            Abs(-(R)normalized.X*offset.Z+(R)normalized.Z*offset.X)))return 1;
        return (R)Abs((R)normalized.X*extent.Y)+Abs(-(R)normalized.Y*extent.X)>=
            Abs((R)normalized.X*offset.Y+-(R)normalized.Y*offset.X)?8u:1u;
    }

    //00940380 -> +1A4. A direct mesh cloned by48B220's unchanged
    //scale/HSV branch retains this loader mask: it does not run48E9D0.
    public static uint LoadedMeshMask(EnbW3dRenderMesh mesh)
        => ((((mesh.Attributes >> 4) & 255) << 1) & 254) | 1;

    // 00940380 followed by configured-model postprocessing 0048E9D0.
    public static uint ConfiguredMeshMask(EnbW3dRenderMesh mesh)
    {
        uint mask = LoadedMeshMask(mesh);
        bool sorted=mesh.GeometrySort.Requested;
        if(!sorted)
            for(int i=0;i<mesh.MaterialPasses.Count;i++)
            {
                var pass=mesh.MaterialPasses[i];
                if(pass.Ordinal!=0)continue;
                for(int shader=0;shader<pass.Shaders.Count;shader++)
                    if((pass.Shaders[shader].FixedFunction.PackedRetailState & 0x400e0)!=0){sorted=true;break;}
                break;
            }
        return !sorted && (mask & 6) == 0 ? 17 : mask | 1;
    }

    // PC24 with a finite normal-range proof. Position inputs span 2^-18..2^50,
    // normal components span 2^-30..2^50;
    // accepted t spans 2^-20..1 (or exact zero). All rounded products,
    // differences and edge tests remain normal binary32 or exact zero. The
    // smallest point/edge quantum is 2^-61 / 2^-41; a rounded edge product
    // and subsequent cancellation remain multiples of 2^-125 (normal).
    // Tiny t and exceptional/out-of-range operands retain the literal path.
    private static bool TryIntersectTriangleSingle(Vector3 origin,Vector3 delta,Vector3 a,
        Vector3 b,Vector3 c,Vector3 normal,float plane,ref float fraction,out bool hit)
    {
        hit=false;
        float denominator=normal.Z*delta.Z+normal.Y*delta.Y+normal.X*delta.X;
        if(denominator==0)return true;
        float distance=normal.Z*origin.Z+normal.Y*origin.Y+normal.X*origin.X;
        float numerator=-(distance-plane),t=numerator/denominator;
        if(!(t>=0&&t<=1))return true;
        if(t<(1f/1048576f) && (t!=0 || numerator!=0))return false;
        Vector3 point=new(t*delta.X+origin.X,t*delta.Y+origin.Y,t*delta.Z+origin.Z);
        Vector3 absolute=Vector3.Abs(normal);
        int axis=absolute.X<absolute.Y?1:0;
        if(MathF.Max(absolute.X,absolute.Y)<absolute.Z)axis=2;
        int u=axis==0?1:0,v=axis==2?1:2;
        bool Side(Vector3 first,Vector3 second)=>0 <=
            (point[v]-first[v])*(second[u]-first[u])-(point[u]-first[u])*(second[v]-first[v]);
        bool first=Side(a,b);
        if(first!=Side(b,c)||first!=Side(c,a)||!(t<fraction))return true;
        fraction=t;hit=true;return true;
    }

    internal static bool IntersectTriangleLiteral(Vector3 origin, Vector3 delta, Vector3 a,
        Vector3 b, Vector3 c, Vector3 normal, ref float fraction)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R plane = (R)a.Y * normal.Y + (R)a.Z * normal.Z + (R)a.X * normal.X;
        R denominator = (R)normal.Z * delta.Z + (R)normal.Y * delta.Y + (R)normal.X * delta.X;
        if (denominator == 0) return false;
        R distance = (R)normal.Z * origin.Z + (R)normal.Y * origin.Y + (R)normal.X * origin.X;
        float t = (float)(-(distance - plane) / denominator);
        if (!(t >= 0 && t <= 1)) return false;
        Vector3 point = new((float)((R)t * delta.X + origin.X),
            (float)((R)(float)((R)t * delta.Y) + origin.Y),
            (float)((R)(float)((R)t * delta.Z) + origin.Z));
        Vector3 absolute = Vector3.Abs(normal);
        int axis = absolute.X < absolute.Y ? 1 : 0;
        if (MathF.Max(absolute.X, absolute.Y) < absolute.Z) axis = 2;
        int u = axis == 0 ? 1 : 0, v = axis == 2 ? 1 : 2;
        bool Side(Vector3 first, Vector3 second)
            => (R)0 <= ((R)point[v] - first[v]) * ((R)second[u] - first[u])
                - ((R)point[u] - first[u]) * ((R)second[v] - first[v]);
        bool first = Side(a, b);
        if (first != Side(b, c) || first != Side(c, a) || !(t < fraction)) return false;
        fraction = t;
        return true;
    }
}
