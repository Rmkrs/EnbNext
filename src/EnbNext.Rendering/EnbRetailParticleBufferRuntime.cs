using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>00960F10 retained single-position-array buffer, with explicit native queued vectors.</summary>
public sealed class EnbRetailParticleBufferRuntime
{
    private readonly EnbRetailParticleBufferRing _ring;
    private readonly EnbRetailParticleBufferProperties _properties;
    private readonly Vector3[] _positions, _velocities;
    private readonly Vector3 _acceleration;
    private readonly EnbRetailLinePrecision _precision;
    private readonly uint _lifetime;
    public EnbRetailParticleBufferRing.State State => _ring.CurrentState;
    public Vector3 PositionAtSlot(int slot) => _positions[slot];
    public Vector3 VelocityAtSlot(int slot) => _velocities[slot];
    public uint TimeAtSlot(int slot) => _ring.StoredTime(slot);
    public int CountLivingRecords(uint clock) => _ring.CountLivingRecords(clock);

    public EnbRetailParticleBufferRuntime(EnbRetailParticleBufferProperties properties, uint lifetime,
        uint initialClock, Vector3 accelerationPerMillisecondSquared, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        ArgumentNullException.ThrowIfNull(properties);
        _properties = properties;
        _lifetime = lifetime;
        _acceleration = accelerationPerMillisecondSquared;
        _precision = precision;
        _ring = new(checked((int)properties.Capacity), lifetime, initialClock);
        _positions = new Vector3[properties.Capacity];
        _velocities = new Vector3[properties.Capacity];
    }

    public void Enqueue(EnbCurrentRetailParticleBirth birth, Vector3 position, Vector3 velocityPerMillisecond)
        => _ring.Enqueue(birth with { NativeBufferPosition = position, NativeBufferVelocityPerMillisecond = velocityPerMillisecond });

    public void Advance(uint clock)
        => _ring.Advance(clock,
            (slot, birth, age) => (_positions[slot], _velocities[slot]) = EnbRetailParticleKinematics.Initialize(
                birth.NativeBufferPosition!.Value, birth.NativeBufferVelocityPerMillisecond!.Value, _acceleration, age, _precision),
            (slot, birth, delta) => (_positions[slot], _velocities[slot]) = EnbRetailParticleKinematics.Advance(
                _positions[slot], _velocities[slot], _acceleration, delta, precision:_precision));

    public IReadOnlyList<EnbCurrentRetailParticleBirth> LiveBirths => _ring.LiveParticles.Select(p => p.Birth with
    {
        Time = p.StoredTime,
        NativeBufferSlot = p.Slot,
        NativeBufferPosition = _positions[p.Slot],
        NativeBufferVelocityPerMillisecond = _velocities[p.Slot],
        NativePropertyOffsets = _properties.AtSlot(p.Slot)
    }).ToArray();

    //0095C360 copies property arrays but resets queue/live state and captures the copy clock.
    public EnbRetailParticleBufferRuntime Clone(uint copyClock)
        => new(_properties.Clone(), _lifetime, copyClock, _acceleration, _precision);
}
