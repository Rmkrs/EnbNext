namespace EnbNext.Rendering;

// CRT initializers006F0FB0/006F1740 call009D0230(0,0).
// Builders006F1590/006F1C40 consume separate process owners BE6CC8/BE6CD0.
// Effect IDs, sequence numbers, view changes and retirement do not reseed them.
public sealed class EnbRetailSeekRandomStreams
{
    public static EnbRetailSeekRandomStreams Process { get; } = new();
    public EnbRetailParticleBirthRandomStream Spline { get; } = new(0, 0);
    public EnbRetailParticleBirthRandomStream Linear { get; } = new(0, 0);
}
