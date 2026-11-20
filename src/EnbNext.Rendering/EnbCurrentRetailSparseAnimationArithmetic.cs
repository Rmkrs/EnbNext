using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbCurrentRetailSparseAnimationArithmetic
{

    /// <summary>Whole009C6F50 uses signed frame comparison, independently of
    /// the channel search's unsigned integer frame.</summary>
    public static int SelectVisibilityKey(IReadOnlyList<uint> times,int frame,ref int cached)
    {
        int candidate=(int)(times[cached]&0x7fffffffu)<=frame?cached+1:0;
        while(candidate<times.Count&&frame>=(int)(times[candidate]&0x7fffffffu))candidate++;
        return cached=Math.Max(0,candidate-1);
    }

    public static float Fraction(float frame,uint current,uint next)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R first=(double)(current&0x7fffffffu),second=(double)(next&0x7fffffffu);
        return(float)(((R)frame-first)/(second-first));
    }

    public static float InterpolateScalar(float first,float second,float fraction)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        return(float)(((R)second-first)*fraction+first);
    }
}
