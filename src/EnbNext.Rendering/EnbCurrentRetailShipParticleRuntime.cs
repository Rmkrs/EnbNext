using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbCurrentRetailParticleBirth(long Time, long Ordinal, Matrix4x4 World)
{
    // 0097EA4B..EA5F reads emitter velocity at birth, before world rotation.
    public Vector3? LocalVelocityPerSecond { get; init; }
    public Vector3 InheritedVelocityPerMillisecond { get; init; }
    public long EmissionRun { get; init; }
    public Quaternion? NativeBirthRotation { get; init; }
    public float NativeBirthScale { get; init; } = 1;
    //Supplied by the retained buffer's physical slot, never ordinal modulo capacity.
    public EnbRetailParticleBufferProperties.Offsets? NativePropertyOffsets { get; init; }
    public int? NativeBufferSlot { get; init; }
    public Vector3? NativeBufferPosition { get; init; }
    public Vector3? NativeBufferVelocityPerMillisecond { get; init; }
    public Vector3? SampledPosition { get; init; }
    public Vector3? SampledVelocityPerMillisecond { get; init; }
}

/// <summary>Bounded persistent ship particle birth history (sprites, jets and engine trails). 0097E530 uses a
/// strict interval; 0097F390 resumes with a fresh previous pose but preserves
/// the interval remainder. Render views only sample these immutable births.</summary>
public sealed class EnbCurrentRetailShipParticleRuntime
{
    private readonly long _interval, _lifetime;
    private readonly uint _burst;
    private readonly uint _capacity;
    private readonly List<EnbCurrentRetailParticleBirth> _births = [];
    private long? _last;
    private long _remainder;
    private Matrix4x4 _previous;
    private bool _emitting;
    private readonly float _inheritance;
    private readonly bool _localSpace;
    private readonly float _nativeBirthScale;
    private readonly EnbRetailParticleRandomOwner? _random;
    private readonly EnbRetailParticleBufferRing? _slots;
    public Matrix4x4 CurrentWorld { get; private set; } = Matrix4x4.Identity;
    public IReadOnlyList<EnbCurrentRetailParticleBirth> Births => _births;
    public EnbRetailParticleMotionHistory MotionHistory {get;}=new();
    public long EmittedCount { get; private set; }
    public long EmissionRun { get; private set; }
    public bool IsEmitting => _emitting;

    public EnbCurrentRetailShipParticleRuntime(EnbCurrentRetailParticleEmitterDefinition d, bool allowInheritedMotion = false,float nativeBirthScale=1f,
        EnbRetailParticleRandomStreams? randomStreams=null)
        : this(d,allowInheritedMotion,nativeBirthScale,randomStreams,false) { }

    public static void Validate(EnbCurrentRetailParticleEmitterDefinition definition,bool allowInheritedMotion=false)
        => _ = new EnbCurrentRetailShipParticleRuntime(definition,allowInheritedMotion,1,null,true);

    private EnbCurrentRetailShipParticleRuntime(EnbCurrentRetailParticleEmitterDefinition d, bool allowInheritedMotion,float nativeBirthScale,
        EnbRetailParticleRandomStreams? randomStreams,bool validationOnly)
    {
        _nativeBirthScale=nativeBirthScale;
        EnbRetailParticleAdmission.Validate(d);
        if (d.MaximumEmissions != 0)
            throw new InvalidDataException("Continuous particle owner requires unlimited emission");
        // 0097D19C..D1A6: x87 division goes directly to __ftol, without a float spill.
        _interval = EnbRetailParticleEmissionArithmetic.Interval(d.EmissionRatePerSecond);
        _lifetime = EnbRetailParticleEmissionArithmetic.Milliseconds(d.LifetimeSeconds);
        _burst = d.RuntimeBurstSize;
        _capacity = EnbRetailParticleEmissionArithmetic.Capacity(d);
        _inheritance = d.VelocityInheritanceFactor;
        _localSpace = d.LocalSpace;
        _remainder = EnbRetailParticleEmissionArithmetic.InitialMilliseconds(d.InitialEmissionTimeSeconds);
        if(!validationOnly)
        {
            _random=new(d,randomStreams);
            _slots=new(checked((int)_capacity),checked((uint)_lifetime),0);
        }
    }

    public void Advance(long time, Matrix4x4 world, bool emitting, Vector3? localVelocityPerSecond = null)
    {
        if (localVelocityPerSecond is {} velocity && !Finite(velocity))
            throw new InvalidDataException("Particle birth velocity must be finite");
        if (time < 0 || (_last.HasValue && time < _last)) throw new InvalidDataException("Exhaust clock moved backwards");
        if (!Matrix4x4.Decompose(world, out var scale, out var rotation, out var position) ||
            !Finite(position) || !Finite(scale) || Vector3.Distance(scale, Vector3.One) > .001f ||
            !float.IsFinite(rotation.LengthSquared()) || MathF.Abs(rotation.LengthSquared() - 1) > .001f)
            throw new InvalidDataException("Exhaust requires a finite rigid attachment pose");
        rotation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(world);
        if (_last == time) return; // A second view/redraw must not advance births.
        if (emitting && !_emitting) EmissionRun = unchecked((byte)(EmissionRun + 1));
        _births.RemoveAll(b => time - b.Time >= _lifetime);
        if ((_last.HasValue || (_localSpace && _remainder > 0)) && emitting)
        {
            long before = _last ?? time;
            if (!_emitting) _previous = world;
            var oldRotation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(_previous);
            var oldPosition = _previous.Translation;
            long delta = time - before;
            delta = EnbRetailParticleEmissionArithmetic.ReduceFrame(unchecked((uint)delta), unchecked((uint)_interval), _capacity, _burst);
            using var arithmetic = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float inverseDelta = (float)((EnbRetailLineArithmetic.Register)1d / (double)delta);
            // 0097E623..0097E688, added after rotation at 0097EB2B..0097EB45.
            // Restart's fresh previous pose intentionally gives zero inherited velocity.
            float inheritanceFactor = (float)((EnbRetailLineArithmetic.Register)inverseDelta * _inheritance);
            float Inherit(float from, float to) => (float)(((EnbRetailLineArithmetic.Register)to - from) * inheritanceFactor);
            Vector3 inherited = _localSpace || _inheritance == 0 ? Vector3.Zero : new(Inherit(oldPosition.X, position.X),
                Inherit(oldPosition.Y, position.Y), Inherit(oldPosition.Z, position.Z));
            long elapsed = checked(_remainder + delta);
            long count = elapsed <= _interval ? 0 : (elapsed - 1) / _interval;
            EnbRetailParticleAdmission.ValidateWork(checked(count*_burst));
            _remainder = elapsed - count * _interval;
            float fraction = (float)((EnbRetailLineArithmetic.Register)1d - (EnbRetailLineArithmetic.Register)(double)elapsed * inverseDelta);
            float fractionStep = (float)((EnbRetailLineArithmetic.Register)(double)_interval * inverseDelta);
            for (long i = 0; i < count; i++)
            {
                fraction = (float)((EnbRetailLineArithmetic.Register)fraction + fractionStep);
                long birth = time - (elapsed - (i + 1) * _interval);
                float t = _localSpace ? 0 : fraction;
                var nativeRotation = _localSpace ? Quaternion.Identity : EnbCurrentRetailParticleBirthTransform.Interpolate(oldRotation, rotation, t);
                float Coordinate(float from, float to) => (float)(((EnbRetailLineArithmetic.Register)to - from) * t + from);
                var pose = _localSpace ? Matrix4x4.Identity : EnbCurrentRetailParticleBirthTransform.Create(nativeRotation) *
                    Matrix4x4.CreateTranslation(new Vector3(Coordinate(oldPosition.X, position.X), Coordinate(oldPosition.Y, position.Y), Coordinate(oldPosition.Z, position.Z)));
                for (uint j = 0; j < _burst; j++)
                    _slots!.Enqueue(_random!.Sample(new(birth, checked(EmittedCount + i * _burst + j), pose)
                        { InheritedVelocityPerMillisecond = inherited, EmissionRun = EmissionRun,
                            LocalVelocityPerSecond = localVelocityPerSecond, NativeBirthRotation = _localSpace ? null : nativeRotation,
                            NativeBirthScale=_nativeBirthScale }));
            }
            EmittedCount = checked(EmittedCount + count * _burst);
        }
        _last = time; _previous = world; _emitting = emitting;
        _slots!.Advance(unchecked((uint)time));
        _births.Clear();
        _births.AddRange(_slots.LiveParticles.Select(p=>p.Birth with
        {NativeBufferSlot=p.Slot,NativePropertyOffsets=_random!.Properties.AtSlot(p.Slot)}));
        if (emitting) CurrentWorld = world;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
