using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailAssetBundleTests
{
    [Fact]
    public void TextureDecoderNormalizesBottomOriginTgaToTopLeftPixels()
    {
        byte[] tga = BuildTga24(
            width: 2,
            height: 2,
            topOrigin: false,
            rightOrigin: false,
            // File order for bottom-left origin: bottom row, then top row.
            new EnbColor32(0, 0, 255, 255),
            new EnbColor32(255, 255, 255, 255),
            new EnbColor32(255, 0, 0, 255),
            new EnbColor32(0, 255, 0, 255));

        Assert.True(EnbRetailTextureDecoder.TryDecode(tga, "paint.tga", out EnbTextureImage image));
        Assert.Equal(EnbTextureFormat.TgaTrueColor, image.Format);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(new EnbColor32(255, 0, 0, 255), image.GetPixel(0, 0));
        Assert.Equal(new EnbColor32(0, 255, 0, 255), image.GetPixel(1, 0));
        Assert.Equal(new EnbColor32(0, 0, 255, 255), image.GetPixel(0, 1));
        Assert.Equal(new EnbColor32(255, 255, 255, 255), image.GetPixel(1, 1));
    }

    [Fact]
    public void TextureDecoderReadsRleTgaAndRejectsTruncatedPacket()
    {
        byte[] tga = BuildRleTga32(
            width: 3,
            height: 1,
            topOrigin: true,
            new EnbColor32(10, 20, 30, 40));

        Assert.True(EnbRetailTextureDecoder.TryDecode(tga, "paint.tga", out EnbTextureImage image));
        Assert.Equal(EnbTextureFormat.TgaRleTrueColor, image.Format);
        Assert.Equal(new EnbColor32(10, 20, 30, 40), image.GetPixel(0, 0));
        Assert.Equal(new EnbColor32(10, 20, 30, 40), image.GetPixel(2, 0));

        Assert.False(EnbRetailTextureDecoder.TryDecode(tga[..^1], "paint.tga", out _));
    }

    [Fact]
    public void TextureResolverPreservesLogicalTgaNameWhileDecodingInstalledDdsFallback()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] dds = BuildSingleBlockDxt1(0xF800, 0x0000, 0u);
            WriteSingleEntryMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                "PAINT.dds",
                dds);

            EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            var resolver = new EnbRetailTextureResolver(catalog);

            EnbResolvedTextureResource resolved = resolver.Resolve("paint.tga");

            Assert.True(resolved.IsResolved);
            Assert.Equal("paint.tga", resolved.LogicalName);
            Assert.Equal("PAINT.dds", resolved.ResourceLocation?.Name);
            Assert.Equal(EnbTextureFormat.DdsDxt1, resolved.Image?.Format);
            Assert.Equal(4, resolved.Image?.Width);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    [Fact]
    public void AssetBundleLeavesMissingOptionalPresentationFilesAbsent()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "release"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "ini"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
            File.WriteAllBytes(Path.Combine(root, "release", "client.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(root, "Data", "client", "ini", "basset.ini"), "[BASE-1]\nNULLOBJECT\n");

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");
            EnbRetailAssetBundle bundle = EnbRetailAssetBundle.Load(paths);

            Assert.NotNull(bundle.PresentationCatalog);
            Assert.Null(bundle.EnvironmentMaps);
            Assert.Null(bundle.SpaceCameraModes);
            Assert.Null(bundle.Decals);
            Assert.Null(bundle.Fonts);
            Assert.False(bundle.Sources[EnbRetailAssetSourceKind.EffectIni].Exists);
            Assert.False(bundle.Sources[EnbRetailAssetSourceKind.SpaceCameraIni].Exists);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildTga24(
        int width,
        int height,
        bool topOrigin,
        bool rightOrigin,
        params EnbColor32[] fileOrderPixels)
    {
        Assert.Equal(width * height, fileOrderPixels.Length);
        byte[] bytes = new byte[18 + (fileOrderPixels.Length * 3)];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), checked((ushort)height));
        bytes[16] = 24;
        bytes[17] = (byte)((topOrigin ? 0x20 : 0) | (rightOrigin ? 0x10 : 0));

        int offset = 18;
        foreach (EnbColor32 color in fileOrderPixels)
        {
            bytes[offset++] = color.B;
            bytes[offset++] = color.G;
            bytes[offset++] = color.R;
        }
        return bytes;
    }

    private static byte[] BuildRleTga32(int width, int height, bool topOrigin, EnbColor32 color)
    {
        int count = checked(width * height);
        if (count is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(width));

        byte[] bytes = new byte[18 + 1 + 4];
        bytes[2] = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), checked((ushort)height));
        bytes[16] = 32;
        bytes[17] = (byte)(topOrigin ? 0x28 : 0x08);
        bytes[18] = (byte)(0x80 | (count - 1));
        bytes[19] = color.B;
        bytes[20] = color.G;
        bytes[21] = color.R;
        bytes[22] = color.A;
        return bytes;
    }

    private static byte[] BuildSingleBlockDxt1(ushort color0, ushort color1, uint indices)
    {
        byte[] bytes = new byte[136];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x20534444u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0008_1007u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 8u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 4u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), 0x31545844u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000u);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(128, 2), color0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(130, 2), color1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(132, 4), indices);
        return bytes;
    }

    private static byte[] BuildMinimalMeshHeader(string name)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        Encoding.ASCII.GetBytes(name).CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 1u);
        return header;
    }

    private static void WriteSingleEntryMix1(string path, string resourceName, byte[] payload)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(resourceName + "\0");
        const uint headerLength = 16;
        uint dataOffset = headerLength;
        uint directoryOffset = checked(dataOffset + (uint)payload.Length);
        uint filenameTableOffset = checked(directoryOffset + 4 + 12);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x3158494Du);
        writer.Write(directoryOffset);
        writer.Write(filenameTableOffset);
        writer.Write(0u);
        writer.Write(payload);
        writer.Write(1u);
        writer.Write(0x12345678u);
        writer.Write(dataOffset);
        writer.Write((uint)payload.Length);
        writer.Write(1u);
        writer.Write((byte)nameBytes.Length);
        writer.Write(nameBytes);
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, container: false);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static child => child).ToArray(), container: true);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        uint size = checked((uint)payload.Length) | (container ? 0x8000_0000u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), size);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EnbNext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
