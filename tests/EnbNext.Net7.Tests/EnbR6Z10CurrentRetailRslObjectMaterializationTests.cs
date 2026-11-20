using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z10CurrentRetailRslObjectMaterializationTests
{
    [Fact]
    public void ObjectOnlyRslStateKeepsFileDistinctWhileRuntimeLazyLookupFindsObjectW3d()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("PVC0101.w3d", BuildMinimalW3d("PVC0101", "pvc0101.tga")),
                ("PVC0101.dds", [0x44, 0x44, 0x53, 0x20]));

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
                """
                [0\STATE0]
                ID=0
                TYPE=object
                OBJECT=PVC0101
                """);
            Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));

            var rslResolver = new EnbRetailRslResourceResolver(resources);
            EnbResolvedRslLibraryResourceGraph graph = rslResolver.Resolve(library);
            Assert.True(graph.TryFindState(0, out EnbResolvedRslStateResource state));
            Assert.Equal(EnbRslFileDependencyStatus.NotAuthored, state.FileDependency.Status);
            Assert.Equal("PVC0101", state.ObjectPrototypeName);

            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                resources,
                state.ObjectPrototypeName!,
                out EnbResolvedW3dResource resolved,
                out string? diagnostic), diagnostic);
            Assert.Equal("PVC0101.w3d", resolved.LogicalFileName);
            Assert.Equal("PVC0101", resolved.RequestedObjectName);
            Assert.Equal("PVC0101.w3d", resolved.ResourceLocation.Name);
            Assert.Equal("PVC0101", Assert.Single(resolved.Model.Meshes).Name);
            EnbResolvedTextureReference texture = Assert.Single(resolved.Textures);
            Assert.Equal("pvc0101.tga", texture.LogicalName);
            Assert.Equal("PVC0101.dds", texture.Location!.Name, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("ROOT.CHILD", "ROOT", "CHILD")]
    [InlineData("ROOT.CHILD.EXTRA", "ROOT", "CHILD.EXTRA")]
    [InlineData(".CHILD", "", ".CHILD")]
    public void DottedLazyLookupLoadsFirstPrefixFileAndRetainsFullRegistryName(
        string objectName, string filePrefix, string meshName)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"),
                (filePrefix + ".w3d", BuildMinimalW3d(meshName, "paint.tga", filePrefix)),
                (objectName + ".w3d", BuildMinimalW3d("WRONG", "wrong.tga")));
            var resources = EnbMixResourceCatalog.Load(directory)!;
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                resources, objectName, out var source, out var diagnostic), diagnostic);
            Assert.Equal(filePrefix + ".w3d", source.LogicalFileName);
            Assert.Equal(objectName, source.RequestedObjectName);
            using var cache = new EnbNext.Rendering.EnbW3dRenderResourceCache();
            Assert.True(cache.TryGetOrCompile(source, out var compiled, out diagnostic), diagnostic);
            Assert.True(cache.TryFindPrototypeSource(objectName, out var registered));
            Assert.Equal(source.ResourceLocation, registered.ResourceLocation);
            Assert.Equal(objectName, EnbNext.Rendering.EnbW3dRenderObjectNameSemantics.RegisteredMeshName(
                Assert.Single(compiled.Meshes)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static byte[] BuildMinimalW3d(string meshName, string textureName, string containerName = "")
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(8, 16), meshName);
        WriteFixedAscii(header.AsSpan(24, 16), containerName);
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
        foreach (var (name, _) in resources)
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
