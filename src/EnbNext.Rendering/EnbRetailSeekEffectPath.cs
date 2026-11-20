using System.Numerics;

namespace EnbNext.Rendering;

// Current retail 006F1590 builds time-keyed lateral knots; 008D57E0
// supplies endpoint/interior tangents and 008D60F0 samples cubic Hermite.
public sealed class EnbRetailSeekEffectPath
{
    private readonly Vector3[] _points;
    private readonly Vector3[] _tangents;
    private readonly uint _duration;
    public EnbRetailSeekEffectPath(uint duration,uint changeTime,float distance,bool oneChange,uint seed)
        :this(duration,changeTime,distance,oneChange,new EnbRetailParticleBirthRandomStream(seed,0)) { }

    public EnbRetailSeekEffectPath(uint duration,uint changeTime,float distance,bool oneChange,EnbRetailParticleBirthRandomStream random)
    {
        duration=duration is 0 or uint.MaxValue?1:duration;
        _duration=duration;
        changeTime=Math.Max(100,changeTime);
        if(!float.IsFinite(distance))throw new InvalidDataException("Invalid seek distance");
        distance=Math.Max(0,distance);
        int count=duration<2ul*changeTime?3:checked((int)(duration/changeTime+1));
        if(duration<2ul*changeTime)distance*=duration/(2f*changeTime);
        if(oneChange)count=3;
        // 006F1590 appends every knot; it has no count limit.
        _points=new Vector3[count];_tangents=new Vector3[count];
        uint first;
        lock(random)first=random.Reserve(count-2);
        float step=1f/(count-1),fraction=step;
        for(int i=1;i<count-1;i++,fraction+=step)
        {
            double angle=EnbRetailSignedRandom.Word(random.Seed,unchecked(first+(uint)(i-1)))*(double)BitConverter.Int32BitsToSingle(0x30c90fdb);
            _points[i]=new(fraction,(float)(Math.Sin(angle)*distance),(float)(Math.Cos(angle)*distance));
        }
        _points[^1]=Vector3.UnitX;
        _tangents[0]=(_points[1]-_points[0])*.25f;
        _tangents[^1]=(_points[^1]-_points[^2])*.25f;
        for(int i=1;i<count-1;i++)_tangents[i]=(_points[i+1]-_points[i-1])*.5f;
    }

    public Matrix4x4 Sample(uint age,Vector3 source,Vector3 target)
    {
        float progress=Math.Clamp(age/(float)_duration,0,1)*(_points.Length-1);
        int i=Math.Min((int)progress,_points.Length-2);float t=progress-i,t2=t*t,t3=t2*t;
        var p=(2*t3-3*t2+1)*_points[i]+(t3-2*t2+t)*_tangents[i]+(-2*t3+3*t2)*_points[i+1]+(t3-t2)*_tangents[i+1];
        var tangent=(6*t2-6*t)*_points[i]+(3*t2-4*t+1)*_tangents[i]+(-6*t2+6*t)*_points[i+1]+(3*t2-2*t)*_tangents[i+1];
        float length=Vector3.Distance(source,target);p.X*=length;tangent.X*=length;
        var basis=LookAlong(target-source,source);
        return LookAlong(Vector3.TransformNormal(tangent,basis),Vector3.Transform(p,basis));
    }

    // 0082ECB0: +X follows the trajectory; +Y lies in the XY plane.
    public static Matrix4x4 LookAlong(Vector3 direction,Vector3 position)
    {
        var x=direction.LengthSquared()>0?Vector3.Normalize(direction):Vector3.Zero;
        var y=new Vector3(-x.Y,x.X,0);if(y.LengthSquared()>0)y=Vector3.Normalize(y);
        var z=Vector3.Cross(x,y);
        return new(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,position.X,position.Y,position.Z,1);
    }

    // 008C29B0 with zero roll, including its coincident/vertical defaults.
    public static Matrix4x4 BetweenPoints(Vector3 origin,Vector3 point)
        => EnbRetailCameraLookAt.BetweenPoints(origin,point);

    // 006E8890 clips the endpoint against the target sphere when flag 0x2000
    // is set. A miss preserves the packet's endpoint.
    public static Vector3 IntersectTarget(Vector3 start,Vector3 end,Vector3 center,float radius)
    {
        //006E8890 constructs the segment using00447C40 normalization;
        //008C9370 chooses the forward exit when the near root is behind us.
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var delta=end-start;
        EnbRetailLineArithmetic.Register x=delta.X,y=delta.Y,z=delta.Z;
        float square=(float)(z*z+y*y+x*x);
        if(square==0)return end;
        var reciprocal=EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
        var direction=new Vector3((float)(x*reciprocal),(float)(y*reciprocal),(float)(z*reciprocal));
        float length=(float)EnbRetailLineArithmetic.Register.Sqrt(square);
        x=(EnbRetailLineArithmetic.Register)center.X-start.X;
        y=(EnbRetailLineArithmetic.Register)center.Y-start.Y;
        z=(EnbRetailLineArithmetic.Register)center.Z-start.Z;
        float projection=(float)(z*direction.Z+y*direction.Y+x*direction.X);
        float discriminant=(float)((EnbRetailLineArithmetic.Register)radius*radius-
            (z*z+y*y+x*x-(EnbRetailLineArithmetic.Register)projection*projection));
        if(discriminant<0)return end;
        float root=(float)EnbRetailLineArithmetic.Register.Sqrt(discriminant);
        var fraction=((EnbRetailLineArithmetic.Register)projection-root)/length;
        if(fraction<0)fraction=((EnbRetailLineArithmetic.Register)root+projection)/length;
        if(!(fraction>=0 && fraction<1))return end;
        float storedFraction=(float)fraction;
        return new((float)((EnbRetailLineArithmetic.Register)delta.X*storedFraction+start.X),
            (float)((EnbRetailLineArithmetic.Register)(float)((EnbRetailLineArithmetic.Register)delta.Y*storedFraction)+start.Y),
            (float)((EnbRetailLineArithmetic.Register)(float)((EnbRetailLineArithmetic.Register)delta.Z*storedFraction)+start.Z));
    }

}
