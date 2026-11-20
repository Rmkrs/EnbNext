using System.Numerics;
using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Net7.World;

/// <summary>70F630 sample arithmetic. Inputs retain keyframe tick units.</summary>
internal static class Net7RetailKeyframeInterpolation
{
    internal static Net7SpatialSample Sample(Net7SpatialSample first,Net7SpatialSample last,uint time,uint remainder,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R scale=R.FromFloatBits(0x3c23d70a);
        float alpha=(float)((R)(double)remainder*scale);
        float Axis(float a,float b,bool rate)
        {
            R value=remainder==0?(R)a:((R)b-a)*alpha+a;
            return (float)(rate?value*scale:value);
        }
        Vector3 Vector(Vector3 a,Vector3 b,bool rate)=>new(Axis(a.X,b.X,rate),Axis(a.Y,b.Y,rate),Axis(a.Z,b.Z,rate));
        var q=remainder==0?first.Orientation:Net7RetailEndpointInterpolation.Orientation(first.Orientation,last.Orientation,alpha,precision);
        return new(time,Vector(first.Position,last.Position,false),q,Vector(first.Velocity,last.Velocity,true),
            Axis(first.Speed,last.Speed,true),Vector(first.Rotation,last.Rotation,true))
        {OrientationColumn0=remainder==0?first.OrientationColumn0:Net7Basis3.FromQuaternion(q).Column0};
    }
}
