using System.Numerics;
using Register=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailPointGroupTransform
{
    //007E6E40 composes the saved view with the local buffer model matrix.
    public static Matrix4x4 Compose(Matrix4x4 model,Matrix4x4 view,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        => EnbRetailAffineComposition.TryCompose(model,view,precision,true,out var result)
            ? result : ComposeLiteral(model,view,precision);

    internal static Matrix4x4 ComposeLiteral(Matrix4x4 model,Matrix4x4 view,EnbRetailLinePrecision precision)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float[] right=Rows(model),left=Rows(view),result=new float[12];
        for(int row=0;row<3;row++)for(int column=0;column<4;column++)
        {
            Register value=row<2
                ? (Register)left[row*4]*right[column]+(Register)left[row*4+2]*right[8+column]+(Register)left[row*4+1]*right[4+column]
                : (Register)left[row*4+2]*right[8+column]+(Register)left[row*4+1]*right[4+column]+(Register)left[row*4]*right[column];
            if(column==3)value+=left[row*4+3];
            result[row*4+column]=(float)value;
        }
        return FromRows(result);
    }

    //009554F8..0095558E uses Y/Z/X for all three coordinates, before
    //00955FE0 expands the camera-facing geometry in the same view space.
    public static Vector3 Point(Vector3 point,Matrix4x4 matrix,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if(precision==EnbRetailLinePrecision.Single24 && InSingleRange(point.X) && InSingleRange(point.Y) && InSingleRange(point.Z) &&
            InSingleRange(matrix.M11)&&InSingleRange(matrix.M12)&&InSingleRange(matrix.M13)&&
            InSingleRange(matrix.M21)&&InSingleRange(matrix.M22)&&InSingleRange(matrix.M23)&&
            InSingleRange(matrix.M31)&&InSingleRange(matrix.M32)&&InSingleRange(matrix.M33)&&
            InSingleRange(matrix.M41)&&InSingleRange(matrix.M42)&&InSingleRange(matrix.M43))
        {
            // With inputs zero or in [2^-30,2^30], every product and
            // cancellation result remains binary32-normal or exact zero.
            // Separate SIMD operations reproduce PC24's Y+Z+X order;
            // do not use Vector3.Transform's different sum order or an FMA.
            var y=new Vector3(matrix.M21,matrix.M22,matrix.M23)*point.Y;
            var z=new Vector3(matrix.M31,matrix.M32,matrix.M33)*point.Z;
            var x=new Vector3(matrix.M11,matrix.M12,matrix.M13)*point.X;
            return ((y+z)+x)+new Vector3(matrix.M41,matrix.M42,matrix.M43);
        }
        return PointLiteral(point,matrix,precision);
    }

    private static bool InSingleRange(float value)
    {
        float magnitude=MathF.Abs(value);
        return magnitude==0 || magnitude>=0.000000000931322574615478515625f && magnitude<=1073741824f;
    }

    internal static Vector3 PointLiteral(Vector3 point,Matrix4x4 matrix,EnbRetailLinePrecision precision)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float Axis(float x,float y,float z,float translation)=>(float)((Register)y*point.Y+(Register)z*point.Z+(Register)x*point.X+translation);
        return new(Axis(matrix.M11,matrix.M21,matrix.M31,matrix.M41),
            Axis(matrix.M12,matrix.M22,matrix.M32,matrix.M42),Axis(matrix.M13,matrix.M23,matrix.M33,matrix.M43));
    }

    private static float[] Rows(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43];
    public static Matrix4x4 FromRows(IReadOnlyList<float> m)=>new(m[0],m[4],m[8],0,m[1],m[5],m[9],0,m[2],m[6],m[10],0,m[3],m[7],m[11],1);
}
