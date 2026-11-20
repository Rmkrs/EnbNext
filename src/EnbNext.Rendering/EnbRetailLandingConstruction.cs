using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>794D5A..794FF6 surface-arrival construction after the physical matrix getter.</summary>
public static class EnbRetailLandingConstruction
{
    public readonly record struct Result(Vector3 Start, Vector3 End, Vector3 StartTangent,
        Vector3 EndTangent, Vector3 FinalUp);

    public static Result Evaluate(Matrix4x4 matrix, Vector3 offset, float forwardSpeed, float downSpeed)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float Start(float x,float y,float z,float translation) =>
            (float)(((R)z*offset.Z+(R)y*offset.Y)+(R)x*offset.X+translation);
        float Direction(float x,float y,float z,float a,float b,float c) =>
            (float)(((R)x*a+(R)z*c)+(R)y*b);
        var start=new Vector3(Start(matrix.M11,matrix.M21,matrix.M31,matrix.M41),
            Start(matrix.M12,matrix.M22,matrix.M32,matrix.M42),Start(matrix.M13,matrix.M23,matrix.M33,matrix.M43));
        var tangent=new Vector3(Direction(matrix.M11,matrix.M21,matrix.M31,forwardSpeed,0,0),
            Direction(matrix.M12,matrix.M22,matrix.M32,forwardSpeed,0,0),Direction(matrix.M13,matrix.M23,matrix.M33,forwardSpeed,0,0));
        R x=tangent.X,y=tangent.Y,z=tangent.Z;
        float square=(float)((R)tangent.X*tangent.X+(R)tangent.Y*tangent.Y+(R)tangent.Z*tangent.Z);
        if(square!=0)
        {
            float inverse=(float)EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
            x*=inverse;y*=inverse;z*=inverse;
        }
        var outgoing=new Vector3((float)(x*forwardSpeed+(R)0),(float)(y*forwardSpeed+(R)0),
            (float)(z*forwardSpeed+(float)((R)downSpeed*-1)));
        var up=new Vector3(Direction(matrix.M11,matrix.M21,matrix.M31,0,0,1),
            Direction(matrix.M12,matrix.M22,matrix.M32,0,0,1),Direction(matrix.M13,matrix.M23,matrix.M33,0,0,1));
        return new(start,matrix.Translation,outgoing,tangent,up);
    }
}
