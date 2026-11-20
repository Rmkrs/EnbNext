namespace EnbNext.Assets;

/// <summary>
/// Current client 0043E230, restricted to positive weights with a signed-int total.
/// The caller supplies raw 32-bit samples; this does not choose or seed a generator.
/// No production playback admission is implied by selecting an authored entry.
/// </summary>
public static class EnbCurrentRetailWeightedSoundSelection
{
    public static int NextChoice(IReadOnlyList<EnbRetailSoundFileChoice> files, Func<uint> nextSample)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(nextSample);
        if (files.Count == 1 && files[0].Weight is null) return 0;
        if (files.Count == 0) throw new ArgumentException("No authored sound choices", nameof(files));

        int total = 0;
        foreach (var file in files)
        {
            if (file.Weight is not int weight || weight <= 0)
                throw new ArgumentException("Expected positive authored weights", nameof(files));
            total = checked(total + weight);
        }

        uint sample = 0;
        if (total > 1)
        {
            uint width = (uint)(total - 1);
            // Unlike radius-delay 009D0000, 0043E25D starts scanning at bit 31.
            uint mask = width;
            mask |= mask >> 1;
            mask |= mask >> 2;
            mask |= mask >> 4;
            mask |= mask >> 8;
            mask |= mask >> 16;
            do { sample = nextSample() & mask; } while (sample > width);
        }

        int ticket = (int)sample + 1, cumulative = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cumulative += files[i].Weight!.Value;
            // NULL is an ordinary interval: return its index without rerolling.
            if (ticket <= cumulative) return i;
        }
        throw new InvalidOperationException("Authored choices changed during selection");
    }
}
