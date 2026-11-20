using System.Numerics;
using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Net7.World;

/// <summary>712780/712FA0 interior arithmetic for evolving and captured endpoint samples.</summary>
internal static class Net7RetailEndpointInterpolation
{
    internal static Vector3 Rotation(Vector3 first,Vector3 last,uint time,uint startTime,uint endTime,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float alpha=(float)((R)(double)unchecked(time-startTime)/(R)(double)unchecked((int)(endTime-startTime)));
        float Axis(float a,float b)=>(float)(((R)b-a)*alpha+a);
        return new(Axis(first.X,last.X),Axis(first.Y,last.Y),Axis(first.Z,last.Z));
    }
    internal static Net7SpatialSample Sample(Net7SpatialSample start,Net7SpatialSample end,uint time,uint startTime,uint endTime,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24,bool evolving=false)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R F(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        //71304D uses signed32 FIDIV even though interval comparisons are unsigned.
        float alpha=(float)((R)(double)unchecked(time-startTime)/(R)(double)unchecked((int)(endTime-startTime)));
        var orientation=Orientation(start.Orientation,end.Orientation,alpha,precision);
        float Lerp(float x,float y)=>(float)((F(y)-F(x))*F(alpha)+F(x));
        Vector3 Blend(Vector3 x,Vector3 y)=>new(Lerp(x.X,y.X),Lerp(x.Y,y.Y),Lerp(x.Z,y.Z));
        return new(time,Blend(start.Position,end.Position),orientation,Blend(start.Velocity,end.Velocity),
            //712AC0 reads the destination scalar for both operands. This value
            //feeds716400/7168A0 transition selection, including nested states.
            Lerp(evolving?end.Speed:start.Speed,end.Speed),Blend(start.Rotation,end.Rotation))
        {OrientationColumn0=Net7Basis3.FromQuaternion(orientation).Column0};
    }

    internal static Quaternion Orientation(Quaternion a,Quaternion b,float alpha,EnbRetailLinePrecision precision)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R F(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        bool Near(float x,float y)
        {
            uint absolute=BitConverter.SingleToUInt32Bits((float)(F(x)-F(y)))&0x7fffffff;
            return (R.CompareFloatMemory(R.FromFloatBits(absolute),0x38d1b717)&0x100)!=0;
        }
        return Near(a.X,b.X)&&Near(a.Y,b.Y)&&Near(a.Z,b.Z)&&Near(a.W,b.W)?a:
            EnbRetailDirectQuaternionInterpolation.Interpolate(a,b,alpha,precision);
    }
}
