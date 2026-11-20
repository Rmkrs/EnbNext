using System.Buffers.Binary;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Literal sine/reciprocal initialization tail008C1F56..008C1FA2.
/// The preceding initialization calls of whole008C1F10 remain separate.</summary>
public static class EnbCurrentRetailSineTableStorage
{
    public static (byte[] Sines,byte[] Reciprocals) Build(
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Double53)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        byte[] sines=new byte[4096],reciprocals=new byte[4096];
        // Current retail SHA249f8524...651c4, retail-sine-table-rate-current.
        // XOR EAX,EAX; MOV ECX,7F7FFFFF; FILD(index); FMUL B0C61C; FSIN;
        // FST(sine); index>0: FLD AE7240 / FDIV ST0,ST1 / FSTP reciprocal;
        // index0: FSTP ST0 / store ECX. Both branches increment until400h.
        for(int index=0;index<1024;index++)
        {
            R argument=(double)index;
            R sine=R.Sine(R.MultiplyFloatMemory(argument,0x3bc90fdb));
            BinaryPrimitives.WriteUInt32LittleEndian(sines.AsSpan(index*4,4),BitConverter.SingleToUInt32Bits((float)sine));
            uint reciprocal=index==0?0x7f7fffffu:BitConverter.SingleToUInt32Bits((float)(R.FromFloatBits(0x3f800000)/sine));
            BinaryPrimitives.WriteUInt32LittleEndian(reciprocals.AsSpan(index*4,4),reciprocal);
        }
        return(sines,reciprocals);
    }
}
