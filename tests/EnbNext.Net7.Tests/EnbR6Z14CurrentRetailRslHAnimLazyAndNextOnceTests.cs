using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z14CurrentRetailRslHAnimLazyAndNextOnceTests
{
    [Theory]
    [InlineData("TSG02.TSG02-1", "TSG02-1.w3d")]
    [InlineData("OWNER.GATE.OPEN", "GATE.OPEN.w3d")]
    public void LazyHAnimFilenameUsesTextAfterFirstDot(string hAnimName, string expected)
    {
        Assert.True(EnbCurrentRetailRslHAnimResolver.TryBuildLazyResourceName(hAnimName, out string resource));
        Assert.Equal(expected, resource);
    }

    [Fact]
    public void HAnimWithoutDotHasNoCurrentRetailLazyFilenameBranch()
    {
        Assert.False(EnbCurrentRetailRslHAnimResolver.TryBuildLazyResourceName("TSG02", out string resource));
        Assert.Equal(string.Empty, resource);
    }

    [Fact]
    public void ResolverLoadsExactLazyW3dAndBindsAnimationToRetainedDrawResource()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("TSG02-1.w3d", BuildMinimalAnimationW3d("TSG02-1", "TSG02", 12, 24)));

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            EnbW3dRenderResource retained = EmptyResource("TSG02.W3D");
            var noAuthoredFile = new EnbResolvedRslFileDependency(
                string.Empty,
                EnbRslFileDependencyStatus.NotAuthored);

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                retained,
                noAuthoredFile,
                resources,
                "TSG02.TSG02-1",
                out EnbCurrentRetailRslHAnimResolution resolution,
                out string? diagnostic), diagnostic);

            Assert.Equal("TSG02.TSG02-1", resolution.HAnimName);
            Assert.Contains("lazy:TSG02-1.w3d@MIX:", resolution.Source, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(12u, resolution.Animation.NumFrames);
            Assert.Equal(24u, resolution.Animation.FrameRate);
            EnbW3dRenderHierarchyAnimation appended = Assert.Single(resolution.DrawResource.Animations);
            Assert.Same(resolution.Animation, appended);
            Assert.Empty(retained.Animations);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private static EnbW3dRenderHierarchyAnimation Animation(uint numFrames, uint frameRate)
        => new(
            Ordinal: 0,
            Version: 0x0004_0002u,
            Name: "TSG02.TSG02-1",
            HierarchyName: "TSG02",
            NumFrames: numFrames,
            FrameRate: frameRate,
            HasLegacySyntheticRootChannelShift: false,
            Channels: Array.Empty<EnbW3dRenderAnimationChannel>(),
            BitChannels: Array.Empty<EnbW3dRenderAnimationBitChannel>());

    private static EnbRslStateFlags Flags()
        => new(
            Delete: false,
            Deactivate: false,
            Stop: false,
            ActivateNext: false,
            Enabled: false,
            RemovePrototype: false,
            Precached: false,
            InstanceData: false,
            ActivateNextOnce: false,
            StopOnce: false,
            Stationary: false,
            StartsEnabled: false,
            DontStopSound: false,
            DontLoopSound: false);

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

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EnbNext.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
