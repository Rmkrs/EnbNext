using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>0096E270 transforms the authored direction without normalization;
/// 0096AB13..6B gates dazzle area against the camera axis retained in ECX.</summary>
public static class EnbRetailDazzleDirection
{
    public static float Apply(float area,float directionArea,Vector3 direction,Matrix4x4 world,Vector3 cameraForward,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if(!(directionArea>0))return area;
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float Transform(float x,float y,float z)=>(float)(((R)z*(R)direction.Z+(R)y*(R)direction.Y)+(R)x*(R)direction.X);
        var transformed=new Vector3(Transform(world.M11,world.M21,world.M31),Transform(world.M12,world.M22,world.M32),Transform(world.M13,world.M23,world.M33));
        return ApplyToCameraAxis(area,directionArea,transformed,cameraForward,precision);
    }

    public static float ApplyToCameraAxis(float area,float directionArea,Vector3 direction,Vector3 cameraForward,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if(!(directionArea>0))return area;
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R dot=-(((R)direction.Y*(R)cameraForward.Y+(R)direction.Z*(R)cameraForward.Z)+(R)direction.X*(R)cameraForward.X);
        R factor=(dot-((R)1-(R)directionArea))/(R)directionArea;
        if(double.IsNaN((double)factor)||(double)factor<0)factor=(R)0;
        else if((double)factor>1)factor=(R)1;
        return (float)((R)area*factor);
    }

}
