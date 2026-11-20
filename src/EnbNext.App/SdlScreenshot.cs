using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;
using EnbNext.Rendering;

namespace EnbNext.App;

/// <summary>Captures the Windows compositor's client image, including GPU surfaces.</summary>
internal static class SdlScreenshot
{
    private static int _nextNumber = 1;

    public static SKBitmap Capture(nint window)
    {
        if (!GetClientRect(window, out var rect)) throw new Win32Exception();
        int width = rect.Right, height = rect.Bottom;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("The game window has no image to capture.");
        nint dc = CreateCompatibleDC(0), dib = 0, previous = 0;
        if (dc == 0) throw new Win32Exception();
        try
        {
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            dib = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
            if (dib == 0) throw new Win32Exception();
            previous = SelectObject(dc, dib);
            if (!PrintWindow(window, dc, 3)) throw new Win32Exception(); // CLIENTONLY | RENDERFULLCONTENT
            var result=new SKBitmap(width,height,SKColorType.Bgra8888,SKAlphaType.Opaque);
            byte[] row=new byte[checked(width*4)];
            for(int y=0;y<height;y++)
            {
                Marshal.Copy(bits+y*row.Length,row,0,row.Length);
                Marshal.Copy(row,0,result.GetPixels()+y*result.RowBytes,row.Length);
            }
            return result;
        }
        finally
        {
            if (previous != 0) SelectObject(dc, previous);
            if (dib != 0) DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    // 0091830F increments a process counter, starting at one, until the name is unused.
    public static string Save(SKBitmap image, string retailReleaseDirectory)
        => SaveOutput(image, retailReleaseDirectory, "ScreenShot", EnbScreenshotFormat.Jpeg);

    public static string SaveOutput(SKBitmap image, string retailReleaseDirectory, string prefix, EnbScreenshotFormat format)
    {
        var size=EnbScreenshotOutput.Extent(image.Width,image.Height,format);
        byte[] rgb=EnbScreenshotOutput.ConvertBgra(image.GetPixelSpan(),image.Width,image.Height,image.RowBytes,format);
        string extension = format switch { EnbScreenshotFormat.Tga => "tga", EnbScreenshotFormat.Bmp => "bmp", _ => "jpg" };
        while (true)
        {
            int number = Interlocked.Increment(ref _nextNumber) - 1;
            string path = Path.Combine(retailReleaseDirectory, prefix + number.ToString("D2", CultureInfo.InvariantCulture) + "." + extension);
            FileStream file;
            try { file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }
            try
            {
                using (file)
                {
                    if (format != EnbScreenshotFormat.Jpeg)
                        EnbScreenshotOutput.WriteLossless(file, rgb, size.Width, size.Height, format);
                    else
                    {
                        using var encoded=new SKBitmap(size.Width,size.Height,SKColorType.Bgra8888,SKAlphaType.Opaque);
                        byte[] row=new byte[checked(size.Width*4)];
                        for(int y=0;y<size.Height;y++)
                        {
                            for(int x=0;x<size.Width;x++)
                            {
                                int p=(y*size.Width+x)*3;row[x*4]=rgb[p+2];row[x*4+1]=rgb[p+1];row[x*4+2]=rgb[p];row[x*4+3]=255;
                            }
                            Marshal.Copy(row,0,encoded.GetPixels()+y*encoded.RowBytes,row.Length);
                        }
                        using var pixmap=encoded.PeekPixels();
                        using var jpeg = pixmap.Encode(new SkiaSharp.SKJpegEncoderOptions(94, SkiaSharp.SKJpegEncoderDownsample.Downsample444, SkiaSharp.SKJpegEncoderAlphaOption.Ignore))
                            ?? throw new IOException("JPEG encoding failed.");
                        ReadOnlySpan<byte> bytes = jpeg.AsSpan();
                        // The encoder supplies JFIF 1.01. Preserve retail's 96 dpi
                        // (009E3870) instead of Skia's unitless 1:1 default.
                        if (bytes.Length < 18 || !bytes[..13].SequenceEqual(new byte[] { 255,216,255,224,0,16,74,70,73,70,0,1,1 }))
                            throw new InvalidDataException("JPEG encoder did not emit a supported JFIF header.");
                        file.Write(bytes[..13]); file.Write(new byte[] { 1,0,96,0,96 }); file.Write(bytes[18..]);
                    }
                }
                return path;
            }
            catch { file.Dispose(); File.Delete(path); throw; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPixels, YPixels; public uint Colors, Important;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
