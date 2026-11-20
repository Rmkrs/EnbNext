using System.Globalization;
using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>Installed fontart/fontcode palette and the established FA_CYCLE evaluator.</summary>
public sealed class EnbFontEffectPalette
{
    private readonly Dictionary<string, int[]> colors = new(StringComparer.OrdinalIgnoreCase);
    private float cycleSpeed;

    public static EnbFontEffectPalette Load(string clientIniDirectory)
    {
        var palette = new EnbFontEffectPalette();
        palette.Reload(clientIniDirectory);
        return palette;
    }

    public void Reload(string clientIniDirectory)
    {
        colors.Clear();
        foreach (string file in new[] { "fontart.ini", "fontcode.ini" })
        {
            var ini = EnbRetailIniDocument.Load(Path.Combine(clientIniDirectory, file));
            foreach (string section in ini.SectionNames)
            {
                if (!ini.TryGetSection(section, out var values)) continue;
                if (values.TryGetValue("ColorID", out var id) && values.TryGetValue("RGB", out var rgb))
                    colors[id] = rgb.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (values.GetValueOrDefault("EffectID") == "FA_CYCLE")
                    cycleSpeed = float.Parse(values["Speed"], CultureInfo.InvariantCulture);
            }
        }
    }

    public EnbRgb Color(string id)
    {
        var value = colors[id];
        return new(value[0] / 255f, value[1] / 255f, value[2] / 255f);
    }

    public EnbRgb Resolve(IReadOnlyDictionary<string, string> properties, bool hovered, long elapsed)
        => hovered && properties.GetValueOrDefault("MouseOver") == "FA_CYCLE"
            ? Cycle(properties["Color"], properties["AltColor"], elapsed) : Color(properties["Color"]);

    public EnbRgb ResolveSelected(IReadOnlyDictionary<string, string> properties, bool hovered,
        bool selected, long elapsed)
    {
        string primary = properties[selected ? "AltColor" : "Color"];
        if (hovered && properties.GetValueOrDefault("MouseOver") == "FA_CYCLE")
            return Cycle(primary, properties["AltColor"], elapsed);
        var value = colors[primary];
        return new(value[0] * .00392156f, value[1] * .00392156f, value[2] * .00392156f);
    }

    public EnbRgb Cycle(string primaryId, string alternateId, long milliseconds)
    {
        var primary = colors[primaryId]; var alternate = colors[alternateId];
        // 006CA0C0 case 0x12: integer half-sums/differences precede the sine.
        double wave = Math.Sin(Math.Max(0, milliseconds) * (double)cycleSpeed * 0.0062831f);
        float Channel(int index) => (float)(((alternate[index] - primary[index]) / 2 * wave +
            (alternate[index] + primary[index]) / 2) * 0.00392156f);
        return new(Channel(0), Channel(1), Channel(2));
    }
}
