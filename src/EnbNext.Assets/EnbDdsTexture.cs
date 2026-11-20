using System.Buffers.Binary;
using System.Numerics;

namespace EnbNext.Assets;

/// <summary>
/// DDS DXT1/2/3/4/5 reader for the installed Earth &amp; Beyond art path. It retains
/// compressed blocks for GPU sampling and decoded pixels for asset consumers.
/// It preserves every authored mip level so the renderer can apply the current
/// retail TextureReduction and mip-filter policy at runtime rather than losing
/// that information in the asset layer.
/// </summary>
public sealed class EnbDdsTexture
{
    private delegate EnbColor32[] DecodeMipDelegate(ReadOnlySpan<byte> blocks, int width, int height);
    private const uint DdsMagic = 0x20534444; // "DDS "
    private const uint Dxt1FourCc = 0x31545844; // "DXT1"
    private const uint Dxt3FourCc = 0x33545844; // "DXT3"
    private const uint Dxt5FourCc = 0x35545844; // "DXT5"
    private const int HeaderLength = 128;
    private const long MaximumDecodedPixelCount = 16_777_216;

    private const int MaximumMipCount = 32;
    private readonly IReadOnlyList<EnbTextureMipLevel> _mipLevels;

    private EnbDdsTexture(IReadOnlyList<EnbTextureMipLevel> mipLevels)
    {
        _mipLevels = mipLevels;
        Width = mipLevels[0].Width;
        Height = mipLevels[0].Height;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<EnbTextureMipLevel> MipLevels => _mipLevels;

    internal EnbColor32[] CopyPixels() => _mipLevels[0].CopyPixels();

    internal EnbTextureMipLevel[] CopyMipLevels()
        => _mipLevels.Select(static level => level.Clone()).ToArray();

    public static bool TryDecodeDxt1(ReadOnlySpan<byte> bytes, out EnbDdsTexture texture)
        => TryDecodeBlockCompressed(bytes, Dxt1FourCc, 8, DecodeDxt1Mip, out texture);

    /// <summary>Adjust endpoints before interpolation, preserving the authored
    /// DXT1 palette mode. Reinterpreting shifted endpoint ordering creates holes
    /// in opaque gas textures. Decoded GPU uploads also handle equal endpoints
    /// without inventing a replacement compressed color.</summary>
    public static bool TryDecodeDxt1Hsv(ReadOnlySpan<byte> bytes, Vector3 shift, out EnbDdsTexture texture)
    {
        texture = default!;
        return TryDecodeBlockCompressed(bytes, Dxt1FourCc, 8,
            (blocks, width, height) => DecodeDxt1Mip(blocks, width, height, shift), out texture, retainCompressed: false);
    }

    public static bool TryDecodeDxt5(ReadOnlySpan<byte> bytes, out EnbDdsTexture texture)
        => TryDecodeBlockCompressed(bytes, Dxt5FourCc, 16, DecodeDxt5Mip, out texture);

    public static bool TryDecodeDxt3(ReadOnlySpan<byte> bytes, out EnbDdsTexture texture)
        => TryDecodeBlockCompressed(bytes, Dxt3FourCc, 16, DecodeDxt3Mip, out texture);

    // D3D8 samples DXT2/4 as the same stored BC2/BC3 channels as DXT3/5.
    // Premultiplication describes authored content, not a sampler unpremultiply.
    public static bool TryDecodeDxt2(ReadOnlySpan<byte> bytes, out EnbDdsTexture texture)
        => TryDecodeBlockCompressed(bytes, 0x32545844, 16, DecodeDxt3Mip, out texture);
    public static bool TryDecodeDxt4(ReadOnlySpan<byte> bytes, out EnbDdsTexture texture)
        => TryDecodeBlockCompressed(bytes, 0x34545844, 16, DecodeDxt5Mip, out texture);

    /// <summary>Native20->21 creates opaque alpha in every block. For this
    /// conversion the BC2 color block and opaque alpha are retained.</summary>
    public static bool TryDecodeDxt1AsDxt2Hsv(ReadOnlySpan<byte> bytes,Vector3 shift,out EnbDdsTexture texture)
    {
        texture=default!;
        return EnbCurrentRetailHsvRuntime.TryConvertDxt1ToDxt2(bytes,shift,out var converted) &&
            TryDecodeBlockCompressed(converted,0x32545844,16,DecodeDxt3Mip,out texture);
    }

    private static bool TryDecodeBlockCompressed(
        ReadOnlySpan<byte> bytes,
        uint expectedFourCc,
        int bytesPerBlock,
        DecodeMipDelegate decodeMip,
        out EnbDdsTexture texture, bool retainCompressed = true)
    {
        texture = default!;
        if (bytes.Length < HeaderLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) != DdsMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != 124u)
        {
            return false;
        }

        uint heightRaw = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
        uint widthRaw = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4));
        uint pixelFormatSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(76, 4));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(84, 4));

        if (widthRaw == 0 || heightRaw == 0 ||
            widthRaw > 16_384 || heightRaw > 16_384 ||
            ((long)widthRaw * heightRaw) > MaximumDecodedPixelCount ||
            pixelFormatSize != 32u || fourCc != expectedFourCc)
        {
            return false;
        }

        int width = checked((int)widthRaw);
        int height = checked((int)heightRaw);
        uint mipCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28, 4));
        if (mipCountRaw > MaximumMipCount)
            return false;
        int mipCount = mipCountRaw == 0 ? 1 : (int)mipCountRaw;

        var mipLevels = new EnbTextureMipLevel[mipCount];
        int sourceOffset = HeaderLength;
        int mipWidth = width;
        int mipHeight = height;
        long decodedPixelCount = 0;

        for (int level = 0; level < mipCount; level++)
        {
            int blockWidth = (mipWidth + 3) / 4;
            int blockHeight = (mipHeight + 3) / 4;
            int mipLength = checked(blockWidth * blockHeight * bytesPerBlock);
            if (mipLength > bytes.Length - sourceOffset)
                return false;

            decodedPixelCount += (long)mipWidth * mipHeight;
            if (decodedPixelCount > MaximumDecodedPixelCount * 2)
                return false;

            // Retail copies these blocks into the device texture (009A142A).
            // CPU pixels remain available to asset consumers, but must not
            // replace the hardware's compressed-format interpolation on upload.
            mipLevels[level] = retainCompressed
                ? new EnbTextureMipLevel(mipWidth,mipHeight,expectedFourCc,bytes.Slice(sourceOffset,mipLength).ToArray())
                : new EnbTextureMipLevel(mipWidth, mipHeight, decodeMip(bytes.Slice(sourceOffset,mipLength),mipWidth,mipHeight));
            sourceOffset += mipLength;
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        texture = new EnbDdsTexture(Array.AsReadOnly(mipLevels));
        return true;
    }

    internal static EnbColor32[] DecodeCompressedMip(ReadOnlySpan<byte> blocks,int width,int height,uint fourCc)
        => fourCc switch
        {
            0x31545844 => DecodeDxt1Mip(blocks,width,height),
            0x32545844 or 0x33545844 => DecodeDxt3Mip(blocks,width,height),
            0x34545844 or 0x35545844 => DecodeDxt5Mip(blocks,width,height),
            _ => throw new InvalidDataException("Unsupported retained compressed mip format."),
        };

    private static EnbColor32[] DecodeDxt1Mip(ReadOnlySpan<byte> blocks, int width, int height)
        => DecodeDxt1Mip(blocks, width, height, Vector3.Zero);

    private static EnbColor32[] DecodeDxt1Mip(ReadOnlySpan<byte> blocks, int width, int height, Vector3 shift)
    {
        int blockWidth = (width + 3) / 4;
        int blockHeight = (height + 3) / 4;
        var pixels = new EnbColor32[checked(width * height)];
        int blockOffset = 0;
        Span<EnbColor32> palette = stackalloc EnbColor32[4];

        for (int blockY = 0; blockY < blockHeight; blockY++)
        {
            for (int blockX = 0; blockX < blockWidth; blockX++)
            {
                ushort color0 = BinaryPrimitives.ReadUInt16LittleEndian(blocks.Slice(blockOffset, 2));
                ushort color1 = BinaryPrimitives.ReadUInt16LittleEndian(blocks.Slice(blockOffset + 2, 2));
                uint indices = BinaryPrimitives.ReadUInt32LittleEndian(blocks.Slice(blockOffset + 4, 4));
                blockOffset += 8;

                bool recolor = EnbCurrentRetailHsvRuntime.ChangesDdsColor(shift);
                EnbColor32 c0 = ExpandRgb565(recolor ? EnbCurrentRetailHsvRuntime.ShiftRgb565(color0, shift) : color0);
                EnbColor32 c1 = ExpandRgb565(recolor ? EnbCurrentRetailHsvRuntime.ShiftRgb565(color1, shift) : color1);
                EnbColor32 c2;
                EnbColor32 c3;

                if (color0 > color1)
                {
                    c2 = Mix(c0, c1, 2, 1, 3);
                    c3 = Mix(c0, c1, 1, 2, 3);
                }
                else
                {
                    c2 = Mix(c0, c1, 1, 1, 2);
                    c3 = new EnbColor32(0, 0, 0, 0);
                }

                palette[0] = c0;
                palette[1] = c1;
                palette[2] = c2;
                palette[3] = c3;

                for (int py = 0; py < 4; py++)
                {
                    int y = (blockY * 4) + py;
                    if (y >= height)
                        break;

                    for (int px = 0; px < 4; px++)
                    {
                        int x = (blockX * 4) + px;
                        if (x >= width)
                            break;

                        int localIndex = (py * 4) + px;
                        int paletteIndex = (int)((indices >> (localIndex * 2)) & 0x3u);
                        pixels[(y * width) + x] = palette[paletteIndex];
                    }
                }
            }
        }

        return pixels;
    }


    private static EnbColor32[] DecodeDxt3Mip(ReadOnlySpan<byte> blocks, int width, int height)
    {
        var pixels = new EnbColor32[checked(width * height)];
        Span<EnbColor32> palette = stackalloc EnbColor32[4];
        int offset = 0;
        for (int by = 0; by < (height + 3) / 4; by++)
        for (int bx = 0; bx < (width + 3) / 4; bx++, offset += 16)
        {
            ulong alphas = BinaryPrimitives.ReadUInt64LittleEndian(blocks.Slice(offset, 8));
            palette[0] = ExpandRgb565(BinaryPrimitives.ReadUInt16LittleEndian(blocks.Slice(offset + 8, 2)));
            palette[1] = ExpandRgb565(BinaryPrimitives.ReadUInt16LittleEndian(blocks.Slice(offset + 10, 2)));
            // BC2 always uses four RGB colors, independent of endpoint ordering.
            palette[2] = Mix(palette[0], palette[1], 2, 1, 3);
            palette[3] = Mix(palette[0], palette[1], 1, 2, 3);
            uint indices = BinaryPrimitives.ReadUInt32LittleEndian(blocks.Slice(offset + 12, 4));
            for (int py = 0; py < 4 && by * 4 + py < height; py++)
            for (int px = 0; px < 4 && bx * 4 + px < width; px++)
            {
                int i = py * 4 + px;
                var rgb = palette[(int)((indices >> (i * 2)) & 3)];
                byte alpha = (byte)(((alphas >> (i * 4)) & 15) * 17);
                pixels[(by * 4 + py) * width + bx * 4 + px] = new(rgb.R, rgb.G, rgb.B, alpha);
            }
        }
        return pixels;
    }

    private static EnbColor32[] DecodeDxt5Mip(ReadOnlySpan<byte> blocks, int width, int height)
    {
        int blockWidth = (width + 3) / 4;
        int blockHeight = (height + 3) / 4;
        var pixels = new EnbColor32[checked(width * height)];
        int blockOffset = 0;
        Span<EnbColor32> colorPalette = stackalloc EnbColor32[4];
        Span<byte> alphaPalette = stackalloc byte[8];

        for (int blockY = 0; blockY < blockHeight; blockY++)
        {
            for (int blockX = 0; blockX < blockWidth; blockX++)
            {
                byte alpha0 = blocks[blockOffset];
                byte alpha1 = blocks[blockOffset + 1];
                ulong alphaIndices = 0;
                for (int i = 0; i < 6; i++)
                    alphaIndices |= (ulong)blocks[blockOffset + 2 + i] << (8 * i);

                alphaPalette[0] = alpha0;
                alphaPalette[1] = alpha1;
                if (alpha0 > alpha1)
                {
                    for (int i = 1; i <= 6; i++)
                        alphaPalette[i + 1] = (byte)(((7 - i) * alpha0 + i * alpha1) / 7);
                }
                else
                {
                    for (int i = 1; i <= 4; i++)
                        alphaPalette[i + 1] = (byte)(((5 - i) * alpha0 + i * alpha1) / 5);
                    alphaPalette[6] = 0;
                    alphaPalette[7] = 255;
                }

                ushort color0 = BinaryPrimitives.ReadUInt16LittleEndian(blocks.Slice(blockOffset + 8, 2));
                ushort color1 = BinaryPrimitives.ReadUInt16LittleEndian(blocks.Slice(blockOffset + 10, 2));
                uint colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(blocks.Slice(blockOffset + 12, 4));
                blockOffset += 16;

                EnbColor32 c0 = ExpandRgb565(color0);
                EnbColor32 c1 = ExpandRgb565(color1);
                colorPalette[0] = c0;
                colorPalette[1] = c1;
                colorPalette[2] = Mix(c0, c1, 2, 1, 3);
                colorPalette[3] = Mix(c0, c1, 1, 2, 3);

                for (int py = 0; py < 4; py++)
                {
                    int y = (blockY * 4) + py;
                    if (y >= height)
                        break;

                    for (int px = 0; px < 4; px++)
                    {
                        int x = (blockX * 4) + px;
                        if (x >= width)
                            break;

                        int localIndex = (py * 4) + px;
                        EnbColor32 rgb = colorPalette[(int)((colorIndices >> (localIndex * 2)) & 0x3u)];
                        byte alpha = alphaPalette[(int)((alphaIndices >> (localIndex * 3)) & 0x7u)];
                        pixels[(y * width) + x] = new EnbColor32(rgb.R, rgb.G, rgb.B, alpha);
                    }
                }
            }
        }

        return pixels;
    }

    public EnbColor32 SampleBilinearWrap(float u, float v)
    {
        if (!float.IsFinite(u) || !float.IsFinite(v))
            return default;

        float wrappedU = u - MathF.Floor(u);
        float wrappedV = v - MathF.Floor(v);
        float x = (wrappedU * Width) - 0.5f;
        float y = (wrappedV * Height) - 0.5f;
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float tx = x - x0;
        float ty = y - y0;

        EnbColor32 c00 = PixelWrap(x0, y0);
        EnbColor32 c10 = PixelWrap(x0 + 1, y0);
        EnbColor32 c01 = PixelWrap(x0, y0 + 1);
        EnbColor32 c11 = PixelWrap(x0 + 1, y0 + 1);

        float r0 = Lerp(c00.R, c10.R, tx);
        float g0 = Lerp(c00.G, c10.G, tx);
        float b0 = Lerp(c00.B, c10.B, tx);
        float a0 = Lerp(c00.A, c10.A, tx);
        float r1 = Lerp(c01.R, c11.R, tx);
        float g1 = Lerp(c01.G, c11.G, tx);
        float b1 = Lerp(c01.B, c11.B, tx);
        float a1 = Lerp(c01.A, c11.A, tx);

        return new EnbColor32(
            ToByte(Lerp(r0, r1, ty)),
            ToByte(Lerp(g0, g1, ty)),
            ToByte(Lerp(b0, b1, ty)),
            ToByte(Lerp(a0, a1, ty)));
    }

    private EnbColor32 PixelWrap(int x, int y)
    {
        int wrappedX = x % Width;
        if (wrappedX < 0)
            wrappedX += Width;
        int wrappedY = y % Height;
        if (wrappedY < 0)
            wrappedY += Height;
        return _mipLevels[0].Pixels.Span[(wrappedY * Width) + wrappedX];
    }

    private static EnbColor32 ExpandRgb565(ushort packed)
    {
        int r5 = (packed >> 11) & 0x1F;
        int g6 = (packed >> 5) & 0x3F;
        int b5 = packed & 0x1F;
        return new EnbColor32(
            (byte)((r5 * 255 + 15) / 31),
            (byte)((g6 * 255 + 31) / 63),
            (byte)((b5 * 255 + 15) / 31),
            255);
    }

    private static EnbColor32 Mix(EnbColor32 a, EnbColor32 b, int weightA, int weightB, int divisor)
        => new(
            (byte)(((a.R * weightA) + (b.R * weightB)) / divisor),
            (byte)(((a.G * weightA) + (b.G * weightB)) / divisor),
            (byte)(((a.B * weightA) + (b.B * weightB)) / divisor),
            (byte)(((a.A * weightA) + (b.A * weightB)) / divisor));

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static byte ToByte(float value)
        => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}

public readonly record struct EnbColor32(byte R, byte G, byte B, byte A);
