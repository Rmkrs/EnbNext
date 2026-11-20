using System.Text;

namespace EnbNext.Assets;

/// <summary>00919300's metadata projection for a single00919A50 package
/// provider. Thumbnail pixel storage is not synthesized by this metadata owner.</summary>
public static class EnbCurrentRetailTextureThumbnailGenerator
{
    public static EnbCurrentRetailTextureThumbnail? Create(EnbCurrentRetailMixSource package,string name,
        uint sourceStamp,Func<sbyte,int> convertHashCharacter)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Create(name,sourceName=>
        {
            uint hash=EnbCurrentRetailMixSource.ComputeNameHash(Encoding.Latin1.GetBytes(sourceName),convertHashCharacter);
            return package.TryReadResource(hash,out var bytes)?new EnbCurrentRetailThumbnailSource(bytes,sourceStamp):null;
        });
    }

    public static EnbCurrentRetailTextureThumbnail? Create(string name,
        Func<string,EnbCurrentRetailThumbnailSource?> readSource)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(readSource);
        int terminator=name.IndexOf('\0');
        if(terminator>=0)name=name[..terminator];
        if(name.Any(character=>character>255))throw new ArgumentException("The native source name is a byte string.",nameof(name));
        if(name.Length<3)throw new ArgumentException("A native source name requires its three-byte suffix.",nameof(name));
        string ddsName=name[..^3]+"dds";
        var dds=readSource(ddsName);
        if(dds is not null && EnbCurrentRetailDdsSourceState.TryRead(dds.Bytes.Span,3,out var source) &&
            source!.LoadReturnedSuccess)
            return new(ddsName,dds.SourceStamp,0,Math.Max(4u,source.ReducedWidth),Math.Max(4u,source.ReducedHeight),
                source.Width,source.Height,source.MipLevels,source.Format);
        var tga=readSource(name);
        if(tga is null || !EnbCurrentRetailTgaHeaderState.TryRead(tga.Bytes.Span,out var header) ||
            header!.Format==0 || !HasCompleteTgaPixels(tga.Bytes.Span,header))return null;
        var shape=EnbCurrentRetailTgaHeaderState.SelectThumbnailShape(header.Width,header.Height);
        return new(name[..^3]+"tga",tga.SourceStamp,0,shape.Width,shape.Height,
            (uint)header.Width,(uint)header.Height,shape.MipLevels,header.Format);
    }

    private static bool HasCompleteTgaPixels(ReadOnlySpan<byte> file,EnbCurrentRetailTgaHeaderState header)
    {
        //Do not publish metadata controlled by an unsafe source buffer size or
        //a native RLE packet writing beyond its allocation.
        if(header.Width<=0 || header.Height<=0)return false;
        long cursor=header.PixelOffset;
        if(header.ColorMapType==1)cursor+=(long)header.ColorMapLength*(header.ColorMapDepth>>3);
        if(cursor<0 || cursor>file.Length)return false;
        int pixelCount=header.Width*header.Height;
        int pixelBytes=header.BytesPerPixel;
        if(header.ImageType is 1 or 2 or 3)
            return (long)pixelCount*pixelBytes<=file.Length-cursor;
        if(header.ImageType is not (9 or 10))return false; //009D2C10 excludes11.
        int remaining=pixelCount;
        while(remaining!=0)
        {
            if(cursor==file.Length)return false;
            byte packet=file[(int)cursor++];
            int count=(packet&127)+1;
            if(count>remaining)return false;
            int readBytes=(packet&128)!=0?pixelBytes:count*pixelBytes;
            if(readBytes>file.Length-cursor)return false;
            cursor+=readBytes;remaining-=count;
        }
        return true;
    }
}

public sealed record EnbCurrentRetailThumbnailSource(ReadOnlyMemory<byte> Bytes,uint SourceStamp)
{
    public EnbMixResourceLocation? ResourceLocation { get; init; }
}
