using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z15CurrentRetailHAnimResourceManagerTests
{
    [Fact]
    public void GetHAnimBuildsPrimaryThenLiteralParentLazyCandidate()
    {
        Assert.True(EnbCurrentRetailRslHAnimResolver.TryBuildLazyResourceCandidates(
            "TSG02.TSG02-1",
            out string primary,
            out string parent));

        Assert.Equal("TSG02-1.w3d", primary);
        Assert.Equal(@"..\TSG02-1.w3d", parent);
    }

    [Fact]
    public void ParentRetryFindsLooseInstallRootResourceAfterPrimaryMiss()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            File.WriteAllBytes(
                Path.Combine(root, "TSG02-1.w3d"),
                BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 24));

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02.W3D"),
                NoAuthoredFile(),
                paths,
                null,
                "TSG02.TSG02-1",
                out EnbCurrentRetailRslHAnimResolution resolution,
                out string? diagnostic), diagnostic);

            Assert.Equal(12u, resolution.Animation.NumFrames);
            Assert.Equal(24u, resolution.Animation.FrameRate);
            Assert.Contains(@"lazy:..\TSG02-1.w3d", resolution.Source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("loose:TSG02-1.w3d", resolution.Source, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrimaryLooseResourceWinsBeforePrimaryMixResource()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            File.WriteAllBytes(
                Path.Combine(Path.GetDirectoryName(paths.ClientExecutablePath)!, "TSG02-1.w3d"),
                BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 11));
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_art_01.mix"),
                ("TSG02-1.w3d", BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 99)));
            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02.W3D"),
                NoAuthoredFile(),
                paths,
                resources,
                "TSG02.TSG02-1",
                out EnbCurrentRetailRslHAnimResolution resolution,
                out string? diagnostic), diagnostic);

            Assert.Equal(11u, resolution.Animation.FrameRate);
            Assert.Contains("@loose:", resolution.Source, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrimaryMixLoadPreventsParentRetry()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            File.WriteAllBytes(
                Path.Combine(root, "TSG02-1.w3d"),
                BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 77));
            WriteMix1(
                Path.Combine(paths.MixFilesDirectory, "mixfile_art_01.mix"),
                ("TSG02-1.w3d", BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 33)));
            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(paths.MixFilesDirectory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02.W3D"),
                NoAuthoredFile(),
                paths,
                resources,
                "TSG02.TSG02-1",
                out EnbCurrentRetailRslHAnimResolution resolution,
                out string? diagnostic), diagnostic);

            Assert.Equal(33u, resolution.Animation.FrameRate);
            Assert.Contains("@MIX:", resolution.Source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"lazy:..\", resolution.Source, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SuccessfulPrimaryW3dLoadWithoutRequestedHAnimDoesNotProbeParentAlias()
    {
        string root = CreateRetailTree();
        try
        {
            EnbInstalledAssetPaths paths = BuildPaths(root);
            File.WriteAllBytes(
                Path.Combine(Path.GetDirectoryName(paths.ClientExecutablePath)!, "TSG02-1.w3d"),
                BuildMinimalAnimationW3d("SOMETHING_ELSE", "TSG02", 12, 11));
            File.WriteAllBytes(
                Path.Combine(root, "TSG02-1.w3d"),
                BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 77));

            Assert.False(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02.W3D"),
                NoAuthoredFile(),
                paths,
                null,
                "TSG02.TSG02-1",
                out _,
                out string? diagnostic));

            Assert.Contains("did not register HAnim", diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not enter the '..\\' retry", diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EnbResolvedRslFileDependency NoAuthoredFile()
        => new(
            string.Empty,
            EnbRslFileDependencyStatus.NotAuthored);

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
            Path.Combine(ini, "EnvironmentMaps.ini"),
            Path.Combine(ini, "SpaceCamera.ini"),
            Path.Combine(ini, "radar.ini"),
            Path.Combine(ini, "cockpit.ini"),
            Path.Combine(ini, "FontSystem.ini"),
            Path.Combine(ini, "GutterColor.ini"),
            Path.Combine(ini, "MetalEquivalentRGBs.ini"),
            Path.Combine(ini, "DecalTextures.ini"),
            Path.Combine(ini, "DecalSwatches.ini"),
            Path.Combine(ini, "SecretDecalTextures.ini"),
            Path.Combine(clientData, "mixfiles"));
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
