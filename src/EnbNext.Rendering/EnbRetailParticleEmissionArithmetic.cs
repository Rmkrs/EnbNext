using EnbNext.Assets;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailParticleEmissionArithmetic
{
    public static uint InitialMilliseconds(float seconds, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
        //0097D29F..D2FB: unordered/nonpositive input becomes zero;
        // positive input multiplies by1000 directly into __ftol's low word.
        => seconds > 0 ? Milliseconds(seconds, precision) : 0;

    public static uint Milliseconds(float seconds, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        //0095BA9B..BADC and0095F21D..F236 multiply then __ftol.
        return unchecked((uint)NativeInteger((double)((Register)seconds*1000d)));
    }

    public static long LifetimeMilliseconds(float seconds)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        // Preserve the established finite-debris 64-bit ownership policy.
        // Ordinary lifetimes still match0095BA9B..BADC exactly; retail stores
        // only the low word after the same __ftol conversion.
        return (long)(double)((Register)seconds*1000d);
    }

    public static uint Interval(float rate, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        if (!(rate > 0)) return 1000;
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        //0097D19C..D1B2 goes directly from FDIV to __ftol.
        return unchecked((uint)NativeInteger((double)((Register)1000d / rate)));
    }

    public static uint Capacity(EnbCurrentRetailParticleEmitterDefinition definition,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        //0097D346..396: lifetime+1, signed burst multiply, rate multiply,
        // __ftol, finite allowance cap, then minimum2.
        //0097D2EA..32E substitutes1 for nonpositive/unordered capacity lifetime.
        //0097D364 uses FIMUL of a signed32-bit burst word.
        float lifetime = definition.LifetimeSeconds > 0 ? definition.LifetimeSeconds : 1f;
        Register count = ((Register)lifetime + 1d) * (double)unchecked((int)definition.RuntimeBurstSize) * definition.EmissionRatePerSecond;
        int capacity = unchecked((int)NativeInteger((double)count));
        if (definition.MaximumEmissions > 0) capacity = Math.Min(capacity, definition.MaximumEmissions);
        return (uint)Math.Max(2, capacity);
    }

    private static long NativeInteger(double value)
    {
        //00A264EC sets truncation mode and executes FISTP qword. Invalid
        // conversion produces the signed64 integer-indefinite word, not
        // the runtime's saturating double-to-long conversion.
        const double upperExclusive = 9223372036854775808d;
        return double.IsNaN(value) || value >= upperExclusive || value < -upperExclusive
            ? long.MinValue : (long)value;
    }

    internal static uint ReduceFrame(uint delta, uint interval, uint capacity, uint burst)
    {
        //0097E540..59D /0097EB8C..BEA: unsigned frame comparison;
        // native capacity / gcd(capacity,burst) supplies the repeat window.
        if (delta <= unchecked(interval * 100u)) return delta;
        uint a = capacity, b = burst;
        while (b != 0) { uint remainder = a % b; a = b; b = remainder; }
        uint window = unchecked(capacity / a * interval);
        return window <= 1 ? 1u : delta % window;
    }
}
