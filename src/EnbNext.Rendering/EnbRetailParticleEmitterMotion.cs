using System.Numerics;
using EnbNext.Assets;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

//0097D180 initializes emitter vectors and scales its owned velocity randomizer.
public readonly record struct EnbRetailParticleEmitterMotion(
    EnbCurrentRetailParticleRandomizer CreationRandomizer,
    EnbCurrentRetailParticleRandomizer VelocityRandomizer,
    Vector3 ConstantVelocityPerMillisecond,
    Vector3 AccelerationPerMillisecondSquared,
    float OutwardVelocityPerMillisecond)
{
    public static EnbRetailParticleEmitterMotion Construct(EnbCurrentRetailParticleEmitterDefinition definition,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        float multiplier = BitConverter.UInt32BitsToSingle(0x3a83126f);
        Vector3 value = definition.VelocityPerSecond;
        return new(definition.CreationRandomizer,
            EnbRetailParticleRandomizer.Scale(definition.VelocityRandomizer, multiplier, precision),
            new((float)((NativeReal)value.X * multiplier), (float)((NativeReal)value.Y * multiplier), (float)((NativeReal)value.Z * multiplier)),
            ConvertUnits(definition.AccelerationPerSecondSquared, 1000000f, precision),
            (float)((NativeReal)definition.OutwardVelocityPerSecond * multiplier));
    }

    //0097D450: X/Y use the spilled reciprocal; Z retains the FDIV result.
    public static Vector3 ConvertUnits(Vector3 value, float units,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        NativeReal reciprocal = (NativeReal)1 / units;
        float stored = (float)reciprocal;
        return new((float)((NativeReal)stored * value.X), (float)((NativeReal)stored * value.Y), (float)(reciprocal * value.Z));
    }
}
