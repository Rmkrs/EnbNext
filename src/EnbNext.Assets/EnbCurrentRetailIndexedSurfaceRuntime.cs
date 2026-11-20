using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0099C510's indexed/luminance input to ARGB8 scratch conversion.
/// Palette bytes use the native RGB or ARGB order supplied by its caller.</summary>
public static class EnbCurrentRetailIndexedSurfaceRuntime
{
    public static void ConvertToArgb(Span<uint> output, int width, int height, int outputPitch,
        ReadOnlySpan<byte> input, int sourceWidth, int sourceHeight, int sourcePitch,
        uint sourceFormat, ReadOnlySpan<byte> palette, int palettePixelBytes, Vector3 shift)
    {
        if(width==0 || height==0)return;
        if(sourceFormat is not (13 or 14) || width<0 || height<0 || sourceWidth<=0 || sourceHeight<=0 ||
            outputPitch<width || sourcePitch<sourceWidth ||
            output.Length<checked((height-1)*outputPitch+width) || input.Length<checked((sourceHeight-1)*sourcePitch+sourceWidth) ||
            (sourceFormat==13 && (palettePixelBytes is not (3 or 4) || palette.Length<256*palettePixelBytes)))
            throw new ArgumentException("Invalid indexed surface buffers or palette.");
        for(int y=0;y<height;y++)
            for(int x=0;x<width;x++)
            {
                uint index=input[(int)((long)y*sourceHeight/height)*sourcePitch+(int)((long)x*sourceWidth/width)];
                uint color;
                if(sourceFormat==14)color=0xff000000 | index<<16 | index<<8 | index;
                else
                {
                    int offset=(int)index*palettePixelBytes;
                    color=palettePixelBytes==3
                        ? 0xff000000 | (uint)palette[offset]<<16 | (uint)palette[offset+1]<<8 | palette[offset+2]
                        : (uint)palette[offset]<<24 | (uint)palette[offset+1]<<16 | (uint)palette[offset+2]<<8 | palette[offset+3];
                }
                output[y*outputPitch+x]=EnbCurrentRetailHsvRuntime.ShiftArgb8(color,shift);
            }
    }
}
