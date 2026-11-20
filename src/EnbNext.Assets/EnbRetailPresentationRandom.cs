namespace EnbNext.Assets;

/// <summary>Process owner 00BDBB84, initialized once with zero by 004441E0.
/// Separate from CRT, 250-word sound/damage, particle and mapper generators.</summary>
public sealed class EnbRetailPresentationRandom(uint seed=0)
{
    public static EnbRetailPresentationRandom Shared { get; } = new();
    private readonly object gate=new();
    private uint state=seed;
    public uint State { get { lock(gate)return state; } }
    public int Next(int first,int second)
    {
        lock(gate)return EnbCurrentRetailIntegerRandom.Next(ref state,first,second);
    }
}
