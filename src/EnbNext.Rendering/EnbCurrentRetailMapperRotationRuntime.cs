using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;
using X=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Production binding of whole00933C50 for admitted finite Mode08 programs.</summary>
public static class EnbCurrentRetailMapperRotationRuntime
{
    // 00933BA1 FMUL B10F9C, followed by the float store at00933BB1.
    public static float Rate(float speed)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (float)EnbRetailExtendedX87Register.MultiplyFloatMemory(
            EnbRetailExtendedX87Register.FromFloatBits(BitConverter.SingleToUInt32Bits(speed)),0x3bcde32e);
    }

    public static EnbW3dMapperRuntimeEvaluation Evaluate(EnbW3dMapperRotationParameters parameters,
        uint now,ref float angle,ref uint last,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        // 00933C50: finite CRT fmod, then a float angle and float trig stores.
        R advanced=(R)(double)unchecked(now-last)*parameters.RadiansPerMillisecond+angle;
        R wrapped=(double)advanced%BitConverter.UInt64BitsToDouble(0x401921fb60000000);
        if((double)wrapped<0)wrapped+=BitConverter.UInt32BitsToSingle(0x40c90fdb);
        float stored=(float)wrapped;
        X input=X.FromFloatBits(BitConverter.SingleToUInt32Bits(stored));
        R cosine=(float)X.Cosine(input),sine=(float)X.Sine(input);
        R u=-((cosine*parameters.Center.X-sine*parameters.Center.Y-parameters.Center.X)*parameters.Scale.X);
        R v=-((sine*parameters.Center.X+cosine*parameters.Center.Y-parameters.Center.Y)*parameters.Scale.Y);
        angle=stored;last=now;
        return new(new((float)(cosine*parameters.Scale.X),(float)(cosine*parameters.Scale.Y)),
            new((float)-(sine*parameters.Scale.X),(float)(sine*parameters.Scale.Y)),
            new((float)u,(float)v),new(1,0,0,1),false);
    }
}
