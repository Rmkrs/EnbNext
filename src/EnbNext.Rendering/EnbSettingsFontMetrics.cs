using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>006BCF00 cell metrics prepared in the asset session's authored font slots.</summary>
public sealed class EnbSettingsFontMetrics(EnbRetailAssetBundle assets)
{
    private readonly int?[] heights = new int?[assets.Fonts?.AvailableFonts.Count ?? 0];
    public int? Height(EnbFontDefinition definition)
    {
        int index = -1;
        for (int i = 0; i < heights.Length; i++)
            if (string.Equals(assets.Fonts!.AvailableFonts[i], definition.Resource, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        if (index >= 0 && heights[index] is int known) return known;
        if (!EnbRetailFontFactory.TryCreate(assets, definition, out var face)) return null;
        if (index >= 0) heights[index] = face.CellHeight;
        return face.CellHeight;
    }
}
