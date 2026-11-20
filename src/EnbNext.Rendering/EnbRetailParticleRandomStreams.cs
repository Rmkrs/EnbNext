namespace EnbNext.Rendering;

/// <summary>Retail009D03B0/009D0410 property-table stream; its owner supplies the seed and consumption order.</summary>
public sealed class EnbRetailParticlePropertyRandomStream
{
    private readonly uint[] _state = new uint[624];
    private int _index;
    public ulong WordsConsumed { get; private set; }

    public EnbRetailParticlePropertyRandomStream(uint seed)
    {
        _state[0] = seed == 0 ? 0x1117u : seed;
        for (int i = 1; i < _state.Length; i++)
            _state[i] = unchecked(_state[i - 1] * 0x10dcdu);
        _index = _state.Length;
    }

    public uint NextUInt32()
    {
        WordsConsumed++;
        if (_index >= _state.Length)
        {
            for (int i = 0; i < 227; i++) _state[i] = Twist(_state[i], _state[i + 1]) ^ _state[i + 397];
            for (int i = 227; i < 623; i++) _state[i] = Twist(_state[i], _state[i + 1]) ^ _state[i - 227];
            _state[623] = Twist(_state[623], _state[0]) ^ _state[396];
            _index = 0;
        }
        uint value = _state[_index++];
        value ^= value >> 11;
        value ^= (value & 0xff3a58adu) << 7;
        value ^= (value & 0xffffdf8cu) << 15;
        return value ^ (value >> 18);
    }

    private static uint Twist(uint current, uint next)
    {
        uint joined = ((next ^ current) & 0x7fffffffu) ^ current;
        return (joined >> 1) ^ ((joined & 1) == 0 ? 0u : 0x9908b0dfu);
    }
}

/// <summary>Retail009D0230/009D0250 birth/noise stream; seed and counter are independent words.</summary>
public sealed class EnbRetailParticleBirthRandomStream(uint seed, uint counter)
{
    private readonly uint _seed = seed;
    private uint _counter = counter;
    public uint Counter => _counter;
    public uint Seed => _seed;

    public uint Reserve(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        uint first = _counter;
        _counter = unchecked(_counter + (uint)count);
        return first;
    }

    public uint NextUInt32() => unchecked((uint)EnbRetailSignedRandom.Word(_seed, _counter++));
}

/// <summary>CRT owners 0095BA80/C06A88 and 00A159C4/C40B10.
/// A scene, view, emitter retirement or GPU reset does not reseed these owners.</summary>
public sealed class EnbRetailParticleRandomStreams
{
    public static EnbRetailParticleRandomStreams Process { get; } = new();
    public object Gate { get; } = new();
    public EnbRetailParticlePropertyRandomStream Properties { get; } = new(0x1105);
    public EnbRetailParticleBirthRandomStream Births { get; } = new(0, 0);

    public uint ReserveLineWords(int count)
    {
        lock (Gate) return Births.Reserve(count);
    }
}
