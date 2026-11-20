using System.Numerics;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailLineJoin
{

    internal static bool IsMergeEligibleRaw(Vector3 aRay, bool aFlag34, Vector3 bRay, bool bFlag34,
        Vector3 bMainPlane, Vector3 bEdgePlane, Vector3 aMainPlane, Vector3 previousOutputEdgePlane)
    {
        // x87 keeps the dot products extended through strict comparison with zero.
        static NativeReal Dot(Vector3 a,Vector3 b)=>(NativeReal)a.Z*b.Z+(NativeReal)a.Y*b.Y+(NativeReal)a.X*b.X;
        return (!bFlag34 && Dot(aRay,bMainPlane)>0 && Dot(aRay,bEdgePlane)>0) ||
            (!aFlag34 && Dot(bRay,-aMainPlane)>0 && Dot(bRay,previousOutputEdgePlane)>0);
    }

    internal static bool WithinMergeAbortRaw(Vector3 candidateRay, Vector3 firstCenter, Vector3 secondCenter, float halfWidth, float factor)
    {
        if(!(factor>0))return true;
        NativeReal limit=(NativeReal)halfWidth*factor;limit*=limit;
        NativeReal DistanceSquared(Vector3 center)
        {
            NativeReal dot=(NativeReal)candidateRay.Z*center.Z+(NativeReal)candidateRay.Y*center.Y+(NativeReal)candidateRay.X*center.X;
            // X/Y projection products spill to float; Z stays extended through
            // subtraction (009C11FA..1220 and 009C1261..1287).
            NativeReal x=(NativeReal)center.X-(float)(dot*candidateRay.X);
            NativeReal y=(NativeReal)center.Y-(float)(dot*candidateRay.Y);
            NativeReal z=center.Z-dot*candidateRay.Z;
            return x*x+y*y+z*z;
        }
        //009C1243/12AC rejects ordered greater only. Native arithmetic can
        // produce unordered candidates even from finite incoming stack bytes.
        return !(DistanceSquared(firstCenter)>limit) && !(DistanceSquared(secondCenter)>limit);
    }
}
