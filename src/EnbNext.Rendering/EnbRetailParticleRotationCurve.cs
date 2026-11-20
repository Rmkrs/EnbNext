using EnbNext.Assets;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public sealed class EnbRetailParticleRotationCurve
{
    private readonly long[] _times;
    private readonly float[] _rates, _halfSlopes, _integrals;
    private readonly EnbRetailLinePrecision _precision;

    //0095FE50 stores rates per millisecond, half-slopes and boundary integrals.
    public EnbRetailParticleRotationCurve(IReadOnlyList<EnbCurrentRetailParticleScalarKey> keys,
        long lifetimeMilliseconds, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        _precision = precision;
        int last = keys.Count - 1;
        while (last > 0 && Milliseconds(keys[last], precision) >= lifetimeMilliseconds) last--;
        _times = new long[last + 1]; _rates = new float[last + 1];
        _halfSlopes = new float[last + 1]; _integrals = new float[last + 1];
        float units = BitConverter.UInt32BitsToSingle(0x3a83126f);
        for (int i = 0; i <= last; i++)
        {
            _times[i] = Milliseconds(keys[i], precision);
            _rates[i] = (float)((NativeReal)keys[i].Value * units);
        }
        for (int i = 0; i <= last; i++)
        {
            if (i < last)
                _halfSlopes[i] = (float)(((NativeReal)_rates[i + 1] - _rates[i]) / (double)(_times[i + 1] - _times[i]) * .5d);
            else if (i < keys.Count - 1)
                _halfSlopes[i] = (float)(((NativeReal)keys[i + 1].Value * units - _rates[i]) * .5d /
                    ((NativeReal)keys[i + 1].TimeSeconds * 1000d - (double)_times[i]));
            if (i > 0)
            {
                NativeReal duration = (double)(_times[i] - _times[i - 1]);
                _integrals[i] = (float)((duration * _halfSlopes[i - 1] + _rates[i - 1]) * duration + _integrals[i - 1]);
            }
        }
    }

    //00961445..0096149F keeps the quadratic and random contributions in
    //registers through256 multiplication; __ftol truncates and AL wraps.
    public byte SampleOrientation(long ageMilliseconds, float randomRatePerMillisecond = 0, float initialRandomTurns = 0)
    {
        int index = _times.Length - 1;
        while (index > 0 && ageMilliseconds < _times[index]) index--;
        if(_halfSlopes[index]==0 && _rates[index]==0 && _integrals[index]==0 && randomRatePerMillisecond==0)
            return unchecked((byte)(long)Math.Truncate(initialRandomTurns*256d));
        using var arithmetic = new EnbRetailLineArithmetic.Scope(_precision);
        NativeReal elapsed = (double)(ageMilliseconds - _times[index]);
        NativeReal turns = (elapsed * _halfSlopes[index] + _rates[index]) * elapsed;
        turns += (NativeReal)(double)ageMilliseconds * randomRatePerMillisecond;
        turns += initialRandomTurns;
        turns += _integrals[index];
        return unchecked((byte)(long)Math.Truncate((double)(turns * 256d)));
    }

    private static long Milliseconds(EnbCurrentRetailParticleScalarKey key, EnbRetailLinePrecision precision)
        => EnbRetailParticleEmissionArithmetic.Milliseconds(key.TimeSeconds, precision);
}
