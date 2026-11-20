namespace EnbNext.Assets;

/// <summary>009D3520's RLE byte consumer. Incoming pixel storage is explicit;
/// short reads retain every byte the file read and decoder did not overwrite.</summary>
public static class EnbCurrentRetailTgaPixelReadRuntime
{
    /// <summary>009D2C10 after successful header/file positioning. Null
    /// buffers are the native absent pointers; nonnull buffers retain incoming
    /// storage. The caller supplies the descriptor already retained by its
    /// source object, including any ordinary-loader orientation mutation.</summary>
    public static int Read(ReadOnlySpan<byte> file,EnbCurrentRetailTgaHeaderState header,
        Memory<byte>? palette,Memory<byte>? pixels,bool reverseColorBytes,out byte descriptor)
    {
        descriptor=header.Descriptor;
        int cursor=header.PixelOffset;
        if(cursor<0 || cursor>file.Length)throw new InvalidDataException("TGA pixel position exceeds its source.");
        if(header.ColorMapType==1)
        {
            int paletteBytes=header.ColorMapDepth>>3;
            int count=header.ColorMapLength*paletteBytes;
            if(paletteBytes<0 || count<0)throw new InvalidDataException("TGA palette size is not bounded.");
            if(palette is {} storage && header.ColorMapLength>=1)
            {
                int offset=header.ColorMapOrigin*paletteBytes;
                if(offset<0 || count>storage.Length-offset)throw new InvalidDataException("TGA palette range exceeds incoming storage.");
                int read=Math.Min(count,file.Length-cursor);
                file.Slice(cursor,read).CopyTo(storage.Span[offset..]);cursor+=read;
                if(read!=count)return -2;
            }
            else
            {
                //The native seek only tests failure; pixels may subsequently
                //short-read beyond EOF. No palette bytes are synthesized.
                cursor=checked(cursor+count);
            }
        }
        if(pixels is not {} pixelStorage)return 0;
        int pixelBytes=(header.PixelDepth+7)>>3;
        if(pixelBytes is <1 or >4 || header.Width<0 || header.Height<0)
            throw new InvalidDataException("TGA pixel dimensions/depth are not bounded.");
        int pixelCount=checked(header.Width*header.Height),size=checked(pixelCount*pixelBytes);
        if(pixelStorage.Length<size)throw new InvalidDataException("TGA pixels exceed incoming storage.");
        var available=cursor>file.Length?ReadOnlySpan<byte>.Empty:file[cursor..];
        if(header.ImageType is 1 or 2 or 3)
        {
            int read=Math.Min(size,available.Length);available[..read].CopyTo(pixelStorage.Span);
            if(read!=size)return -2;
        }
        else if(header.ImageType is 9 or 10)
        {
            int result=DecodeRle(available,pixelBytes,pixelCount,pixelStorage.Span,out _);
            if(result!=0)return result;
        }
        else return -6;
        if(reverseColorBytes && (header.ImageType is 2 or 10) && pixelBytes>2)
            for(int index=0;index<pixelCount;index++)pixelStorage.Span.Slice(index*pixelBytes,pixelBytes).Reverse();
        if((descriptor&16)!=0)
        {
            for(int row=0;row<header.Height;row++)for(int column=0;column<header.Width/2;column++)
                Swap(pixelStorage.Span,(row*header.Width+column)*pixelBytes,(row*header.Width+header.Width-1-column)*pixelBytes,pixelBytes);
            descriptor&=239;
        }
        if((descriptor&32)!=0)
        {
            int pitch=header.Width*pixelBytes;
            for(int row=0;row<header.Height/2;row++)Swap(pixelStorage.Span,row*pitch,(header.Height-1-row)*pitch,pitch);
            descriptor&=223;
        }
        return 0;
    }

    private static void Swap(Span<byte> storage,int first,int second,int length)
    {
        for(int index=0;index<length;index++)(storage[first+index],storage[second+index])=(storage[second+index],storage[first+index]);
    }

    public static int DecodeRle(ReadOnlySpan<byte> input,int pixelBytes,int pixelCount,Span<byte> pixels,out int consumed)
    {
        if(pixelBytes is <1 or >4 || pixelCount<0 || pixels.Length<checked(pixelBytes*pixelCount))
            throw new ArgumentException("Invalid bounded TGA RLE storage.");
        consumed=0;
        int remaining=pixelCount,offset=0;
        while(remaining!=0)
        {
            if(consumed==input.Length)return -2; //009D363F.
            byte packet=input[consumed++];int count=(packet&127)+1;
            if(count>remaining)throw new InvalidDataException("TGA RLE packet exceeds its pixel allocation.");
            bool repeat=(packet&128)!=0;
            int requested=repeat?pixelBytes:count*pixelBytes;
            int read=Math.Min(requested,input.Length-consumed);
            input.Slice(consumed,read).CopyTo(pixels[offset..]);consumed+=read;
            //009D3626 does not replicate a partially read run pixel.
            if(read!=requested)return -2;
            if(repeat)
                for(int index=1;index<count;index++)pixels.Slice(offset,pixelBytes).CopyTo(pixels.Slice(offset+index*pixelBytes,pixelBytes));
            offset+=count*pixelBytes;remaining-=count;
        }
        return 0;
    }
}
