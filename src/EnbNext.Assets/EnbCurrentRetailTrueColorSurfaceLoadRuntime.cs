using System.Numerics;
using System.Runtime.InteropServices;

namespace EnbNext.Assets;

/// <summary>0094ACC0's conversion sequence for true-color TGA source pixels.
/// The texture owner supplies its allocated levels and their incoming bytes;
/// this routine preserves levels and pixels the native loader never writes.</summary>
public static class EnbCurrentRetailTrueColorSurfaceLoadRuntime
{
    /// <summary>0094ACC0's source read joined to its surface consumer.
    /// Raw pixel allocation and the caller's palette storage are explicit;
    /// partial file reads never initialize the remaining bytes or publish a
    /// replacement image. Device allocations remain supplied by the owner.</summary>
    public static bool TryLoadTga(ReadOnlySpan<byte> file,Memory<byte> sourcePixels,Memory<byte> palette,
        int width,int height,uint destinationFormat,IReadOnlyList<Memory<byte>> levels,IReadOnlyList<int> pitches,Vector3 shift)
    {
        ArgumentNullException.ThrowIfNull(levels);ArgumentNullException.ThrowIfNull(pitches);
        if(levels.Count!=pitches.Count)throw new ArgumentException("TGA allocated levels and pitches must agree.");
        if(levels.Count==0)return false; //0094ACCF precedes source opening.
        if(!EnbCurrentRetailTgaHeaderState.TryRead(file,out var header) || header!.Format==0)return false;
        if(header.Width<=0 || header.Height<=0)throw new InvalidDataException("TGA source dimensions do not define bounded pixels.");
        //0094AD48 toggles this retained header before009D2EA0/009D2C10.
        header=header with {Descriptor=(byte)(header.Descriptor^32)};
        if(EnbCurrentRetailTgaPixelReadRuntime.Read(file,header,palette,sourcePixels,false,out _)!=0)return false;
        if(header.Format is 13 or 14)
            LoadIndexed(sourcePixels.Span,header.Width,header.Height,header.Format,palette.Span,
                header.ColorMapDepth>>3,width,height,destinationFormat,levels,pitches,shift);
        else if(header.Format==9)
            LoadAlpha(sourcePixels.Span,header.Width,header.Height,width,height,destinationFormat,levels,pitches,shift);
        else
            Load(sourcePixels.Span,header.Width,header.Height,header.PixelDepth,width,height,destinationFormat,levels,pitches,shift);
        return true;
    }

    /// <summary>0094ACC0 leaves format9 input unstaged unless dimensions
    /// change. Its mip compaction remains in the one-byte source format.</summary>
    public static int LoadAlpha(Span<byte> source,int sourceWidth,int sourceHeight,int width,int height,
        uint destinationFormat,IReadOnlyList<Memory<byte>> levels,IReadOnlyList<int> pitches,Vector3 shift)
    {
        ArgumentNullException.ThrowIfNull(levels);ArgumentNullException.ThrowIfNull(pitches);
        if(levels.Count!=pitches.Count || destinationFormat is not (2 or 3 or 4 or 6 or 7 or 9 or 14) ||
            sourceWidth<=0 || sourceHeight<=0 || width<=0 || height<=0 || source.Length<checked(sourceWidth*sourceHeight))
            throw new ArgumentException("Invalid alpha surface task.");
        if(levels.Count==0)return 0;
        if(width!=sourceWidth || height!=sourceHeight)
        {
            var resized=new byte[checked(width*height*4)];
            EnbCurrentRetailAlphaSurfaceRuntime.ConvertToArgb(MemoryMarshal.Cast<byte,uint>(resized.AsSpan()),width,height,width,
                source,sourceWidth,sourceHeight,sourceWidth,false,shift);
            return Load(resized,width,height,32,width,height,destinationFormat,levels,pitches,Vector3.Zero);
        }
        int sourcePitch=sourceWidth,written=0;
        foreach(var level in levels)
        {
            if(destinationFormat is 9 or 14)
                ConvertToByte(level.Span,width,height,pitches[written],source,sourceWidth,sourceHeight,sourcePitch,9,destinationFormat,shift);
            else if(destinationFormat is 2 or 3)
                EnbCurrentRetailAlphaSurfaceRuntime.ConvertToArgb(MemoryMarshal.Cast<byte,uint>(level.Span),width,height,pitches[written]/4,
                    source,sourceWidth,sourceHeight,sourcePitch,true,shift);
            else
            {
                var expanded=new uint[checked(width*height)];var packed=MemoryMarshal.Cast<byte,ushort>(level.Span);int pitch=pitches[written]/2;
                if(pitch<width || packed.Length<checked((height-1)*pitch+width))throw new ArgumentException("Invalid packed alpha destination.");
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                    expanded[y*width+x]=EnbCurrentRetailPackedSurfaceRuntime.ExpandWord(packed[y*pitch+x],destinationFormat);
                EnbCurrentRetailAlphaSurfaceRuntime.ConvertToArgb(expanded,width,height,width,source,sourceWidth,sourceHeight,sourcePitch,true,shift);
                EnbCurrentRetailPackedSurfaceRuntime.ConvertArgbToPacked(packed,width,height,pitch,expanded,width,height,width,destinationFormat,false,Vector3.Zero);
            }
            written++;width>>=1;height>>=1;sourceWidth>>=1;sourceHeight>>=1;shift=Vector3.Zero;
            if(width==0 || height==0 || sourceWidth==0 || sourceHeight==0)break;
        }
        return written;
    }

    /// <summary>Connects the authored TGA decoder's oriented pixels to
    /// 0094ACC0's surface consumer. Allocation dimensions, format, pitches
    /// and incoming storage are supplied by the texture owner.</summary>
    public static int LoadDecodedTga(EnbTextureImage image, int width, int height,
        uint destinationFormat, IReadOnlyList<Memory<byte>> levels,
        IReadOnlyList<int> pitches, Vector3 shift)
    {
        ArgumentNullException.ThrowIfNull(image);
        if(image.Format is not (EnbTextureFormat.TgaTrueColor or EnbTextureFormat.TgaRleTrueColor or EnbTextureFormat.TgaGrayscale) ||
            image.TgaPixelDepth is not (8 or 16 or 24 or 32))
            throw new ArgumentException("The surface source must be a decoded retail TGA.", nameof(image));
        int pixelBytes=image.TgaPixelDepth/8;
        var source=new byte[checked(image.Width*image.Height*pixelBytes)];
        for(int y=0;y<image.Height;y++)
            for(int x=0;x<image.Width;x++)
            {
                var pixel=image.GetPixel(x,y);
                int offset=(y*image.Width+x)*pixelBytes;
                if(pixelBytes==1)source[offset]=image.CurrentRetailSurfaceFormat==9?pixel.A:pixel.R;
                else if(pixelBytes==2)
                {
                    //The format6 expansion retains every authored ARGB1555
                    //bit; recovering the word preserves its packed consumer.
                    ushort word=(ushort)((pixel.A==0?0:0x8000) | (pixel.R>>3)<<10 | (pixel.G>>3)<<5 | pixel.B>>3);
                    source[offset]=(byte)word;source[offset+1]=(byte)(word>>8);
                }
                else
                {
                    source[offset]=pixel.B;source[offset+1]=pixel.G;source[offset+2]=pixel.R;
                    if(pixelBytes==4)source[offset+3]=pixel.A;
                }
            }
        if(image.CurrentRetailSurfaceFormat==9)
            return LoadAlpha(source,image.Width,image.Height,width,height,destinationFormat,levels,pitches,shift);
        return pixelBytes==1
            ? LoadIndexed(source,image.Width,image.Height,14,ReadOnlySpan<byte>.Empty,0,width,height,destinationFormat,levels,pitches,shift)
            : Load(source,image.Width,image.Height,image.TgaPixelDepth,width,height,destinationFormat,levels,pitches,shift);
    }

    /// <summary>0094ACC0 always stages indexed/luminance pixels through
    /// ARGB8, even without resizing. HSV is consumed by that first stage.</summary>
    public static int LoadIndexed(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        uint sourceFormat, ReadOnlySpan<byte> palette, int palettePixelBytes,
        int width, int height, uint destinationFormat, IReadOnlyList<Memory<byte>> levels,
        IReadOnlyList<int> pitches, Vector3 shift)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if(levels.Count==0)return 0;
        var converted=new byte[checked(width*height*4)];
        EnbCurrentRetailIndexedSurfaceRuntime.ConvertToArgb(MemoryMarshal.Cast<byte,uint>(converted.AsSpan()),width,height,width,
            source,sourceWidth,sourceHeight,sourceWidth,sourceFormat,palette,palettePixelBytes,shift);
        return Load(converted,width,height,32,width,height,destinationFormat,levels,pitches,Vector3.Zero);
    }

    public static int Load(Span<byte> source, int sourceWidth, int sourceHeight, int sourcePixelDepth,
        int width, int height, uint destinationFormat, IReadOnlyList<Memory<byte>> levels,
        IReadOnlyList<int> pitches, Vector3 shift)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(pitches);
        if(levels.Count!=pitches.Count || sourcePixelDepth is not (16 or 24 or 32) || destinationFormat is not (2 or 3 or 4 or 6 or 7 or 9 or 14) ||
            sourceWidth<=0 || sourceHeight<=0 || width<=0 || height<=0)
            throw new ArgumentException("Invalid true-color surface task.");
        if(levels.Count==0)return 0; //0094ACCF rejects an empty allocation.
        int sourceFormat=sourcePixelDepth==16?6:sourcePixelDepth==24?1:2;
        int sourcePitch=checked(sourceWidth*(sourcePixelDepth/8));
        if(source.Length<checked(sourcePitch*sourceHeight))throw new ArgumentException("Source pixels exceed the supplied buffer.");
        if(sourceFormat==6 || width!=sourceWidth || height!=sourceHeight)
        {
            //0094AE65..AED1 writes a complete ARGB8 scratch surface before
            //entering the mip loop, and clears the HSV words immediately.
            var resized=new byte[checked(width*height*4)];
            if(sourceFormat==6)
                EnbCurrentRetailPackedSurfaceRuntime.ConvertToArgb(MemoryMarshal.Cast<byte,uint>(resized.AsSpan()),width,height,width,
                    MemoryMarshal.Cast<byte,ushort>(source),sourceWidth,sourceHeight,sourcePitch/2,6,shift);
            else if(sourceFormat==1)
                EnbCurrentRetailRgbSurfaceRuntime.ConvertToXrgb(resized,width,height,width*4,
                    source,sourceWidth,sourceHeight,sourcePitch,false,shift);
            else
                EnbCurrentRetailArgbSurfaceRuntime.Convert(MemoryMarshal.Cast<byte,uint>(resized.AsSpan()),width,height,width,
                    MemoryMarshal.Cast<byte,uint>(source),sourceWidth,sourceHeight,sourcePitch/4,false,shift);
            source=resized;
            sourceFormat=2;
            sourceWidth=width;sourceHeight=height;sourcePitch=checked(width*4);
            shift=Vector3.Zero;
        }
        int written=0;
        foreach(var level in levels)
        {
            if(destinationFormat is 9 or 14)
                ConvertToByte(level.Span,width,height,pitches[written],source,sourceWidth,sourceHeight,sourcePitch,(uint)sourceFormat,destinationFormat,shift);
            else if(destinationFormat is 4 or 6 or 7)
            {
                if(sourceFormat==1)
                    EnbCurrentRetailPackedSurfaceRuntime.ConvertRgbToPacked(MemoryMarshal.Cast<byte,ushort>(level.Span),width,height,pitches[written]/2,
                        source,sourceWidth,sourceHeight,sourcePitch,destinationFormat,true,shift);
                else
                    EnbCurrentRetailPackedSurfaceRuntime.ConvertArgbToPacked(MemoryMarshal.Cast<byte,ushort>(level.Span),width,height,pitches[written]/2,
                        MemoryMarshal.Cast<byte,uint>(source),sourceWidth,sourceHeight,sourcePitch/4,destinationFormat,true,shift);
            }
            else if(sourceFormat==1)
                EnbCurrentRetailRgbSurfaceRuntime.ConvertToXrgb(level.Span,width,height,pitches[written],
                    source,sourceWidth,sourceHeight,sourcePitch,true,shift);
            else if(destinationFormat==2)
                EnbCurrentRetailArgbSurfaceRuntime.Convert(MemoryMarshal.Cast<byte,uint>(level.Span),width,height,pitches[written]/4,
                    MemoryMarshal.Cast<byte,uint>(source),sourceWidth,sourceHeight,sourcePitch/4,true,shift);
            else
                EnbCurrentRetailArgbSurfaceRuntime.ConvertBetweenArgbAndXrgb(MemoryMarshal.Cast<byte,uint>(level.Span),width,height,pitches[written]/4,
                    MemoryMarshal.Cast<byte,uint>(source),sourceWidth,sourceHeight,sourcePitch/4,true,shift);
            written++;
            width>>=1;height>>=1;sourceWidth>>=1;sourceHeight>>=1;
            shift=Vector3.Zero;
            if(width==0 || height==0 || sourceWidth==0 || sourceHeight==0)break;
        }
        return written;
    }

    //0099C510's generic format9 destination writes converted blue. Mip
    //calls use this generic path even when both source/destination are9;
    //the separate non-mip same-format byte-copy path does not apply here.
    private static void ConvertToByte(Span<byte> output,int width,int height,int outputPitch,
        Span<byte> source,int sourceWidth,int sourceHeight,int sourcePitch,uint sourceFormat,uint destinationFormat,Vector3 shift)
    {
        if(outputPitch<width || output.Length<checked((height-1)*outputPitch+width))
            throw new ArgumentException("Invalid byte destination pitch.");
        var converted=new uint[checked(width*height)];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            converted[y*width+x]=destinationFormat==14?(uint)output[y*outputPitch+x]*0x010101u:output[y*outputPitch+x];
        if(sourceFormat==9)
            EnbCurrentRetailAlphaSurfaceRuntime.ConvertToArgb(converted,width,height,width,source,sourceWidth,sourceHeight,sourcePitch,true,shift);
        else if(sourceFormat==1)
            EnbCurrentRetailRgbSurfaceRuntime.ConvertToXrgb(MemoryMarshal.AsBytes(converted.AsSpan()),width,height,width*4,
                source,sourceWidth,sourceHeight,sourcePitch,true,shift);
        else
            EnbCurrentRetailArgbSurfaceRuntime.ConvertBetweenArgbAndXrgb(converted,width,height,width,
                MemoryMarshal.Cast<byte,uint>(source),sourceWidth,sourceHeight,sourcePitch/4,true,shift);
        //0099E31B..E372 (and the other quad stores) uses these integer
        //weights. They sum to65536, preserving unwritten incoming L8 bytes.
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            uint color=converted[y*width+x];
            output[y*outputPitch+x]=destinationFormat==14
                ? (byte)(((color&255)*4725+(color>>8&255)*46885+(color>>16&255)*13926)>>16)
                : (byte)color;
        }
    }
}
