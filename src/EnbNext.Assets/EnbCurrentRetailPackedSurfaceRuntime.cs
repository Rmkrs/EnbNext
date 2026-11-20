using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0099C510's packed 16-bit source to ARGB8 scratch conversion.
/// Channel expansion shifts the authored bits without replicating low bits.</summary>
public static class EnbCurrentRetailPackedSurfaceRuntime
{
    /// <summary>0099C510's generic ARGB8-to-packed destination branch.
    /// Source compaction retains the first unquantized converted quad pixel.</summary>
    public static void ConvertArgbToPacked(Span<ushort> output, int width, int height, int outputPitch,
        Span<uint> input, int sourceWidth, int sourceHeight, int sourcePitch,
        uint destinationFormat, bool generateMip, Vector3 shift)
    {
        var expanded=ExpandDestination(output,width,height,outputPitch,destinationFormat);
        EnbCurrentRetailArgbSurfaceRuntime.ConvertBetweenArgbAndXrgb(expanded,width,height,width,
            input,sourceWidth,sourceHeight,sourcePitch,generateMip,shift);
        StoreDestination(output,expanded,width,height,outputPitch,destinationFormat);
    }

    public static void ConvertRgbToPacked(Span<ushort> output, int width, int height, int outputPitch,
        Span<byte> input, int sourceWidth, int sourceHeight, int sourcePitch,
        uint destinationFormat, bool generateMip, Vector3 shift)
    {
        var expanded=ExpandDestination(output,width,height,outputPitch,destinationFormat);
        EnbCurrentRetailRgbSurfaceRuntime.ConvertToXrgb(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expanded.AsSpan()),
            width,height,width*4,input,sourceWidth,sourceHeight,sourcePitch,generateMip,shift);
        StoreDestination(output,expanded,width,height,outputPitch,destinationFormat);
    }

    private static uint[] ExpandDestination(ReadOnlySpan<ushort> output, int width, int height, int pitch, uint format)
    {
        if(format is not (4 or 6 or 7) || width<0 || height<0 || pitch<width ||
            (height!=0 && output.Length<checked((height-1)*pitch+width)))
            throw new ArgumentException("Invalid packed destination surface.");
        var expanded=new uint[checked(width*height)];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)expanded[y*width+x]=ExpandWord(output[y*pitch+x],format);
        return expanded;
    }

    private static void StoreDestination(Span<ushort> output, ReadOnlySpan<uint> expanded, int width, int height, int pitch, uint format)
    {
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            uint color=expanded[y*width+x],r=color>>16&255,g=color>>8&255,b=color&255,a=color>>24;
            output[y*pitch+x]=(ushort)(format switch
            {
                4 => (r&0xf8)<<8 | (g&0xfc)<<3 | b>>3,
                6 => (a==0?0u:0x8000u) | (r&0xf8)<<7 | (g&0xf8)<<2 | b>>3,
                _ => (a&0xf0)<<8 | (r&0xf0)<<4 | g&0xf0 | b>>4,
            });
        }
    }

    public static void ConvertToArgb(Span<uint> output, int width, int height, int outputPitch,
        ReadOnlySpan<ushort> input, int sourceWidth, int sourceHeight, int sourcePitch,
        uint sourceFormat, Vector3 shift)
    {
        if(width==0 || height==0)return;
        if(sourceFormat is not (4 or 6 or 7) || width<0 || height<0 || sourceWidth<=0 || sourceHeight<=0 ||
            outputPitch<width || sourcePitch<sourceWidth ||
            output.Length<checked((height-1)*outputPitch+width) || input.Length<checked((sourceHeight-1)*sourcePitch+sourceWidth))
            throw new ArgumentException("Invalid packed surface buffers or format.");
        for(int y=0;y<height;y++)
            for(int x=0;x<width;x++)
            {
                uint word=input[(int)((long)y*sourceHeight/height)*sourcePitch+(int)((long)x*sourceWidth/width)];
                uint color=ExpandWord((ushort)word,sourceFormat);
                output[y*outputPitch+x]=EnbCurrentRetailHsvRuntime.ShiftArgb8(color,shift);
            }
    }

    internal static uint ExpandWord(ushort source, uint sourceFormat)
    {
        uint word=source;
        return sourceFormat switch
        {
            4 => 0xff000000 | ((word>>8)&0xf8)<<16 | ((word>>3)&0xfc)<<8 | ((word<<3)&0xff),
            6 => ((word&0x8000)!=0?0xff000000u:0) | ((word>>7)&0xf8)<<16 | ((word>>2)&0xf8)<<8 | ((word<<3)&0xff),
            7 => ((word>>8)&0xf0)<<24 | ((word>>4)&0xf0)<<16 | (word&0xf0)<<8 | ((word<<4)&0xff),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceFormat))
        };
    }
}
