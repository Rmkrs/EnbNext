using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4UMaterialVariantSelectionTests
{
    [Theory]
    [InlineData(0x0100_0000u, 0x0023u, (byte)0x23)]
    [InlineData(0x0200_0000u, 0x0024u, (byte)0x24)]
    [InlineData(0x0400_0000u, 0x0025u, (byte)0x25)]
    [InlineData(0x0600_0000u, 0x0025u, (byte)0x25)]
    [InlineData(0x0800_0000u, 0x0023u, (byte)0x23)]
    public void CurrentRetailSelectorChoosesExactlyOneRootMaterialVariant(
        uint attributes,
        uint expectedChunkType,
        byte expectedShaderMarker)
    {
        byte[] bytes = BuildMeshWithAllMaterialVariants(attributes);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);

        Assert.Equal<uint?>(expectedChunkType, mesh.CurrentRetailMaterialVariantChunkType);
        EnbW3dMaterialInfo info = mesh.MaterialInfo
            ?? throw new Xunit.Sdk.XunitException("Selected material variant did not supply MaterialInfo.");
        Assert.Equal((uint)1, info.PassCount);
        Assert.Equal((uint)1, info.ShaderCount);
        Assert.Single(mesh.MaterialPasses);
        Assert.Equal(expectedShaderMarker, Assert.Single(mesh.Shaders).Raw.Span[0]);
    }

    [Fact]
    public void CurrentRetailSelectorDoesNotUseVariant26WhenSelectorOneIsFixed()
    {
        byte[] bytes = BuildMeshWithAllMaterialVariants(0x0800_0000u);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);

        Assert.Equal<uint?>(0x0023u, mesh.CurrentRetailMaterialVariantChunkType);
        Assert.Equal((byte)0x23, Assert.Single(mesh.Shaders).Raw.Span[0]);
        Assert.DoesNotContain(mesh.Shaders, shader => shader[0] == 0x26);
    }

    [Fact]
    public void AttributesOutsideHighVariantFamilyDoNotMergeVariantContainers()
    {
        // 0x20000000 is the authored N-Patch request. It is outside
        // the 0x0F000000 material-variant selector family and therefore must not
        // accidentally make the generic recursive decoder consume every variant.
        byte[] bytes = BuildMeshWithAllMaterialVariants(0x2000_0000u);

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);

        Assert.Null(mesh.CurrentRetailMaterialVariantChunkType);
        Assert.Null(mesh.MaterialInfo);
        Assert.Empty(mesh.Shaders);
        Assert.Empty(mesh.MaterialPasses);
    }

    private static byte[] BuildMeshWithAllMaterialVariants(uint attributes)
        => ContainerChunk(
            0x0000,
            LeafChunk(0x001F, BuildMeshHeader(attributes)),
            LeafChunk(0x0002, Vector3Payload(Vector3.Zero)),
            MaterialVariant(0x0023, 0x23),
            MaterialVariant(0x0024, 0x24),
            MaterialVariant(0x0025, 0x25),
            MaterialVariant(0x0026, 0x26));

    private static byte[] MaterialVariant(uint chunkType, byte shaderMarker)
    {
        byte[] shader = new byte[16];
        shader[0] = shaderMarker;
        return ContainerChunk(
            chunkType,
            LeafChunk(0x0028, UInt32Payload(1, 0, 1, 0)),
            LeafChunk(0x0029, shader),
            ContainerChunk(0x0038, LeafChunk(0x003A, UInt32Payload(0))));
    }

    private static byte[] BuildMeshHeader(uint attributes)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), attributes);
        WriteFixedAscii(header.AsSpan(8, 16), "R4U");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), EnbW3dVertexChannels.Location);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), EnbW3dFaceChannels.Face);
        WriteSingle(header.AsSpan(112, 4), 1.0f);
        return header;
    }

    private static byte[] Vector3Payload(Vector3 value)
    {
        byte[] bytes = new byte[12];
        WriteSingle(bytes.AsSpan(0, 4), value.X);
        WriteSingle(bytes.AsSpan(4, 4), value.Y);
        WriteSingle(bytes.AsSpan(8, 4), value.Z);
        return bytes;
    }

    private static byte[] UInt32Payload(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
        return bytes;
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] ContainerChunk(uint type, params byte[][] children)
    {
        byte[] payload = Concat(children);
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length | 0x8000_0000u);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Concat(params byte[][] values)
    {
        byte[] result = new byte[values.Sum(static value => value.Length)];
        int offset = 0;
        foreach (byte[] value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }
        return result;
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));
}
