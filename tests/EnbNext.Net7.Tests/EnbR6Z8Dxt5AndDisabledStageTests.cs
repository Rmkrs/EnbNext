using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z8Dxt5AndDisabledStageTests
{
    [Fact]
    public void Dxt5DecoderUsesBc3AlphaPaletteAndOpaqueFourColorRgbBlock()
    {
        byte[] dds = BuildDxt5(
            4,
            4,
            mipCount: 1,
            alpha0: 255,
            alpha1: 0,
            color0: 0x001F,
            color1: 0xF800,
            alphaPattern: Enumerable.Range(0, 16).Select(static i => (byte)(i & 7)).ToArray(),
            colorIndices: 3u);

        Assert.True(EnbRetailTextureDecoder.TryDecode(dds, "R6Z8.DDS", out EnbTextureImage image));
        Assert.Equal(EnbTextureFormat.DdsDxt5, image.Format);
        Assert.Equal((4, 4), (image.Width, image.Height));
        Assert.Equal(new EnbColor32(170, 0, 85, 255), image.GetPixel(0, 0));
        Assert.Equal(new EnbColor32(0, 0, 255, 0), image.GetPixel(1, 0));
        Assert.Equal(new EnbColor32(0, 0, 255, 218), image.GetPixel(2, 0));
        Assert.Equal(new EnbColor32(0, 0, 255, 36), image.GetPixel(3, 1));
    }

    [Fact]
    public void Dxt5DecoderPreservesAuthoredMipChain()
    {
        byte[] dds = BuildDxt5(8, 8, mipCount: 2, alpha0: 255, alpha1: 0, color0: 0x07E0, color1: 0x001F);

        Assert.True(EnbRetailTextureDecoder.TryDecode(dds, "R6Z8_CHAIN.DDS", out EnbTextureImage image));
        Assert.Equal(EnbTextureFormat.DdsDxt5, image.Format);
        Assert.Collection(
            image.MipLevels,
            mip => Assert.Equal((8, 8), (mip.Width, mip.Height)),
            mip => Assert.Equal((4, 4), (mip.Width, mip.Height)));

        EnbTextureUploadMipPlan upload = EnbTextureUploadMipPlanner.Compile(
            image,
            EnbRenderTextureRuntimeSettings.RetailDefaults);
        Assert.Equal(1, upload.UploadMipCount);
    }

    [Fact]
    public void UnsupportedDdsFourCcStillFailsClosed()
    {
        byte[] dds = BuildDxt5(4, 4, mipCount: 1, alpha0: 255, alpha1: 0, color0: 0xFFFF, color1: 0);
        // DXT2/DXT4 are admitted retail formats; use an actually unknown code.
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(84, 4), 0x36545844u); // DXT6
        Assert.False(EnbRetailTextureDecoder.TryDecode(dds, "R6Z8_UNSUPPORTED.DDS", out _));
    }

    private static byte[] BuildDxt5(
        int width,
        int height,
        int mipCount,
        byte alpha0,
        byte alpha1,
        ushort color0,
        ushort color1,
        byte[]? alphaPattern = null,
        uint colorIndices = 0u)
    {
        var payload = new List<byte>();
        int mipWidth = width;
        int mipHeight = height;
        for (int level = 0; level < mipCount; level++)
        {
            int blocks = ((mipWidth + 3) / 4) * ((mipHeight + 3) / 4);
            for (int block = 0; block < blocks; block++)
            {
                byte[] encoded = new byte[16];
                encoded[0] = alpha0;
                encoded[1] = alpha1;
                ulong alphaIndices = 0;
                if (alphaPattern is not null)
                {
                    for (int i = 0; i < Math.Min(16, alphaPattern.Length); i++)
                        alphaIndices |= ((ulong)alphaPattern[i] & 7u) << (i * 3);
                }
                for (int i = 0; i < 6; i++)
                    encoded[2 + i] = (byte)(alphaIndices >> (i * 8));
                BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(8, 2), color0);
                BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(10, 2), color1);
                BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(12, 4), colorIndices);
                payload.AddRange(encoded);
            }
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        byte[] bytes = new byte[128 + payload.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x20534444u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x000A_1007u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(((width + 3) / 4) * 16)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), checked((uint)mipCount));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), 0x35545844u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), mipCount > 1 ? 0x0040_1008u : 0x1000u);
        payload.CopyTo(bytes, 128);
        return bytes;
    }
}
