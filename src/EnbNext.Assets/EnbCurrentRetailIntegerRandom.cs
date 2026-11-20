namespace EnbNext.Assets;

/// <summary>Current client 009D0000. The caller owns the native generator state.</summary>
public static class EnbCurrentRetailIntegerRandom
{
    public static int Next(ref uint state, int first, int second)
    {
        if (first == second) return first;
        int lower = Math.Min(first, second), upper = Math.Max(first, second);
        uint width = unchecked((uint)(upper - lower));
        // The binary scans from bit 14, even for ranges wider than 15 bits.
        int bit = 14;
        while ((width & (1u << bit)) == 0 && bit > 0) bit--;
        uint mask = (1u << (bit + 1)) - 1;
        // Native unsigned width+1 comparison skips sampling for the full int range.
        uint sample = unchecked(width + 1);
        while (sample > width)
        {
            state = unchecked(state * 0x41c64e6d + 0x3039);
            sample = (state >> 10) & mask & 0x7fff;
        }
        return unchecked(lower + (int)sample);
    }
}
