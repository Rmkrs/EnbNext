using System.Globalization;

namespace EnbNext.Assets;

public sealed record EnbRetailAnimationSound(double Seconds, string Preset, bool Flat);

public static class EnbRetailAnimationSounds
{
    public static IReadOnlyList<EnbRetailAnimationSound> Read(EnbRetailIniDocument ini, string animation, double framesPerSecond)
    {
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (!ini.TryGetSection(animation, out var section)) return [];
        return section.Values.Select(value => value.Split(',', StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length >= 2 && parts[1].Length > 0 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var frame) && double.IsFinite(frame) && frame >= 0)
            .Select(parts => new EnbRetailAnimationSound(double.Parse(parts[0], CultureInfo.InvariantCulture) / framesPerSecond,
                parts[1], parts.Length > 2 && parts[2].Equals("2D", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(cue => cue.Seconds).ToArray();
    }
}
