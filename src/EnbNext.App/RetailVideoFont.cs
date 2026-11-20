using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace EnbNext.App;

/// <summary>9AA250/9A9FC0: Arial 14, GDI UTF16 glyphs, four-bit alpha and one-pixel advances.</summary>
internal sealed class RetailVideoFont : EnbNext.Engine.IEnbVideoFont
{
    private readonly IntPtr _dc, _font, _bitmap, _oldFont, _oldBitmap, _bits;
    private readonly int _height, _stride = (6 * 14 + 3) & ~3;
    private readonly Dictionary<char, (int Width, byte[] Pixels)> _glyphs = [];
    private bool _disposed;
    internal int Height => _height;
    public RetailVideoFont()
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        try
        {
            _dc = CreateCompatibleDC(screen);
            uint cp = GetACP(); uint charset = cp == 932 ? 128u : cp is 936 or 950 ? 136u : cp == 949 ? 129u : 1u;
            _font = CreateFontW(-MulDiv(14, GetDeviceCaps(screen, 90), 72), 0, 0, 0, 400, 0, 0, 0, charset, 0, 0, 4, 2, "arial");
            var info = new BitmapInfo { Size = 40, Width = 28, Height = -28, Planes = 1, BitCount = 24 };
            _bitmap = CreateDIBSection(screen, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_dc == IntPtr.Zero || _font == IntPtr.Zero || _bitmap == IntPtr.Zero) throw new IOException("Unable to create the retail video subtitle font.");
            _oldFont = SelectObject(_dc, _font); _oldBitmap = SelectObject(_dc, _bitmap);
            SetBkColor(_dc, 0); SetTextColor(_dc, 0x00ffffff);
            byte[] metrics = new byte[60];
            if (!GetTextMetricsW(_dc, metrics)) throw new IOException("Unable to read subtitle font metrics.");
            _height = BitConverter.ToInt32(metrics);
        }
        catch { Dispose(); throw; }
        finally { ReleaseDC(IntPtr.Zero, screen); }
    }

    private (int Width, byte[] Pixels) Glyph(char value)
    {
        if (_glyphs.TryGetValue(value, out var glyph)) return glyph;
        if (!GetTextExtentPoint32W(_dc, value.ToString(), 1, out var size)) throw new IOException("Subtitle glyph measurement failed.");
        int width = unchecked((short)size.X);
        if (width <= 0) return _glyphs[value] = (0, []);
        var rect = new NativeRect { Right = 28, Bottom = 28 };
        if (!ExtTextOutW(_dc, 0, 0, 2, ref rect, value.ToString(), 1, IntPtr.Zero)) throw new IOException("Subtitle glyph rasterization failed.");
        GdiFlush(); byte[] bitmap = new byte[_stride * 28]; Marshal.Copy(_bits, bitmap, 0, bitmap.Length);
        byte[] pixels = new byte[checked(width * _height)];
        for (int y = 0; y < Math.Min(_height, 28); y++)
            for (int x = 0; x < Math.Min(width, 28); x++) pixels[y * width + x] = (byte)((bitmap[y * _stride + x * 3] >> 4) * 17);
        return _glyphs[value] = (width, pixels);
    }

    public (EnbUiRaster Raster, int MeasuredWidth) Build(string? text, uint color) => VideoFontLayout.Build(text, color, _height, Glyph);

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_dc != IntPtr.Zero) { if (_oldFont != IntPtr.Zero) SelectObject(_dc, _oldFont); if (_oldBitmap != IntPtr.Zero) SelectObject(_dc, _oldBitmap); }
        if (_font != IntPtr.Zero) DeleteObject(_font); if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap); if (_dc != IntPtr.Zero) DeleteDC(_dc);
        _glyphs.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("kernel32.dll")] private static extern int MulDiv(int number, int numerator, int denominator);
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CreateFontW(int h,int w,int e,int o,int weight,uint italic,uint underline,uint strike,uint charset,uint output,uint clip,uint quality,uint pitch,string face);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr dc,uint color);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc,uint color);
    [DllImport("gdi32.dll")] private static extern bool GetTextMetricsW(IntPtr dc,[Out] byte[] metrics);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr dc,string text,int length,out NativeSize size);
    [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] private static extern bool ExtTextOutW(IntPtr dc,int x,int y,uint options,ref NativeRect rect,string text,uint length,IntPtr spacing);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
