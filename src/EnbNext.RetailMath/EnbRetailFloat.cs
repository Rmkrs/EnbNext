using System;

namespace EnbNext.Rendering;

/// <summary>Single-precision helpers shared by retail-equivalent geometry code.</summary>
public static class EnbRetailFloat
{
    /// <summary>Retail reciprocal square root: fixed seed, three Newton steps, single precision.</summary>
    public static float ReciprocalSquareRoot(float square)
    {
        uint bits = BitConverter.SingleToUInt32Bits(square);
        float half = BitConverter.UInt32BitsToSingle(unchecked(bits - 0x800000));
        float value = BitConverter.UInt32BitsToSingle(unchecked(0xBE6EB508u - bits) >> 1);
        float squared = value * value * half;
        float first = 1.5f - squared;
        float corrected = squared * first * first;
        float second = 1.5f - corrected;
        return value * first * second * (1.5f - corrected * second * second);
    }
}
