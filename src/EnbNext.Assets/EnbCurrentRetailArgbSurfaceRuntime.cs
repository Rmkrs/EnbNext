using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0099C510's same-format ARGB8/XRGB8 branch. Mip generation
/// writes the current level to output and compacts the next level back into
/// input with its retained pitch. Callers own the incoming buffer contents.</summary>
public static class EnbCurrentRetailArgbSurfaceRuntime
{
    public static void Convert(Span<uint> output, int width, int height, int outputPitch,
        Span<uint> input, int sourceWidth, int sourceHeight, int sourcePitch,
        bool generateMip, Vector3 shift)
        => ConvertCore(output,width,height,outputPitch,input,sourceWidth,sourceHeight,sourcePitch,generateMip,shift,true);

    /// <summary>0099C510's generic conversion between ARGB8 and XRGB8.
    /// Different native format IDs select first-pixel source compaction.</summary>
    public static void ConvertBetweenArgbAndXrgb(Span<uint> output, int width, int height, int outputPitch,
        Span<uint> input, int sourceWidth, int sourceHeight, int sourcePitch,
        bool generateMip, Vector3 shift)
        => ConvertCore(output,width,height,outputPitch,input,sourceWidth,sourceHeight,sourcePitch,generateMip,shift,false);

    private static void ConvertCore(Span<uint> output, int width, int height, int outputPitch,
        Span<uint> input, int sourceWidth, int sourceHeight, int sourcePitch,
        bool generateMip, Vector3 shift, bool sameFormat)
    {
        if (width == 0 || height == 0) return;
        if (width < 0 || height < 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
            outputPitch < width || sourcePitch < sourceWidth ||
            output.Length < checked((height - 1) * outputPitch + width) ||
            input.Length < checked((sourceHeight - 1) * sourcePitch + sourceWidth))
            throw new ArgumentException("Surface dimensions exceed the supplied buffers.");
        uint Shift(uint color) => EnbCurrentRetailHsvRuntime.ShiftArgb8(color, shift);
        if (width != sourceWidth || height != sourceHeight)
        {
            //0099CF83..D16A uses integer accumulators and division for
            //nearest source selection; no filtered resampling is substituted.
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    output[y * outputPitch + x] = Shift(input[(int)((long)y * sourceHeight / height) * sourcePitch +
                        (int)((long)x * sourceWidth / width)]);
            return;
        }
        if (!generateMip)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    output[y * outputPitch + x] = Shift(input[y * sourcePitch + x]);
            return;
        }
        if (width == 1)
        {
            //0099C5CD..C70F copies the first word only.
            output[0] = Shift(input[0]);
            return;
        }
        for (int y = 0; y < height >> 1; y++)
            for (int x = 0; x < width >> 1; x++)
            {
                int source = y * 2 * sourcePitch + x * 2;
                uint a = Shift(input[source]), b = Shift(input[source + 1]);
                uint c = Shift(input[source + sourcePitch]), d = Shift(input[source + sourcePitch + 1]);
                int target = y * 2 * outputPitch + x * 2;
                output[target] = a; output[target + 1] = b;
                output[target + outputPitch] = c; output[target + outputPitch + 1] = d;
                //0099CD12..CD3C floors each channel contribution separately.
                input[y * sourcePitch + x] = sameFormat
                    ? (a >> 2 & 0x3f3f3f3f) + (b >> 2 & 0x3f3f3f3f) +
                        (c >> 2 & 0x3f3f3f3f) + (d >> 2 & 0x3f3f3f3f)
                    : a;
            }
    }
}
