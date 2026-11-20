using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>797813..7978D4: tractor-camera offset from ship to target.</summary>
public static class EnbRetailLootCameraPlacement
{
    public static Vector3 Evaluate(Vector3 start,Vector3 target,float angle,float shipRadius,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R Load(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        float sine=(float)R.Sine(Load(angle)),cosine=(float)R.Cosine(Load(angle));
        float ahead=(float)(Load(-3)*Load(shipRadius));
        float side=(float)(Load(cosine)*Load(3)*Load(shipRadius));
        float up=(float)(Load(sine)*Load(3)*Load(shipRadius));
        var m=EnbRetailCameraLookAt.BetweenPoints(start,target);
        float Row(float vertical,float lateral,float forward,float translation)
            =>(float)(Load(vertical)*Load(up)+Load(lateral)*Load(side)+Load(forward)*Load(ahead)+Load(translation));
        return new(Row(m.M31,m.M21,m.M11,m.M41),Row(m.M32,m.M22,m.M12,m.M42),Row(m.M33,m.M23,m.M13,m.M43));
    }
}
