using System.Text;

namespace EnbNext.Rendering;

public enum EnbScreenshotFormat { Tga = 0, Bmp = 1, Jpeg = 2 }

/// <summary>009182C0's image conversion and lossless file formats. Input is top-down BGRA.</summary>
public static class EnbScreenshotOutput
{
    public const float CaptureGamma = 1.3f;

    public static byte[] GammaLookup()
    {
        // 0091839D stores the reciprocal as float before the x87 pow call.
        float exponent = 1f / CaptureGamma;
        return Enumerable.Range(0, 256).Select(i => (byte)(256 * Math.Pow(i / 256d, exponent))).ToArray();
    }

    public static (int Width, int Height) Extent(int width, int height, EnbScreenshotFormat format)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (width <= 0 || height <= 0) throw new InvalidDataException("The capture has no pixels.");
        if (format == EnbScreenshotFormat.Jpeg) { width &= ~7; height &= ~7; }
        if (width == 0 || height == 0) throw new InvalidDataException("JPEG capture requires at least eight pixels in each dimension.");
        if (format == EnbScreenshotFormat.Tga && (width > ushort.MaxValue || height > ushort.MaxValue))
            throw new InvalidDataException("The capture exceeds TGA's dimension fields.");
        return (width, height);
    }

    public static byte[] ConvertBgra(ReadOnlySpan<byte> pixels, int width, int height, int pitch, EnbScreenshotFormat format)
    {
        var extent = Extent(width, height, format);
        if (pitch < checked(width * 4) || pixels.Length < checked((height - 1) * pitch + width * 4))
            throw new ArgumentException("Incomplete capture pixels.", nameof(pixels));
        byte[] rgb = new byte[checked(extent.Width * extent.Height * 3)];
        byte[] gamma = GammaLookup();
        for (int y = 0, target = 0; y < extent.Height; y++)
            for (int x = 0; x < extent.Width; x++, target += 3)
            {
                int source = y * pitch + x * 4;
                rgb[target] = gamma[pixels[source + 2]];
                rgb[target + 1] = gamma[pixels[source + 1]];
                rgb[target + 2] = gamma[pixels[source]];
            }
        return rgb;
    }

    public static void WriteLossless(Stream stream, ReadOnlySpan<byte> rgb, int width, int height, EnbScreenshotFormat format)
    {
        if (format is not (EnbScreenshotFormat.Tga or EnbScreenshotFormat.Bmp)) throw new ArgumentOutOfRangeException(nameof(format));
        Extent(width, height, format);
        if (rgb.Length != checked(width * height * 3)) throw new ArgumentException("Incomplete RGB image.", nameof(rgb));
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        int stride = format == EnbScreenshotFormat.Bmp ? checked((width * 3 + 3) & ~3) : checked(width * 3);
        if (format == EnbScreenshotFormat.Tga)
        {
            writer.Write(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            writer.Write((ushort)width); writer.Write((ushort)height); writer.Write((byte)24); writer.Write((byte)0);
        }
        else
        {
            writer.Write((ushort)0x4d42);
            // Correct the native extra factor of three in bfSize; retain valid BI_RGB layout.
            writer.Write(checked(54 + stride * height)); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(width); writer.Write(height);
            writer.Write((ushort)1); writer.Write((ushort)24); writer.Write(0); writer.Write(0);
            writer.Write(0xb12); writer.Write(0xb12); writer.Write(0); writer.Write(0);
        }
        byte[] row = new byte[stride];
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 3;
                row[x * 3] = rgb[p + 2]; row[x * 3 + 1] = rgb[p + 1]; row[x * 3 + 2] = rgb[p];
            }
            writer.Write(row);
        }
        if (format == EnbScreenshotFormat.Tga)
        {
            writer.Write(0); writer.Write(0); writer.Write("TRUEVISION-XFILE.\0"u8);
        }
    }
}
