using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>793D91..794177 space-approach geometry with the side-choice input explicit.</summary>
public static class EnbRetailSpaceLandingConstruction
{
    public readonly record struct Result(Vector3 Start,Vector3 End,Vector3 Camera,
        Vector3 CameraTarget,Vector3 Direction,Vector3 Turn);

    public static Result Evaluate(Vector3 ship,Vector3 planet,float radius,bool reverseSide)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var direction=Normalize(new((float)((R)ship.X-planet.X),(float)((R)ship.Y-planet.Y),(float)((R)ship.Z-planet.Z)));
        var side=new Vector3((float)((R)0-direction.Y),(float)((R)direction.X-0),0);
        var up=new Vector3((float)((R)direction.Y*0-(R)side.Y*direction.Z),
            (float)((R)direction.Z*side.X-(R)direction.X*0),
            (float)((R)side.Y*direction.X-(R)direction.Y*side.X));
        if(reverseSide)side=-side;
        float Radial(float a,float b)=>(float)(((R)a+b)*radius*.5f);
        var radial=new Vector3(Radial(side.X,up.X),Radial(side.Y,up.Y),Radial(side.Z,up.Z));
        float Camera(float d,float p)=>(float)((R)d*radius*.5f+p);
        var camera=new Vector3(Camera(direction.X,planet.X),Camera(direction.Y,planet.Y),Camera(direction.Z,planet.Z));
        var target=new Vector3((float)((R)planet.X+radial.X),(float)((R)planet.Y+radial.Y),(float)((R)planet.Z+radial.Z));
        var start=new Vector3((float)((R)direction.X*100+camera.X),(float)((R)direction.Y*100+camera.Y),(float)((R)direction.Z*100+camera.Z));
        var turn=Normalize(radial);
        var end=new Vector3((float)(((R)turn.X-direction.X)*1000+start.X),
            (float)(((R)turn.Y-direction.Y)*1000+start.Y),(float)(((R)turn.Z-direction.Z)*1000+start.Z));
        return new(start,end,camera,target,direction,turn);
    }

    //7941F6..794318: the direction is first stored after scaling by100;
    //the endpoint tangent consumes that stored vector multiplied by5.
    public static (Vector3 Start,Vector3 End) Tangents(Vector3 direction,Vector3 turn)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float Scale(float v,float scale)=>(float)((R)v*scale);
        var scaled=new Vector3(Scale(direction.X,100),Scale(direction.Y,100),Scale(direction.Z,100));
        float End(float t,float d)=>(float)((R)Scale(t,1000)-(R)d*5);
        return (-scaled,new(End(turn.X,scaled.X),End(turn.Y,scaled.Y),End(turn.Z,scaled.Z)));
    }

    private static Vector3 Normalize(Vector3 v)
    {
        R square=(R)v.X*v.X+(R)v.Y*v.Y+(R)v.Z*v.Z;
        if((float)square==0)return v;
        R inverse=(double)EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
        return new((float)((R)v.X*inverse),(float)((R)v.Y*inverse),(float)((R)v.Z*inverse));
    }
}
