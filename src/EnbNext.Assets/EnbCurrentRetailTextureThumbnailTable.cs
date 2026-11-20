using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Assets;

/// <summary>00919DD0's THU6 entries. SourceStamp is the source file's packed
/// DOS date/time (00684A60). Names retain their authored suffix.</summary>
public sealed record EnbCurrentRetailTextureThumbnail(
    string Name,uint SourceStamp,uint PixelOffset,uint ThumbnailWidth,uint ThumbnailHeight,
    uint Width,uint Height,uint MipLevels,uint Format);

/// <summary>Historical THU6 decoding for compatibility checks, plus shared texture-name keys.
/// Client texture metadata is generated directly from source assets.</summary>
public static class EnbCurrentRetailTextureThumbnailTable
{

    /// <summary>00919D30/0091A137..141 remove the final four bytes and use
    /// 00A27F25's ASCII folding. They do not trim or parse an extension.</summary>
    public static string LookupKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if(name.Length<4)throw new ArgumentException("A thumbnail name requires its four-byte suffix.",nameof(name));
        return FoldAscii(name[..^4]);
    }

    internal static string FoldAscii(string name)
    {
        var key=name.ToCharArray();
        for(int index=0;index<key.Length;index++)
            if(key[index] is >= 'A' and <= 'Z')key[index]+=(char)32;
        return new(key);
    }
}
