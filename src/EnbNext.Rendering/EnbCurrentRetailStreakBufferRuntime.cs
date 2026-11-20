using System.Numerics;
using EnbNext.Assets;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>0097E530/0097E810 birth sampling and 00960F80 streak evaluation.
/// Bounded additive mode-3/4 family, with constant acceleration and no random
/// property curves except authored color, size, frame and blur variation. Births are independent of the emitter after creation.
/// Property tables and birth vectors consume the native process streams.</summary>
public sealed class EnbCurrentRetailStreakBufferRuntime
{
    private readonly EnbCurrentRetailParticleEmitterDefinition _definition;
    private readonly float _scale;
    private readonly Vector3 _acceleration;
    private readonly EnbRetailParticleRandomOwner _random;
    private readonly EnbRetailParticleBufferRuntime _buffer;
    private readonly uint _interval, _lifetime;
    private readonly Curve _opacity, _size, _blur, _frame;
    private readonly int _capacity;
    private uint? _lastTime;
    private uint _lastSampleTime;
    private uint _remainder;
    private Vector3 _position;
    private Quaternion _rotation;
    private bool _emitting;
    private int _remaining;
    private bool _completed;
    private Matrix4x4 _localWorld = Matrix4x4.Identity;

    public long EmittedParticleCount { get; private set; }
    public int RetainedParticleCount => _buffer.CountLivingRecords(Math.Max(_lastTime??0,_lastSampleTime));
    public EnbRetailParticleBufferRing.State BufferState => _buffer.State;
    public IReadOnlyList<EnbCurrentRetailParticleBirth> LiveBirths => _buffer.LiveBirths;
    public uint LastTimeMilliseconds => _lastTime ?? 0;
    public bool IsEmitting => _emitting && !_completed;

    public EnbCurrentRetailStreakBufferRuntime(EnbCurrentRetailParticleEmitterDefinition definition, float scale, uint seed, EnbRetailParticleRandomStreams? randomStreams=null)
    {
        Validate(definition);
        if (!float.IsFinite(scale) || scale <= 0) throw new InvalidDataException("Streak scale must be finite and positive");
        _definition = definition; _scale = scale;
        using(var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24))
        {
            //0097D450 divides by1e6 through a stored reciprocal;0095E8B0
            // subsequently scales the buffer acceleration once.
            float reciprocal=(float)((Register)1d/1000000d);
            float Acceleration(float value)=>(float)((Register)(float)((Register)value*reciprocal)*scale);
            var a=definition.AccelerationPerSecondSquared;
            _acceleration=new(Acceleration(a.X),Acceleration(a.Y),Acceleration(a.Z));
        }
        _remaining=definition.MaximumEmissions;
        // 0097D19C..D1A6 divides in x87 and truncates without a float spill.
        _interval = EnbRetailParticleEmissionArithmetic.Interval(definition.EmissionRatePerSecond);
        _lifetime = EnbRetailParticleEmissionArithmetic.Milliseconds(definition.LifetimeSeconds);
        _remainder = EnbRetailParticleEmissionArithmetic.InitialMilliseconds(definition.InitialEmissionTimeSeconds);
        _opacity = Scalar(definition.Opacity); _size = Scalar(definition.Size); _blur = Scalar(definition.BlurTime ?? new(0,[new(0,0)]));
        _frame=Scalar(definition.Frame);
        _capacity=checked((int)EnbRetailParticleEmissionArithmetic.Capacity(definition));
        _random=new(definition,randomStreams);
        _buffer=new(_random.Properties,_lifetime,0,_acceleration);
    }
    public static void Validate(EnbCurrentRetailParticleEmitterDefinition d)
    {
        EnbRetailParticleAdmission.Validate(d);
        if(d.RenderMode is not (3 or 4))throw new InvalidDataException("Streak buffer requires mode3/4");
        // Decoder guarantees authored ordering. Validate manually constructed definitions too.
        CheckTimes(d.Color.Keys.Select(k => k.TimeSeconds));
        foreach (var p in new[] { d.Opacity, d.Size, d.Rotation, d.Frame, d.BlurTime }.OfType<EnbCurrentRetailParticleScalarProperty>())
        {
            CheckTimes(p.Keys.Select(k => k.TimeSeconds));
            if (p.Keys.Any(k => !float.IsFinite(k.Value))) throw new InvalidDataException("Non-finite streak curve");
        }
    }

    /// <summary>Supply the current attachment's rigid world pose, with its
    /// translation already composed through the scaled hierarchy. Local emitter
    /// positions, velocities and widths receive the source radius exactly once.</summary>
    public void Advance(uint timeMilliseconds, Matrix4x4 rigidWorld, bool emitting = true)
    {
        if ((_lastTime is uint last && timeMilliseconds < last) || timeMilliseconds < _lastSampleTime)
            throw new InvalidDataException("Streak emitter clock moved backwards");
        if (!Matrix4x4.Decompose(rigidWorld, out var scale, out var rotation, out var position) ||
            !Finite(position) || !Finite(scale) || Vector3.Distance(scale, Vector3.One) > 1e-4f ||
            !float.IsFinite(rotation.LengthSquared()) || MathF.Abs(rotation.LengthSquared() - 1) > 1e-4f ||
            !float.IsFinite(rigidWorld.M14 + rigidWorld.M24 + rigidWorld.M34 + rigidWorld.M44) ||
            MathF.Abs(rigidWorld.M14) > 1e-6f || MathF.Abs(rigidWorld.M24) > 1e-6f ||
            MathF.Abs(rigidWorld.M34) > 1e-6f || MathF.Abs(rigidWorld.M44 - 1) > 1e-6f)
            throw new InvalidDataException("Streak emitter requires a finite rigid world pose");


        // 0097F446 restores the allowance only when a completed emitter
        // is enabled again; remaining visible never restarts a finite burst.
        if(emitting && !_emitting && _completed)
        { _remaining=_definition.MaximumEmissions;_completed=false; }
        if (emitting) _localWorld=rigidWorld;
        if (emitting && !_completed && ((_lastTime is uint previous && timeMilliseconds > previous) ||
            (_definition.LocalSpace && _lastTime is null && _remainder > _interval)))
        {
            uint before=_lastTime ?? timeMilliseconds;
            // 0097F390 resets the old pose when enabling an emitter; do not
            // inherit displacement accumulated while it was hidden/stopped.
            if (!_emitting) { _position = position; _rotation = rotation; }
            uint delta = EnbRetailParticleEmissionArithmetic.ReduceFrame(timeMilliseconds-before,_interval,(uint)_capacity,_definition.RuntimeBurstSize);
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            float inverseDelta=(float)((Register)1d/(double)delta);
            float inheritance=(float)((Register)inverseDelta*_definition.VelocityInheritanceFactor);
            float Inherit(float a,float b)=>(float)(((Register)b-a)*inheritance);
            var inherited = _definition.LocalSpace || _definition.VelocityInheritanceFactor==0 ? Vector3.Zero :
                new Vector3(Inherit(_position.X,position.X),Inherit(_position.Y,position.Y),Inherit(_position.Z,position.Z));
            uint elapsed = unchecked(_remainder + delta);
            ulong events = elapsed<=_interval ? 0 : _interval==0 ? ((ulong)_remaining+_definition.RuntimeBurstSize-1)/_definition.RuntimeBurstSize : (elapsed - 1) / _interval; // strict interval < remainder
            if(_definition.MaximumEmissions>0) events=Math.Min(events,((ulong)_remaining+_definition.RuntimeBurstSize-1)/_definition.RuntimeBurstSize);
            ulong emitted=events*_definition.RuntimeBurstSize;
            if(_definition.MaximumEmissions>0) emitted=Math.Min(emitted,(ulong)_remaining);
            EnbRetailParticleAdmission.ValidateWork(checked((long)emitted));
            uint remainder = (uint)(elapsed - events * _interval);
            float fraction=(float)((Register)1d-(Register)(double)elapsed*inverseDelta);
            float fractionStep=(float)((Register)(double)_interval*inverseDelta);
            long firstOrdinal = EmittedParticleCount;
            for (ulong i = 0; i < events; i++)
            {
                fraction=(float)((Register)fraction+fractionStep);
                uint birth = unchecked((uint)(timeMilliseconds - (elapsed - (i + 1) * _interval)));
                // 009661F9/0097DEF0: world startup records the pose before the
                // next callback emits. Prewarm can precede that pose and wrap
                // the uint birth clock; derive the signed factor from its age.
                float Coordinate(float a,float b)=>(float)(((Register)b-a)*fraction+a);
                var bornPosition = _definition.LocalSpace ? Vector3.Zero :
                    new Vector3(Coordinate(_position.X,position.X),Coordinate(_position.Y,position.Y),Coordinate(_position.Z,position.Z));
                var bornRotation = _definition.LocalSpace ? Quaternion.Identity :
                    EnbCurrentRetailParticleBirthTransform.Interpolate(_rotation, rotation, fraction);
                for (uint j = 0; j < Math.Min(_definition.RuntimeBurstSize,emitted-i*_definition.RuntimeBurstSize); j++)
                {
                    var sampled=_random.Sample(new(birth,firstOrdinal++,Matrix4x4.CreateTranslation(bornPosition))
                    {NativeBirthRotation=bornRotation,NativeBirthScale=_scale,InheritedVelocityPerMillisecond=inherited});
                    _buffer.Enqueue(sampled,sampled.SampledPosition!.Value,sampled.SampledVelocityPerMillisecond!.Value);
                }
            }
            EmittedParticleCount = checked(EmittedParticleCount + (long)emitted);
            if(_definition.MaximumEmissions>0) {_remaining-=(int)emitted;_completed=_remaining==0;}
            _remainder = remainder;
        }
        _lastTime = timeMilliseconds; _position = position; _rotation = rotation; _emitting = emitting;
    }

    public IReadOnlyList<EnbCurrentRetailStreakSample> Sample(uint timeMilliseconds)
    {
        if ((_lastTime is uint last && timeMilliseconds < last) || timeMilliseconds < _lastSampleTime)
            throw new InvalidDataException("Streak sample precedes the emitter clock");
        _lastSampleTime = timeMilliseconds;
        _buffer.Advance(timeMilliseconds);
        var births=_buffer.LiveBirths;
        var samples = new EnbCurrentRetailStreakSample[births.Count];
        for (int i = 0; i < births.Count; i++)
        {
            var birth = births[i];
            uint age = unchecked(timeMilliseconds - (uint)birth.Time);
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            //0095D8E0 consumes queued births before advancing existing arrays.
            // The ordinary0097D910 loader passes zero history selector at
            //0097DA01, forwarded to buffer+208 by0097D3C5/3CB/0095BCE4.
            var head=birth.NativeBufferPosition!.Value;
            var velocity=birth.NativeBufferVelocityPerMillisecond!.Value;
            var offsets=birth.NativePropertyOffsets!.Value;
            // 00960F80: tail = current position - velocity/ms * blur seconds * 1000.
            // No history lookup, age clamp, or emitter-relative endpoint.
            // 00961C00–00961C94 adds blur randomness before multiplying
            // velocity, without the non-negative clamp used for widths.
            float blur=(float)((Register)_blur.At(age).X+offsets.BlurTime);
            float Tail(float p,float v)=>(float)((Register)p-(Register)(float)((Register)blur*v)*1000d);
            var tail = new Vector3(Tail(head.X,velocity.X),Tail(head.Y,velocity.Y),Tail(head.Z,velocity.Z));
            // 0097ECB0 births stay local; 0097DD90 forwards the current
            // emitter matrix to the buffer. Removal freezes its last transform.
            if(_definition.LocalSpace)
            {
                head=Vector3.Transform(head,_localWorld);
                tail=Vector3.Transform(tail,_localWorld);
            }
            var rgb = Vector3.Clamp(EnbRetailParticleColorCurve.Sample(_definition.Color.Keys,age,_lifetime)+offsets.Color, Vector3.Zero, Vector3.One);
            var color = new EnbRenderColor32(Byte(rgb.X), Byte(rgb.Y), Byte(rgb.Z), Byte((float)((Register)_opacity.At(age).X+offsets.Opacity)));
            // 009634F0 returns case 2 for a texture: 0095E500 copies current
            // RGBA to the tail. Untextured ONE/ONE remains case 0, zero RGBA.
            // 00961393–009613DC adds the retained slot offset to the curve,
            // then clamps negative widths to zero.
            float size = MathF.Max(0, _size.At(age).X+offsets.Size) * _scale;
            if (!Finite(head) || !Finite(tail) || !float.IsFinite(size)) throw new InvalidDataException("Non-finite streak sample");
            // 0095E500 passes frame floats to LineGroup, but no orientation
            // array: sprite initial-orientation randomness does not rotate streaks.
            samples[i] = new(head, tail, size, color, EnbCurrentRetailStreakRuntime.TailColor(_definition,color))
                {Frame=_frame.At(age).X+offsets.Frame};
        }
        return samples;
    }

    private Curve Scalar(EnbCurrentRetailParticleScalarProperty p) => new(p.Keys.Select(k => (k.TimeSeconds, new Vector4(k.Value))), _lifetime);
    private static byte Byte(float value) => (byte)Math.Clamp((int)(value * 255f), 0, 255);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static void CheckTimes(IEnumerable<float> times)
    {
        var values = times.ToArray();
        if (values.Length == 0 || values[0] != 0 || values.Any(t => !float.IsFinite(t) || t < 0) ||
            values.Zip(values.Skip(1)).Any(p => p.First > p.Second)) throw new InvalidDataException("Invalid streak curve times");
    }

    // 0095EF40/00960920 compile millisecond keys; 00960F80 chooses the LAST
    // key <= age. Equal-time keys are retained, but their zero-length interval
    // is never sampled. The first key at/after lifetime supplies the final slope
    // using its authored float time (it is not an active integer-time key).
    private sealed class Curve
    {
        private readonly uint[] _times;
        private readonly Vector4[] _values, _slopes;
        public Curve(IEnumerable<(float Time, Vector4 Value)> source, uint lifetime)
        {
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            var keys = source.ToArray();
            int count = 1;
            // Native setup preserves PC24 through multiply and __ftol.
            while (count < keys.Length && EnbRetailParticleEmissionArithmetic.Milliseconds(keys[count].Time) < lifetime) count++;
            _times = new uint[count]; _values = new Vector4[count]; _slopes = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                _times[i] = EnbRetailParticleEmissionArithmetic.Milliseconds(keys[i].Time); _values[i] = keys[i].Value;
                if (i + 1 == keys.Length) continue;
                Register next = i + 1 < count ? (double)EnbRetailParticleEmissionArithmetic.Milliseconds(keys[i + 1].Time) : (Register)keys[i + 1].Time * 1000d;
                Register duration = next - (double)_times[i];
                var a=keys[i].Value; var b=keys[i+1].Value;
                _slopes[i] = new Vector4(
                    (float)(((Register)b.X-a.X)/duration),(float)(((Register)b.Y-a.Y)/duration),
                    (float)(((Register)b.Z-a.Z)/duration),(float)(((Register)b.W-a.W)/duration));
            }
        }
        public Vector4 At(uint age)
        {
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            int i = _times.Length - 1;
            while (age < _times[i]) i--;
            float Channel(float value,float slope)=>(float)((Register)slope*(double)(age-_times[i])+value);
            var v=_values[i];var s=_slopes[i];return new(Channel(v.X,s.X),Channel(v.Y,s.Y),Channel(v.Z,s.Z),Channel(v.W,s.W));
        }
    }
}
