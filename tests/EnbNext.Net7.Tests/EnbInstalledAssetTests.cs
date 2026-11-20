using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Client;

namespace EnbNext.Net7.Tests;

public sealed class EnbInstalledAssetTests
{
    [Fact]
    public void ExplicitRetailRootResolvesClientBassetAndMixFilesWithoutRegistryState()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "release"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "ini"));
            Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
            File.WriteAllBytes(Path.Combine(root, "release", "client.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(root, "Data", "client", "ini", "basset.ini"), "[BASE-378]\nFILE=BACK_blue.W3D\nNAME=BACK_blue\n");
            File.WriteAllText(Path.Combine(root, "Data", "client", "ini", "cbasset.ini"), "[BASE-175]\nRadius=71259.5625\nRadiusAdjust=-36000.0000\n");

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");

            Assert.Equal(Path.GetFullPath(root), paths.RootDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "release", "client.exe"), paths.ClientExecutablePath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client"), paths.ClientDataDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini"), paths.ClientIniDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "output"), paths.ClientOutputDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "common"), paths.CommonDataDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "basset.ini"), paths.BassetIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "cbasset.ini"), paths.CbassetIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "effect.ini"), paths.EffectIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "rs.ini"), paths.RsIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "envmaps.ini"), paths.EnvironmentMapsIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "spacecam.ini"), paths.SpaceCameraIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "fc_radar.ini"), paths.RadarIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "fc_cpit.ini"), paths.CockpitIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "fontsys.ini"), paths.FontSystemIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "gutter_color.ini"), paths.GutterColorIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "ini", "metal_equivalent_rgbs.ini"), paths.MetalEquivalentRgbsIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "common", "decal_textures.ini"), paths.DecalTexturesIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "common", "decal_swatches.ini"), paths.DecalSwatchesIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "common", "secret_decal_textures.ini"), paths.SecretDecalTexturesIniPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Data", "client", "mixfiles"), paths.MixFilesDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public void ClientBaseAssetCatalogAppliesRadiusAdjustBeforeObjectScale()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "cbasset.ini");
            File.WriteAllText(path, "[BASE-175]\nRadius=71259.5625\nRadiusAdjust=-36000.0000\n");

            EnbClientBaseAssetCatalog catalog = EnbClientBaseAssetCatalog.Load(path)
                ?? throw new Xunit.Sdk.XunitException("Synthetic cbasset catalog was not loaded.");

            Assert.True(catalog.TryResolve(175, out EnbClientBaseAssetGeometry geometry));
            Assert.Equal(71259.5625f, geometry.Radius, 3);
            Assert.Equal(-36000.0f, geometry.RadiusAdjust, 3);
            Assert.True(geometry.TryScale(2.0f, out float worldRadius));
            Assert.Equal(70519.125f, worldRadius, 3);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClientBaseAssetCatalogClampsOnlyTheFinalOrdinaryRadius()
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"cbasset.ini");
            File.WriteAllText(path,"[BASE-1]\nRadius=-3\nRadiusAdjust=1\n[BASE-2]\nRadius=-3\nRadiusAdjust=5\n[BASE-3]\nRadius=3\nRadiusAdjust=-5\n");
            var catalog=EnbClientBaseAssetCatalog.Load(path)!;
            foreach(var (id,expected) in new[]{(1,0f),(2,4f),(3,0f)})
            {
                Assert.True(catalog.TryResolve(id,out var geometry));
                Assert.True(geometry.TryScale(2,out var radius));
                Assert.Equal(expected,radius);
            }
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void ClientBaseAssetCatalogPreservesExplicitZeroRadius()
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"cbasset.ini");
            File.WriteAllText(path,"[BASE-81]\nRadius=0.0000\n[BASE-82]\nRadius=4\nRadiusAdjust=-4\n[BASE-2427]\nDescription=Missing radius uses native zero default\n");
            var catalog=EnbClientBaseAssetCatalog.Load(path)!;
            foreach(int id in new[]{81,82,2427})
            {
                Assert.True(catalog.TryResolve(id,out var geometry));
                Assert.True(geometry.TryScale(3,out var radius));
                Assert.Equal(0,radius);
            }
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void Mix1CatalogFindsNamedW3dAndFirstEnumeratedArtArchiveWins()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] first = [0x11, 0x22, 0x33];
            byte[] second = [0x44, 0x55, 0x66, 0x77];
            WriteSingleEntryMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                "BACK_blue.W3D",
                first);
            WriteSingleEntryMix1(
                Path.Combine(directory, "mixfile_art_02.mix"),
                "BACK_blue.W3D",
                second);

            EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX1 catalog was not loaded.");

            Assert.Equal(2, catalog.ArchiveCount);
            Assert.Equal(1, catalog.ResourceCount);
            Assert.True(catalog.TryLocateResource("back_BLUE.w3d", out EnbMixResourceLocation location));
            // Retail 004026D5/004E5440: the first enumerated package keeps the name.
            Assert.Equal("mixfile_art_01.mix", location.ArchiveName);
            Assert.Equal((long)first.Length, location.DataLength);
            Assert.True(catalog.TryReadResource("BACK_blue.W3D", out byte[] bytes, out EnbMixResourceLocation readLocation));
            Assert.Equal(first, bytes);
            Assert.Equal(location, readLocation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    [Fact]
    public void Mix1CatalogResolvesW3dTgaReferenceToInstalledDdsTexture()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] texture = [0x44, 0x44, 0x53, 0x20, 0x01, 0x02, 0x03];
            WriteSingleEntryMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                "NEB_GREY.dds",
                texture);

            EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX1 catalog was not loaded.");

            Assert.True(catalog.TryLocateResource("NEB_GREY.tga", out EnbMixResourceLocation location));
            Assert.Equal("NEB_GREY.dds", location.Name);
            Assert.True(catalog.TryReadResource("neb_grey.TGA", out byte[] bytes, out EnbMixResourceLocation readLocation));
            Assert.Equal(texture, bytes);
            Assert.Equal(location, readLocation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    [Fact]
    public void W3dProbeReadsMeshCountsAndTextureReferencesFromChunkTree()
    {
        byte[] header = new byte[116];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        System.Text.Encoding.ASCII.GetBytes("SPHERE01").CopyTo(header, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), 120u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 89u);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(112, 4),
            BitConverter.SingleToInt32Bits(1670.03f));

        byte[] mesh = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            ContainerChunk(
                0x0030,
                ContainerChunk(0x0031, LeafChunk(0x0032, System.Text.Encoding.ASCII.GetBytes("NEB_GREY.tga\0"))),
                ContainerChunk(0x0031, LeafChunk(0x0032, System.Text.Encoding.ASCII.GetBytes("NEB_blue.tga\0")))));

        Assert.True(EnbW3dProbe.TryInspect(mesh, out EnbW3dProbeResult result));
        Assert.Equal("SPHERE01", result.MeshName);
        Assert.Equal(89, result.VertexCount);
        Assert.Equal(120, result.TriangleCount);
        Assert.Equal(1670.03f, result.BoundingRadius, 2);
        Assert.Equal(["NEB_GREY.tga", "NEB_blue.tga"], result.TextureNames);
    }


    [Fact]
    public void DdsDxt1DecoderReadsTopMipPixelsWithoutPlatformImageDependencies()
    {
        byte[] dds = BuildSingleBlockDxt1(0xF800, 0x0000, 0u);

        Assert.True(EnbDdsTexture.TryDecodeDxt1(dds, out EnbDdsTexture texture));
        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);

        EnbColor32 color = texture.SampleBilinearWrap(0.5f, 0.5f);
        Assert.InRange(color.R, (byte)248, byte.MaxValue);
        Assert.InRange(color.G, (byte)0, (byte)8);
        Assert.InRange(color.B, (byte)0, (byte)8);
        Assert.Equal(byte.MaxValue, color.A);
    }

    [Fact]
    public void W3dProbeRejectsChunkWhoseDeclaredPayloadRunsPastInput()
    {
        byte[] malformed = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(0, 4), 0u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(4, 4), 0x8000_0010u);

        Assert.False(EnbW3dProbe.TryInspect(malformed, out _));
    }


    private static byte[] BuildSingleBlockDxt1(ushort color0, ushort color1, uint indices)
    {
        byte[] bytes = new byte[136];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x20534444u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0008_1007u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 4u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 4u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 8u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 4u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), 0x31545844u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(128, 2), color0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(130, 2), color1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(132, 4), indices);
        return bytes;
    }

    private static byte[] UInt32Payload(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
        return bytes;
    }

    private static byte[] Vector2Payload(IEnumerable<Vector2> values)
    {
        Vector2[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 8];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan((index * 8) + 0, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 8) + 4, 4), snapshot[index].Y);
        }
        return bytes;
    }

    private static byte[] Vector3Payload(IEnumerable<Vector3> values)
    {
        Vector3[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 12];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan((index * 12) + 0, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 12) + 4, 4), snapshot[index].Y);
            WriteSingle(bytes.AsSpan((index * 12) + 8, 4), snapshot[index].Z);
        }
        return bytes;
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(value));

    private static void WriteSingleEntryMix1(string path, string resourceName, byte[] payload)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(resourceName + "\0");
        if (nameBytes.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(resourceName));

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
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        uint size = checked((uint)payload.Length) | (container ? 0x8000_0000u : 0u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), size);
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
