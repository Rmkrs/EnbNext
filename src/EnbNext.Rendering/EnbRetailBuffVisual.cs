using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Retained buff widget timing: 6661F0, 666440 and 665F20.</summary>
public sealed class EnbRetailBuffVisual(bool permanent, long removalTime, int sourceCount)
{
    private bool _permanent = permanent;
    private long _removalTime = removalTime;
    private int _sourceCount = sourceCount;
    public float FadeDuration { get; private set; } = 1;
    public float Phase { get; private set; }
    public float Opacity { get; private set; } = 1;

    public bool Observe(bool permanent, long removalTime, int sourceCount, uint clientTime)
    {
        // 560BC0 starts a new fade only for increased occupied source count,
        // or a future deadline extended from a previously temporary widget.
        bool changed = sourceCount > _sourceCount ||
            (!_permanent && removalTime > clientTime && removalTime > _removalTime);
        _permanent = permanent; _removalTime = removalTime; _sourceCount = sourceCount;
        if (changed) { FadeDuration = 1; Phase = 0; }
        return changed;
    }

    public void Advance(uint clientTime, uint elapsedMilliseconds)
    {
        var result = Sample(_permanent, _removalTime, clientTime, elapsedMilliseconds, FadeDuration, Phase);
        FadeDuration = result.Duration; Phase = result.Phase; Opacity = result.Opacity;
    }

    public static (float Opacity, float Duration, float Phase) Sample(bool permanent, long removalTime,
        uint clientTime, uint elapsedMilliseconds, float duration, float phase,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(precision);
        float delta = (float)((R)(double)elapsedMilliseconds * R.FromFloatBits(0x3a83126f));
        if (!permanent && phase < duration)
        {
            R next = (R)delta + (R)phase;
            phase = (float)next;
            if ((double)next > duration) return (1, 0, 0);
            return ((float)(next / (R)duration), duration, phase);
        }
        long remaining = removalTime != 0 && removalTime >= clientTime ? removalTime - clientTime : 0;
        // 665150 initializes the warning interval to 30 seconds. 665F20
        // clamps the truncated angular rate to 3..15 and retains phase.
        if (!permanent && remaining < 30000)
        {
            R rate = (R)(double)Math.Clamp(30000 - remaining, int.MinValue, int.MaxValue) * R.FromFloatBits(0x3a03126f);
            if ((double)rate < 3) rate = 3;
            else if ((double)rate > 15) rate = 15;
            int angularRate = (int)R.TruncateInteger64(rate);
            R next = (R)angularRate * (R)delta + (R)phase;
            phase = (float)next;
            return ((float)((R.Cosine(next) + (R)1) * (R).5), duration, phase);
        }
        return (1, 0, 0);
    }
}
