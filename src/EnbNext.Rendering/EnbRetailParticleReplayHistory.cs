using EnbNext.Assets;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retains sampled births for the finite authored-pose consumers.
/// Repeated views replay records without consuming either random stream.</summary>
public sealed class EnbRetailParticleReplayHistory
{
    private readonly EnbRetailParticleRandomOwner _random;
    private readonly EnbCurrentRetailParticleEmitterDefinition _definition;
    private readonly EnbRetailParticleBufferRing _slots;
    private long _emitted;
    public long EmittedCount => _emitted;
    private readonly long _lifetime;
    private readonly List<EnbCurrentRetailParticleBirth>? _longLived;
    public EnbRetailParticleMotionHistory Motion { get; } = new();

    private EnbRetailParticleReplayHistory(EnbRetailParticleReplayHistory source)
    {
        _definition=source._definition;_lifetime=source._lifetime;
        _random=source._random.Clone();
        _slots=new(checked((int)_random.Properties.Capacity),EnbRetailParticleEmissionArithmetic.Milliseconds(_definition.LifetimeSeconds),0);
        if(source._longLived is not null)_longLived=[];
    }

    public EnbRetailParticleReplayHistory(EnbCurrentRetailParticleEmitterDefinition definition,
        EnbRetailParticleRandomStreams? streams=null)
    {
        _definition=definition;
        _lifetime=EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(definition.LifetimeSeconds);
        // Preserve the existing admitted finite-debris wide lifetime policy.
        // The native uint ring remains authoritative for ordinary lifetimes.
        uint? capacity=null;
        if(_lifetime>uint.MaxValue)
        {
            capacity=checked((uint)Math.Max(2,definition.MaximumEmissions));
            _longLived=[];
        }
        _random=new(definition,streams,capacity);
        _slots=new(checked((int)_random.Properties.Capacity),EnbRetailParticleEmissionArithmetic.Milliseconds(definition.LifetimeSeconds),0);
    }

    public void ObserveFinite(long clock,Func<long,Matrix4x4> world,Func<long,Vector3>? inherited=null)
    {
        uint interval=EnbRetailParticleEmissionArithmetic.Interval(_definition.EmissionRatePerSecond);
        long initial=EnbRetailParticleEmissionArithmetic.InitialMilliseconds(_definition.InitialEmissionTimeSeconds);
        long elapsed=clock+initial;
        long count=elapsed==0?0:interval==0?_definition.MaximumEmissions:checked((elapsed-1)/interval*_definition.RuntimeBurstSize);
        if(_definition.MaximumEmissions>0)count=Math.Min(count,_definition.MaximumEmissions);
        Prepare(count,clock,ordinal=>
        {
            long time=(ordinal/_definition.RuntimeBurstSize+1)*interval-initial;
            var pose=world(time);
            Matrix4x4.Decompose(pose,out var scale,out _,out _);
            return new(time,ordinal,pose) {NativeBirthScale=scale.X,
                NativeBirthRotation=EnbCurrentRetailParticleBirthTransform.ExtractOrientation(pose),
                InheritedVelocityPerMillisecond=inherited?.Invoke(time)??Vector3.Zero};
        });
    }

    public IReadOnlyList<EnbCurrentRetailParticleBirth> Prepare(long emitted,long clock,
        Func<long,EnbCurrentRetailParticleBirth> birth)
    {
        if(emitted<_emitted)throw new InvalidDataException("Particle replay emission count moved backwards");
        EnbRetailParticleAdmission.ValidateWork(emitted-_emitted);
        if(_longLived is not null)
        {
            for(;_emitted<emitted;_emitted++)
            {
                int slot=(int)(_emitted%_random.Properties.Capacity);
                _longLived.Add(_random.Sample(birth(_emitted)) with
                    {NativeBufferSlot=slot,NativePropertyOffsets=_random.Properties.AtSlot(slot)});
            }
            _longLived.RemoveAll(b=>clock-b.Time>=_lifetime);
            return _longLived;
        }
        for(;_emitted<emitted;_emitted++)_slots.Enqueue(_random.Sample(birth(_emitted)));
        _slots.Advance(unchecked((uint)clock));
        return _slots.LiveParticles.Select(p=>p.Birth with
        {NativeBufferSlot=p.Slot,NativePropertyOffsets=_random.Properties.AtSlot(p.Slot)}).ToArray();
    }
}
