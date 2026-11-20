using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>795EB2..795F96 approach placement, including distinct side/up spills.</summary>
public static class EnbRetailGateCameraPlacement
{
    //795D62..795E15 ->795F87: the gate getter's matrix is consumed as stored,
    //before the separate RenderObject validation/repair at6F3C60.
    public static Vector3 Commit(Matrix4x4 matrix,float angle,float shipRadius,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R Load(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        float sine=(float)R.Sine(Load(angle)),cosine=(float)R.Cosine(Load(angle));
        R side=Load(cosine)*Load(shipRadius);
        float upBase=(float)(Load(sine)*Load(shipRadius));
        float ahead=(float)(Load(shipRadius)*Load(5));
        side*=Load(3);
        R up=Load(upBase)*Load(3);
        float Row(float vertical,float lateral,float forward,float translation)
            =>(float)(up*Load(vertical)+side*Load(lateral)+Load(forward)*Load(ahead)+Load(translation));
        return new(Row(matrix.M31,matrix.M21,matrix.M11,matrix.M41),Row(matrix.M32,matrix.M22,matrix.M12,matrix.M42),Row(matrix.M33,matrix.M23,matrix.M13,matrix.M43));
    }
    public static Vector3 Approach(Vector3 start,Vector3 target,float angle,float shipRadius,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R Load(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        float sine=(float)R.Sine(Load(angle)),cosine=(float)R.Cosine(Load(angle));
        float up=(float)(Load(sine)*Load(shipRadius));
        float ahead=(float)(Load(shipRadius)*Load(3));
        float side=(float)(Load(cosine)*Load(shipRadius)*Load(1.5f));
        up=(float)(Load(up)*Load(1.5f));
        var m=EnbRetailCameraLookAt.BetweenPoints(start,target);
        float Row(float vertical,float lateral,float forward,float translation)
            =>(float)(Load(vertical)*Load(up)+Load(lateral)*Load(side)+Load(forward)*Load(ahead)+Load(translation));
        return new(Row(m.M31,m.M21,m.M11,m.M41),Row(m.M32,m.M22,m.M12,m.M42),Row(m.M33,m.M23,m.M13,m.M43));
    }
}
