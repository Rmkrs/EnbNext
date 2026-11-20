using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Logical voices survive physical admission failure (009FD3E0/009FD9B0).
/// Definition limits and recovery follow 009F1360 and 009F0FE0. Hardware channel
/// counts are backend capabilities, not authored limits.</summary>
public sealed class EnbRetailSoundArbitration
{
    private readonly List<Voice> _voices = [];

    public sealed class Voice(string definition, int maximum, int soundType, float priority, float score)
    {
        public string Definition { get; } = definition;
        public int Maximum { get; } = maximum;
        public int SoundType { get; } = soundType;
        public float Priority { get; } = priority;
        public float Score { get; set; } = score;
        public bool Audible { get; internal set; }
    }

    public Voice Start(string definition, int maximum, int soundType, float priority, float score)
    {
        // 009FDCD0 clamps authored priority before the allocator sees it.
        var voice = new Voice(definition, maximum, soundType, Math.Clamp(priority,0,1), score);
        Admit(voice);
        _voices.Add(voice);
        return voice;
    }

    private int Count(Voice voice) => _voices.Count(v => v.Audible && v.SoundType == 1 &&
        string.Equals(v.Definition, voice.Definition, StringComparison.OrdinalIgnoreCase));

    private void Admit(Voice voice)
    {
        Voice? victim = null;
        float lowest = voice.Score;
        foreach (var other in _voices)
            if (other.Audible && other.SoundType == 1 &&
                string.Equals(other.Definition, voice.Definition, StringComparison.OrdinalIgnoreCase) && other.Score < lowest)
            { lowest = other.Score; victim = other; }
        if (Count(voice) >= voice.Maximum)
        {
            if (victim is null) return;
            victim.Audible = false;
        }
        voice.Audible = true;
    }

    public void Remove(Voice voice) => _voices.Remove(voice);

    /// <summary>One eligible retained voice per manager update, highest positive
    /// authored priority; list order wins ties. A full definition cannot steal
    /// a channel during recovery (009F0FE0), only at initial admission.</summary>
    public Voice? Recover()
    {
        Voice? selected = null;
        float priority = 0;
        foreach (var voice in _voices)
            if (!voice.Audible && voice.Priority > priority && Count(voice) < voice.Maximum)
            { selected = voice; priority = voice.Priority; }
        if (selected is not null) Admit(selected);
        return selected;
    }

    // 009F9B10: this is a proximity score, independent of playback gain.
    // Preserve the native asymmetric approximate-length arithmetic.
    public static float SpatialScore(Vector3 delta, float radius)
    {
        float largest = MathF.Abs(delta.X), middle = MathF.Abs(delta.Y), remaining = MathF.Abs(delta.Z);
        if (largest < middle) (largest, middle) = (middle, largest);
        if (largest < remaining) (largest, remaining) = (remaining, largest);
        if (middle < remaining) middle = remaining;
        double distance = remaining * .25 + middle * .34375 + largest;
        return distance <= 0 ? 1 : (float)(1 - distance / radius);
    }
}
