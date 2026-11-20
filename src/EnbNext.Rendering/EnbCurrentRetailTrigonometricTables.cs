using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>008C1F17..008C1FA2 pre-device PC53 table loops after the separate
/// object setup. Table entries retain the authored step and float stores.</summary>
public static class EnbCurrentRetailTrigonometricTables
{
    private static readonly float[] InverseCosines=CreateInverseCosines();

    public static float InverseCosineEntry(int index)=>InverseCosines[index+512];
    public static float SineEntry(int index)=>EnbRetailPointGroupExpansion.SineTableEntry(index);

    private static float[] CreateInverseCosines()
    {
        var result=new float[1024];
        for(int index=0;index<result.Length;index++)
        {
            double value=(index-512)*(double)BitConverter.UInt32BitsToSingle(0x3b000000);
            //00A26B58..66: (1+x)*(1-x), FSQRT, then FPATAN.
            result[index]=(float)Math.Atan2(Math.Sqrt((1+value)*(1-value)),value);
        }
        return result;
    }

    /// <summary>Finite-domain00A26B20/00A26B3D arithmetic with incoming PC24.
    /// Error-manager side effects belong to the CRT owner, not this value seam.</summary>
    public static double InverseCosineSinglePrecision(double value)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R positive=(R)1+value,negative=(R)1-value;
        return Math.Atan2((double)R.Sqrt(negative*positive),value);
    }
}
