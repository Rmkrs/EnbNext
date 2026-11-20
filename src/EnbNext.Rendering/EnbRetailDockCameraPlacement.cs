using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>792D64..792E1F: authored dock camera offset through8C29B0.
/// Preserve trig spills, separate radius factors and row accumulation order.</summary>
public static class EnbRetailDockCameraPlacement
{
    public static Vector3 Evaluate(Vector3 start,Vector3 end,float ahead,float angle,float radiusFactor,float shipRadius,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R Load(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        float sine=(float)R.Sine(Load(angle)),cosine=(float)R.Cosine(Load(angle));
        float side=(float)(Load(cosine)*Load(radiusFactor)*Load(shipRadius));
        float up=(float)(Load(sine)*Load(radiusFactor)*Load(shipRadius));
        var m=EnbRetailCameraLookAt.BetweenPoints(start,end);
        float Row(float forward,float vertical,float lateral,float translation)
            =>(float)(Load(forward)*Load(ahead)+Load(up)*Load(vertical)+Load(side)*Load(lateral)+Load(translation));
        return new(Row(m.M11,m.M31,m.M21,m.M41),Row(m.M12,m.M32,m.M22,m.M42),Row(m.M13,m.M33,m.M23,m.M43));
    }
}
