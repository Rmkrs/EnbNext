using System.Numerics;
using Register=EnbNext.Rendering.EnbRetailLineArithmetic.Register;
namespace EnbNext.Rendering;

/// <summary>Shared008D2710 extraction and008D2C10 construction, without normalization.</summary>
public static class EnbRetailQuaternionConversions
{
    public static Quaternion ExtractOrientation(Matrix4x4 matrix,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        ReadOnlySpan<float> m = [matrix.M11, matrix.M21, matrix.M31, matrix.M41,
            matrix.M12, matrix.M22, matrix.M32, matrix.M42,
            matrix.M13, matrix.M23, matrix.M33, matrix.M43];
        Register trace = (Register)m[5] + m[0] + m[10];
        if (trace > 0)
        {
            Register root = Register.Sqrt(trace + 1d);
            float w = (float)(root * .5d);
            Register factor = (Register).5d / root;
            return new((float)(((Register)m[9] - m[6]) * factor),
                (float)(((Register)m[2] - m[8]) * factor),
                (float)(((Register)m[4] - m[1]) * factor), w);
        }
        int i = m[5] > m[0] ? 1 : 0;
        if (m[10] > m[i * 5]) i = 2;
        int j = (i + 1) % 3, k = (j + 1) % 3;
        Register squareRoot = Register.Sqrt((Register)m[i * 5] - ((Register)m[k * 5] + m[j * 5]) + 1d);
        Span<float> q = stackalloc float[4];
        q[i] = (float)(squareRoot * .5d);
        Register multiplier = squareRoot;
        if (squareRoot != 0 && !double.IsNaN((double)squareRoot)) multiplier = (Register).5d / squareRoot;
        q[3] = (float)(((Register)m[j + k * 4] - m[k + j * 4]) * multiplier);
        q[j] = (float)(((Register)m[i + j * 4] + m[j + i * 4]) * multiplier);
        q[k] = (float)(((Register)m[i + k * 4] + m[k + i * 4]) * multiplier);
        return new(q[0], q[1], q[2], q[3]);
    }

    public static Matrix4x4 QuaternionBasis(Quaternion q,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float zz=(float)((Register)q.Z*q.Z),xy=(float)((Register)q.X*q.Y),wz=(float)((Register)q.W*q.Z);
        Register yy=(Register)q.Y*q.Y,wy=(Register)q.W*q.Y,xz=(Register)q.X*q.Z;
        float xx=(float)((Register)q.X*q.X),zy=(float)((Register)q.Z*q.Y),wx=(float)((Register)q.W*q.X);
        float Twice(Register value)=>(float)(value+value);
        float Diagonal(Register value)=>(float)((Register)1d-(value+value));
        return FromRows([
            Diagonal(yy+zz),Twice((Register)xy-wz),Twice(xz+wy),0,
            Twice((Register)wz+xy),Diagonal((Register)xx+zz),Twice((Register)zy-wx),0,
            Twice(xz-wy),Twice((Register)wx+zy),Diagonal((Register)xx+yy),0]);
    }

    private static Matrix4x4 FromRows(ReadOnlySpan<float> m)=>new(m[0],m[4],m[8],0,m[1],m[5],m[9],0,m[2],m[6],m[10],0,m[3],m[7],m[11],1);
}