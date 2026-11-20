using System.Numerics;

namespace EnbNext.Assets;

public sealed record EnbRetailDebrisDefinition(float Spacing, float Size, float MinimumDistance,
    float MaximumDistance, Vector4 Color, bool Additive, float FadeIn, float FadeOut,
    int Primitive, int RowsColumnsLog2, string Texture, float BlurTime);

/// <summary>0053D250: contiguous settings.ini Debris%02d sections, in file-selector order.</summary>
public static class EnbRetailDebrisCatalog
{
    public static IReadOnlyList<EnbRetailDebrisDefinition> Read(EnbRetailIniDocument ini)
    {
        var result = new List<EnbRetailDebrisDefinition>();
        for (int index = 0; ini.TryGetSection($"Debris{index:D2}", out var section); index++)
        {
            float F(string key, float fallback) => EnbRetailIniDocument.TryReadFloat(section, key, out var value) ? value : fallback;
            int I(string key, int fallback) => EnbRetailIniDocument.ReadCurrentRetailInteger(section, key, fallback);
            float min = MathF.Max(0, F("MinDist", 0)), max = MathF.Max(1, F("MaxDist", 900));
            if (max <= min) max = min * 1.1f;
            float fadeIn = Math.Clamp(F("FadeIn", .01f), .01f, .99f), fadeOut = Math.Clamp(F("FadeOut", .01f), .01f, .99f);
            if (fadeIn <= fadeOut) fadeIn = (fadeOut + 1) * .5f;
            //009CB860: first character 0/F/N is false,1/T/Y true; all other input uses the caller default(true).
            bool additive = !section.TryGetValue("Additive", out var raw) || raw.Length==0 || raw[0] is not ('0' or 'F' or 'f' or 'N' or 'n');
            result.Add(new(F("Spacing", 200), F("Size", .0001f), min, max,
                new(F("R", 1), F("G", 1), F("B", 1), F("A", 1)), additive, fadeIn, fadeOut,
                I("Primitive", 0), (int)Math.Min(4u, unchecked((uint)I("RowsColumnsLog2", 0))),
                section.GetValueOrDefault("Texture", ""), F("BlurTime", 0)));
        }
        return result;
    }
}
