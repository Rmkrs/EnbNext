namespace EnbNext.Rendering;

/// <summary>009D0080/009D0180's 250-word XOR generator and inclusive rejection sampling.</summary>
public sealed class EnbRetailMovieRandom
{
    // 00444200 seeds shared sound-choice/damage BDADC8 with zero. 006E2BA0 seeds BE67E8 with the
    // CRT rand() result (default startup state 1 yields 41).
    public static EnbRetailMovieRandom Damage { get; } = new(0);
    public static EnbRetailMovieRandom Shake { get; } = new(EnbRetailCrtRandomSource.Current.Next());
    private readonly uint[] _state = new uint[250];
    private int _first, _second = 103;
    private readonly object _gate = new();

    public EnbRetailMovieRandom(uint seed)
    {
        for (uint i = 0; i < _state.Length; i++) _state[i] = unchecked((uint)EnbRetailSignedRandom.Word(seed, i));
    }

    public uint NextWord()
    {
        lock(_gate)
        {
            uint value=_state[_first]^=_state[_second];
            if(++_first==250)_first=0;
            if(++_second==250)_second=0;
            return value;
        }
    }

    public int Next(int first, int second)
    {
        if (first == second) return first;
        int lower = Math.Min(first, second), upper = Math.Max(first, second);
        uint width = unchecked((uint)(upper - lower));
        int bit = 31;
        while (bit > 0 && (width & (1u << bit)) == 0) bit--;
        uint mask = bit == 31 ? uint.MaxValue : (1u << (bit + 1)) - 1;
        uint sample = unchecked(width + 1);
        lock (_gate)
            while (sample > width)
            {
                sample = NextWord() & mask;
            }
        return unchecked(lower + (int)sample);
    }
}
