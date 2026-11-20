using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Retail stepped and ping-pong UV mappers, plus their INI rate/period conversions.</summary>
public static class EnbCurrentRetailMapperSteppingRuntime
{
    public static float Rate(float perSecond)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (float)R.MultiplyFloatMemory(R.FromFloatBits(BitConverter.SingleToUInt32Bits(perSecond)),0x3a83126f);
    }
    public static float Rate(string text,float defaultValue)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (float)R.MultiplyFloatMemory(Operand(text,defaultValue),0x3a83126f);
    }
    public static float Period(float seconds)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        var value=R.MultiplyFloatMemory(R.FromFloatBits(BitConverter.SingleToUInt32Bits(seconds)),0x447a0000);
        if((R.CompareFloatMemory(value,0)&0x100)!=0)value=-value;
        return (float)value;
    }
    public static float Period(string text,float defaultValue)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        var value=R.MultiplyFloatMemory(Operand(text,defaultValue),0x447a0000);
        if((R.CompareFloatMemory(value,0)&0x100)!=0)value=-value;
        return (float)value;
    }
    private static R Operand(string text,float defaultValue)
    {
        if(!EnbNext.Assets.EnbRetailMapperNumber.TryParseOperand(text,out float value,out bool percentage))
            return R.FromFloatBits(BitConverter.SingleToUInt32Bits(defaultValue));
        var operand=R.FromFloatBits(BitConverter.SingleToUInt32Bits(value));
        return percentage?R.MultiplyFloatMemory(operand,0x3c23d70a):operand;
    }
    /// <summary>Stepped UV offset: whole steps accumulate from elapsed time; offsets either wrap to
    /// their fraction or, with the clamp fix, stay within ±scale.</summary>
    public static EnbW3dMapperRuntimeEvaluation Step(EnbW3dMapperStepParameters parameters, uint now,
        ref Vector2 offset, ref float elapsed, ref uint last)
    {
        uint delta = unchecked(now - last); last = now;
        float accumulated = (float)((double)delta + elapsed);
        elapsed = accumulated;
        int steps = Truncate(accumulated * parameters.StepsPerMillisecond);
        if (steps != 0)
        {
            offset = new((float)((double)steps * parameters.Step.X) + offset.X, (float)((double)steps * parameters.Step.Y) + offset.Y);
            elapsed = elapsed - (float)(steps / (double)parameters.StepsPerMillisecond);
        }
        if (!parameters.ClampFix)
            offset = new(offset.X - (float)Math.Floor(offset.X), offset.Y - (float)Math.Floor(offset.Y));
        else
            offset = new(Clamp(offset.X, parameters.Scale.X), Clamp(offset.Y, parameters.Scale.Y));
        return new(parameters.Scale, Vector2.Zero, offset, new(1, 0, 0, 1), false);

        static float Clamp(float value, float limit)
        {
            if (!(value >= -limit)) return -limit;
            return value <= limit ? value : limit;
        }
    }

    /// <summary>Ping-pong UV offset: elapsed time wraps at the period and runs back down in its
    /// second half.</summary>
    public static EnbW3dMapperRuntimeEvaluation PingPong(EnbW3dMapperPingPongParameters parameters, uint now,
        ref float elapsed, ref uint last)
    {
        uint delta = unchecked(now - last); last = now;
        float accumulated = (float)((double)delta + elapsed);
        elapsed = accumulated;
        float period = parameters.PeriodMilliseconds, half = period * 0.5f;
        Vector2 offset = Vector2.Zero;
        if (period > 0)
        {
            int cycles = Truncate(accumulated / period);
            float phase = accumulated - (float)((double)cycles * period);
            elapsed = phase;
            float position = phase > half ? period - phase : phase;
            offset = new(position * parameters.PerMillisecond.X, position * parameters.PerMillisecond.Y);
        }
        return new(parameters.Scale, Vector2.Zero, offset, new(1, 0, 0, 1), false);
    }

    // CRT ftol: truncation to a 64-bit integer, of which retail reloads the low 32 bits.
    private static int Truncate(float value)
    {
        if (!(Math.Abs((double)value) < 9.2233720368547758E18)) return 0;
        return unchecked((int)(long)value);
    }
}
