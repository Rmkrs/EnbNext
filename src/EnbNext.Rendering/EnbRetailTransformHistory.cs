using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>718FD0/7190E0 history consumers, including unsigned time difference and asymmetric spills.</summary>
public static class EnbRetailTransformHistory
{
    private static R F(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
    public static Vector3 Velocity(Vector3 current,Vector3 previous,uint elapsed,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        if(elapsed==0)return Vector3.Zero;
        R reciprocal=F(1)/(R)(double)elapsed;
        R x=F(current.X)-F(previous.X);
        float y=(float)(F(current.Y)-F(previous.Y)),z=(float)(F(current.Z)-F(previous.Z));
        return new((float)(x*reciprocal),(float)(F(y)*reciprocal),(float)(F(z)*reciprocal));
    }
    public static Vector3 AngularVelocity(Matrix4x4 current,Matrix4x4 previous,uint elapsed,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        R Angle(float y,float x)=>R.EvaluateControlled(33,F(x),F(y),control);
        float x=(float)(F((float)Angle(current.M23,current.M22))-Angle(previous.M23,previous.M22));
        float y=(float)(F((float)Angle(current.M31,current.M33))-Angle(previous.M31,previous.M33));
        R z=F((float)Angle(current.M12,current.M11))-Angle(previous.M12,previous.M11);
        if(elapsed!=0)
        {
            R reciprocal=F(1)/(R)(double)elapsed;
            x=(float)(reciprocal*F(x));y=(float)(reciprocal*F(y));z=reciprocal*z;
        }
        return new(x,y,(float)z);
    }
}
