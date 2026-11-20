using System.Numerics;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Camera-owned BDB1C0, seeded at 00444240; independent of mapper C00720.
/// 009D0410 words, 009D0510 inclusive rejection sampling, 009D0580 live x87 draws.</summary>
public sealed class EnbRetailCameraRandomSource
{
    public static EnbRetailCameraRandomSource Process { get; } = new();
    private readonly Func<uint> _word;
    public EnbRetailCameraRandomSource(uint seed = 0x1105)
        => _word = new EnbW3dRetailMapperRandomSource(seed).NextUInt32;
    public EnbRetailCameraRandomSource(Func<uint> word) => _word = word;
    public int Next(int min, int max)
    {
        if (min == max) return min;
        if (min > max) (min, max) = (max, min);
        uint range = unchecked((uint)(max - min));
        uint mask = uint.MaxValue >> BitOperations.LeadingZeroCount(range);
        uint value;
        do value = _word() & mask; while (value > range);
        return unchecked(min + (int)value);
    }
    // 00795E7C..87 is signed remainder, including negative quadrants.
    public int GateQuadrant() => unchecked((int)_word()) % 4;
    private R Unit() => R.MultiplyFloatMemory((R)(double)_word(), 0x2f800000);
    private static R F(float value) => R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
    public float Scale(float scale)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        return (float)(Unit() * F(scale));
    }
    public float Range(float scale, float offset, float origin = 0)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        return (float)(Unit() * F(scale) + F(offset) + F(origin));
    }
    public float Degrees(float scale, float offset = 0)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        return (float)R.MultiplyFloatMemory(Unit() * F(scale) + F(offset), 0x3c8efa35);
    }
    public float LootAngle()
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        return (float)(Unit() * R.MultiplyFloatMemory(F(45), 0x3c8efa35));
    }
    public float Centered(float scale, float origin = 0)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        return (float)((Unit() - F(.5f)) * F(scale) + F(origin));
    }
    //79827C..79828B stores A without popping the value used to derive B.
    public (float First,float Opposite) WarpDepartureAngles()
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        R angle=(Unit()-F(.5f))*F(.62831855f);
        return ((float)angle,(float)(angle+F(MathF.PI)));
    }
    //7965D3..7965E2 retains the random range until after the radius multiply.
    public float TakeoffAhead(float radius)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (float)((Unit()*F(7)+F(10))*F(radius));
    }
    public float CenteredDegrees(float scale)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        return (float)R.MultiplyFloatMemory((Unit() - F(.5f)) * F(scale), 0x3c8efa35);
    }
}
