using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using SkiaSharp;

namespace EnbNext.Host.Sdl;

/// <summary>Modern UI text: Bahnschrift (Windows' DIN-style UI face; Segoe UI if absent) with a dark outline for contrast over space.</summary>
internal sealed class SdlModernTextRenderer : IEnbModernTextRenderer
{
    private readonly SKTypeface face = SKFontManager.Default.MatchFamily("Bahnschrift", SKFontStyle.Normal)
        ?? SKFontManager.Default.MatchFamily("Segoe UI", SKFontStyle.Normal) ?? SKTypeface.Default;
    // A small most-recent cache: frame text changes rarely, while the HUD rebuilds every frame during gauge animation.
    private readonly List<(string Text, float Size, EnbRgb Color, EnbUiRaster Raster, float InkOffset)> cache = [];

    private SKFont Font(float pixelSize) => new(face, pixelSize) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };
    public float Padding(float pixelSize) => MathF.Ceiling(Math.Max(1.5f, pixelSize / 9f)) + 1;

    public float InkCenterOffset(string text, float pixelSize)
    {
        for (int i = cache.Count - 1; i >= 0; i--)
            if (cache[i].Text == text && cache[i].Size == pixelSize) return cache[i].InkOffset;
        using var font = Font(pixelSize);
        return InkOffset(font, text, pixelSize);
    }

    private float InkOffset(SKFont font, string text, float pixelSize)
    {
        var metrics = font.Metrics;
        font.MeasureText(text, out SKRect ink);
        if (ink.IsEmpty) return 0;
        // The image height is rounded up to whole pixels (Render), so measure from that image's centre.
        float pad = Padding(pixelSize), height = MathF.Ceiling(-metrics.Ascent + metrics.Descent + 2 * pad), baseline = pad - metrics.Ascent;
        return baseline + ink.MidY - height / 2;
    }

    public float Measure(string text, float pixelSize)
    {
        using var font = Font(pixelSize);
        return font.MeasureText(text);
    }

    public EnbUiRaster? Render(string text, float pixelSize, EnbRgb color)
    {
        if (text.Length == 0) return null;
        for (int i = 0; i < cache.Count; i++)
            if (cache[i].Text == text && cache[i].Size == pixelSize && cache[i].Color == color)
            {
                var hit = cache[i]; cache.RemoveAt(i); cache.Add(hit); return hit.Raster;
            }
        using var font = Font(pixelSize);
        float pad = Padding(pixelSize), stroke = pad - 1;
        var metrics = font.Metrics;
        int width = (int)MathF.Ceiling(font.MeasureText(text) + 2 * pad), height = (int)MathF.Ceiling(-metrics.Ascent + metrics.Descent + 2 * pad);
        if (width <= 0 || height <= 0) return null;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            float baseline = pad - metrics.Ascent;
            using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke * 2, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0, 0, 0, 200) };
            canvas.DrawText(text, pad, baseline, font, outline);
            using var fill = new SKPaint { IsAntialias = true, Color = new SKColor((byte)(color.R * 255), (byte)(color.G * 255), (byte)(color.B * 255)) };
            canvas.DrawText(text, pad, baseline, font, fill);
        }
        var pixels = new EnbNext.Assets.EnbColor32[width * height];
        var span = bitmap.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new(span[i * 4], span[i * 4 + 1], span[i * 4 + 2], span[i * 4 + 3]);
        var raster = EnbUiRaster.FromPixels(width, height, pixels);
        if (cache.Count == 96) cache.RemoveAt(0);
        cache.Add((text, pixelSize, color, raster, InkOffset(font, text, pixelSize)));
        return raster;
    }
}
