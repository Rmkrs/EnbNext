using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;

namespace EnbNext.Assets;

public enum EnbTextureFormat
{
    DdsDxt1,
    DdsDxt5,
    TgaTrueColor,
    TgaRleTrueColor,
    CurrentRetailRuntimeRgb8,
    CurrentRetailRuntimeArgb4444,
    CurrentRetailMissingArgb8,
    DdsDxt3,
    TgaGrayscale,
    CurrentRetailPackedSurface,
    CurrentRetailRuntimeArgb8,
    DdsDxt2,
    DdsDxt4,
}

/// <summary>One decoded authored texture mip in top-left pixel order.</summary>
public sealed class EnbTextureMipLevel
{
    private EnbColor32[]? _pixels;
    private readonly byte[]? _packedPixels;
    private readonly byte[]? _compressedBlocks;

    internal EnbTextureMipLevel(int width, int height, EnbColor32[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<EnbColor32> Pixels => GetPixels();
    public ReadOnlyMemory<byte> CurrentRetailPackedPixels => _packedPixels is {} pixels ? pixels : ReadOnlyMemory<byte>.Empty;
    public ReadOnlyMemory<byte> CompressedBlocks => _compressedBlocks is {} blocks ? blocks : ReadOnlyMemory<byte>.Empty;
    public uint CompressedFourCc { get; }

    internal EnbTextureMipLevel(int width, int height, uint fourCc, byte[] blocks)
    { Width=width; Height=height; CompressedFourCc=fourCc; _compressedBlocks=blocks; }

    private EnbColor32[] GetPixels()
    {
        if (Volatile.Read(ref _pixels) is {} existing) return existing;
        // Compressed blocks are the immutable source of truth. CPU consumers
        // materialize their decoded view only when they actually need pixels.
        var decoded=EnbDdsTexture.DecodeCompressedMip(_compressedBlocks!,Width,Height,CompressedFourCc);
        return Interlocked.CompareExchange(ref _pixels,decoded,null) ?? decoded;
    }

    internal EnbTextureMipLevel(int width, int height, EnbColor32[] pixels, byte[] packedPixels)
        : this(width,height,pixels) => _packedPixels=packedPixels;

    public EnbColor32 GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width)
            throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(y));
        return GetPixels()[(y * Width) + x];
    }

    internal EnbColor32[] CopyPixels() => (EnbColor32[])GetPixels().Clone();
    internal EnbTextureMipLevel Clone() => _compressedBlocks is {} blocks
        ? new(Width,Height,CompressedFourCc,(byte[])blocks.Clone())
        : _packedPixels is null ? new(Width, Height, CopyPixels())
        : new(Width,Height,CopyPixels(),(byte[])_packedPixels.Clone());
}

/// <summary>
/// Platform-neutral decoded texture image used by the presentation and
/// rendering asset layers. DDS images retain their authored mip chain; TGA
/// images currently contain the decoded top level only. The top-level Pixels
/// and GetPixel members remain compatibility conveniences for existing callers.
/// </summary>
public sealed class EnbTextureImage
{
    private readonly IReadOnlyList<EnbTextureMipLevel> _mipLevels;

    internal EnbTextureImage(int width, int height, EnbTextureFormat format, EnbColor32[] pixels)
        : this(format, [new EnbTextureMipLevel(width, height, pixels)])
    {
    }

    internal EnbTextureImage(EnbTextureFormat format, IReadOnlyList<EnbTextureMipLevel> mipLevels)
    {
        if (mipLevels.Count == 0)
            throw new ArgumentException("A decoded texture requires at least one mip level.", nameof(mipLevels));

        Format = format;
        _mipLevels = mipLevels;
        Width = mipLevels[0].Width;
        Height = mipLevels[0].Height;
    }

    public int Width { get; }

    public int Height { get; }

    public EnbTextureFormat Format { get; }

    // Retained for format-dependent retail consumers (009687F0 maps 24/32
    // bits to RGB8/ARGB8). Opaque pixels do not remove an authored alpha channel.
    public int TgaPixelDepth { get; init; }
    /// <summary>Native packed storage retained separately from the CPU converter's shifted channel bytes.</summary>
    public uint CurrentRetailSurfaceFormat { get; internal init; }

    public IReadOnlyList<EnbTextureMipLevel> MipLevels => _mipLevels;

    public ReadOnlyMemory<EnbColor32> Pixels => _mipLevels[0].Pixels;

    public EnbColor32 GetPixel(int x, int y) => _mipLevels[0].GetPixel(x, y);

    /// <summary>Retains allocated native RGB8/ARGB8/XRGB8 levels for GPU
    /// publication. Pitch padding is excluded; XRGB's unused byte is not alpha.</summary>
    public static EnbTextureImage CreateCurrentRetailColorSurface(uint pixelFormat,
        IReadOnlyList<(int Width,int Height,ReadOnlyMemory<byte> Pixels,int Pitch)> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if(pixelFormat is not (1 or 2 or 3) || levels.Count==0)
            throw new ArgumentException("Invalid color texture format or levels.");
        int bytes=pixelFormat==1?3:4,width=levels[0].Width,height=levels[0].Height;
        var frozen=new EnbTextureMipLevel[levels.Count];
        for(int index=0;index<levels.Count;index++)
        {
            var level=levels[index];
            if(width<=0 || height<=0 || level.Width!=width || level.Height!=height ||
                level.Pitch<checked(width*bytes) || level.Pixels.Length<checked((height-1)*level.Pitch+width*bytes))
                throw new ArgumentException("Invalid color texture level dimensions or pitch.");
            var pixels=new EnbColor32[checked(width*height)];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                var value=level.Pixels.Span.Slice(y*level.Pitch+x*bytes,bytes);
                pixels[y*width+x]=new(value[2],value[1],value[0],pixelFormat==2?value[3]:(byte)255);
            }
            frozen[index]=new(width,height,pixels);
            width=Math.Max(1,width>>1);height=Math.Max(1,height>>1);
        }
        // Like the packed/alpha surface factories, these are already allocated
        // levels. The upload planner must not apply source reduction again.
        return new(EnbTextureFormat.CurrentRetailPackedSurface,Array.AsReadOnly(frozen))
            {CurrentRetailSurfaceFormat=pixelFormat};
    }

    /// <summary>Freezes format9's alpha bytes. D3DFMT_A8 sampling supplies
    /// zero RGB; RGBA8 preserves those values and all eight alpha bits.</summary>
    public static EnbTextureImage CreateCurrentRetailAlphaSurface(
        IReadOnlyList<(int Width,int Height,ReadOnlyMemory<byte> Pixels,int Pitch)> levels)
        => CreateCurrentRetailByteSurface(levels,9);

    /// <summary>Freezes native14 L8 bytes with replicated RGB and opaque alpha.</summary>
    public static EnbTextureImage CreateCurrentRetailLuminanceSurface(
        IReadOnlyList<(int Width,int Height,ReadOnlyMemory<byte> Pixels,int Pitch)> levels)
        => CreateCurrentRetailByteSurface(levels,14);

    private static EnbTextureImage CreateCurrentRetailByteSurface(
        IReadOnlyList<(int Width,int Height,ReadOnlyMemory<byte> Pixels,int Pitch)> levels,uint format)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if(levels.Count==0)throw new ArgumentException("An alpha texture requires a level.",nameof(levels));
        var frozen=new EnbTextureMipLevel[levels.Count];int width=levels[0].Width,height=levels[0].Height;
        for(int index=0;index<levels.Count;index++)
        {
            var level=levels[index];
            if(width<=0 || height<=0 || level.Width!=width || level.Height!=height || level.Pitch<width ||
                level.Pixels.Length<checked((height-1)*level.Pitch+width))throw new ArgumentException("Invalid alpha level dimensions or pitch.");
            var pixels=new EnbColor32[checked(width*height)];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                byte value=level.Pixels.Span[y*level.Pitch+x];
                pixels[y*width+x]=format==14?new(value,value,value,255):new(0,0,0,value);
            }
            frozen[index]=new(width,height,pixels);width=Math.Max(1,width>>1);height=Math.Max(1,height>>1);
        }
        return new(EnbTextureFormat.CurrentRetailPackedSurface,Array.AsReadOnly(frozen)){CurrentRetailSurfaceFormat=format};
    }

    public static EnbTextureImage CreateCurrentRetailPackedSurface(uint pixelFormat,
        IReadOnlyList<(int Width, int Height, ReadOnlyMemory<byte> Pixels, int Pitch)> levels,
        EnbTextureFormat format=EnbTextureFormat.CurrentRetailPackedSurface, int tgaPixelDepth=0)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if(pixelFormat is not (4 or 6 or 7) || levels.Count==0)throw new ArgumentException("Invalid packed texture format or levels.");
        var frozen=new EnbTextureMipLevel[levels.Count];
        int expectedWidth=levels[0].Width,expectedHeight=levels[0].Height;
        for(int index=0;index<levels.Count;index++)
        {
            var level=levels[index];
            if(level.Width<=0 || level.Height<=0 || level.Width!=expectedWidth || level.Height!=expectedHeight ||
                level.Pitch<checked(level.Width*2) || level.Pixels.Length<checked((level.Height-1)*level.Pitch+level.Width*2))
                throw new ArgumentException("Invalid packed texture level dimensions or pitch.");
            var packed=new byte[checked(level.Width*level.Height*2)];var colors=new EnbColor32[level.Width*level.Height];
            for(int y=0;y<level.Height;y++)for(int x=0;x<level.Width;x++)
            {
                int offset=y*level.Pitch+x*2,target=y*level.Width+x;
                ushort word=BinaryPrimitives.ReadUInt16LittleEndian(level.Pixels.Span.Slice(offset,2));
                BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(target*2,2),word);
                uint color=EnbCurrentRetailPackedSurfaceRuntime.ExpandWord(word,pixelFormat);
                colors[target]=new((byte)(color>>16),(byte)(color>>8),(byte)color,(byte)(color>>24));
            }
            frozen[index]=new(level.Width,level.Height,colors,packed);
            expectedWidth=Math.Max(1,expectedWidth>>1);expectedHeight=Math.Max(1,expectedHeight>>1);
        }
        return new(format,Array.AsReadOnly(frozen)){CurrentRetailSurfaceFormat=pixelFormat,TgaPixelDepth=tgaPixelDepth};
    }

    /// <summary>
    /// Freezes a current-retail generated RGB8 texture and its already-built mip
    /// chain. Runtime surface conversions discard source alpha, so every stored
    /// pixel is normalized to opaque just as D3DFMT_R8G8B8 is when sampled.
    /// </summary>
    public static EnbTextureImage CreateCurrentRetailRuntimeRgb8(
        IReadOnlyList<(int Width, int Height, IReadOnlyList<EnbColor32> Pixels)> mipLevels)
    {
        ArgumentNullException.ThrowIfNull(mipLevels);
        if (mipLevels.Count == 0)
            throw new ArgumentException("A current-retail runtime texture requires at least one mip level.", nameof(mipLevels));

        var frozen = new EnbTextureMipLevel[mipLevels.Count];
        int expectedWidth = mipLevels[0].Width;
        int expectedHeight = mipLevels[0].Height;
        for (int index = 0; index < mipLevels.Count; index++)
        {
            (int width, int height, IReadOnlyList<EnbColor32> pixels) = mipLevels[index];
            ArgumentNullException.ThrowIfNull(pixels);
            if (width <= 0 || height <= 0 || width != expectedWidth || height != expectedHeight)
            {
                throw new ArgumentException(
                    $"Runtime mip {index} is {width}x{height}; expected {expectedWidth}x{expectedHeight}.",
                    nameof(mipLevels));
            }
            if (pixels.Count != checked(width * height))
            {
                throw new ArgumentException(
                    $"Runtime mip {index} has {pixels.Count} pixels for {width}x{height}.",
                    nameof(mipLevels));
            }

            var copy = new EnbColor32[pixels.Count];
            for (int pixel = 0; pixel < copy.Length; pixel++)
            {
                EnbColor32 source = pixels[pixel];
                copy[pixel] = new EnbColor32(source.R, source.G, source.B, byte.MaxValue);
            }
            frozen[index] = new EnbTextureMipLevel(width, height, copy);
            expectedWidth = Math.Max(1, expectedWidth >> 1);
            expectedHeight = Math.Max(1, expectedHeight >> 1);
        }

        return new EnbTextureImage(
            EnbTextureFormat.CurrentRetailRuntimeRgb8,
            Array.AsReadOnly(frozen));
    }

    /// <summary>
    /// Freezes a current-retail generated A4R4G4B4 texture. The supplied RGBA8
    /// pixels are quantized exactly to four bits per channel and expanded back
    /// to RGBA8 for the backend-neutral decoded representation.
    /// </summary>
    public static EnbTextureImage CreateCurrentRetailRuntimeArgb4444(
        int width,
        int height,
        IReadOnlyList<EnbColor32> pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "A current-retail runtime texture requires positive dimensions.");
        if (pixels.Count != checked(width * height))
        {
            throw new ArgumentException(
                $"Runtime texture has {pixels.Count} pixels for {width}x{height}.",
                nameof(pixels));
        }

        var quantized = new EnbColor32[pixels.Count];
        for (int index = 0; index < quantized.Length; index++)
        {
            EnbColor32 source = pixels[index];
            quantized[index] = new EnbColor32(
                QuantizeArgb4444Channel(source.R),
                QuantizeArgb4444Channel(source.G),
                QuantizeArgb4444Channel(source.B),
                QuantizeArgb4444Channel(source.A));
        }

        return new EnbTextureImage(
            EnbTextureFormat.CurrentRetailRuntimeArgb4444,
            Array.AsReadOnly(new[] { new EnbTextureMipLevel(width, height, quantized) }));
    }

    /// <summary>Freezes a generated format2 A8R8G8B8 surface without discarding alpha.</summary>
    public static EnbTextureImage CreateCurrentRetailRuntimeArgb8(int width, int height,
        IReadOnlyList<EnbColor32> pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Count != checked(width * height))
            throw new ArgumentException("Runtime texture dimensions and pixel count differ.", nameof(pixels));
        return new EnbTextureImage(EnbTextureFormat.CurrentRetailRuntimeArgb8,
            Array.AsReadOnly(new[] { new EnbTextureMipLevel(width, height, pixels.ToArray()) }))
            { CurrentRetailSurfaceFormat = 2 };
    }

    private static byte QuantizeArgb4444Channel(byte value)
        => (byte)((value >> 4) * 17);
}

/// <summary>
/// Deterministic retail texture decoder. The currently evidenced Earth &amp;
/// Beyond DDS path now includes the live R6Z7-observed DXT1 and DXT5 families.
/// Other DDS FourCC values fail explicitly rather than being interpreted approximately. TGA support
/// covers true-color type 2 and RLE true-color type 10, 24/32 bpp, while
/// normalizing all origin combinations to top-left pixel order.
/// </summary>
public static class EnbRetailTextureDecoder
{
    /// <summary>Native20->21's opaque blocks use the modern BC2 carrier;
    /// native format21 remains part of the allocation request.</summary>
    public static bool TryDecodeCurrentRetailDxt1ToDxt2(ReadOnlySpan<byte> bytes,Vector3 shift,out EnbTextureImage image)
    {
        image=default!;
        if(!EnbDdsTexture.TryDecodeDxt1AsDxt2Hsv(bytes,shift,out var converted))return false;
        image=new(EnbTextureFormat.DdsDxt3,converted.MipLevels){CurrentRetailSurfaceFormat=21};
        return true;
    }

    private const uint DdsMagic = 0x20534444; // "DDS "
    private const int TgaHeaderLength = 18;
    private const int MaximumDimension = 16_384;
    private const int MaximumPixelCount = 16_777_216;

    public static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        string? resolvedResourceName,
        out EnbTextureImage image)
    {
        image = default!;

        if (bytes.Length >= sizeof(uint) &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) == DdsMagic)
        {
            uint fourCc = bytes.Length >= 88
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(84, 4))
                : 0u;
            if (fourCc == 0x31545844u && EnbDdsTexture.TryDecodeDxt1(bytes, out EnbDdsTexture dxt1))
            {
                image = new EnbTextureImage(
                    EnbTextureFormat.DdsDxt1,
                    dxt1.MipLevels);
                return true;
            }

            if (fourCc == 0x33545844u && EnbDdsTexture.TryDecodeDxt3(bytes, out EnbDdsTexture dxt3))
            {
                image = new EnbTextureImage(EnbTextureFormat.DdsDxt3, dxt3.MipLevels);
                return true;
            }

            if (fourCc == 0x32545844u && EnbDdsTexture.TryDecodeDxt2(bytes, out var dxt2))
            {
                image = new(EnbTextureFormat.DdsDxt2,dxt2.MipLevels){CurrentRetailSurfaceFormat=21};
                return true;
            }
            if (fourCc == 0x34545844u && EnbDdsTexture.TryDecodeDxt4(bytes, out var dxt4))
            {
                image = new(EnbTextureFormat.DdsDxt4,dxt4.MipLevels){CurrentRetailSurfaceFormat=23};
                return true;
            }

            if (fourCc == 0x35545844u && EnbDdsTexture.TryDecodeDxt5(bytes, out EnbDdsTexture dxt5))
            {
                image = new EnbTextureImage(
                    EnbTextureFormat.DdsDxt5,
                    dxt5.MipLevels);
                return true;
            }

            return false;
        }

        if (!string.Equals(
                Path.GetExtension(resolvedResourceName ?? string.Empty),
                ".tga",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryDecodeTga(bytes, out image);
    }

    private static bool TryDecodeTga(ReadOnlySpan<byte> bytes, out EnbTextureImage image)
    {
        image = default!;
        if (bytes.Length < TgaHeaderLength)
            return false;

        int idLength = bytes[0];
        int colorMapType = bytes[1];
        int imageType = bytes[2];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(12, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(14, 2));
        int pixelDepth = bytes[16];
        byte descriptor = bytes[17];

        bool grayscale=imageType==3 && pixelDepth==8;
        if (colorMapType != 0 ||
            (!grayscale && imageType is not 2 and not 10) ||
            width <= 0 || height <= 0 ||
            width > MaximumDimension || height > MaximumDimension ||
            (!grayscale && pixelDepth is not 8 and not 16 and not 24 and not 32))
        {
            return false;
        }

        int pixelCount;
        int bytesPerPixel = pixelDepth / 8;
        int dataOffset;
        try
        {
            pixelCount = checked(width * height);
            if (pixelCount > MaximumPixelCount)
                return false;
            dataOffset = checked(TgaHeaderLength + idLength);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (dataOffset > bytes.Length)
            return false;

        var pixels = new EnbColor32[pixelCount];
        int sourceOffset = dataOffset;
        int emitted = 0;
        bool topOrigin = (descriptor & 0x20) != 0;
        bool rightOrigin = (descriptor & 0x10) != 0;

        if (imageType is 2 or 3)
        {
            int required;
            try
            {
                required = checked(pixelCount * bytesPerPixel);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (required > bytes.Length - sourceOffset)
                return false;

            while (emitted < pixelCount)
            {
                EnbColor32 color = ReadTgaPixel(bytes, ref sourceOffset, bytesPerPixel);
                WriteNormalizedTgaPixel(pixels, width, height, emitted++, topOrigin, rightOrigin, color);
            }
        }
        else
        {
            var unpacked=new byte[checked(pixelCount*bytesPerPixel)];
            try
            {
                if(EnbCurrentRetailTgaPixelReadRuntime.DecodeRle(bytes[sourceOffset..],bytesPerPixel,pixelCount,unpacked,out _)!=0)return false;
            }
            catch(InvalidDataException){return false;}
            //This public decoder publishes complete images only. The native
            //byte consumer separately exposes short-read retained storage.
            int offset=0;
            while(emitted<pixelCount)
                WriteNormalizedTgaPixel(pixels,width,height,emitted++,topOrigin,rightOrigin,
                    ReadTgaPixel(unpacked,ref offset,bytesPerPixel));
        }

        if(pixelDepth==16)
        {
            var packed=new byte[checked(pixelCount*2)];
            for(int index=0;index<pixels.Length;index++)
            {
                var pixel=pixels[index];
                ushort word=(ushort)((pixel.A==0?0:0x8000) | (pixel.R>>3)<<10 | (pixel.G>>3)<<5 | pixel.B>>3);
                BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(index*2,2),word);
            }
            image=EnbTextureImage.CreateCurrentRetailPackedSurface(6,
                [(width,height,(ReadOnlyMemory<byte>)packed,width*2)],
                imageType==2?EnbTextureFormat.TgaTrueColor:EnbTextureFormat.TgaRleTrueColor,16);
            return true;
        }
        if(pixelDepth==8 && !grayscale)
        {
            //009687F0 selects format9 for non-indexed, non-grayscale
            //eight-bit input. The byte is alpha, with zero color channels.
            for(int index=0;index<pixels.Length;index++)
                pixels[index]=new(0,0,0,pixels[index].R);
        }
        image = new EnbTextureImage(
            width,
            height,
            grayscale ? EnbTextureFormat.TgaGrayscale : imageType == 2 ? EnbTextureFormat.TgaTrueColor : EnbTextureFormat.TgaRleTrueColor,
            pixels) { TgaPixelDepth = pixelDepth, CurrentRetailSurfaceFormat = pixelDepth==8 && !grayscale ? 9u : 0u };
        return true;
    }

    private static EnbColor32 ReadTgaPixel(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        int bytesPerPixel)
    {
        if(bytesPerPixel==1)
        {
            byte luminance=bytes[offset++];
            return new(luminance,luminance,luminance,255);
        }
        if(bytesPerPixel==2)
        {
            //009687F0 maps16-bit TGA to format6;0099C510 expands ARGB1555
            //by shifting authored bits, without low-bit replication.
            uint word=EnbCurrentRetailPackedSurfaceRuntime.ExpandWord(
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset,2)),6);
            offset+=2;
            return new((byte)(word>>16),(byte)(word>>8),(byte)word,(byte)(word>>24));
        }
        byte b = bytes[offset++];
        byte g = bytes[offset++];
        byte r = bytes[offset++];
        byte a = bytesPerPixel == 4 ? bytes[offset++] : byte.MaxValue;
        return new EnbColor32(r, g, b, a);
    }

    private static void WriteNormalizedTgaPixel(
        EnbColor32[] pixels,
        int width,
        int height,
        int emittedIndex,
        bool topOrigin,
        bool rightOrigin,
        EnbColor32 color)
    {
        int fileX = emittedIndex % width;
        int fileY = emittedIndex / width;
        int x = rightOrigin ? (width - 1) - fileX : fileX;
        int y = topOrigin ? fileY : (height - 1) - fileY;
        pixels[(y * width) + x] = color;
    }
}

public enum EnbTextureResolutionStatus
{
    Decoded,
    MissingResource,
    UnsupportedOrInvalidTexture,
}

public sealed record EnbResolvedTextureResource(
    string LogicalName,
    EnbTextureResolutionStatus Status,
    EnbMixResourceLocation? ResourceLocation = null,
    EnbTextureImage? Image = null,
    string? Diagnostic = null)
{
    public bool IsResolved => Status == EnbTextureResolutionStatus.Decoded;
    /// <summary>TextureClass+38 survives recoloring; the registry key is separate.</summary>
    public string? CurrentRetailOriginalTextureName { get; init; }
}

/// <summary>
/// Resolves and decodes installed texture resources while retaining the MIX
/// archive location as runtime provenance. Logical .tga references retain the
/// MIX catalog's retail-compatible .dds fallback behavior.
/// </summary>
public sealed class EnbRetailTextureResolver
{
    private readonly EnbMixResourceCatalog? _resources;
    private readonly EnbCurrentRetailThumbnailFileSource? _currentRetailFiles;
    private readonly EnbTextureOverridePack? _overrides;
    private readonly ConcurrentDictionary<string, EnbResolvedTextureResource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public EnbRetailTextureResolver(EnbMixResourceCatalog? resources,EnbCurrentRetailThumbnailFileSource? currentRetailFiles=null, EnbTextureOverridePack? overrides=null)
    {
        _resources = resources;
        _currentRetailFiles=currentRetailFiles;
        _overrides=overrides;
    }

    /// <summary>The installed client whose 009AF0F0 image a failed recolor load retains.</summary>
    public string? ClientExecutablePath { get; init; }

    public EnbResolvedTextureResource Resolve(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        string normalized = logicalName.Trim();
        return _cache.GetOrAdd(normalized, ResolveCore);
    }

    public EnbResolvedTextureResource ResolveCurrentRetailHsv(string logicalName, Vector3 shift,
        Func<string, Vector3, EnbResolvedTextureResource?>? loadSurface = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        string normalized = NormalizeHsvSourceName(logicalName);
        // Failed named loads publish 009AF0F0 before ordinary HSV processing.
        if (_cache.TryGetValue(normalized, out var prepared) && prepared.Image?.Format == EnbTextureFormat.CurrentRetailMissingArgb8) return prepared;
        //0048B7B0 gates the named variant;0048CB40's direct constructor
        //has no HSV threshold and remains a separate operation below.
        if (!EnbCurrentRetailAssetVariantName.ChangesHsv(shift) || normalized.StartsWith('!')) return Resolve(normalized);
        string variant = EnbCurrentRetailAssetVariantName.CreateTexture(normalized, shift);
        return _cache.GetOrAdd(variant, _ => loadSurface?.Invoke(normalized,shift) is {} surface
            ? surface with {LogicalName=variant,CurrentRetailOriginalTextureName=normalized}
            : RecolorCore(normalized, variant, shift));
    }

    /// <summary>0048CB40 always constructs and publishes a fresh recolored
    /// texture. Existing references retain their image; subsequent named
    ///0048B7B0 lookups find the newest equal formatted key.</summary>
    public EnbResolvedTextureResource RecolorCurrentRetailTexture(string logicalName, Vector3 shift)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        string normalized=NormalizeHsvSourceName(logicalName);
        if (normalized.StartsWith('!')) return Resolve(normalized);
        if (_cache.TryGetValue(normalized,out var prepared) && prepared.Image?.Format==EnbTextureFormat.CurrentRetailMissingArgb8) return prepared;
        string variant=EnbCurrentRetailAssetVariantName.CreateTexture(normalized,shift);
        var recolored=RecolorCore(normalized,variant,shift);
        _cache[variant]=recolored;
        return recolored;
    }

    /// <summary>0048D1F0 queries0048CF90 before calling0048CB40.
    /// Unlike0048B7B0, this lookup has no HSV threshold.</summary>
    public EnbResolvedTextureResource ResolveCurrentRetailMeshHsv(EnbResolvedTextureResource source, Vector3 shift,
        Func<string, Vector3, EnbResolvedTextureResource?>? loadSurface = null)
    {
        string name = source.CurrentRetailOriginalTextureName ?? source.LogicalName;
        string key = EnbCurrentRetailAssetVariantName.CreateTexture(name, shift);
        if (_cache.TryGetValue(key, out var existing)) return existing;
        if (!name.StartsWith('!') && source.Image?.Format != EnbTextureFormat.CurrentRetailMissingArgb8 &&
            loadSurface?.Invoke(name, shift) is {} surface)
        {
            var result=surface with {LogicalName=key,CurrentRetailOriginalTextureName=name};
            _cache[key]=result;
            return result;
        }
        return RecolorCurrentRetailTexture(name, shift);
    }

    private string NormalizeHsvSourceName(string name)
    {
        if(_currentRetailFiles is null)return name.Trim();
        int terminator=name.IndexOf('\0');
        return terminator<0?name:name[..terminator];
    }

    /// <summary>Reads the retained source name through the established file
    /// owner.009A0870 replaces its last three bytes for DDS;0094ACC0 opens the
    /// original name. Neither branch retries a different package owner.</summary>
    public bool TryReadCurrentRetailSource(string logicalName,bool compressed,
        out byte[] bytes,out EnbMixResourceLocation location)
    {
        string name=NormalizeHsvSourceName(logicalName);
        bytes=[];location=null!;
        if(_overrides?.TryRead(name,compressed,out bytes,out location)==true)return true;
        if(_currentRetailFiles is not null)
        {
            if(compressed)
            {
                // 009A0870 strncpy(+0xA8,name,0x100) then writes "dds" over the three bytes before
                // the terminator: a shorter name yields "", "s" or "ds". A 256-byte name has no
                // terminator, so its strlen reads past the buffer: no DDS source (safety).
                if(name.Length>=256 || name.Any(character=>character>255))return false;
                name=name.Length<3?"dds"[(3-name.Length)..]:name[..^3]+"dds";
                if(name.Length==0)return false;
            }
            var source=_currentRetailFiles.Read(name);
            if(source?.ResourceLocation is not {} selected)return false;
            bytes=source.Bytes.ToArray();location=selected;return true;
        }
        // The historical catalog-only API keeps its existing lookup contract.
        return _resources is not null && _resources.TryReadResource(name,out bytes,out location);
    }

    private EnbResolvedTextureResource RecolorCore(string normalized, string variant, Vector3 shift)
    {
        if(!TryReadCurrentRetailSource(normalized,true,out var bytes,out var location))
        {
            //0048CB40 publishes the recolored object regardless; its load task
            //(00949550/009493F0) retains 009AF0F0's image when no source opens.
            if(ClientExecutablePath is {} executable && !TryReadCurrentRetailSource(normalized,false,out _,out _))
                return new EnbResolvedTextureResource(variant,EnbTextureResolutionStatus.Decoded,
                    Image:LoadCurrentRetailMissingImage(executable),
                    Diagnostic:$"HSV texture '{normalized}' has no source. Current retail failed-load image (009AF0F0).")
                    { CurrentRetailOriginalTextureName = normalized };
            throw new InvalidDataException($"HSV texture '{normalized}' is unavailable");
        }
        EnbTextureImage image;
        if (EnbDdsTexture.TryDecodeDxt1Hsv(bytes, shift, out var dxt1))
            image = new EnbTextureImage(EnbTextureFormat.DdsDxt1, dxt1.MipLevels);
        else if (EnbCurrentRetailDdsSourceState.TryRead(bytes, 0, out var dds) && dds!.HasPixelAllocation &&
            dds.Format is 21 or 22 or 23)
            //009A0D30's same-format branch writes DXT2/3/4 only without an HSV
            //change (009A1081); a recolor leaves the zeroed allocation unwritten.
            image = EnbCurrentRetailAllocatedDdsImage.Create(bytes, 0, dds.Width, dds.Height, dds.Format,
                dds.MipLevels, shift);
        else if (!EnbCurrentRetailHsvRuntime.TryRecolorDds(bytes, shift, out var recolored) ||
            !EnbRetailTextureDecoder.TryDecode(recolored, location.Name, out image))
            throw new InvalidDataException($"HSV texture '{normalized}' requires the current DDS endpoint family");
        return new EnbResolvedTextureResource(variant, EnbTextureResolutionStatus.Decoded, location, image)
            { CurrentRetailOriginalTextureName = normalized };
    }

    /// <summary>Explicit mesh-consumer admission of the current retail failed-load image.
    /// The source lookup stays MissingResource; only the returned runtime key resolves it.</summary>
    public EnbResolvedTextureResource ResolveCurrentRetailMissingMeshTexture(string logicalName, string executable)
        => ResolveCurrentRetailMissingTexture(logicalName, executable);

    /// <summary>Explicit consumer admission of the native TextureClass failed-load image.
    /// Ordinary Resolve and unsupported decoders are unchanged.</summary>
    public EnbResolvedTextureResource ResolveCurrentRetailMissingTexture(string logicalName, string executable)
    {
        var source = Resolve(logicalName);
        if (source.Status != EnbTextureResolutionStatus.MissingResource) return source;
        string imageKey = "retail-missing-image:" + Path.GetFullPath(executable);
        var image = LoadCurrentRetailMissingImage(executable);
        string key = imageKey + ":" + source.LogicalName;
        return _cache.GetOrAdd(key, _ => new(key, EnbTextureResolutionStatus.Decoded, Image: image,
            Diagnostic: source.Diagnostic + " Current retail failed-load image (009AF0F0)."));
    }

    private EnbTextureImage LoadCurrentRetailMissingImage(string executable)
    {
        string imageKey = "retail-missing-image:" + Path.GetFullPath(executable);
        return _cache.GetOrAdd(imageKey, _ => new(imageKey, EnbTextureResolutionStatus.Decoded,
            Image: EnbRetailMissingTexture.Load(executable))).Image!;
    }

    private EnbResolvedTextureResource ResolveCore(string logicalName)
    {
        byte[] bytes=[];
        EnbMixResourceLocation location=null!;
        bool replacement=_overrides?.TryRead(logicalName,logicalName.EndsWith(".dds",StringComparison.OrdinalIgnoreCase),out bytes,out location)==true;
        if (!replacement && (_resources is null ||
            !_resources.TryReadResource(logicalName,out bytes,out location)))
        {
            return new EnbResolvedTextureResource(
                logicalName,
                EnbTextureResolutionStatus.MissingResource,
                Diagnostic: $"Texture resource '{logicalName}' is not available in the installed MIX archives.");
        }

        if (!EnbRetailTextureDecoder.TryDecode(bytes, location.Name, out EnbTextureImage image))
        {
            return new EnbResolvedTextureResource(
                logicalName,
                EnbTextureResolutionStatus.UnsupportedOrInvalidTexture,
                location,
                Diagnostic: DescribeUnsupportedTexture(bytes, location));
        }

        return new EnbResolvedTextureResource(
            logicalName,
            EnbTextureResolutionStatus.Decoded,
            location,
            image);
    }

    private static string DescribeUnsupportedTexture(ReadOnlySpan<byte> bytes, EnbMixResourceLocation location)
    {
        string header;
        if (bytes.Length >= 88 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) == 0x20534444u)
        {
            uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(84, 4));
            Span<char> chars = stackalloc char[4];
            chars[0] = (char)(fourCc & 0xFF);
            chars[1] = (char)((fourCc >> 8) & 0xFF);
            chars[2] = (char)((fourCc >> 16) & 0xFF);
            chars[3] = (char)((fourCc >> 24) & 0xFF);
            string fourCcText = new(chars);
            header = $"DDS fourCC='{fourCcText}'/0x{fourCc:X8}";
        }
        else if (bytes.Length >= 18 && string.Equals(Path.GetExtension(location.Name), ".tga", StringComparison.OrdinalIgnoreCase))
        {
            int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(12, 2));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(14, 2));
            header = $"TGA cmap={bytes[1]} type={bytes[2]} size={width}x{height} bpp={bytes[16]} descriptor=0x{bytes[17]:X2}";
        }
        else
        {
            int prefixLength = Math.Min(bytes.Length, 12);
            header = $"prefix={Convert.ToHexString(bytes[..prefixLength])}";
        }

        return $"Texture resource '{location.Name}' in '{location.ArchiveName}' ({location.DataLength} bytes) is not decoded by the current EnbNext texture boundary; {header}.";
    }

}
