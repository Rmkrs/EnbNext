using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>
/// Renders current-retail interface text from fontsys.ini atlas textures. The
/// radar layout constructs RADAR_NAME/RADAR_DIST with FontType FONT_TINY, which
/// the installed fontsys profile maps to font_tiny5/font_small9/etc.
/// </summary>
public static class EnbRetailFontFactory
{
    private const int GlyphColumns = 16;
    private const int GlyphRows = 16;
    private const int AlphaThreshold = 0;

    // 982610/982AD0: the movie's direct TextureFont has one-pixel tracking,
    // a half-H-width space and no fontsys profile scaling.
    public static EnbRetailFontFace? CreateMovieFont(EnbRetailAssetBundle assets)
    {
        if (assets.RetailMixResources is not { } resources ||
            new EnbRetailTextureResolver(resources).Resolve("font12x16.tga").Image is not { } image ||
            image.Width % 16 != 0 || image.Height % 16 != 0) return null;
        int width = image.Width / 16, height = image.Height / 16;
        var glyphs = new EnbRetailFontGlyph[256];
        for (int i = 0; i < glyphs.Length; i++)
        {
            var glyph = ScanGlyph(image, i % 16 * width, i / 16 * height, width, height);
            glyphs[i] = i > 128 || glyph.IsBlank ? glyph with { MaxX = -1, MaxY = -1, Advance = 0 } : glyph;
        }
        glyphs[32] = glyphs[32] with { Advance = glyphs[72].Advance / 2 };
        return new("font12x16.tga", image, width, height, 1, 1, glyphs) { IsMovieTextureFont = true };
    }

    public static bool TryResolveDefinition(
        EnbRetailAssetBundle assets,
        string fontId,
        int displayWidth,
        int displayHeight,
        out EnbFontDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontId);
        definition = default!;
        EnbFontSystemCatalog? fonts = assets.Fonts;
        if (fonts is null || fonts.Profiles.Count == 0)
            return false;

        EnbFontProfile profile;
        if (!fonts.TrySelectProfile(displayWidth, displayHeight, out profile))
            profile = fonts.Profiles[0];

        EnbFontDefinition? selected = profile.Fonts.FirstOrDefault(candidate =>
            string.Equals(candidate.FontId, fontId, StringComparison.OrdinalIgnoreCase));
        if (selected is null || string.IsNullOrWhiteSpace(selected.Resource))
            return false;

        definition = selected;
        return true;
    }

    public static bool TryCreate(
        EnbRetailAssetBundle assets,
        EnbFontDefinition definition,
        out EnbRetailFontFace face)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(definition);
        face = default!;
        if (string.IsNullOrWhiteSpace(definition.Resource) ||
            assets.RetailMixResources is not { } resources)
        {
            return false;
        }

        EnbResolvedTextureResource resolved =
            new EnbRetailTextureResolver(resources).Resolve(definition.Resource);
        if (!resolved.IsResolved ||
            resolved.Image is not EnbTextureImage image ||
            image.Width % GlyphColumns != 0 ||
            image.Height % GlyphRows != 0)
        {
            return false;
        }

        face = CreateGridFace(definition, image)!;
        return face is not null;
    }

    // 6BC3A0 converts to A4R4G4B4 before 6BC120 scans the grid. The
    // linked D3DX reader/writer (8F33D4/8F261B) rounds, rather than dropping
    // the low nibble. Keep this distinct from other texture upload paths.
    public static EnbRetailFontFace? CreateGridFace(EnbFontDefinition definition, EnbTextureImage image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Resource);
        {
            bool alreadyPacked = image.CurrentRetailSurfaceFormat == 7;
            var colors = new EnbColor32[checked(image.Width * image.Height)];
            byte Channel(byte value) => (byte)((alreadyPacked ? value >> 4 : (value * 15 + 127) / 255) * 17);
            for (int i = 0; i < image.Pixels.Length; i++)
            {
                var pixel = image.Pixels.Span[i];
                colors[i] = new EnbColor32(Channel(pixel.R), Channel(pixel.G), Channel(pixel.B), Channel(pixel.A));
            }
            // Font quads sample normalized four-bit channels. The packed
            // scratch-surface factory instead models 99C510's shift-only copy.
            image = EnbTextureImage.CreateCurrentRetailRuntimeArgb4444(image.Width, image.Height, colors);
        }
        int cellWidth = image.Width / GlyphColumns;
        int cellHeight = image.Height / GlyphRows;
        var glyphs = new EnbRetailFontGlyph[GlyphColumns * GlyphRows];
        int firstWidth = 0, top = cellHeight, bottom = 0;
        for (int index = 0; index < glyphs.Length; index++)
        {
            int cellX = (index % GlyphColumns) * cellWidth;
            int cellY = (index / GlyphColumns) * cellHeight;
            glyphs[index] = ScanGlyph(image, cellX, cellY, cellWidth, cellHeight);
            var glyph = glyphs[index];
            if (glyph.IsBlank)
                glyphs[index] = glyph with { Advance = firstWidth == 0 ? 1 - Math.Min(0, definition.Spacing) : firstWidth };
            else
            {
                if (firstWidth == 0) firstWidth = Math.Max(glyph.Advance, 1 - definition.Spacing);
                top = Math.Min(top, glyph.MinY);
                bottom = Math.Max(bottom, glyph.MaxY);
            }
        }
        // An empty optional atlas has no drawable glyphs. Do not allocate the
        // byte-wrapped height produced by retail's invalid all-empty input.
        if (firstWidth == 0) return null;
        for (int i = 0; i < glyphs.Length; i++)
            glyphs[i] = glyphs[i] with { CellY = glyphs[i].CellY + top, MinY = glyphs[i].MinY - top, MaxY = glyphs[i].MaxY - top };
        int croppedHeight = bottom - top + 1;
        return new EnbRetailFontFace(
            definition.Resource,
            image,
            cellWidth,
            croppedHeight,
            definition.Spacing,
            Math.Max(0.01f, definition.Scale),
            glyphs) { LineSpacing = Math.Max(-(croppedHeight / 2), definition.LineSpacing) };
    }

    private static EnbRetailFontGlyph ScanGlyph(
        EnbTextureImage image,
        int cellX,
        int cellY,
        int cellWidth,
        int cellHeight)
    {
        int minX = cellWidth;
        int minY = cellHeight;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < cellHeight; y++)
        {
            for (int x = 0; x < cellWidth; x++)
            {
                if (image.GetPixel(cellX + x, cellY + y).A <= AlphaThreshold)
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            int blankAdvance = Math.Max(1, cellWidth / 2);
            return new EnbRetailFontGlyph(cellX, cellY, 0, 0, -1, -1, blankAdvance);
        }

        return new EnbRetailFontGlyph(
            cellX,
            cellY,
            minX,
            minY,
            maxX,
            maxY,
            Math.Max(1, maxX - minX + 1));
    }
}

public sealed class EnbRetailFontFace
{
    private readonly EnbRetailFontGlyph[] _glyphs;
    private readonly int[] _advances;
    private readonly EnbTextureImage _image;

    public EnbRetailFontFace(
        string resourceName,
        EnbTextureImage image,
        int cellWidth,
        int cellHeight,
        int spacing,
        float scale,
        EnbRetailFontGlyph[] glyphs)
    {
        ResourceName = resourceName;
        _image = image;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        Spacing = spacing;
        Scale = scale;
        _glyphs = glyphs;
        _advances = glyphs.Select(glyph => glyph.Advance).ToArray();
        int top=cellHeight,bottom=0;
        foreach(var glyph in glyphs)
            if(!glyph.IsBlank){top=Math.Min(top,Math.Clamp(glyph.MinY,0,cellHeight));bottom=Math.Max(bottom,Math.Clamp(glyph.MaxY+1,0,cellHeight));}
        LineInkTop=bottom>top?top:0;
        LineInkBottom=bottom>top?bottom:cellHeight;
        Baseline=Math.Clamp(glyphs['H'].MaxY+1,LineInkTop,LineInkBottom);

    }

    public int LineInkTop { get; }
    public int LineInkBottom { get; }
    public int Baseline { get; }
    public string ResourceName { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public int Spacing { get; }
    public int LineSpacing { get; init; }
    public bool IsMovieTextureFont { get; init; }
    public float Scale { get; }

    public EnbUiRaster RenderText(string text, EnbRgb tint)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = text.Length == 0 ? " " : text;
        int width = Math.Max(1, MeasureWidth(normalized));
        int height = CellHeight;
        var pixels = new byte[checked(width * height * 4)];
        int penX = 0;
        foreach (char c in normalized)
        {
            if (!IsMovieTextureFont && unchecked((byte)c) == 0) break;
            EnbRetailFontGlyph glyph = _glyphs[(byte)c];
            BlitGlyph(glyph, pixels, width, height, penX, tint);
            penX += glyph.Advance + Spacing;
        }

        return new(width, height, System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(pixels));
    }

    public int MeasureWidth(string text)
    {
        if (!IsMovieTextureFont) return EnbRetailFontMetrics.MeasureWidth(text, _advances, Spacing);
        int width = 0;
        foreach (char c in text)
            width += _glyphs[(byte)c].Advance + Spacing;
        return Math.Max(1, width - Spacing);
    }

    public int CharacterAdvance(char character)=>_glyphs[(byte)character].Advance+Spacing;

    private void BlitGlyph(
        EnbRetailFontGlyph glyph,
        byte[] output,
        int outputWidth,
        int outputHeight,
        int penX,
        EnbRgb tint)
    {
        if (glyph.IsBlank)
            return;

        for (int y = glyph.MinY; y <= glyph.MaxY; y++)
        {
            int outputY = y;
            if ((uint)outputY >= (uint)outputHeight)
                continue;

            for (int x = glyph.MinX; x <= glyph.MaxX; x++)
            {
                EnbColor32 source = _image.GetPixel(glyph.CellX + x, glyph.CellY + y);
                if (source.A <= 0)
                    continue;

                int outputX = penX + x - glyph.MinX;
                if ((uint)outputX >= (uint)outputWidth)
                    continue;

                int index = ((outputY * outputWidth) + outputX) * 4;
                output[index] = Modulate(source.B, tint.B);
                output[index + 1] = Modulate(source.G, tint.G);
                output[index + 2] = Modulate(source.R, tint.R);
                output[index + 3] = source.A;
            }
        }
    }

    private static byte Modulate(byte source, float tint)
        => (byte)Math.Clamp((int)MathF.Round(source * Math.Clamp(tint, 0f, 1f)), 0, 255);
}

public sealed record EnbRetailFontGlyph(
    int CellX,
    int CellY,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY,
    int Advance)
{
    public bool IsBlank => MaxX < MinX || MaxY < MinY;
}

