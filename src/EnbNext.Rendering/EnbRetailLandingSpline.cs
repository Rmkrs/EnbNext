using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>8D60F0/8D6270 two-key Hermite spline at authored times0 and1.</summary>
public static class EnbRetailLandingSpline
{
    public readonly record struct Sample(Vector3 Position, Vector3 Velocity);

    public static float Fraction(uint elapsed)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        return (float)((R)elapsed*(float)((R)1/4000));
    }

    public static Sample Evaluate(Vector3 start, Vector3 end, Vector3 startTangent,
        Vector3 endTangent, float fraction)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float t = Math.Clamp(fraction,0,1);
        R square = (R)t*t, cube = square*t;
        float a = (float)((cube+cube)-square*3+1);
        float b = (float)(square*3-(cube+cube));
        float c = (float)(cube-(square+square)+t);
        R d = cube-square;
        Vector3 Combine(float first, float last, float outgoing, R incoming) => new(
            (float)(incoming*endTangent.X+(R)outgoing*startTangent.X+(R)first*start.X+(R)last*end.X),
            (float)(incoming*endTangent.Y+(R)outgoing*startTangent.Y+(R)first*start.Y+(R)last*end.Y),
            (float)(incoming*endTangent.Z+(R)outgoing*startTangent.Z+(R)first*start.Z+(R)last*end.Z));
        var position = fraction<0 ? start : fraction>1 ? end : Combine(a,b,c,d);
        R sixSquare=square*6, sixT=(R)t*6, threeSquare=square*3;
        a=(float)(sixSquare-sixT);b=(float)(sixT-sixSquare);
        c=(float)(threeSquare-(R)t*4+1);d=threeSquare-((R)t+t);
        return new(position,Combine(a,b,c,d));
    }
}
