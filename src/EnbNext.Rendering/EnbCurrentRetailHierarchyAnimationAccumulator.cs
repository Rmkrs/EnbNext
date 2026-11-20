using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>00989DA5..00989F5D aggregate pose arithmetic. Ownership and sample
/// ordering remain with the caller; zero/unordered weights are skipped there.</summary>
public struct EnbCurrentRetailHierarchyAnimationAccumulator
{
    private float _x,_y,_z,_total;
    private int _contributors;
    private Quaternion _rotation;

    public void AddTranslation(Vector3 translation,float weight,float scale)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        _contributors++;
        R factor=(R)weight*scale;
        float weightedX=(float)((R)translation.X*factor),weightedY=(float)((R)translation.Y*factor);
        R weightedZ=factor*translation.Z;
        _x=(float)((R)weightedX+_x);_y=(float)((R)_y+weightedY);_z=(float)((R)_z+weightedZ);
        _total=(float)((R)weight+_total);
    }

    public void AddRotation(Quaternion rotation,float weight,Func<Quaternion,Quaternion,float,Quaternion> interpolate)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        _rotation=_contributors==1?rotation:interpolate(_rotation,rotation,(float)((R)weight/_total));
    }

    public Matrix4x4 Apply(Matrix4x4 resolved)
    {
        if(_total==0||float.IsNaN(_total))return resolved;
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float x=_x,y=_y,z=_z;
        float Axis(float a,float b,float c,float t)=>(float)(((R)z*c+(R)y*b)+(R)x*a+t);
        resolved.M41=Axis(resolved.M11,resolved.M21,resolved.M31,resolved.M41);
        resolved.M42=Axis(resolved.M12,resolved.M22,resolved.M32,resolved.M42);
        resolved.M43=Axis(resolved.M13,resolved.M23,resolved.M33,resolved.M43);
        return EnbRetailPointGroupTransform.Compose(EnbRetailParticleHierarchyTransform.QuaternionBasis(_rotation),resolved);
    }
}
