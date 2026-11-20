using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public interface IEnbCurrentRetailAnimationAggregateNormalizationProvider
{
    int Count { get; }
    int PivotCount(int entry);
    bool HasMask(int entry);
    bool HasAnimation(int entry);
    float Weight(int entry);
    void SetWeight(int entry,float value);
    float MaskWeight(int entry,int pivot);
    void RetainMask(int entry);
    void SetMaskWeight(int entry,int pivot,float value);
    void ReleaseMask(int entry);
}

/// <summary>Whole00931970 over explicit borrowed aggregate storage.</summary>
public static class EnbCurrentRetailAnimationAggregateNormalizer
{
    private static bool Reciprocal(R sum,out float reciprocal)
    {
        reciprocal=0;
        if(sum==0||double.IsNaN((double)sum))return false;
        float difference=(float)(sum-(R)1);
        difference=BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(difference)&0x7fffffffu);
        if(!(difference>BitConverter.UInt32BitsToSingle(0x38d1b717)))return false;
        reciprocal=(float)((R)1/sum);return true;
    }

    public static void NormalizeUnmasked(Span<float> weights)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R sum=0;foreach(float weight in weights)sum=sum+weight;
        if(Reciprocal(sum,out float reciprocal))for(int entry=0;entry<weights.Length;entry++)weights[entry]=(float)((R)weights[entry]*reciprocal);
    }
}
