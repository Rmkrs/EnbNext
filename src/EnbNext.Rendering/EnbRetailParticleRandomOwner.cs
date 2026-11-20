using System.Numerics;
using EnbNext.Assets;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Emitter-owned sampling state. Construction draws property tables;
/// births draw vectors; copying tables and observing births draw nothing.</summary>
public sealed class EnbRetailParticleRandomOwner
{
    private readonly EnbRetailParticleRandomStreams _streams;
    private readonly EnbCurrentRetailParticleEmitterDefinition _definition;
    public EnbRetailParticleBufferProperties Properties { get; }

    public EnbRetailParticleRandomOwner(EnbCurrentRetailParticleEmitterDefinition definition,
        EnbRetailParticleRandomStreams? streams = null, uint? capacity = null)
    {
        if((capacity??EnbRetailParticleEmissionArithmetic.Capacity(definition))>EnbRetailParticleAdmission.MaximumParticles)
            throw new InvalidDataException("Particle allocation exceeds the local resource budget (1000000 particles)");
        _definition = definition;
        _streams = streams ?? EnbRetailParticleRandomStreams.Process;
        lock (_streams.Gate)
        {
            var setters=new Stack<EnbCurrentRetailParticleColorProperty>();
            var initial=definition.Color;
            while(initial.BeforeRuntimeSetter is {} before)
            {setters.Push(initial);initial=before;}
            Properties = EnbRetailParticleBufferProperties.Construct(definition.Copy().SetColor(initial),
                capacity??EnbRetailParticleEmissionArithmetic.Capacity(definition), _streams.Properties);
            //0048D850 calls0095EF40 after creation; its rebuilt random table
            //consumes new words even when the resulting amplitude is unchanged.
            while(setters.TryPop(out var color))Properties.SetColor(color.Random,_streams.Properties);
        }
    }

    private EnbRetailParticleRandomOwner(EnbRetailParticleRandomOwner source)
    {
        _definition = source._definition;
        _streams = source._streams;
        Properties = source.Properties.Clone();
    }

    public EnbRetailParticleRandomOwner Clone() => new(this);

    public void SetColorRandom(Vector3 amplitude)
    {
        lock (_streams.Gate) Properties.SetColor(amplitude,_streams.Properties);
    }

    public EnbCurrentRetailParticleBirth Sample(EnbCurrentRetailParticleBirth birth,
        EnbCurrentRetailParticleEmitterDefinition? currentDefinition=null)
    {
        var definition=currentDefinition??_definition;
        using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float scale = birth.NativeBirthScale;
        float Dimension(float value) => value > 0 ? value : 0;
        EnbCurrentRetailParticleRandomizer Construct(EnbCurrentRetailParticleRandomizer randomizer)
            => randomizer with { Values = new(Dimension(randomizer.Values.X), Dimension(randomizer.Values.Y), Dimension(randomizer.Values.Z)) };
        var motion = EnbRetailParticleEmitterMotion.Construct(definition.Copy().SetCreationRandomizer(Construct(definition.CreationRandomizer)).SetVelocityRandomizer(Construct(definition.VelocityRandomizer)).SetVelocityPerSecond(birth.LocalVelocityPerSecond ?? definition.VelocityPerSecond));
        Vector3 Scale(Vector3 value) => new((float)((R)value.X * scale), (float)((R)value.Y * scale), (float)((R)value.Z * scale));
        lock (_streams.Gate)
        {
            var sampled = EnbRetailParticleBirthSampler.Sample(
                EnbRetailParticleRandomizer.Scale(motion.CreationRandomizer, scale),
                EnbRetailParticleRandomizer.Scale(motion.VelocityRandomizer, scale),
                Scale(motion.ConstantVelocityPerMillisecond), (float)((R)motion.OutwardVelocityPerMillisecond * scale),
                definition.LocalSpace, birth.NativeBirthRotation ?? EnbCurrentRetailParticleBirthTransform.ExtractOrientation(birth.World),
                birth.World.Translation, birth.InheritedVelocityPerMillisecond, _streams.Births);
            return birth with { SampledPosition = sampled.Position, SampledVelocityPerMillisecond = sampled.VelocityPerMillisecond };
        }
    }
}
