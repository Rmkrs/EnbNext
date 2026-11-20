namespace EnbNext.Rendering;

/// <summary>6BD270 / 6BD420 finite authored-font sound ownership.</summary>
public sealed class EnbRetailFontSoundLifetime
{
    private uint _updatedAt;
    public int RemainingMilliseconds { get; private set; }
    public bool Retained { get; private set; }
    public bool Stopped { get; private set; }

    public EnbRetailFontSoundLifetime(float durationSeconds, uint now)
    {
        _updatedAt = now;
        Retained = durationSeconds >= 0;
        if (Retained)
            RemainingMilliseconds = unchecked((int)(long)(durationSeconds * 1000f));
    }

    public bool Advance(uint now)
    {
        if (!Retained) return Stopped;
        uint delta = unchecked(now - _updatedAt);
        _updatedAt = now;
        if (RemainingMilliseconds < 60000)
            RemainingMilliseconds = unchecked(RemainingMilliseconds - (int)delta);
        if (RemainingMilliseconds < 0)
        {
            Retained = false;
            Stopped = true;
            RemainingMilliseconds = 0;
        }
        return Stopped;
    }
}
