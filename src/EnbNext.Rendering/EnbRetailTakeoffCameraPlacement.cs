using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>796FF2..797077 initial view and7965E5..796671 relocated view.</summary>
public static class EnbRetailTakeoffCameraPlacement
{
    private static R F(float value)=>R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
    public static Vector3 Evaluate(Matrix4x4 basis,float angle,float shipRadius,float ahead,bool relocated,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float sine=(float)R.Sine(F(angle)),cosine=(float)R.Cosine(F(angle));
        R side=relocated?F(cosine)*F(1.5f)*F(shipRadius):F((float)(F(cosine)*F(shipRadius)*F(1.5f)));
        float up=(float)(relocated?F(sine)*F(1.5f)*F(shipRadius):F(sine)*F(shipRadius)*F(1.5f));
        float Row(float vertical,float lateral,float forward,float translation)
        {
            R sum=F(vertical)*F(up)+F(lateral)*side;
            if(relocated)sum+=F(forward)*F(ahead);
            return (float)(sum+F(translation));
        }
        return new(Row(basis.M31,basis.M21,basis.M11,basis.M41),Row(basis.M32,basis.M22,basis.M12,basis.M42),Row(basis.M33,basis.M23,basis.M13,basis.M43));
    }
}
