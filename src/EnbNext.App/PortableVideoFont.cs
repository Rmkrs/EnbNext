using SkiaSharp;

namespace EnbNext.App;

/// <summary>The retail subtitle font (Arial 14 pt at 96 DPI, four-bit alpha)
/// rasterized with Skia where GDI is unavailable. Fontconfig resolves Arial
/// to its metric-compatible substitute when Arial itself is not installed.</summary>
internal sealed class PortableVideoFont : EnbNext.Engine.IEnbVideoFont
{
    private readonly SKTypeface _typeface;
    private readonly SKFont _font;
    private readonly int _height, _ascent;
    private readonly Dictionary<char, (int Width, byte[] Pixels)> _glyphs = [];
    private bool _disposed;

    public PortableVideoFont()
    {
        _typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        // CreateFontW(-MulDiv(14, 96, 72)): a 19-pixel character height.
        _font = new SKFont(_typeface, 19f) { Edging = SKFontEdging.Antialias, Subpixel = false };
        var metrics = _font.Metrics;
        _ascent = (int)Math.Ceiling(-metrics.Ascent);
        _height = Math.Max(1, _ascent + (int)Math.Ceiling(metrics.Descent));
    }

    private (int Width, byte[] Pixels) Glyph(char value)
    {
        if (_glyphs.TryGetValue(value, out var glyph)) return glyph;
        string text = value.ToString();
        int width = (int)Math.Round(_font.MeasureText(text));
        if (width <= 0) return _glyphs[value] = (0, []);
        using var bitmap = new SKBitmap(new SKImageInfo(width, _height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawText(text, 0, _ascent, _font, paint);
        }
        byte[] pixels = new byte[width * _height];
        ReadOnlySpan<byte> source = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;
        for (int y = 0; y < _height; y++)
            for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)((source[y * stride + x] >> 4) * 17);
        return _glyphs[value] = (width, pixels);
    }

    public (EnbUiRaster Raster, int MeasuredWidth) Build(string? text, uint color) => VideoFontLayout.Build(text, color, _height, Glyph);

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _font.Dispose(); _typeface.Dispose(); _glyphs.Clear();
    }
}
