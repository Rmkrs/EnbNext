using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailBaseAssetResolverTests
{
    [Fact]
    public void FileNameBaseAssetResolvesInstalledW3dAndTextureDependencies()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] w3d = BuildMinimalW3d("SHIPROOT", "paint.tga");
            byte[] texture = [0x44, 0x44, 0x53, 0x20, 0x01, 0x02, 0x03, 0x04];
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("SHIP01.W3D", w3d),
                ("paint.dds", texture));

            var presentation = new EnbRetailPresentationCatalog();
            presentation.LoadBaseAssetsText(
                """
                [BASE-42]
                FILE=SHIP01.W3D
                NAME=SHIPROOT
                OverbrightOff=TRUE
                """);

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            var resolver = new EnbRetailBaseAssetResolver(presentation, resources);

            EnbBaseAssetResolution resolution = resolver.Resolve(42);

            Assert.True(resolution.IsResolved);
            Assert.Equal(EnbBaseAssetResolutionStatus.W3d, resolution.Status);
            Assert.True(resolution.Definition.OverbrightOff);
            EnbResolvedW3dResource resolved = Assert.IsType<EnbResolvedW3dResource>(resolution.W3d);
            Assert.Equal("SHIP01.W3D", resolved.LogicalFileName);
            Assert.Equal("SHIPROOT", resolved.RequestedObjectName);
            Assert.Equal("SHIP01.W3D", resolved.ResourceLocation.Name);
            Assert.Single(resolved.Model.Meshes);
            Assert.Equal("SHIPROOT", resolved.Model.Meshes[0].Name);
            Assert.Equal(0, resolved.MissingTextureCount);

            EnbResolvedTextureReference dependency = Assert.Single(resolved.Textures);
            Assert.Equal("paint.tga", dependency.LogicalName);
            Assert.NotNull(dependency.Location);
            Assert.Equal("paint.dds", dependency.Location!.Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RslBranchRemainsExplicitResourceLibraryReferenceWithoutInventingMixMapping()
    {
        var presentation = new EnbRetailPresentationCatalog();
        presentation.LoadBaseAssetsText(
            """
            [BASE-48]
            RSLID=2
            LINKID=7
            RSID=3
            """);

        var resolver = new EnbRetailBaseAssetResolver(presentation, resources: null);

        EnbBaseAssetResolution resolution = resolver.Resolve(48);

        Assert.True(resolution.IsResolved);
        Assert.Equal(EnbBaseAssetResolutionStatus.RslReference, resolution.Status);
        EnbResolvedRslReference rsl = Assert.IsType<EnbResolvedRslReference>(resolution.Rsl);
        Assert.Equal(2, rsl.RslId);
        Assert.Equal(7, rsl.LinkId);
        Assert.Equal(3, rsl.RsId);
        Assert.Null(rsl.Library);
        Assert.Equal(2, rsl.InitialStateCommands.Count);
        Assert.Null(resolution.W3d);
    }

    [Fact]
    public void FileNameBranchReportsMissingAndInvalidResourcesWithoutFallingThrough()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("BROKEN.W3D", [0x01, 0x02, 0x03, 0x04]));

            var presentation = new EnbRetailPresentationCatalog();
            presentation.LoadBaseAssetsText(
                """
                [BASE-10]
                FILE=MISSING.W3D
                NAME=MISSING

                [BASE-11]
                FILE=BROKEN.W3D
                NAME=BROKEN
                """);

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            var resolver = new EnbRetailBaseAssetResolver(presentation, resources);

            EnbBaseAssetResolution missing = resolver.Resolve(10);
            Assert.False(missing.IsResolved);
            Assert.Equal(EnbBaseAssetResolutionStatus.MissingResource, missing.Status);

            EnbBaseAssetResolution invalid = resolver.Resolve(11);
            Assert.False(invalid.IsResolved);
            Assert.Equal(EnbBaseAssetResolutionStatus.InvalidW3d, invalid.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NullAndProceduralWidthBranchesRemainDataDescriptors()
    {
        var presentation = new EnbRetailPresentationCatalog();
        presentation.LoadBaseAssetsText(
            """
            [BASE-1]
            NULLOBJECT=TRUE

            [BASE-2]
            Width=17.5
            """);
        var resolver = new EnbRetailBaseAssetResolver(presentation, resources: null);

        EnbBaseAssetResolution nullObject = resolver.Resolve(1);
        Assert.True(nullObject.IsResolved);
        Assert.Equal(EnbBaseAssetResolutionStatus.NullObject, nullObject.Status);

        EnbBaseAssetResolution width = resolver.Resolve(2);
        Assert.True(width.IsResolved);
        Assert.Equal(EnbBaseAssetResolutionStatus.ProceduralWidth, width.Status);
        Assert.Equal(17.5f, width.ProceduralWidth!.Value);
    }

    private static byte[] BuildMinimalW3d(string meshName, string textureName)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(8, 16), meshName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 1u);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(112, 4),
            BitConverter.SingleToInt32Bits(1.0f));

        byte[] vertex = new byte[12];
        WriteSingle(vertex.AsSpan(0, 4), 1.0f);
        WriteSingle(vertex.AsSpan(4, 4), 2.0f);
        WriteSingle(vertex.AsSpan(8, 4), 3.0f);

        return ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, vertex),
            ContainerChunk(
                0x0030,
                ContainerChunk(
                    0x0031,
                    LeafChunk(0x0032, Encoding.ASCII.GetBytes(textureName + "\0")))));
    }

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static child => child).ToArray(), container: true);

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, container: false);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), type);
        uint size = checked((uint)payload.Length);
        if (container)
            size |= 0x8000_0000u;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), size);
        payload.CopyTo(result, 8);
        return result;
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(value));

    private static void WriteMix1(
        string path,
        params (string Name, byte[] Payload)[] resources)
    {
        const uint headerLength = 16;

        uint dataLength = checked((uint)resources.Sum(resource => resource.Payload.Length));
        uint directoryOffset = checked(headerLength + dataLength);
        uint filenameTableOffset = checked(
            directoryOffset + 4u + ((uint)resources.Length * 12u));

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(0x3158494Du);
        writer.Write(directoryOffset);
        writer.Write(filenameTableOffset);
        writer.Write(0u);

        var offsets = new uint[resources.Length];
        uint cursor = headerLength;
        for (int index = 0; index < resources.Length; index++)
        {
            offsets[index] = cursor;
            writer.Write(resources[index].Payload);
            cursor = checked(cursor + (uint)resources[index].Payload.Length);
        }

        writer.Write((uint)resources.Length);
        for (int index = 0; index < resources.Length; index++)
        {
            writer.Write(0x1000_0000u + (uint)index);
            writer.Write(offsets[index]);
            writer.Write((uint)resources[index].Payload.Length);
        }

        writer.Write((uint)resources.Length);
        foreach (var (name, _) in resources)
        {
            byte[] storedName = Encoding.ASCII.GetBytes(name + "\0");
            if (storedName.Length == 0 || storedName.Length > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(resources));

            writer.Write((byte)storedName.Length);
            writer.Write(storedName);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EnbNext.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
