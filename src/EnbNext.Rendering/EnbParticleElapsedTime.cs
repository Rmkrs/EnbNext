namespace EnbNext.Rendering;

/// <summary>Extends successive client ticks for particle tails longer than one
/// 32-bit clock period. Observations must be less than one clock period apart.</summary>
public sealed class EnbParticleElapsedTime(uint startTick)
{
    private uint _previous=startTick;
    public long Milliseconds { get; private set; }
    public long Observe(uint tick)
    {
        Milliseconds+=unchecked(tick-_previous);
        _previous=tick;
        return Milliseconds;
    }
}
