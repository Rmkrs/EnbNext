using System.Buffers.Binary;

namespace EnbNext.Assets;

/// <summary>009D2980's file header path, separate from decoded pixel data.
/// The footer read is required even when no extension signature is present.</summary>
public sealed record EnbCurrentRetailTgaHeaderState(short Width,short Height,byte ImageType,
    byte ColorMapType,short ColorMapOrigin,short ColorMapLength,sbyte ColorMapDepth,
    sbyte PixelDepth,byte Descriptor,int PixelOffset,uint ExtensionOffset,uint Format,int BytesPerPixel)
{
    public static bool TryRead(ReadOnlySpan<byte> file,out EnbCurrentRetailTgaHeaderState? state)
    {
        state=null;
        if(file.Length<26)return false;
        var footer=file[^26..];
        uint extension=BinaryPrimitives.ReadUInt32LittleEndian(footer);
        bool hasExtension=footer.Slice(8,16).SequenceEqual("TRUEVISION-XFILE"u8) && extension!=0;
        if(hasExtension && (extension>(uint)file.Length || 0x1ef>(uint)file.Length-extension))return false;
        //Native MOVSX at009D2BB6 uses signed ID length after the18-byte read.
        int pixelOffset=18+unchecked((sbyte)file[0]);
        if(pixelOffset<0)return false;
        var (format,pixelBytes)=SelectFormat(file[1],file[2],unchecked((sbyte)file[16]));
        state=new(BinaryPrimitives.ReadInt16LittleEndian(file[12..]),BinaryPrimitives.ReadInt16LittleEndian(file[14..]),
            file[2],file[1],BinaryPrimitives.ReadInt16LittleEndian(file[3..]),BinaryPrimitives.ReadInt16LittleEndian(file[5..]),
            unchecked((sbyte)file[7]),unchecked((sbyte)file[16]),file[17],pixelOffset,hasExtension?extension:0,format,pixelBytes);
        return true;
    }

    /// <summary>009688E0's thumbnail format mapper, not009687F0's ordinary
    /// loader mapper. Palette type1 takes priority over grayscale image type3.</summary>
    public static (uint Format,int BytesPerPixel) SelectFormat(byte colorMapType,byte imageType,sbyte pixelDepth)
        =>pixelDepth switch
        {
            8=>(colorMapType==1?13u:imageType==3?14u:9u,1),
            16=>(6u,2),24=>(1u,3),32=>(2u,4),_=>(0u,0)
        };

    /// <summary>00919300's TGA thumbnail dimensions/count. Pixel read success
    /// is a separate prerequisite for publishing the resulting metadata.</summary>
    public static (uint Width,uint Height,uint MipLevels) SelectThumbnailShape(short width,short height)
    {
        if(width<0 || height<0)throw new ArgumentOutOfRangeException(nameof(width),"Negative native dimensions do not define bounded thumbnail storage.");
        uint levels=1,power=1;
        while(power<(uint)width && power<(uint)height){power*=2;levels++;}
        uint thumbWidth=(uint)(width>>3),thumbHeight=(uint)(height>>3);
        while(thumbWidth>32 || thumbHeight>32){thumbWidth>>=2;thumbHeight>>=2;}
        uint roundedWidth=1,roundedHeight=1;
        while(roundedWidth<thumbWidth)roundedWidth*=2;
        while(roundedHeight<thumbHeight)roundedHeight*=2;
        return (roundedWidth,roundedHeight,levels);
    }
}
