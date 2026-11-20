namespace EnbNext.Rendering;

/// <summary>009FDD20 -> 009FDD90 -> 009FDE80 finite-voice clock.</summary>
public sealed class EnbRetailSpeechClock(uint started, uint duration, int loops)
{
    private readonly bool _finite = loops != 0;
    public uint Started { get; set; } = started;
    public uint Duration { get; } = duration;
    public int RemainingLoops { get; private set; } = loops;
    public uint Elapsed { get; private set; }
    public bool Complete { get; private set; }

    public void Advance(uint now)
    {
        if (Complete || !_finite || Duration == 0) return;
        Elapsed = unchecked(now - Started);
        if (Elapsed <= Duration) return;
        uint traversed = Elapsed / Duration;
        Elapsed %= Duration;
        Started = unchecked(now - Elapsed);
        RemainingLoops = unchecked(RemainingLoops - (int)traversed);
        Complete = RemainingLoops < 1;
    }
}
