using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6XEnvironmentBackdropTests
{
    [Fact]
    public void AuthoredBackdropPreservesTriangleTopologyBothUvStreamsAndDcg()
    {
        byte[] bytes = BuildBackdropW3d(inwardFacing: true, materialAttributes: 0u);

        Assert.True(EnbW3dEnvironmentBackdrop.TryDecode(bytes, out EnbW3dEnvironmentBackdrop backdrop));
        Assert.Equal("SPHERE01", backdrop.Mesh.Name);
        Assert.Equal(6, backdrop.Mesh.Vertices.Count);
        Assert.Equal(8, backdrop.Mesh.Triangles.Count);
        Assert.Equal("NEB_GREY.tga", backdrop.Stage0TextureName);
        Assert.Equal("NEB_blue.tga", backdrop.Stage1TextureName);

        Assert.Equal(new EnbW3dTriangle(0, 4, 2, 0, Vector3.UnitX, 0), backdrop.Mesh.Triangles[0]);
        Assert.Equal(new Vector2(0.10f, 0.80f), backdrop.Stage0.RuntimeTexCoords[0]);
        Assert.Equal(new Vector2(0.60f, 0.30f), backdrop.Stage1.RuntimeTexCoords[0]);
        Assert.Equal(0.0f, backdrop.VertexIntensity[0], 4);
        Assert.Equal(128.0f / 255.0f, backdrop.VertexIntensity[3], 4);
        Assert.Equal(1.0f, backdrop.VertexIntensity[5], 4);
    }

    [Fact]
    public void AuthoredBackdropShaderCompilesToSelectedOpaqueTwoStageState()
    {
        byte[] bytes = BuildBackdropW3d(inwardFacing: true, materialAttributes: 0u);

        Assert.True(EnbW3dEnvironmentBackdrop.TryDecode(bytes, out EnbW3dEnvironmentBackdrop backdrop));
        ReadOnlySpan<byte> raw = backdrop.Mesh.Shaders[0].Raw.Span;
        var record = new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(12, 4)));

        Assert.True(EnbW3dMaterialCompiler.TryDecodeShader(record, out EnbW3dFixedFunctionState state, out string? diagnostic), diagnostic);
        Assert.Equal(EnbRenderDepthCompare.Always, state.DepthCompare);
        Assert.False(state.DepthWriteEnabled);
        Assert.Equal(EnbRenderCullMode.Clockwise, state.CullMode);
        Assert.Equal(EnbRenderBlendFactor.One, state.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.Zero, state.DestinationBlend);
        Assert.False(state.BlendEnabled);
        Assert.False(state.AlphaTest.Enabled);
        Assert.Equal(EnbW3dFogMode.Disabled, state.FogMode);
        Assert.Equal(
            new EnbW3dTextureChannelSemantics(
                EnbW3dTextureOperation.Modulate,
                EnbW3dTextureArgument.Texture,
                EnbW3dTextureArgument.Diffuse),
            state.TextureCombinerSemantics.Stage0.Color);
        Assert.Equal(
            new EnbW3dTextureChannelSemantics(
                EnbW3dTextureOperation.Modulate,
                EnbW3dTextureArgument.Texture,
                EnbW3dTextureArgument.Current),
            state.TextureCombinerSemantics.Stage1.Color);
    }

    [Fact]
    public void AuthoredBackdropRejectsOutwardFacingSphereInsteadOfInventingCullBehavior()
    {
        byte[] bytes = BuildBackdropW3d(inwardFacing: false, materialAttributes: 0u);

        Assert.False(EnbW3dEnvironmentBackdrop.TryDecode(bytes, out _));
    }

    [Fact]
    public void AuthoredBackdropRejectsMapperStateAbsentFromSelectedRetailEnvironment()
    {
        byte[] bytes = BuildBackdropW3d(inwardFacing: true, materialAttributes: 0x0001_0000u);

        Assert.False(EnbW3dEnvironmentBackdrop.TryDecode(bytes, out _));
    }

    private static byte[] BuildBackdropW3d(bool inwardFacing, uint materialAttributes)
    {
        Vector3[] vertices =
        [
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ,
        ];

        (uint A, uint B, uint C)[] outward =
        [
            (0, 2, 4),
            (2, 1, 4),
            (1, 3, 4),
            (3, 0, 4),
            (2, 0, 5),
            (1, 2, 5),
            (3, 1, 5),
            (0, 3, 5),
        ];
        byte[] triangles = Concat(outward.Select(face => inwardFacing
            ? TrianglePayload(face.A, face.C, face.B)
            : TrianglePayload(face.A, face.B, face.C)).ToArray());

        Vector2[] stage0SourceUvs =
        [
            new(0.10f, 0.20f),
            new(0.20f, 0.30f),
            new(0.30f, 0.40f),
            new(0.40f, 0.50f),
            new(0.50f, 0.60f),
            new(0.60f, 0.70f),
        ];
        Vector2[] stage1SourceUvs =
        [
            new(0.60f, 0.70f),
            new(0.50f, 0.60f),
            new(0.40f, 0.50f),
            new(0.30f, 0.40f),
            new(0.20f, 0.30f),
            new(0.10f, 0.20f),
        ];
        byte[] dcg =
        [
            0, 0, 0, 255,
            32, 32, 32, 255,
            64, 64, 64, 255,
            128, 128, 128, 255,
            192, 192, 192, 255,
            255, 255, 255, 255,
        ];

        byte[] shader =
        [
            0x07, 0x00, 0x00, 0x00,
            0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x02,
        ];

        return ContainerChunk(
            0x0000,
            LeafChunk(0x001F, MeshHeader((uint)vertices.Length, (uint)outward.Length)),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            LeafChunk(0x0020, triangles),
            LeafChunk(0x0028, UInt32Payload(1, 1, 1, 2)),
            ContainerChunk(0x002A, VertexMaterial(materialAttributes)),
            LeafChunk(0x0029, shader),
            ContainerChunk(
                0x0030,
                Texture("NEB_GREY.tga"),
                Texture("NEB_blue.tga")),
            ContainerChunk(
                0x0038,
                LeafChunk(0x0039, UInt32Payload(0)),
                LeafChunk(0x003A, UInt32Payload(0)),
                LeafChunk(0x003B, dcg),
                ContainerChunk(
                    0x0048,
                    LeafChunk(0x0049, UInt32Payload(0)),
                    LeafChunk(0x004A, Vector2Payload(stage0SourceUvs))),
                ContainerChunk(
                    0x0048,
                    LeafChunk(0x0049, UInt32Payload(1)),
                    LeafChunk(0x004A, Vector2Payload(stage1SourceUvs)))));
    }

    private static byte[] MeshHeader(uint vertexCount, uint triangleCount)
    {
        byte[] bytes = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(bytes.AsSpan(8, 16), "SPHERE01");
        WriteFixedAscii(bytes.AsSpan(24, 16), "BACK_BLUE");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), triangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44, 4), vertexCount);
        WriteSingle(bytes.AsSpan(76, 4), -1f);
        WriteSingle(bytes.AsSpan(80, 4), -1f);
        WriteSingle(bytes.AsSpan(84, 4), -1f);
        WriteSingle(bytes.AsSpan(88, 4), 1f);
        WriteSingle(bytes.AsSpan(92, 4), 1f);
        WriteSingle(bytes.AsSpan(96, 4), 1f);
        WriteSingle(bytes.AsSpan(112, 4), 1f);
        return bytes;
    }

    private static byte[] VertexMaterial(uint attributes)
    {
        byte[] info = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0, 4), attributes);
        new byte[] { 255, 255, 255, 0 }.CopyTo(info, 16);
        WriteSingle(info.AsSpan(20, 4), 1.0f);
        WriteSingle(info.AsSpan(24, 4), 1.0f);
        return ContainerChunk(
            0x002B,
            LeafChunk(0x002C, Encoding.ASCII.GetBytes("Material #5\0")),
            LeafChunk(0x002D, info));
    }

    private static byte[] Texture(string name)
        => ContainerChunk(
            0x0031,
            LeafChunk(0x0032, Encoding.ASCII.GetBytes(name + "\0")),
            LeafChunk(0x0033, UInt32Payload(0x0000_0004u, 1u, 0x4170_0000u)));

    private static byte[] TrianglePayload(uint v0, uint v1, uint v2)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), v0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), v1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), v2);
        WriteSingle(bytes.AsSpan(16, 4), 1f);
        return bytes;
    }

    private static byte[] UInt32Payload(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
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

    private static byte[] Concat(params byte[][] values)
        => values.SelectMany(static value => value).ToArray();

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(), destination);
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, isContainer: false);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static child => child).ToArray(), isContainer: true);

    private static byte[] Chunk(uint type, byte[] payload, bool isContainer)
    {
        byte[] result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(4, 4),
            checked((uint)payload.Length) | (isContainer ? 0x8000_0000u : 0u));
        payload.CopyTo(result, 8);
        return result;
    }
}
