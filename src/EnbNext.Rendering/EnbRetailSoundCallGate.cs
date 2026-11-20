using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Retail 0043E360: per-definition deadline, tested before acquiring a voice.</summary>
public sealed class EnbRetailSoundCallGate
{
    private readonly Dictionary<string, uint> _nextCalls = new(StringComparer.OrdinalIgnoreCase);

    public Func<uint> Clock { get; set; } = static () => unchecked((uint)Environment.TickCount);

    public bool TryCall(EnbRetailSoundDefinition definition) => TryCall(definition, Clock());

    public bool TryCall(EnbRetailSoundDefinition definition, uint milliseconds)
    {
        int interval = RetailIntervalMilliseconds(definition.EffectiveSecondsBetweenCalls);
        if (interval == 0) return true;
        if (_nextCalls.TryGetValue(definition.Name, out uint next) &&
            unchecked((int)(milliseconds - next)) < 0) return false;
        _nextCalls[definition.Name] = unchecked(milliseconds + (uint)interval);
        return true;
    }

    /// <summary>
    /// 0043DCBE..0043DCD5: SoundSecsBetweenCalls * 1000.0f (00AE73B4) through
    /// __ftol (00A264EC, truncating FISTP qword); +60 keeps the low dword.
    /// NaN and values outside the int64 range give 0x8000000000000000, so 0.
    /// </summary>
    public static int RetailIntervalMilliseconds(float seconds)
    {
        double value = Math.Truncate((double)seconds * 1000.0);
        if (double.IsNaN(value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0) return 0;
        return unchecked((int)(long)value);
    }
}
