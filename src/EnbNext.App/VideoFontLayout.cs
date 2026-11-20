using System.Collections.Immutable;

namespace EnbNext.App;

/// <summary>9AA250 subtitle layout shared by the platform glyph sources: one-pixel
/// advances, tab stops of five advances, newline rows of the font height.</summary>
internal static class VideoFontLayout
{
    internal static (EnbUiRaster Raster, int MeasuredWidth) Build(string? text, uint color, int lineHeight, Func<char, (int Width, byte[] Pixels)> glyphSource)
    {
        text ??= "";
        var placements = new List<(int X, int Y, int Width, byte[] Pixels)>();
        int x = 0, y = 0, width = 0, measured = 0;
        foreach (char c in text)
        {
            if (c == '\0') break;
            var glyph = glyphSource(c); int advance = glyph.Width == 0 ? 0 : glyph.Width + 1;
            if (c != '\n') measured += advance;
            if (c == '\n') { width = Math.Max(width, x); x = 0; y += lineHeight; continue; }
            if (c == '\t') { if (advance != 0) x = ((int)Math.Round(x / (double)(advance * 5)) + 1) * advance * 5; continue; }
            if (c != ' ') placements.Add((x, y, glyph.Width, glyph.Pixels));
            x += advance;
        }
        width = Math.Max(1, Math.Max(width, x)); int height = Math.Max(1, y + lineHeight);
        byte[] pixels = new byte[checked(width * height * 4)];
        foreach (var glyph in placements)
            for (int row = 0; row < lineHeight; row++)
                for (int col = 0; col < glyph.Width; col++)
                {
                    int d = ((glyph.Y + row) * width + glyph.X + col) * 4;
                    pixels[d] = (byte)color; pixels[d + 1] = (byte)(color >> 8); pixels[d + 2] = (byte)(color >> 16);
                    pixels[d + 3] = glyph.Pixels[row * glyph.Width + col];
                }
        return (new(width, height, ImmutableArray.Create(pixels)), measured);
    }
}
