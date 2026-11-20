using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Per-voice Doppler history, sampled from coherent presented world transforms.</summary>
public sealed class EnbCurrentRetailEngineDopplerRuntime
{
    private readonly EnbRetailSoundDefinition _definition;
    private Vector3 _source, _listener, _filtered;
    private uint _time;
    private bool _seeded;
    public float Pitch { get; private set; }

    public EnbCurrentRetailEngineDopplerRuntime(EnbRetailSoundDefinition definition)
    {
        if (EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(definition) is string error)
            throw new ArgumentException(error, nameof(definition));
        _definition = definition;
        Pitch = definition.EffectivePitch;
    }

    public float Update(Vector3 source, Vector3 listener, uint sampleTime, bool enabled)
    {
        if (!Finite(source) || !Finite(listener))
        {
            _seeded = false;
            _filtered = Vector3.Zero;
            return Pitch = _definition.EffectivePitch;
        }
        uint elapsed = unchecked(sampleTime - _time);
        bool disabled = !enabled || (_definition.EffectiveMinDopplerShift == 1 && _definition.EffectiveMaxDopplerShift == 1);
        if (!_seeded || disabled || elapsed > int.MaxValue)
        {
            _seeded = true;
            _source = source; _listener = listener; _time = sampleTime;
            _filtered = Vector3.Zero;
            return Pitch = _definition.EffectivePitch;
        }
        // A repeated compositor frame is not a new motion sample.
        if (elapsed == 0) return Pitch;
        Vector3 sourceVelocity = (source - _source) / elapsed;
        Vector3 listenerVelocity = (listener - _listener) / elapsed;
        bool sourceMoved = source != _source;
        _source = source; _listener = listener; _time = sampleTime;
        // 009F7E80 calls the pitch update only for a changed source translation.
        // Unlike the old callback cache, stationary listener samples have zero
        // velocity, rather than indefinitely retaining an earlier movement.
        if (!sourceMoved) return Pitch;
        var step = EnbCurrentRetailDopplerMath.Step(sourceVelocity, listenerVelocity,
            listener - source, _filtered, Pitch, elapsed, _definition.EffectiveDopplerTweak,
            _definition.EffectivePitch * _definition.EffectiveMinDopplerShift,
            _definition.EffectivePitch * _definition.EffectiveMaxDopplerShift);
        _filtered = step.FilteredMotion;
        return Pitch = step.Pitch;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
