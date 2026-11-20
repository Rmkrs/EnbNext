using System.Buffers.Binary;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>
/// Exact current-retail pseudo-random stream used by Mode10 texture mappers.
/// client.exe seeds the mapper-owned 624-word generator at 00932140 with
/// 0x1105 and consumes it through 009D0410/009D0580. The generator is the
/// original 32-bit MT19937 recurrence/tempering with the retail 69069 seed
/// expansion and a 2^-32 floating conversion.
/// </summary>
public sealed class EnbW3dRetailMapperRandomSource
{
    //00932140 seeds C00720 once at process startup. Authored materials,
    //effect copies and GPU consumers all advance that same owner.
    public static EnbW3dRetailMapperRandomSource Process { get; } = new();

    public const uint CurrentRetailSeed = 0x00001105u;
    public const uint ZeroSeedFallback = 0x00001117u;

    private const int StateCount = 624;
    private const int PeriodOffset = 397;
    private const uint MatrixA = 0x9908B0DFu;

    private readonly object _gate = new();
    private readonly uint[] _state = new uint[StateCount];
    private int _index;
    private byte[]? _storage;

    public EnbW3dRetailMapperRandomSource(uint seed = CurrentRetailSeed)
        => Reseed(seed);

    public void Reseed(uint seed)
    {
        lock (_gate)
        {
            uint value = seed == 0u ? ZeroSeedFallback : seed;
            _state[0] = value;
            for (int i = 1; i < StateCount; i++)
            {
                value = unchecked(value * 69069u);
                _state[i] = value;
            }
            _index = StateCount;
        }
    }

    public uint NextUInt32()
    {
        lock (_gate)
            return NextUInt32Core();
    }

    public float NextUnitFloat()
    {
        lock (_gate)
            return UnitFloat(NextUInt32Core());
    }

    internal EnbW3dRetailMapperRandomState NextRandomizedState()
    {
        lock (_gate)
        {
            using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
            uint angleBits = NextUInt32Core();
            uint firstOffsetBits = NextUInt32Core();
            uint secondOffsetBits = NextUInt32Core();
            // 009D0580 leaves the normalized random value live in x87 ST0.
            // The Mode10 caller multiplies the first draw by the 32-bit 2pi
            // constant before storing to float, so do not round it to float first.
            float angle = (float)R.MultiplyFloatMemory(UnitRegister(angleBits),0x40c90fdb);
            return new EnbW3dRetailMapperRandomState(
                angle,
                UnitFloat(firstOffsetBits),
                UnitFloat(secondOffsetBits));
        }
    }

    private uint NextUInt32Core()
    {
        if (_index >= StateCount)
            Twist();

        uint value = _state[_index++];
        value ^= value >> 11;
        value ^= (value << 7) & 0x9D2C5680u;
        value ^= (value << 15) & 0xEFC60000u;
        value ^= value >> 18;
        return value;
    }

    private void Twist()
    {
        for (int i = 0; i < StateCount - PeriodOffset; i++)
        {
            uint combined = (_state[i] & 0x80000000u) | (_state[i + 1] & 0x7FFFFFFFu);
            _state[i] = _state[i + PeriodOffset] ^ (combined >> 1) ^ ((combined & 1u) == 0u ? 0u : MatrixA);
        }

        for (int i = StateCount - PeriodOffset; i < StateCount - 1; i++)
        {
            uint combined = (_state[i] & 0x80000000u) | (_state[i + 1] & 0x7FFFFFFFu);
            _state[i] = _state[i + (PeriodOffset - StateCount)] ^ (combined >> 1) ^ ((combined & 1u) == 0u ? 0u : MatrixA);
        }

        uint finalCombined = (_state[StateCount - 1] & 0x80000000u) | (_state[0] & 0x7FFFFFFFu);
        _state[StateCount - 1] = _state[PeriodOffset - 1] ^ (finalCombined >> 1) ^ ((finalCombined & 1u) == 0u ? 0u : MatrixA);
        _index = 0;
    }

    private static float UnitFloat(uint value)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        return (float)UnitRegister(value);
    }

    private static R UnitRegister(uint value)=>R.MultiplyFloatMemory((R)(double)value,0x2f800000);

    // Both the whole retail matrix and public draws mutate this one stream.
    internal T WithStorage<T>(Func<byte[],T> action)
    {
        lock(_gate)
        {
            // Lease this stream's scratch storage; nested calls get a separate
            // buffer, preserving the callback's existing reentrant behavior.
            byte[] bytes=_storage??new byte[2500];
            _storage=null;
            for(int n=0;n<StateCount;n++)BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(n*4,4),_state[n]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2496,4),_index);
            try{return action(bytes);}
            finally
            {
                for(int n=0;n<StateCount;n++)_state[n]=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(n*4,4));
                _index=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(2496,4));
                _storage=bytes;
            }
        }
    }
}

internal readonly record struct EnbW3dRetailMapperRandomState(
    float AngleRadians,
    float FirstOffset,
    float SecondOffset);
