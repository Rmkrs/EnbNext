using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>009C3356..337A and009C3486..34AE, incoming PC24/nearest-even.
/// The raw offset is deliberately retained rather than rounded to one half.</summary>
public static class EnbCurrentRetailClassicAnimationArithmetic
{
    public static (int Current,int Next,float Fraction) SelectFrames(float frame,uint frameCount)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float shifted=(float)((R)frame-BitConverter.UInt32BitsToSingle(0x3effffde));
        double integer=Math.Round(shifted,MidpointRounding.ToEven);
        int current=!double.IsFinite(integer)||integer<int.MinValue||integer>int.MaxValue?int.MinValue:(int)integer;
        int next=unchecked(current+1);
        if(next>=unchecked((int)frameCount))next=0;
        float fraction=(float)((R)frame-current);
        return(current,next,fraction);
    }

    public static float InterpolateScalar(float first,float second,float fraction)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        return(float)(((R)second-first)*fraction+first);
    }
}
