using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>798296..798486,79865A..79873C,798924..7989F8: warp offsets and departure selection.</summary>
public static class EnbRetailWarpCameraPlacement
{
    private static R F(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
    //7988F3..798919: square sum spills before FSQRT; placement uses the unclamped speed.
    public static float Speed(Vector3 velocity,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float squared=(float)(F(velocity.X)*F(velocity.X)+F(velocity.Y)*F(velocity.Y)+F(velocity.Z)*F(velocity.Z));
        return (float)R.Sqrt(F(squared));
    }
    private static R Row(float up,float side,float forward,float translation,float vertical,float lateral,float ahead)
        =>F(up)*F(vertical)+F(side)*F(lateral)+F(forward)*F(ahead)+F(translation);
    public static Vector3 Approach(Vector3 origin,Vector3 direction,float distanceOrSpeed,float angle,float radius,bool final,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float sine=(float)R.Sine(F(angle)),cosine=(float)R.Cosine(F(angle));
        float ahead=(float)(F(distanceOrSpeed)*F(final?1000:.75f));
        float side=(float)(F(cosine)*F(radius)),up=(float)(F(sine)*F(radius));
        side=(float)(final?F(side)*F(1.25f):F(side)+F(side));
        up=(float)(final?F(up)*F(1.25f):F(up)+F(up));
        var m=EnbRetailDirectionBasis.Create(direction,origin);
        return new((float)Row(m.M31,m.M21,m.M11,m.M41,up,side,ahead),
            (float)Row(m.M32,m.M22,m.M12,m.M42,up,side,ahead),
            (float)Row(m.M33,m.M23,m.M13,m.M43,up,side,ahead));
    }
    public static Vector3 Departure(Vector3 origin,Vector3 direction,float firstAngle,float secondAngle,float radius,Vector3 previous,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float sinA=(float)R.Sine(F(firstAngle)),cosA=(float)R.Cosine(F(firstAngle));
        float sideA=(float)(F(cosA)*F(radius)),upA=(float)(F(sinA)*F(radius));
        float sinB=(float)R.Sine(F(secondAngle)),cosB=(float)R.Cosine(F(secondAngle));
        R sideBBase=F(cosB)*F(radius);
        float upB=(float)(F(sinB)*F(radius)),ahead=(float)(F(radius)+F(radius));
        sideA=(float)(F(sideA)*F(1.25f));upA=(float)(F(upA)*F(1.25f));
        float sideB=(float)(sideBBase*F(1.25f));upB=(float)(F(upB)*F(1.25f));
        var m=EnbRetailDirectionBasis.Create(direction,origin);
        R ax=Row(m.M31,m.M21,m.M11,m.M41,upA,sideA,ahead);
        float ay=(float)Row(m.M32,m.M22,m.M12,m.M42,upA,sideA,ahead);
        float az=(float)Row(m.M33,m.M23,m.M13,m.M43,upA,sideA,ahead);
        Vector3 b=new((float)Row(m.M31,m.M21,m.M11,m.M41,upB,sideB,ahead),
            (float)Row(m.M32,m.M22,m.M12,m.M42,upB,sideB,ahead),
            (float)Row(m.M33,m.M23,m.M13,m.M43,upB,sideB,ahead));
        float dx=(float)(ax-F(previous.X)),dy=(float)(F(ay)-F(previous.Y)),dz=(float)(F(az)-F(previous.Z));
        R bx=F(b.X)-F(previous.X),by=F(b.Y)-F(previous.Y),bz=F(b.Z)-F(previous.Z);
        R aDistance=F(dz)*F(dz)+F(dy)*F(dy)+F(dx)*F(dx),bDistance=bz*bz+by*by+bx*bx;
        return (R.CompareRegisters(bDistance,aDistance)&0x4100)==0?new((float)ax,ay,az):b;
    }
}
