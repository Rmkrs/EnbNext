using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail sine texture mapper 00934010: time accumulates in radians
/// (2pi/1000 per millisecond); each axis offset is amplitude*sin(time*frequency+phase*pi).</summary>
public static class EnbCurrentRetailMapperSineRuntime
{
    private static readonly float Rate = BitConverter.UInt32BitsToSingle(0x3bcde32e); // 0B0012C
    private static readonly float Pi = BitConverter.UInt32BitsToSingle(0x40490fdb);   // 0AE78F8

    public static EnbW3dMapperRuntimeEvaluation Evaluate(EnbW3dMapperSineParameters parameters,
        uint now, ref float time, ref uint last)
    {
        float accumulated = (float)((double)unchecked(now - last) * Rate) + time;
        time = accumulated; last = now;
        float u = accumulated * parameters.UFrequency + parameters.UPhase * Pi;
        float v = accumulated * parameters.VFrequency + parameters.VPhase * Pi;
        float uOffset = (float)(Math.Sin(u) * (double)parameters.UAmplitude);
        float vOffset = (float)(Math.Sin(v) * (double)parameters.VAmplitude);
        return new(parameters.Scale, Vector2.Zero, new(uOffset, vOffset), new(1, 0, 0, 1), false);
    }
}
