using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>718830: type13 basis from sampled spline position/velocity and authored up vectors.</summary>
public static class EnbRetailLandingMatrix
{
    public static Matrix4x4 Evaluate(Vector3 position, Vector3 velocity, Vector3 initialUp,
        Vector3 finalUp, uint elapsed, float inverseDuration)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var forward = Normalize(velocity);
        float fraction = (float)((R)elapsed * inverseDuration);
        if (fraction > 1) fraction = 1;
        var interpolated = EnbRetailDirectQuaternionInterpolation.Interpolate(
            new(initialUp,1),new(finalUp,1),fraction);
        var side = Normalize(Cross(new(interpolated.X,interpolated.Y,interpolated.Z),forward));
        var up = Normalize(Cross(forward,side));
        return new(forward.X,forward.Y,forward.Z,0,side.X,side.Y,side.Z,0,
            up.X,up.Y,up.Z,0,position.X,position.Y,position.Z,1);
    }

    private static Vector3 Cross(Vector3 a, Vector3 b) => new(
        (float)((R)a.Y*b.Z-(R)a.Z*b.Y),
        (float)((R)a.Z*b.X-(R)a.X*b.Z),
        (float)((R)a.X*b.Y-(R)a.Y*b.X));

    private static Vector3 Normalize(Vector3 value)
    {
        float xSquare = (float)((R)value.X*value.X);
        R square = (R)value.Y*value.Y+(R)value.Z*value.Z+xSquare;
        if ((float)square == 0) return value;
        R inverse = (double)EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
        return new((float)((R)value.X*inverse),(float)((R)value.Y*inverse),(float)((R)value.Z*inverse));
    }
}
