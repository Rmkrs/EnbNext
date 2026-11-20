using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>008D2030 over the owner's initialized trigonometric tables and
/// retained inverse-cosine consumer. It does not manufacture table contents.</summary>
public static class EnbCurrentRetailQuaternionBlendRuntime
{
    // Literal integer sequence at008D20F0..2144, repeated for sine lookups.
    private static int LookupIndex(float value)
    {
        uint raw=BitConverter.SingleToUInt32Bits(value),absolute=raw&0x7fffffff;
        int exponent=((int)absolute>>23)-127,shift=31-exponent,sign=(int)raw>>31;
        uint nonnegativeExponent=unchecked((uint)~(exponent>>31));
        uint fraction=absolute&0x7fffff;
        uint exact=(((1u<<(shift&31))-1)&(fraction<<8))==0?1u:0u;
        uint correction=exact&unchecked((uint)((((int)absolute-1)>>31)^(int)nonnegativeExponent))&unchecked((uint)sign);
        uint integral=((fraction|0xff800000u)<<8)>>(shift&31);
        return unchecked((int)(correction+((integral&nonnegativeExponent)^unchecked((uint)sign))));
    }

    private static R Sine(float scaledAngle,Func<int,float> sine)
    {
        int index=LookupIndex(scaledAngle);
        R fraction=(R)scaledAngle-index;
        return ((R)1-fraction)*sine(index&1023)+fraction*sine(unchecked(index+1)&1023);
    }

    public static Quaternion Interpolate(Quaternion first,Quaternion second,float fraction,
        Func<int,float> retainedInverseCosineTable,Func<int,float> retainedSineTable,
        Func<double,double> retainedInverseCosine)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R liveDot=(((R)first.X*second.X+(R)first.Z*second.Z)+(R)first.Y*second.Y)+(R)second.W*first.W;
        float dot=(float)liveDot;
        bool negateSecond=liveDot<0||double.IsNaN((double)liveDot);
        if(negateSecond)dot=-dot;
        R firstWeight;
        float secondWeight=fraction;
        R separation=(R)1-dot;
        if(separation<BitConverter.UInt32BitsToSingle(0x322bcc76)||double.IsNaN((double)separation))
            firstWeight=(R)1-fraction;
        else
        {
            R angle;
            if(MathF.Abs(dot)<=BitConverter.UInt32BitsToSingle(0x3f79999a)||float.IsNaN(dot))
            {
                float scaled=(float)((R)dot*512);
                int index=LookupIndex(scaled);
                R local=(R)scaled-index;
                angle=((R)1-local)*retainedInverseCosineTable(index)+local*retainedInverseCosineTable(unchecked(index+1));
            }
            else angle=retainedInverseCosine(dot);
            float scale=BitConverter.UInt32BitsToSingle(0x4322f983);
            float inverseSine=(float)((R)1/Sine((float)(angle*scale),retainedSineTable));
            float portion=(float)(angle*fraction);
            firstWeight=Sine((float)((angle-portion)*scale),retainedSineTable)*inverseSine;
            secondWeight=(float)(Sine((float)((R)portion*scale),retainedSineTable)*inverseSine);
        }
        R signedSecond=negateSecond?-(R)secondWeight:secondWeight;
        return new((float)(firstWeight*first.X+signedSecond*second.X),
            (float)(firstWeight*first.Y+signedSecond*second.Y),
            (float)(firstWeight*first.Z+signedSecond*second.Z),
            (float)(signedSecond*second.W+firstWeight*first.W));
    }
}
