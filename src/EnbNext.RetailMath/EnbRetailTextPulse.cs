using EnbNext.Rendering;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.RetailMath;

public static class EnbRetailTextPulse
{
    // 6D68F4..6D6951: a single subtraction, not modulo or a triangle wave.
    public static float Advance(float alpha,uint milliseconds,bool enabled=true)
    {
        if(!enabled)return alpha;
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        R delta=R.FromInteger64(milliseconds,0x7f)*BitConverter.UInt32BitsToSingle(0x3a83126f);
        R value=delta+delta+alpha;
        return (float)(R.CompareFloatMemory(value,0x3f800000)==0 ? value-1f : value);
    }
}
