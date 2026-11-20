using System.Numerics;

namespace EnbNext.Rendering;

internal static class EnbRetailAffineComposition
{
    // Keep PC24's extended exponent and each routine's addition order. The
    // exceptional-input oracle remains managed, including its NaN policy.
    internal static bool TryCompose(Matrix4x4 local, Matrix4x4 parent,
        EnbRetailLinePrecision precision, bool reverseLastRow, out Matrix4x4 result)
    {
        Span<float> right=stackalloc float[12] {local.M11,local.M21,local.M31,local.M41,local.M12,local.M22,local.M32,local.M42,local.M13,local.M23,local.M33,local.M43};
        Span<float> left=stackalloc float[12] {parent.M11,parent.M21,parent.M31,parent.M41,parent.M12,parent.M22,parent.M32,parent.M42,parent.M13,parent.M23,parent.M33,parent.M43};
        foreach(float value in right)if(!float.IsFinite(value)){result=default;return false;}
        foreach(float value in left)if(!float.IsFinite(value)){result=default;return false;}
        Span<float> output=stackalloc float[12];
        if(precision==EnbRetailLinePrecision.Single24 && SingleRange(right) && SingleRange(left))
        {
            // PC24 rounds after each operation. In this range products and
            // cancellation results remain normal (or exact zero), so binary32
            // has the same result without saving/restoring the x87 state.
            // Keep X+Z+Y / Z+Y+X order and separate products; no FMA.
            var rightX=new Vector4(right[0],right[1],right[2],right[3]);
            var rightY=new Vector4(right[4],right[5],right[6],right[7]);
            var rightZ=new Vector4(right[8],right[9],right[10],right[11]);
            for(int row=0;row<3;row++)
            {
                // Four independent columns share the same operation order.
                // Separate SIMD multiply/add instructions preserve PC24 rounding;
                // only translation receives the final parent addition.
                Vector4 x=rightX*left[row*4];
                Vector4 z=rightZ*left[row*4+2];
                Vector4 y=rightY*left[row*4+1];
                Vector4 value=reverseLastRow && row==2 ? (z+y)+x : (x+z)+y;
                value.W+=left[row*4+3];
                value.CopyTo(output.Slice(row*4,4));
            }
        }
        else
        {
            // Binary32 operands have at most 48 product bits and exponents
            // within binary64's normal range, even for subnormal inputs.
            // Binary64 therefore reproduces PC53, including the final FST32.
            // PC24 outside the binary32-safe range rounds each operation to a
            // 24-bit significand while keeping the register exponent.
            bool single=precision==EnbRetailLinePrecision.Single24;
            double R(double value)=>single?EnbRetailLineArithmetic.Register.RoundPc24(value):value;
            for(int row=0;row<3;row++)for(int column=0;column<4;column++)
            {
                double x=R((double)left[row*4]*right[column]);
                double z=R((double)left[row*4+2]*right[8+column]);
                double y=R((double)left[row*4+1]*right[4+column]);
                double value=reverseLastRow && row==2 ? R(R(z+y)+x) : R(R(x+z)+y);
                if(column==3)value=R(value+left[row*4+3]);
                output[row*4+column]=(float)value;
            }
        }
        result=new(output[0],output[4],output[8],0,output[1],output[5],output[9],0,output[2],output[6],output[10],0,output[3],output[7],output[11],1);
        return true;
    }

    // Nonzero operands in [2^-51, 2^60] produce products in [2^-102, 2^120].
    // PC24 product spacing is at least 2^-125, so cancellation remains normal
    // (or zero). Three products plus translation stay below 2^122. Thus every
    // separately rounded operation fits binary32 without exponent loss.
    private static bool SingleRange(ReadOnlySpan<float> values)
    {
        foreach(float value in values)
        {
            float magnitude=MathF.Abs(value);
            if(magnitude!=0 && (magnitude<4.44089209850062616169452667236328125e-16f || magnitude>1152921504606846976f))return false;
        }
        return true;
    }

}
