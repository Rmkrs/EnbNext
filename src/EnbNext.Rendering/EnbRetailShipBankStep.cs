using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>6F3FA0 increment/angle update, including498CC0/498E00.</summary>
public static class EnbRetailShipBankStep
{
    public static void Advance(byte objectType,bool local,float target,float elapsedMilliseconds,
        ref float angle,ref float increment,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float maximum=objectType==1?.02f:.025f;
        R stoppingAcceleration=objectType==1?(R)(local?.02f:.025f)*.1f:(R).025f*.0125f;
        R stopping=(R)increment*increment/(stoppingAcceleration+stoppingAcceleration);
        R threshold=(R.CompareFloatMemory(stopping,BitConverter.SingleToUInt32Bits(maximum))&0x4100)==0?stopping:(R)maximum;
        R error=R.Abs((R)target-angle);
        float desired=(R.CompareRegisters(error,threshold)&0x4100)==0
            ?angle<target?maximum:target<angle?-maximum:0:0;
        float delta=(float)((R)increment-desired);
        if(MathF.Abs(delta)<=BitConverter.UInt32BitsToSingle(0x38d1b717))
        {
            if(desired==0)increment=0;
        }
        else
        {
            //6F2820 stores the construction acceleration as float.
            float acceleration=(float)(objectType==1?(R).02f*.1f:(R).025f*.0125f);
            R amount=(R)elapsedMilliseconds*acceleration;
            bool rising=increment<desired;
            R next=rising?(R)increment+amount:(R)increment-amount;
            increment=(float)next;
            int comparison=R.CompareFloatMemory(next,BitConverter.SingleToUInt32Bits(desired));
            if(rising?(comparison&0x100)==0:(comparison&0x4100)!=0)increment=desired;
        }
        angle=(float)((R)increment+angle);
    }
}
