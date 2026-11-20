using System.Numerics;

namespace EnbNext.Rendering;

public static class EnbCurrentRetailMapperBumpRuntime
{
    // Retail's 1024-entry sine table (one full turn), indexed with linear interpolation.
    private static readonly Lazy<float[]> Sines = new(() =>
    {
        byte[] bytes = EnbCurrentRetailSineTableStorage.Build(EnbRetailLinePrecision.Double53).Sines;
        var values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++) values[i] = BitConverter.ToSingle(bytes, i * 4);
        return values;
    });
    private const double TwoPi = 6.2831854820251465; // retail double 0x401921FB60000000
    private const float HalfPi = 1.5707964f, TableScale = 162.97466f, MillisecondsToSeconds = 0.001f;

    public static float Rate(float rotation)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (float)EnbRetailExtendedX87Register.MultiplyFloatMemory(
            EnbRetailExtendedX87Register.FromFloatBits(BitConverter.SingleToUInt32Bits(rotation)),0x40c90fdb);
    }

    /// <summary>Rotating bump-environment matrix (cos, -sin, sin, cos) × scale.</summary>
    public static Vector4 Evaluate(EnbW3dMapperBumpParameters parameters, uint now, ref float angle, ref uint last)
    {
        uint delta = unchecked(now - last); last = now;
        float advanced = (float)((double)delta * parameters.RadiansPerSecond) * MillisecondsToSeconds + angle;
        double wrapped = advanced % TwoPi;
        angle = (float)wrapped;
        float cosine = Lookup((float)(wrapped + HalfPi) * TableScale) * parameters.Scale;
        float sine = Lookup((float)(wrapped * TableScale)) * parameters.Scale;
        return new(cosine, -sine, sine, cosine);
    }

    private static float Lookup(float position)
    {
        int index = (int)MathF.Floor(position);
        float fraction = position - index;
        var table = Sines.Value;
        return (1f - fraction) * table[index & 0x3ff] + fraction * table[(index + 1) & 0x3ff];
    }
}
