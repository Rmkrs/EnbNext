using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Shared consumer admission. The ceiling is a local allocation policy,
/// not a retail format limit; finite allowance bounds the native buffer.</summary>
public static class EnbRetailParticleAdmission
{
    public const int MaximumParticles = 1_000_000;

    internal static void ValidateWork(long births)
    {
        if(births>MaximumParticles)throw new InvalidDataException("Particle callback exceeds the local emission work budget (1000000 births)");
    }

    public static void Validate(EnbCurrentRetailParticleEmitterDefinition d)
    {
        if (d.RenderMode > 4 || d.CreationRandomizer.ClassId > 3 || d.VelocityRandomizer.ClassId > 3 ||
            d.MaximumEmissions < 0 || d.RuntimeBurstSize == 0 ||
            !float.IsFinite(d.EmissionRatePerSecond) || !float.IsFinite(d.LifetimeSeconds) || d.LifetimeSeconds <= 0 ||
            (double)d.LifetimeSeconds * 1000 >= long.MaxValue - 10000d ||
            !float.IsFinite(d.InitialEmissionTimeSeconds) || !float.IsFinite(d.VelocityInheritanceFactor) ||
            !float.IsFinite(d.OutwardVelocityPerSecond) || !Finite(d.VelocityPerSecond) ||
            !Finite(d.CreationRandomizer.Values) || !Finite(d.VelocityRandomizer.Values) || !Finite(d.AccelerationPerSecondSquared))
            throw new InvalidDataException("Particle definition has invalid emitter state");
        if (d.RenderMode == 2 && !EnbCurrentRetailParticleEmitterRuntime.SupportsJetLine(d))
            throw new InvalidDataException("Particle line has invalid renderer data");
        // 0097E530 cannot terminate an unlimited zero-interval emission loop.
        if (d.MaximumEmissions == 0 && EnbRetailParticleEmissionArithmetic.Interval(d.EmissionRatePerSecond) == 0)
            throw new InvalidDataException("Unlimited zero-interval particle emission cannot terminate");
        if (d.RuntimeBurstSize > MaximumParticles || EnbRetailParticleEmissionArithmetic.Capacity(d) > MaximumParticles)
            throw new InvalidDataException("Particle allocation exceeds the local resource budget (1000000 particles)");
        if(!Finite(d.Color.Random) || !float.IsFinite(d.InitialOrientationRandom))
            throw new InvalidDataException("Non-finite particle property");
        foreach(var property in new[]{d.Opacity,d.Size,d.Rotation,d.Frame,d.BlurTime}.OfType<EnbCurrentRetailParticleScalarProperty>())
            if(!float.IsFinite(property.Random) || property.Keys.Count==0 || property.Keys.Any(k=>!float.IsFinite(k.TimeSeconds)||!float.IsFinite(k.Value)))
                throw new InvalidDataException("Invalid particle property curve");
        // Gravity/elasticity are legacy file fields, not inputs to 0097D180/0095BA90.
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
