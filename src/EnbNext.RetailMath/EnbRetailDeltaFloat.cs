using EnbNext.Rendering;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.RetailMath;

public static class EnbRetailDeltaFloat
{
    // 004A6380: resolved property values; cache/notification ownership is
    // outside this arithmetic boundary. FILD retains the integer through FMUL.
    public static float Evaluate(float start, float rate, long end, uint time)
    {
        if (rate == 0) return start;
        if (end <= time) return rate < 0 ? 0 : 1;
        long remaining = end - time;
        int duration = (int)Math.Min(remaining, int.MaxValue);
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        R product = R.MultiplyFloatMemory(R.FromInteger64(duration, 0x7f), BitConverter.SingleToUInt32Bits(rate));
        float value = (float)(rate < 0 ? -product : (R)1 - product);
        return rate < 0 ? MathF.Min(MathF.Min(value, 1), start) : MathF.Max(MathF.Max(value, 0), start);
    }
}
