using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z16CurrentRetailMixPackageScopeTests
{
    [Fact]
    public void RetailPackageCatalogEnumeratesEveryMixFamilyWhileLegacyCatalogRemainsArtOnly()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"), ("ART_ONLY.W3D", new byte[] { 1 }));
            WriteMix1(Path.Combine(directory, "mixfile_sound_01.mix"), ("TSG02-1.w3d", new byte[] { 2 }));
            WriteMix1(Path.Combine(directory, "mixfile_talktree_eng_01.MIX"), ("DIALOG.DAT", new byte[] { 3 }));
            File.WriteAllText(Path.Combine(directory, "not-a-package.txt"), "ignored");

            EnbMixResourceCatalog art = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic art catalog was not loaded.");
            EnbMixResourceCatalog retail = EnbMixResourceCatalog.LoadRetailPackages(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail package catalog was not loaded.");

            Assert.Equal(1, art.ArchiveCount);
            Assert.False(art.TryLocateResource("TSG02-1.w3d", out _));

            Assert.Equal(3, retail.DiscoveredArchiveCount);
            Assert.Equal(3, retail.ArchiveCount);
            Assert.True(retail.TryLocateResource("TSG02-1.w3d", out EnbMixResourceLocation location));
            Assert.Equal("mixfile_sound_01.mix", location.ArchiveName);
            Assert.True(retail.TryGetResourceLocations("TSG02-1.w3d", out IReadOnlyList<EnbMixResourceLocation> locations));
            Assert.Single(locations);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RetailAssetBundleCarriesArtAndCompletePackageViewsSeparately()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            WriteMix1(Path.Combine(paths.MixFilesDirectory, "mixfile_art_01.mix"), ("ART.W3D", new byte[] { 1 }));
            WriteMix1(Path.Combine(paths.MixFilesDirectory, "mixfile_sound_01.mix"), ("SOUND.DAT", new byte[] { 2 }));

            EnbRetailAssetBundle bundle = EnbRetailAssetBundle.Load(paths);

            Assert.NotNull(bundle.MixResources);
            Assert.NotNull(bundle.RetailMixResources);
            Assert.Equal(1, bundle.MixResources!.ArchiveCount);
            Assert.Equal(2, bundle.RetailMixResources!.ArchiveCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HAnimResolverUsesCompleteRetailPackageNamespaceAfterLooseMiss()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_art_01.mix"),
                ("UNRELATED.W3D", new byte[] { 1, 2, 3 }));
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_sound_01.mix"),
                ("TSG02-1.w3d", BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 24)));

            EnbMixResourceCatalog art = EnbMixResourceCatalog.Load(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic art catalog was not loaded.");
            EnbMixResourceCatalog retail = EnbMixResourceCatalog.LoadRetailPackages(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail package catalog was not loaded.");

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02.W3D"),
                NoAuthoredFile(),
                paths,
                art,
                retail,
                "TSG02.TSG02-1",
                out EnbCurrentRetailRslHAnimResolution resolution,
                out string? diagnostic), diagnostic);

            Assert.Equal(24u, resolution.Animation.FrameRate);
            Assert.Contains("lazy:TSG02-1.w3d@MIX:mixfile_sound_01.mix/TSG02-1.w3d", resolution.Source, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DuplicateResourceNameAcrossRetailPackagesResolvesFromTheFirstEnumeratedPackage()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_sound_01.mix"),
                ("TSG02-1.w3d", BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 24)));
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_talktree_eng_01.mix"),
                ("TSG02-1.w3d", BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 30)));

            EnbMixResourceCatalog retail = EnbMixResourceCatalog.LoadRetailPackages(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail package catalog was not loaded.");

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02.W3D"),
                NoAuthoredFile(),
                paths,
                null,
                retail,
                "TSG02.TSG02-1",
                out _,
                out string? diagnostic), diagnostic);
            Assert.True(retail.TryGetResourceLocations("TSG02-1.w3d", out IReadOnlyList<EnbMixResourceLocation> locations));
            Assert.Equal(
                Path.GetFileName(EnbNext.Installation.EnbPathCase.EnumerateFiles(paths.MixFilesDirectory, "*.mix").First()),
                locations[0].ArchiveName,
                ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnreadablePackageRegistersNothingAndReadableMatchStillResolves()
    {
        // Retail 009D7A60/009D7E20: a package with a bad header registers no names
        // and 004026D5 continues with the next package.
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_sound_01.mix"),
                ("TSG02-1.w3d", BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 24)));
            File.WriteAllBytes(
                Path.Combine(paths.MixFilesDirectory, "mixfile_talktree_eng_01.mix"),
                new byte[] { 0x00, 0x01, 0x02, 0x03 });

            EnbMixResourceCatalog retail = EnbMixResourceCatalog.LoadRetailPackages(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail package catalog was not loaded.");

            Assert.Equal(1, retail.ArchiveCount);
            Assert.Equal(2, retail.DiscoveredArchiveCount);
            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02.W3D"),
                NoAuthoredFile(),
                paths,
                null,
                retail,
                "TSG02.TSG02-1",
                out _,
                out string? diagnostic), diagnostic);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DuplicateNameWithDifferentPayloadsKeepsTheFirstEnumeratedPackage()
    {
        // Retail 004026D5 registers packages in _findfirst/_findnext order and
        // 004E5440 inserts a name only when it is absent.
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(Path.Combine(directory, "mixfile_art_26.mix"), ("PVF03A.W3D", new byte[] { 26 }));
            WriteMix1(Path.Combine(directory, "mixfile_art_09.mix"), ("PVF03A.W3D", new byte[] { 9 }));
            string first = Path.GetFileName(EnbNext.Installation.EnbPathCase.EnumerateFiles(directory, "*.mix").First());

            foreach (EnbMixResourceCatalog catalog in new[]
                     {
                         EnbMixResourceCatalog.Load(directory)!,
                         EnbMixResourceCatalog.LoadRetailPackages(directory)!,
                     })
            {
                Assert.True(catalog.TryLocateResource("pvf03a.w3d", out EnbMixResourceLocation location));
                Assert.Equal(first, location.ArchiveName, ignoreCase: true);
                Assert.True(catalog.TryGetResourceLocations("PVF03A.W3D", out IReadOnlyList<EnbMixResourceLocation> locations));
                Assert.Equal(first, locations[0].ArchiveName, ignoreCase: true);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static EnbResolvedRslFileDependency NoAuthoredFile()
        => new(string.Empty, EnbRslFileDependencyStatus.NotAuthored);

    private static EnbW3dRenderResource EmptyResource(string logicalName)
        => new(
            new EnbW3dRenderResourceKey(logicalName, "test.mix", 0, 0),
            logicalName,
            Array.Empty<EnbW3dRenderMesh>(),
            Array.Empty<EnbW3dRenderHierarchy>(),
            Array.Empty<EnbW3dRenderHierarchyAnimation>(),
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            Array.Empty<EnbW3dRenderHlod>());

    private static string CreateRetailTree()
    {
        string root = CreateTemporaryDirectory();
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

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnbNext.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] BuildMinimalAnimationW3d(
        string name,
        string hierarchyName,
        uint numFrames,
        uint frameRate)
    {
        byte[] header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(4, 16), name);
        WriteFixedAscii(header.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), frameRate);
        return ContainerChunk(0x0200, LeafChunk(0x0201, header));
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
