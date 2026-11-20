using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6CloseoutPersistentHAnimRegistryTests
{
    [Fact]
    public void MissingAnimationLookupIsRetainedUntilNamespaceReset()
    {
        var registry = new EnbCurrentRetailRslHAnimRegistryRuntime();
        var retained = EmptyResource("MISSING.W3D");
        var file = new EnbResolvedRslFileDependency(string.Empty, EnbRslFileDependencyStatus.NotAuthored);
        Assert.False(registry.TryResolve(retained, file, null, null, null, "MISSING.MISSING-1", out _, out var diagnostic));
        for (int frame = 0; frame < 120; frame++)
        {
            Assert.False(registry.TryResolve(retained, file, null, null, null, "MISSING.MISSING-1", out _, out var repeated));
            Assert.Equal(diagnostic, repeated);
        }
        Assert.Equal(1UL, registry.MissCount);
        Assert.Equal(120UL, registry.HitCount);
        registry.Clear();
        Assert.False(registry.TryResolve(retained, file, null, null, null, "MISSING.MISSING-1", out _, out _));
        Assert.Equal(1UL, registry.MissCount);
        Assert.Equal(0UL, registry.HitCount);
    }

    [Fact]
    public void SuccessfulLazyHAnimRegistrationIsReusedWithoutReReadingTheArchive()
    {
        string directory = CreateTemporaryDirectory();
        string archive = Path.Combine(directory, "mixfile_art_01.mix");
        try
        {
            WriteMix1(
                archive,
                ("TSG02A-1.w3d", BuildMinimalAnimationW3d("TSG02A-1", "TSG02A", 31, 30)));

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            EnbW3dRenderResource retained = EmptyResource("TSG02A.W3D");
            var noAuthoredFile = new EnbResolvedRslFileDependency(
                string.Empty,
                EnbRslFileDependencyStatus.NotAuthored);
            var registry = new EnbCurrentRetailRslHAnimRegistryRuntime();

            Assert.True(registry.TryResolve(
                retained,
                noAuthoredFile,
                installedPaths: null,
                resources,
                retailPackageResources: null,
                "TSG02A.TSG02A-1",
                out EnbCurrentRetailRslHAnimResolution first,
                out string? firstDiagnostic), firstDiagnostic);
            Assert.Equal(1, registry.Count);
            Assert.Equal(0UL, registry.HitCount);
            Assert.Equal(1UL, registry.MissCount);

            File.Delete(archive);

            Assert.True(registry.TryResolve(
                retained,
                noAuthoredFile,
                installedPaths: null,
                resources,
                retailPackageResources: null,
                "TSG02A.TSG02A-1",
                out EnbCurrentRetailRslHAnimResolution second,
                out string? secondDiagnostic), secondDiagnostic);

            Assert.Same(first, second);
            Assert.False(second.RequiresAcquisitionMaintenance);
            Assert.Equal(registry.Generation,second.RegistryGeneration);
            Assert.Equal(1, registry.Count);
            Assert.Equal(1UL, registry.HitCount);
            Assert.Equal(1UL, registry.MissCount);
            registry.Clear();
            Assert.NotEqual(registry.Generation,second.RegistryGeneration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClearingRegistryForAssetNamespaceChangeForcesLazyLookupAgain()
    {
        string directory = CreateTemporaryDirectory();
        string archive = Path.Combine(directory, "mixfile_art_01.mix");
        try
        {
            WriteMix1(
                archive,
                ("TSG02A-1.w3d", BuildMinimalAnimationW3d("TSG02A-1", "TSG02A", 31, 30)));

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            EnbW3dRenderResource retained = EmptyResource("TSG02A.W3D");
            var noAuthoredFile = new EnbResolvedRslFileDependency(
                string.Empty,
                EnbRslFileDependencyStatus.NotAuthored);
            var registry = new EnbCurrentRetailRslHAnimRegistryRuntime();

            Assert.True(registry.TryResolve(
                retained,
                noAuthoredFile,
                installedPaths: null,
                resources,
                retailPackageResources: null,
                "TSG02A.TSG02A-1",
                out _,
                out string? firstDiagnostic), firstDiagnostic);

            File.Delete(archive);
            registry.Clear();

            Assert.False(registry.TryResolve(
                retained,
                noAuthoredFile,
                installedPaths: null,
                resources,
                retailPackageResources: null,
                "TSG02A.TSG02A-1",
                out _,
                out _));
            Assert.Equal(0, registry.Count);
            Assert.Equal(0UL, registry.HitCount);
            Assert.Equal(1UL, registry.MissCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FileRegistrationInvalidatesNegativeOwnersEvenWhenRequestedNameMisses()
    {
        Assert.True(EnbW3dModel.TryDecode(BuildMinimalAnimationW3d("TSG02A-1","TSG02A",31,30),out var model));
        var registry=new EnbCurrentRetailRslHAnimRegistryRuntime();
        var retained=EmptyResource("ROOT.W3D");
        var file=new EnbResolvedRslFileDependency("ANIM.W3D",EnbRslFileDependencyStatus.ResolvedW3d,Model:model);
        Assert.False(registry.TryResolve(retained,file,null,null,null,"ABSENT.ABSENT",out _,out _));
        Assert.Equal(0,registry.Count);
        Assert.Equal(1,registry.RegistrationCount);
        Assert.False(registry.HasPendingAcquisition(retained));
        Assert.True(registry.TryResolve(retained,file,null,null,null,"TSG02A.TSG02A-1",out _,out _));
        Assert.Equal(1,registry.RegistrationCount);
        ulong generation=registry.Generation;registry.Clear();
        Assert.NotEqual(generation,registry.Generation);Assert.Equal(0,registry.RegistrationCount);
    }

    private static EnbW3dRenderResource EmptyResource(string logicalName)
        => new(
            new EnbW3dRenderResourceKey(logicalName, "test.mix", 0, 0),
            logicalName,
            Array.Empty<EnbW3dRenderMesh>(),
            Array.Empty<EnbW3dRenderHierarchy>(),
            Array.Empty<EnbW3dRenderHierarchyAnimation>(),
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            Array.Empty<EnbW3dRenderHlod>());

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

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnbNext.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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
