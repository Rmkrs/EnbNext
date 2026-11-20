using System.Numerics;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailParticleKinematics
{
    //00962070 consumes queued births;009624A0 advances existing stored arrays.
    // Acceleration is already in units/ms², as constructed by0097D450.
    public static (Vector3 Position,Vector3 Velocity) Initialize(Vector3 position,Vector3 velocity,
        Vector3 acceleration,long age,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float t=age;
        if(acceleration==Vector3.Zero)
            return (new((float)((Register)velocity.X*t+position.X),(float)((Register)(float)((Register)velocity.Y*t)+position.Y),
                (float)((Register)(float)((Register)velocity.Z*t)+position.Z)),velocity);
        float Position(float p,float v,float a,bool spill)
        {
            Register half=(Register)a*.5d;
            if(spill)half=(float)half;
            Register term=half*t;
            if(spill)term=(float)term;
            term+=v;
            if(spill)term=(float)term;
            term*=t;
            if(spill)term=(float)term;
            return (float)(term+p);
        }
        float Velocity(float v,float a,bool spill)
        {
            Register motion=(Register)a*t;
            if(spill)motion=(float)motion;
            return (float)(motion+v);
        }
        return (new(Position(position.X,velocity.X,acceleration.X,false),Position(position.Y,velocity.Y,acceleration.Y,true),
            Position(position.Z,velocity.Z,acceleration.Z,true)),
            new(Velocity(velocity.X,acceleration.X,false),Velocity(velocity.Y,acceleration.Y,true),Velocity(velocity.Z,acceleration.Z,true)));
    }

    public static (Vector3 Position,Vector3 Velocity) Advance(Vector3 position,Vector3 velocity,
        Vector3 acceleration,long delta,bool pingPong=false,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float t=delta;
        if(acceleration==Vector3.Zero)
            return Initialize(position,velocity,acceleration,delta,precision);
        Register halfSquare=(Register)t*t*.5d;
        float Position(float p,float v,float a,bool spill)
        {
            float offset=(float)(halfSquare*a);
            Register motion=(Register)t*v;
            if(spill)motion=(float)motion;
            motion+=pingPong?p:offset;
            if(spill)motion=(float)motion;
            return (float)(motion+(pingPong?offset:p));
        }
        float Velocity(float v,float a)=>(float)((Register)(float)((Register)t*a)+v);
        return (new(Position(position.X,velocity.X,acceleration.X,false),Position(position.Y,velocity.Y,acceleration.Y,true),
            Position(position.Z,velocity.Z,acceleration.Z,true)),
            new(Velocity(velocity.X,acceleration.X),Velocity(velocity.Y,acceleration.Y),Velocity(velocity.Z,acceleration.Z)));
    }
}
