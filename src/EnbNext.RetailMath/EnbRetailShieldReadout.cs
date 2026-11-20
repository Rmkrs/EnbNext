using System.Globalization;
using EnbNext.Rendering;
using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.RetailMath;

public static class EnbRetailShieldReadout
{
    // 5BD540/5BE120/5BE330 values, followed by 5BCE50 integer or %3.0f formatting.
    public static string? FormatValue(int field, float? maximum, float? percent)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24, true);
        int Truncate(R value) => unchecked((int)R.TruncateInteger64(value));
        if (field == 11 && maximum is float max)
            return Truncate((R)max).ToString(CultureInfo.InvariantCulture);
        if (field == 12 && percent is float fraction)
            return Truncate((R)fraction * (R)100f).ToString(CultureInfo.InvariantCulture);
        if (field == 10 && maximum is float currentMax && percent is float currentPercent)
        {
            R integer = R.FromInteger64(Truncate((R)currentMax), 0x7f);
            float value = (float)R.MultiplyFloatMemory(integer, BitConverter.SingleToUInt32Bits(currentPercent));
            if (value == 0) return "  0"; // Retail CRT suppresses the sign of exact zero.
            return Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture).PadLeft(3);
        }
        return null;
    }
}
