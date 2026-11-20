using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z20CurrentRetailHAnimHierarchyIdentityTests
{
    [Fact]
    public void SelectedAggregateUsesHierarchyIdentityRatherThanVisibleWrapperNameForHAnimPrefix()
    {
        Assert.Equal(
            "TSG02A.TSG02A-1",
            EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(
                hierarchyIdentity: "TSG02A",
                authoredAnimation: string.Empty,
                authoredSuffix: "-1"));

        Assert.NotEqual(
            "TSG02.TSG02-1",
            EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(
                hierarchyIdentity: "TSG02A",
                authoredAnimation: string.Empty,
                authoredSuffix: "-1"));
    }

    [Fact]
    public void LazyFilenameNowFollowsHierarchyQualifiedSelectedKey()
    {
        Assert.True(EnbCurrentRetailRslHAnimResolver.TryBuildLazyResourceCandidates(
            "TSG02A.TSG02A-1",
            out string primary,
            out string parent));

        Assert.Equal("TSG02A-1.w3d", primary);
        Assert.Equal(@"..\TSG02A-1.w3d", parent);
    }

    [Fact]
    public void HeaderOnlyLazyProbeFindsCompressedSelectedRegistryIdentityWithoutPretendingItIsExecutable()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_art_11.mix"),
                ("TSG02A-1.W3D", BuildCompressedAnimationHeaderW3d(
                    name: "TSG02A-1",
                    hierarchyName: "TSG02A",
                    numFrames: 31,
                    frameRate: 30,
                    flavor: 0)));

            EnbMixResourceCatalog art = EnbMixResourceCatalog.Load(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic art catalog was not loaded.");
            EnbMixResourceCatalog retail = EnbMixResourceCatalog.LoadRetailPackages(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail package catalog was not loaded.");

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryProbeLazyHeader(
                paths,
                art,
                retail,
                "TSG02A.TSG02A-1",
                out EnbCurrentRetailRslHAnimHeaderResolution resolution,
                out string? diagnostic), diagnostic);

            Assert.Equal("TSG02A.TSG02A-1", resolution.HAnimName);
            Assert.Equal(EnbW3dHAnimFamily.Compressed0280, resolution.Header.Family);
            Assert.Equal("TSG02A-1", resolution.Header.Name);
            Assert.Equal("TSG02A", resolution.Header.HierarchyName);
            Assert.Equal("TSG02A.TSG02A-1", resolution.Header.RegistryName);
            Assert.Equal(31u, resolution.Header.NumFrames);
            Assert.Equal(30f, resolution.Header.FrameRate);
            Assert.Equal((ushort?)0, resolution.Header.CompressionFlavor);
            Assert.Contains(
                "lazy-header:TSG02A-1.w3d@MIX:mixfile_art_11.mix/TSG02A-1.W3D",
                resolution.Source,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildCompressedAnimationHeaderW3d(
        string name,
        string hierarchyName,
        uint numFrames,
        ushort frameRate,
        ushort flavor)
    {
        byte[] header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(4, 16), name);
        WriteFixedAscii(header.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40, 2), frameRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42, 2), flavor);
        return ContainerChunk(0x0280, LeafChunk(0x0281, header));
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

    private static string CreateRetailTree()
    {
        string root = Path.Combine(Path.GetTempPath(), "EnbNext.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "release"));
        Directory.CreateDirectory(Path.Combine(root, "Data", "client", "ini"));
        Directory.CreateDirectory(Path.Combine(root, "Data", "client", "output"));
        Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
        Directory.CreateDirectory(Path.Combine(root, "Data", "common"));
        return root;
    }

    private static EnbInstalledAssetPaths BuildPaths(string root)
    {
        string clientData = Path.Combine(root, "Data", "client");
        string ini = Path.Combine(clientData, "ini");
        string output = Path.Combine(clientData, "output");
        string common = Path.Combine(root, "Data", "common");
        return new EnbInstalledAssetPaths(
            root,
            Path.Combine(root, "release", "client.exe"),
            clientData,
            ini,
            output,
            common,
            Path.Combine(ini, "basset.ini"),
            Path.Combine(ini, "cbasset.ini"),
            Path.Combine(ini, "effect.ini"),
            Path.Combine(ini, "rs.ini"),
            Path.Combine(ini, "envmaps.ini"),
            Path.Combine(ini, "spacecam.ini"),
            Path.Combine(ini, "fc_radar.ini"),
            Path.Combine(ini, "fc_cpit.ini"),
            Path.Combine(ini, "fontsys.ini"),
            Path.Combine(ini, "gutter_color.ini"),
            Path.Combine(ini, "metal_equivalent_rgbs.ini"),
            Path.Combine(common, "decal_textures.ini"),
            Path.Combine(common, "decal_swatches.ini"),
            Path.Combine(common, "secret_decal_textures.ini"),
            Path.Combine(clientData, "mixfiles"));
    }

    private static void WriteMix1(string path, params (string Name, byte[] Payload)[] resources)
    {
        const uint headerLength = 16;
        uint dataLength = checked((uint)resources.Sum(resource => resource.Payload.Length));
        uint directoryOffset = checked(headerLength + dataLength);
        uint filenameTableOffset = checked(directoryOffset + 4u + ((uint)resources.Length * 12u));

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
        foreach ((string name, _) in resources)
        {
            byte[] storedName = Encoding.ASCII.GetBytes(name + "\0");
            writer.Write((byte)storedName.Length);
            writer.Write(storedName);
        }
    }
}
