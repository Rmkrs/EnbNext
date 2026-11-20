using System.Numerics;
using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Net7.World;

internal static class Net7RetailKeyframeSample
{
    //7117A0 repairs the retained basis before70F630 requests the other getters.
    internal static Net7SpatialSample Raw(Matrix4x4 input,float speed,Vector3 drift,float tilt,float turn,uint time,
        out Matrix4x4 basis,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        basis=EnbRetailSpatialBasis.Validate(input).Transform;
        var q=EnbRetailQuaternionConversions.ExtractOrientation(basis,precision);
        //7117D0 uses q*(speed,0,0)*conjugate(q), with X/Y product spills.
        R zero=0;
        float a=(float)((R)speed*q.W+(zero-zero));
        float b=(float)(zero-(zero-(R)speed*q.Z));
        float c=(float)((zero-(R)speed*q.Y)+zero);
        R d=-((R)speed*q.X+zero+zero),nx=-(R)q.X,ny=-(R)q.Y,nz=-(R)q.Z;
        float x=(float)((nz*b-ny*c)+(R)a*q.W+nx*d);
        float y=(float)(((R)b*q.W+ny*d)-(nz*a-nx*c));
        R z=(ny*a-nx*b)+(R)c*q.W+nz*d;
        var velocity=new Vector3((float)((R)x+drift.X),(float)((R)y+drift.Y),(float)(z+drift.Z));
        return new(time,basis.Translation,q,velocity,speed,new(0,tilt,turn))
        {OrientationColumn0=new(basis.M11,basis.M12,basis.M13)};
    }
}
