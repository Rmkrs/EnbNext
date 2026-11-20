using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>008D23C0: direct x87 interpolation, including its float spills and
/// near-equal linear branch. Inputs and output are not normalized.</summary>
public static class EnbRetailDirectQuaternionInterpolation
{
    //008D1C90 mutates the desired quaternion before799610's epsilon test.
    public static Quaternion AlignTo(Quaternion previous,Quaternion desired,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R dot=(R)previous.W*desired.W+(R)previous.Z*desired.Z+(R)previous.Y*desired.Y+(R)previous.X*desired.X;
        return (R.CompareFloatMemory(dot,0)&0x100)!=0
            ?new(-desired.X,-desired.Y,-desired.Z,-desired.W):desired;
    }
    public static Quaternion Interpolate(Quaternion first,Quaternion second,float fraction,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        R dot=(R)first.X*second.X+(R)first.Z*second.Z+(R)first.Y*second.Y+(R)second.W*first.W;
        float storedDot=(float)dot;
        bool negate=(R.CompareFloatMemory(dot,0)&0x100)!=0;
        if(negate)storedDot=-storedDot;
        R separation=(R)1-storedDot;
        R oldWeight,newWeight=fraction;
        if((R.CompareFloatMemory(separation,0x322bcc76)&0x100)==0)
        {
            //A26B58..66: PC is retained, RC becomes nearest. This branch's
            //finite absolute dot is strictly below one; no CRT domain path.
            R x=storedDot;
            R root=R.Sqrt(((R)1+x)*((R)1-x));
            float angle=(float)R.EvaluateControlled(33,x,root,control);
            float sine=(float)R.Sine(angle);
            float reciprocal=(float)((R)1/sine);
            float portion=(float)((R)angle*fraction);
            float remainder=(float)((R)angle-portion);
            oldWeight=(float)((R)(float)R.Sine(remainder)*reciprocal);
            newWeight=(R)(float)R.Sine(portion)*reciprocal;
        }
        else oldWeight=(R)1-fraction;
        if(negate)newWeight=-newWeight;
        return new((float)(oldWeight*first.X+newWeight*second.X),
            (float)(oldWeight*first.Y+newWeight*second.Y),
            (float)(oldWeight*first.Z+newWeight*second.Z),
            (float)(newWeight*second.W+oldWeight*first.W));
    }
}
