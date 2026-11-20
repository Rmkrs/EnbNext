using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>008C25C0 camera matrix construction. Retains x87 product order,
/// performs no quaternion normalization, and leaves translation to the caller.</summary>
public static class EnbRetailCameraQuaternionBasis
{
    public static Matrix4x4 Create(Quaternion q,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float Twice(R v)=>(float)(v+v);
        float Diagonal(R v)=>(float)((R)1-(v+v));
        return new(
            Diagonal((R)q.Y*q.Y+(R)q.Z*q.Z),Twice((R)q.W*q.Z+(R)q.X*q.Y),Twice((R)q.X*q.Z-(R)q.W*q.Y),0,
            Twice((R)q.X*q.Y-(R)q.W*q.Z),Diagonal((R)q.X*q.X+(R)q.Z*q.Z),Twice((R)q.W*q.X+(R)q.Z*q.Y),0,
            Twice((R)q.W*q.Y+(R)q.X*q.Z),Twice((R)q.Z*q.Y-(R)q.W*q.X),Diagonal((R)q.Y*q.Y+(R)q.X*q.X),0,
            0,0,0,1);
    }
}
