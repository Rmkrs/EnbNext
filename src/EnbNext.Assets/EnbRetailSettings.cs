using System.Globalization;

namespace EnbNext.Assets;

public sealed record EnbRetailSoundOptions(float Music, float Effects, float Dialog, bool Doppler);

/// <summary>Current client 006BF330/006C5C60/006C57B0: resolution-specific font strings.</summary>
public static class EnbRetailChatFonts
{
    public static EnbFontDefinition Resolve(EnbFontProfile profile, EnbFontDefinition authored,
        IReadOnlyDictionary<string, string> options, IReadOnlyList<string> available)
    {
        if (authored.FontId is null || !options.TryGetValue(authored.FontId, out var value)) return authored;
        string prefix = $"{profile.ResWidth}x{profile.ResHeight}:";
        foreach (string line in value.Split("\\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var fields = line[prefix.Length..].Split(':', 2);
            if (fields.Length != 2 || !available.Contains(fields[0], StringComparer.Ordinal)) return authored;
            var metrics = fields[1].Split(',');
            if (metrics.Length != 3 || !float.TryParse(metrics[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) ||
                !float.IsFinite(scale) || !int.TryParse(metrics[1], out int spacing) || !int.TryParse(metrics[2], out int lineSpacing)) return authored;
            return authored with { Resource = fields[0], Scale = scale, Spacing = spacing, LineSpacing = lineSpacing };
        }
        return authored;
    }

    public static string Update(string previous, EnbFontProfile profile, EnbFontDefinition font)
    {
        string prefix = $"{profile.ResWidth}x{profile.ResHeight}:";
        string current = FormattableString.Invariant($"{prefix}{font.Resource}:{font.Scale:0.0},{font.Spacing},{font.LineSpacing}");
        return string.Join("\\n", new[] { current }.Concat(previous.Split("\\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)))) + "\\n";
    }
}
