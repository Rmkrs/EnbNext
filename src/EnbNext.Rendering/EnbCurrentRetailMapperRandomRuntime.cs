using System.Numerics;
using X = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

public static class EnbCurrentRetailMapperRandomRuntime
{
    /// <summary>Randomized UV mapper: each whole event draws a new angle and offset pair from the
    /// shared retail generator; between events the offsets scroll and wrap.</summary>
    public static EnbW3dMapperRuntimeEvaluation Evaluate(EnbW3dMapperRandomizedParameters parameters,
        EnbW3dRetailMapperRandomSource source, uint now, ref float elapsed, ref uint last,
        ref float angle, ref float first, ref float second)
    {
        uint delta = unchecked(now - last); last = now;
        float accumulated = (float)((double)delta + elapsed);
        elapsed = accumulated;
        float events = parameters.EventsPerMillisecond;
        if (events != 0)
        {
            int count = Truncate(accumulated * events);
            if (count != 0)
            {
                var drawn = source.NextRandomizedState();
                angle = drawn.AngleRadians; first = drawn.FirstOffset; second = drawn.SecondOffset;
                elapsed = elapsed - (float)(count / (double)events);
            }
        }
        X input = X.FromFloatBits(BitConverter.SingleToUInt32Bits(angle));
        X cosine = X.Cosine(input), sine = X.Sine(input);
        uint sx = BitConverter.SingleToUInt32Bits(parameters.Scale.X), sy = BitConverter.SingleToUInt32Bits(parameters.Scale.Y);
        float u = parameters.PerMillisecond.X * elapsed + first, v = parameters.PerMillisecond.Y * elapsed + second;
        return new(new((float)X.MultiplyFloatMemory(cosine, sx), (float)X.MultiplyFloatMemory(cosine, sy)),
            new((float)-X.MultiplyFloatMemory(sine, sy), (float)X.MultiplyFloatMemory(sine, sx)),
            new((float)((double)u % 1d), (float)((double)v % 1d)), new(1, 0, 0, 1), false);
    }

    // CRT ftol: truncation to a 64-bit integer, of which retail reloads the low 32 bits.
    private static int Truncate(float value)
    {
        if (!(Math.Abs((double)value) < 9.2233720368547758E18)) return 0;
        return unchecked((int)(long)value);
    }
}
