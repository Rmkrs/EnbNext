using System.Globalization;

namespace EnbNext.Assets;

public sealed record EnbW3dAnimationSoundCue(int Ordinal, int Frame, string Sound, bool Flat);
public sealed record EnbW3dAnimationSoundTrack(string BoneName, IReadOnlyList<EnbW3dAnimationSoundCue> Cues)
{
    // 00909F90: strict lower boundary, inclusive upper boundary, authored order.
    public IEnumerable<EnbW3dAnimationSoundCue> Crossed(float previous, float current) =>
        Cues.Where(cue => previous < cue.Frame && cue.Frame <= current);
}

/// <summary>00909540: animation registry name, then frame/name/optional sound type.</summary>
public sealed class EnbW3dAnimationSoundCatalog
{
    private readonly Dictionary<string, EnbW3dAnimationSoundTrack> _tracks = new(StringComparer.OrdinalIgnoreCase);
    public bool TryGet(string animation, out EnbW3dAnimationSoundTrack track) => _tracks.TryGetValue(animation, out track!);
    public static EnbW3dAnimationSoundCatalog Parse(string text)
    {
        var result = new EnbW3dAnimationSoundCatalog();
        var ini = EnbRetailIniDocument.Parse(text);
        foreach (string section in ini.SectionNames)
        {
            if (!ini.TryGetSection(section, out var values)) continue;
            var cues = new List<EnbW3dAnimationSoundCue>();
            foreach (var (key, value) in values)
            {
                if (key.Equals("BoneName", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = value.Split(',');
                if (parts.Length is < 2 or > 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame) || frame < 0 || string.IsNullOrWhiteSpace(parts[1])) continue;
                cues.Add(new(cues.Count, frame, parts[1].Trim(), parts.Length == 3 && IntegerPrefix(parts[2]) == 2));
            }
            result._tracks[section] = new(values.GetValueOrDefault("BoneName", "root"), cues);
        }
        return result;
    }

    // 00909A26 -> 00A27762 -> 00A276D7. Authored "2D" is integer 2.
    private static int IntegerPrefix(string text)
    {
        var value = text.AsSpan().TrimStart();
        bool negative = !value.IsEmpty && value[0] == '-';
        if (!value.IsEmpty && value[0] is '+' or '-') value = value[1..];
        int result = 0;
        foreach (char digit in value)
        {
            if (digit is < '0' or > '9') break;
            result = unchecked(result * 10 + digit - '0');
        }
        return negative ? unchecked(-result) : result;
    }
}
