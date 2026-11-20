using System.Numerics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>008C2E80 min/max and008C2F30 center/extent transforms.
/// Twelve row-major words are retained from the concrete hierarchy owner.</summary>
public static class EnbCurrentRetailAffineBounds
{
    public static (Vector3 Minimum, Vector3 Maximum) MinMax(ReadOnlySpan<float> matrix, Vector3 minimum, Vector3 maximum)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 low = default, high = default;
        for (int row = 0; row < 3; row++)
        {
            float lower = (float)(R)matrix[row * 4 + 3], upper = lower;
            for (int column = 0; column < 3; column++)
            {
                R first = (R)minimum[column] * matrix[row * 4 + column];
                float second = (float)((R)matrix[row * 4 + column] * maximum[column]);
                bool less = first < second || double.IsNaN((double)first) || float.IsNaN(second);
                lower = (float)((less ? first : (R)second) + lower);
                upper = (float)((less ? (R)second : first) + upper);
            }
            low[row] = lower; high[row] = upper;
        }
        return (low, high);
    }

    public static (Vector3 Center, Vector3 Extent) CenterExtent(ReadOnlySpan<float> matrix, Vector3 center, Vector3 extent)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 resultCenter = default, resultExtent = default;
        for (int row = 0; row < 3; row++)
        {
            float position = matrix[row * 4 + 3], radius = 0;
            for (int column = 0; column < 3; column++)
            {
                position = (float)((R)center[column] * matrix[row * 4 + column] + position);
                float product = (float)((R)extent[column] * matrix[row * 4 + column]);
                float absolute = BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(product) & 0x7fffffff);
                radius = (float)((R)absolute + radius);
            }
            resultCenter[row] = position; resultExtent[row] = radius;
        }
        return (resultCenter, resultExtent);
    }
}
