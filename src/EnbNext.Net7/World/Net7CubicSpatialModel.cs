using System.Numerics;
using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Net7.World;

/// <summary>7119B0 captured Hermite trajectory and711D60 sampling.</summary>
internal sealed class Net7CubicSpatialModel:INet7SpatialModel
{
    //7120A0 chooses destination+24, falling back to start only if absent.
    public float BankHeadingTarget => _end.BankHeadingTarget;
    private INet7SpatialModel _start;
    private readonly INet7SpatialModel _end;
    private readonly uint _startTime,_endTime;
    private readonly Vector3 _position,_delta,_startTangent,_endTangent;
    private readonly bool _reverse;
    private Matrix4x4 _basis;
    private readonly EnbRetailLinePrecision _precision;
    public string IntervalAudit=>$"cubic[{_startTime}..{_endTime}]";
    public INet7SpatialModel Resolve(uint time)
    {
        //711D10 transfers the destination, or resolves only the start child.
        if(time>=_endTime)return _end;
        _start=_start.Resolve(time);
        return this;
    }

    internal Net7CubicSpatialModel(INet7SpatialModel start,INet7SpatialModel end,uint startTime,uint endTime,
        Net7SpatialSample first,Net7SpatialSample last,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        _start=start;_end=end;_startTime=startTime;_endTime=endTime;_precision=precision;
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R duration=(R)(double)unchecked(endTime-startTime);float storedDuration=(float)duration;
        _position=first.Position;
        _delta=new((float)((R)last.Position.X-first.Position.X),(float)((R)last.Position.Y-first.Position.Y),(float)((R)last.Position.Z-first.Position.Z));
        _startTangent=new((float)(duration*first.Velocity.X),(float)((R)storedDuration*first.Velocity.Y),(float)((R)storedDuration*first.Velocity.Z));
        _endTangent=new((float)((R)storedDuration*last.Velocity.X),(float)((R)storedDuration*last.Velocity.Y),(float)((R)storedDuration*last.Velocity.Z));
        _basis=EnbRetailQuaternionConversions.QuaternionBasis(first.Orientation,precision);
        _basis.Translation=first.Position;_reverse=last.Speed<0;
    }

    public bool TrySample(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Net7SpatialSample sample)
    {
        if(time>=_endTime)return _end.TrySample(world,time,context,out sample);
        if(time<=_startTime)return _start.TrySample(world,time,context,out sample);
        using var scope=new EnbRetailLineArithmetic.Scope(_precision,true);
        var (position,velocity)=Curve(time);
        R squared=(R)velocity.Z*velocity.Z+(R)velocity.Y*velocity.Y+(R)velocity.X*velocity.X;
        if((R.CompareFloatMemory(squared,0x38d1b717)&0x4500)==0)
        {
            float Target(float p,float v)=>(float)(_reverse?(R)p-v:(R)v+p);
            _basis=EnbRetailCameraLookAt.BetweenPoints(position,new(Target(position.X,velocity.X),Target(position.Y,velocity.Y),Target(position.Z,velocity.Z)));
        }
        var orientation=EnbRetailQuaternionConversions.ExtractOrientation(_basis,_precision);
        float speed=(float)R.Sqrt((R)(float)squared);
        if(_reverse)speed=-speed;
        if(!TrySampleRotation(world,time,context,out var rotation))
        {sample=default;return false;}
        sample=new(time,position,orientation,velocity,speed,rotation)
        {OrientationColumn0=Net7Basis3.FromQuaternion(orientation).Column0};
        return true;
    }

    public bool TrySampleVelocity(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 velocity)
    {
        if(time>=_endTime)return _end.TrySampleVelocity(world,time,context,out velocity);
        if(time<=_startTime)return _start.TrySampleVelocity(world,time,context,out velocity);
        using var scope=new EnbRetailLineArithmetic.Scope(_precision,true);
        velocity=Curve(time).Velocity;return true;
    }

    //711F85/711FA6 request only rotation. Do not advance a nested cubic's
    //retained orientation when the retail quaternion output is null.
    public bool TrySampleRotation(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 rotation)
    {
        if(time>=_endTime)return _end.TrySampleRotation(world,time,context,out rotation);
        if(time<=_startTime)return _start.TrySampleRotation(world,time,context,out rotation);
        if(!_start.TrySampleRotation(world,time,context,out var first)||!_end.TrySampleRotation(world,time,context,out var last))
        {rotation=default;return false;}
        rotation=Net7RetailEndpointInterpolation.Rotation(first,last,time,_startTime,_endTime,_precision);return true;
    }

    //712170: X/Y partial sums spill; Z stays on the x87 stack.
    private (Vector3 Position,Vector3 Velocity) Curve(uint time)
    {
        R fraction=(R)(double)unchecked(time-_startTime)/(R)(double)unchecked((int)(_endTime-_startTime));
        float t=(float)fraction;
        R squared=fraction*t;float t2=(float)squared,t3=(float)(squared*t);
        R p=(R)t2*3-((R)t3+t3),a=(R)t3-((R)t2+t2)+t,b=(R)t3-t2;
        float Position(float origin,float delta,float first,float last,bool retainZ)
        {
            float bt=(float)(b*last),at=(float)(a*first);
            R pd=p*delta;if(!retainZ)pd=(float)pd;
            R sum=pd+at;if(!retainZ)sum=(float)sum;
            sum+=bt;if(!retainZ)sum=(float)sum;
            return (float)(sum+origin);
        }
        var position=new Vector3(Position(_position.X,_delta.X,_startTangent.X,_endTangent.X,false),
            Position(_position.Y,_delta.Y,_startTangent.Y,_endTangent.Y,false),Position(_position.Z,_delta.Z,_startTangent.Z,_endTangent.Z,true));
        R dp=(R)t*6-(R)t2*6;
        float da=(float)((R)t2*3-(R)t*4+1),db=(float)((R)t2*3-((R)t+t));
        float reciprocal=(float)((R)1/(R)(double)unchecked(_endTime-_startTime));
        float Velocity(float delta,float first,float last,int axis)
        {
            R pd=dp*delta;if(axis!=2)pd=(float)pd;
            R at=(R)da*first;if(axis!=0)at=(float)at;
            float bt=(float)((R)db*last);
            R sum=axis==2?pd+at:at+pd;if(axis!=2)sum=(float)sum;
            sum+=bt;if(axis!=2)sum=(float)sum;
            return (float)(sum*reciprocal);
        }
        return (position,new(Velocity(_delta.X,_startTangent.X,_endTangent.X,0),
            Velocity(_delta.Y,_startTangent.Y,_endTangent.Y,1),Velocity(_delta.Z,_startTangent.Z,_endTangent.Z,2)));
    }
}
