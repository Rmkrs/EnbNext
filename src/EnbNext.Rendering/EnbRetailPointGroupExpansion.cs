using System.Numerics;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>00957370 orientation tables and 00955FE0 view-space point expansion.</summary>
public static class EnbRetailPointGroupExpansion
{
    private static readonly float[] Sines = CreateSines();
    private static readonly Vector3[][] Tables24 = CreateTables(EnbRetailLinePrecision.Single24);
    private static readonly Vector3[][] Tables53 = CreateTables(EnbRetailLinePrecision.Double53);

    public static float SineTableEntry(int index) => Sines[index];
    public static Vector3 Corner(int mode, byte orientation, int corner,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
        => (precision == EnbRetailLinePrecision.Single24 ? Tables24 : Tables53)[mode][orientation * (mode == 1 ? 4 : 3) + corner];

    public static Vector3 Expand(Vector3 center, float size, int mode, byte orientation, int corner,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        var offset = Corner(mode, orientation, corner, precision);
        // Both products spill to binary32 before addition. In the normal
        // exponent range SSE's binary32 result is the same PC24/spill result;
        // retain the register path for underflow, overflow and nonfinite data.
        double px=(double)size*offset.X, py=(double)size*offset.Y;
        static bool NormalOrZero(double value) => value==0 ||
            Math.Abs(value)>=1.1754943508222875E-38 && Math.Abs(value)<=float.MaxValue;
        if(NormalOrZero(px)&&NormalOrZero(py)&&float.IsFinite(center.Z)&&float.IsFinite(size))
        {
            float x=(float)px,y=(float)py;
            double sx=(double)x+center.X,sy=(double)y+center.Y;
            if(NormalOrZero(sx)&&NormalOrZero(sy))
                return new((float)sx,(float)sy,size*offset.Z+center.Z);
        }
        return ExpandLiteral(center,size,mode,orientation,corner,precision);
    }

    internal static Vector3 ExpandLiteral(Vector3 center, float size, int mode, byte orientation, int corner,
        EnbRetailLinePrecision precision)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        var offset = Corner(mode, orientation, corner, precision);
        float x = (float)((Register)size * offset.X), y = (float)((Register)size * offset.Y);
        return new((float)((Register)x + center.X), (float)((Register)y + center.Y),
            (float)((Register)size * offset.Z + center.Z));
    }

    private static float[] CreateSines()
    {
        //008C1F68/6E/70: pre-device PC53 FILD * authored float step, FSIN, FST.
        var result = new float[1024];
        float step = BitConverter.Int32BitsToSingle(0x3bc90fdb);
        for (int i = 0; i < result.Length; i++) result[i] = (float)Math.Sin(i * (double)step);
        return result;
    }

    private static Vector3[][] CreateTables(EnbRetailLinePrecision precision)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        Vector3[][] authored = [[new(0,-2,0),new(-1.732f,1,0),new(1.732f,1,0)],
            [new(-.5f,.5f,0),new(-.5f,-.5f,0),new(.5f,-.5f,0),new(.5f,.5f,0)]];
        Vector3[][] result = [new Vector3[256 * 3], new Vector3[256 * 4]];
        Register angle = 0;
        for (int orientation = 0; orientation < 256; orientation++)
        {
            Register cosine = Lookup(angle + BitConverter.Int32BitsToSingle(0x3fc90fdb));
            Register sine = Lookup(angle);
            for (int mode = 0; mode < 2; mode++) for (int corner = 0; corner < authored[mode].Length; corner++)
            {
                var p = authored[mode][corner];
                Register y = mode == 0 ? sine * p.X + cosine * p.Y : cosine * p.Y + sine * p.X;
                result[mode][orientation * authored[mode].Length + corner] = new((float)(cosine * p.X - sine * p.Y), (float)y, p.Z);
            }
            angle += BitConverter.Int32BitsToSingle(0x3cc90fdb);
        }
        return result;
    }

    private static Register Lookup(Register angle)
    {
        float scaled = (float)(angle * BitConverter.Int32BitsToSingle(0x4322f983));
        int index = (int)Math.Floor(scaled);
        Register fraction = (Register)scaled - index;
        return ((Register)1 - fraction) * Sines[index & 1023] + fraction * Sines[(index + 1) & 1023];
    }
}
