using System.Numerics;
using System.Runtime.InteropServices;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Retail 3x4 affine rows (row r: three basis terms, then the
/// translation): rigid inverse, product and box transform at x87 PC24
/// with the retail operation order.</summary>
internal static class EnbRetailAffineRows
{
    public static void Rows(Matrix4x4 matrix,Span<float> rows)
    {
        rows[0]=matrix.M11;rows[1]=matrix.M21;rows[2]=matrix.M31;rows[3]=matrix.M41;
        rows[4]=matrix.M12;rows[5]=matrix.M22;rows[6]=matrix.M32;rows[7]=matrix.M42;
        rows[8]=matrix.M13;rows[9]=matrix.M23;rows[10]=matrix.M33;rows[11]=matrix.M43;
    }
    public static Matrix4x4 Matrix(ReadOnlySpan<float> rows)
        => new(rows[0],rows[4],rows[8],0,rows[1],rows[5],rows[9],0,rows[2],rows[6],rows[10],0,rows[3],rows[7],rows[11],1);

    public static Matrix4x4 RigidInverse(Matrix4x4 matrix)
    {
        Span<float> rows=stackalloc float[12],inverse=stackalloc float[12];
        Rows(matrix,rows);RigidInverse(rows,inverse);return Matrix(inverse);
    }
    /// <summary>008C3B20: transposed rotation, then the negated rotated translation.</summary>
    public static void RigidInverse(ReadOnlySpan<float> m,Span<float> d)
    {
        d[0]=m[0];d[1]=m[4];d[2]=m[8];
        d[4]=m[1];d[5]=m[5];d[6]=m[9];
        d[8]=m[2];d[9]=m[6];d[10]=m[10];
        float tx=Loaded(m[3]),ty=Loaded(m[7]),tz=Loaded(m[11]);
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R z=((R)d[10]*tz+(R)d[8]*tx)+((R)d[9]*ty);
        R y=((R)d[6]*tz+(R)d[4]*tx)+((R)d[5]*ty);
        R x=((R)d[2]*tz+(R)d[1]*ty)+((R)tx*d[0]);
        d[3]=Negated((float)x);d[7]=Negated((float)y);d[11]=Negated((float)z);
    }
    /// <summary>first * second. The destination may alias either input.</summary>
    public static void Multiply(ReadOnlySpan<float> first,ReadOnlySpan<float> second,Span<float> destination)
        => EnbCurrentRetailHierarchyPoseRuntime.Multiply(MemoryMarshal.AsBytes(first),MemoryMarshal.AsBytes(second),MemoryMarshal.AsBytes(destination));

    /// <summary>008C2F30: every center accumulation and absolute extent
    /// accumulation is stored as a float after each column.</summary>
    public static (Vector3 Center,Vector3 Extent) TransformBox(ReadOnlySpan<float> m,Vector3 center,Vector3 extent,bool nativeNaNs=false)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,nativeNaNs);
        Vector3 outCenter=default,outExtent=default;
        for(int row=0;row<3;row++)
        {
            float target=m[row*4+3],half=0;
            for(int column=0;column<3;column++)
            {
                R product=(R)center[column]*m[row*4+column];
                target=(float)(product+target);
                float size=(float)((R)extent[column]*m[row*4+column]);
                float positive=BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(size)&0x7fffffff);
                half=(float)((R)positive+half);
            }
            outCenter[row]=target;outExtent[row]=half;
        }
        return (outCenter,outExtent);
    }
    // FLD of a stored float quiets a signaling NaN before the value is used.
    private static float Loaded(float value)=>BitConverter.UInt32BitsToSingle(EnbRetailX87Load.LoadedFloatWord(BitConverter.SingleToUInt32Bits(value)));
    private static float Negated(float value)=>BitConverter.UInt32BitsToSingle(EnbRetailX87Load.LoadedFloatWord(BitConverter.SingleToUInt32Bits(value))^0x80000000);
}
