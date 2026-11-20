using System.Numerics;
using Register=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailParticleHierarchyTransform
{
    //0098A0A0 scales the stored authored translations, leaving the basis intact.
    public static Matrix4x4 ScaleTranslation(Matrix4x4 local,float scale,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        local.M41=(float)((Register)local.M41*scale);
        local.M42=(float)((Register)local.M42*scale);
        local.M43=(float)((Register)local.M43*scale);
        return local;
    }

    //008C2CD0 accumulates X+Z+Y in every row (unlike007E6E40 row2).
    public static Matrix4x4 Compose(Matrix4x4 local,Matrix4x4 parent,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        => EnbRetailAffineComposition.TryCompose(local,parent,precision,false,out var result)
            ? result : ComposeLiteral(local,parent,precision);

    internal static Matrix4x4 ComposeLiteral(Matrix4x4 local,Matrix4x4 parent,EnbRetailLinePrecision precision)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float[] right=Rows(local),left=Rows(parent),result=new float[12];
        for(int row=0;row<3;row++)for(int column=0;column<4;column++)
        {
            Register value=(Register)left[row*4]*right[column]+(Register)left[row*4+2]*right[8+column]+(Register)left[row*4+1]*right[4+column];
            if(column==3)value+=left[row*4+3];
            result[row*4+column]=(float)value;
        }
        return EnbRetailPointGroupTransform.FromRows(result);
    }

    //00989755..7CD: X/Y scale products spill; Z stays in x87. Translation
    //uses Z+Y+X;007E6E40 then applies the008D2C10 sampled quaternion basis.
    public static Matrix4x4 ApplyAnimation(Matrix4x4 resolved,Vector3 translation,Quaternion rotation,float scale,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float x=(float)((Register)translation.X*scale),y=(float)((Register)translation.Y*scale);
        Register z=(Register)translation.Z*scale;
        float Axis(float a,float b,float c,float t)=>(float)(z*c+(Register)y*b+(Register)x*a+t);
        resolved.M41=Axis(resolved.M11,resolved.M21,resolved.M31,resolved.M41);
        resolved.M42=Axis(resolved.M12,resolved.M22,resolved.M32,resolved.M42);
        resolved.M43=Axis(resolved.M13,resolved.M23,resolved.M33,resolved.M43);
        return EnbRetailPointGroupTransform.Compose(QuaternionBasis(rotation,precision),resolved,precision);
    }

    //008D2C10's explicit product spills; the quaternion is consumed as stored.
    public static Matrix4x4 QuaternionBasis(Quaternion q,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        =>EnbRetailQuaternionConversions.QuaternionBasis(q,precision);
    private static float[] Rows(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43];
}
