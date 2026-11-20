using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0099C510's RGB8 source/destination branch. Unlike the ARGB8
/// fast path, its mip source compaction keeps the first converted quad pixel.
/// Incoming buffers, pitches and write coverage remain caller-owned.</summary>
public static class EnbCurrentRetailRgbSurfaceRuntime
{
    public static void Convert(Span<byte> output, int width, int height, int outputPitch,
        Span<byte> input, int sourceWidth, int sourceHeight, int sourcePitch,
        bool generateMip, Vector3 shift)
        => ConvertCore(output,width,height,outputPitch,input,sourceWidth,sourceHeight,sourcePitch,generateMip,shift,3);

    /// <summary>RGB8 to XRGB8 selected by 009689A0 for the ordinary
    /// 24-bit TGA load. Source compaction remains in RGB8.</summary>
    public static void ConvertToXrgb(Span<byte> output, int width, int height, int outputPitch,
        Span<byte> input, int sourceWidth, int sourceHeight, int sourcePitch,
        bool generateMip, Vector3 shift)
        => ConvertCore(output,width,height,outputPitch,input,sourceWidth,sourceHeight,sourcePitch,generateMip,shift,4);

    private static void ConvertCore(Span<byte> output, int width, int height, int outputPitch,
        Span<byte> input, int sourceWidth, int sourceHeight, int sourcePitch,
        bool generateMip, Vector3 shift, int outputBytesPerPixel)
    {
        if (width == 0 || height == 0) return;
        if (width < 0 || height < 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
            outputPitch < checked(width * outputBytesPerPixel) || sourcePitch < checked(sourceWidth * 3) ||
            output.Length < checked((height - 1) * outputPitch + width * outputBytesPerPixel) ||
            input.Length < checked((sourceHeight - 1) * sourcePitch + sourceWidth * 3))
            throw new ArgumentException("Surface dimensions exceed the supplied buffers.");
        uint Shift(uint color) => EnbCurrentRetailHsvRuntime.ShiftArgb8(color | 0xff000000, shift);
        if (width != sourceWidth || height != sourceHeight || !generateMip)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int source = (int)((long)y * sourceHeight / height) * sourcePitch + (int)((long)x * sourceWidth / width) * 3;
                    Write(output, y * outputPitch + x * outputBytesPerPixel, Shift(Read(input, source)),outputBytesPerPixel);
                }
            return;
        }
        if (width == 1)
        {
            Write(output, 0, Shift(Read(input, 0)),outputBytesPerPixel);
            return;
        }
        for (int y = 0; y < height >> 1; y++)
            for (int x = 0; x < width >> 1; x++)
            {
                int source = y * 2 * sourcePitch + x * 6;
                uint a = Shift(Read(input, source)), b = Shift(Read(input, source + 3));
                uint c = Shift(Read(input, source + sourcePitch)), d = Shift(Read(input, source + sourcePitch + 3));
                int target = y * 2 * outputPitch + x * 2 * outputBytesPerPixel;
                Write(output, target, a,outputBytesPerPixel); Write(output, target + outputBytesPerPixel, b,outputBytesPerPixel);
                Write(output, target + outputPitch, c,outputBytesPerPixel); Write(output, target + outputPitch + outputBytesPerPixel, d,outputBytesPerPixel);
                //0099E750..E906 stores the first converted pixel back to
                //the compacted source row; there is no ARGB quarter sum here.
                Write(input, y * sourcePitch + x * 3, a);
            }
    }

    private static uint Read(ReadOnlySpan<byte> source, int offset)
        => (uint)(source[offset] | source[offset + 1] << 8 | source[offset + 2] << 16);
    private static void Write(Span<byte> target, int offset, uint pixel, int bytesPerPixel=3)
    {
        target[offset] = (byte)pixel;
        target[offset + 1] = (byte)(pixel >> 8);
        target[offset + 2] = (byte)(pixel >> 16);
        if(bytesPerPixel==4)target[offset+3]=(byte)(pixel>>24);
    }
}
