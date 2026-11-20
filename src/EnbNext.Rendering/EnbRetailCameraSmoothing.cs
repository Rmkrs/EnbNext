using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;
namespace EnbNext.Rendering;

/// <summary>799610 retained position/orientation update. The caller owns the
/// dispatch decision to snap or smooth; orientation stays in native camera axes.</summary>
public static class EnbRetailCameraSmoothing
{
    //799E40's79AD5D..79AD98 branch compares retained squared distance.
    public static bool IsDiscontinuity(Vector3 previous,Vector3 desired,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R x=(R)previous.X-desired.X,y=(R)previous.Y-desired.Y,z=(R)previous.Z-desired.Z;
        return (R.CompareFloatMemory(z*z+y*y+x*x,0x4abebc20)&0x4100)==0;
    }
    public static (Vector3 Position,Quaternion Orientation) Update(Vector3 previous,Quaternion orientation,
        Vector3 target,Quaternion desired,double elapsed,bool fixedRate=false,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float positionRate,orientationRate;
        if(fixedRate)positionRate=orientationRate=.005f;
        else
        {
            R x=(R)previous.X-target.X,y=(R)previous.Y-target.Y,z=(R)previous.Z-target.Z;
            float square=(float)(z*z+y*y+x*x);
            float distance=(float)R.Sqrt((R)square);
            positionRate=(float)(((R)distance*.0004f*10f+1f)*.001f);
            orientationRate=.002f;
        }
        desired=EnbRetailDirectQuaternionInterpolation.AlignTo(orientation,desired,precision);
        R dt=(double)elapsed,weight=dt*positionRate;
        Vector3 position=target;
        if((R.CompareFloatMemory(weight,0x3f800000)&0x100)!=0)
        {
            float alpha=(float)weight;
            float Axis(float old,float next)=>(float)(((R)next-old)*alpha+old);
            position=new(Axis(previous.X,target.X),Axis(previous.Y,target.Y),Axis(previous.Z,target.Z));
        }
        bool Near(float a,float b)=>MathF.Abs((float)((R)a-b))<.0001f;
        if(Near(orientation.X,desired.X)&&Near(orientation.Y,desired.Y)&&Near(orientation.Z,desired.Z)&&Near(orientation.W,desired.W))
            return(position,orientation);
        weight=dt*orientationRate;
        return(position,(R.CompareFloatMemory(weight,0x3f800000)&0x100)!=0
            ?EnbRetailDirectQuaternionInterpolation.Interpolate(orientation,desired,(float)weight,precision):desired);
    }
}
