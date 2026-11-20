using EnbNext.Rendering;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.RetailMath;

public static class EnbRetailTextRotation
{
    // 6CA1FD..6CA25D; positive app scale is the negative of native raster Y.
    public static (float Scale,bool Complete) Sample(int milliseconds,float speed,float initial,float intensity)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        R angle=R.FromInteger64(milliseconds,0x7f)*(R)speed*(R)BitConverter.UInt32BitsToSingle(0x3bcde277);
        if(initial==intensity)return ((float)R.Cosine(angle),true);
        bool complete=R.CompareFloatMemory(angle,0x3fc90fda)==0;
        if(complete)angle=(R)BitConverter.UInt32BitsToSingle(0x3fc90fda);
        return ((float)(initial<intensity?R.Sine(angle):R.Cosine(angle)),complete);
    }
}
