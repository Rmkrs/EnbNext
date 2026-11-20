using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4HW3dTextureAlphaSemanticsTests
{
    [Fact]
    public void AlphaTestUsesInverseSourceAlphaSpecificRetailReferenceAndCompare()
    {
        uint packed = (EnbW3dMaterialCompiler.RetailW3dShaderBaseline & ~0x0000_C000u) |
                      (3u << 14) |
                      0x0004_0000u;

        Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
            packed,
            out EnbW3dFixedFunctionState state,
            out string? diagnostic), diagnostic);

        Assert.Equal(EnbRenderBlendFactor.InverseSourceAlpha, state.SourceBlend);
        Assert.True(state.AlphaTest.Enabled);
        Assert.Equal((byte)0x9F, state.AlphaTest.Reference);
        Assert.Equal(EnbRenderDepthCompare.LessOrEqual, state.AlphaTest.Compare);
    }

    [Fact]
    public void AlphaTestUsesDefaultRetailReferenceAndCompareForOtherSourceBlend()
    {
        uint packed = EnbW3dMaterialCompiler.RetailW3dShaderBaseline | 0x0004_0000u;

        Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
            packed,
            out EnbW3dFixedFunctionState state,
            out string? diagnostic), diagnostic);

        Assert.NotEqual(EnbRenderBlendFactor.InverseSourceAlpha, state.SourceBlend);
        Assert.True(state.AlphaTest.Enabled);
        Assert.Equal((byte)0x60, state.AlphaTest.Reference);
        Assert.Equal(EnbRenderDepthCompare.GreaterOrEqual, state.AlphaTest.Compare);
    }

    [Fact]
    public void DisabledAlphaTestCarriesNeutralFiniteState()
    {
        Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
            EnbW3dMaterialCompiler.RetailW3dShaderBaseline,
            out EnbW3dFixedFunctionState state,
            out string? diagnostic), diagnostic);

        Assert.False(state.AlphaTest.Enabled);
        Assert.Equal(EnbRenderDepthCompare.Always, state.AlphaTest.Compare);
        Assert.Equal((byte)0, state.AlphaTest.Reference);
    }

    [Theory]
    [InlineData(0x0000u, 0, EnbRenderTextureAddressMode.Wrap, EnbRenderTextureAddressMode.Wrap, false)]
    [InlineData(0x0004u, 1, EnbRenderTextureAddressMode.Wrap, EnbRenderTextureAddressMode.Wrap, false)]
    [InlineData(0x0040u, 2, EnbRenderTextureAddressMode.Wrap, EnbRenderTextureAddressMode.Wrap, false)]
    [InlineData(0x0080u, 3, EnbRenderTextureAddressMode.Wrap, EnbRenderTextureAddressMode.Wrap, false)]
    [InlineData(0x00C0u, 4, EnbRenderTextureAddressMode.Wrap, EnbRenderTextureAddressMode.Wrap, false)]
    [InlineData(0x0018u, 0, EnbRenderTextureAddressMode.Clamp, EnbRenderTextureAddressMode.Clamp, false)]
    [InlineData(0x1000u, 0, EnbRenderTextureAddressMode.Wrap, EnbRenderTextureAddressMode.Wrap, true)]
    public void TextureInfoFirstDwordCompilesExactCurrentRetailAuthoredState(
        uint attributes,
        byte expectedSelector,
        EnbRenderTextureAddressMode expectedU,
        EnbRenderTextureAddressMode expectedV,
        bool expectedLegacyCapabilityPath)
    {
        EnbW3dTextureSamplingSemantics semantics = EnbW3dTextureSemanticCompiler.Compile(
            new EnbW3dTextureInfo(attributes, 0x1122_3344u, 0x5566_7788u));

        Assert.True(semantics.AuthoredInfoPresent);
        Assert.Equal(attributes, semantics.AuthoredAttributes);
        Assert.Equal(expectedSelector, semantics.RetailCreationSelector);
        Assert.Equal(expectedU, semantics.AddressU);
        Assert.Equal(expectedV, semantics.AddressV);
        Assert.Equal(expectedLegacyCapabilityPath, semantics.LegacyCapabilityFormatRequested);
        Assert.Equal(0x1122_3344u, semantics.AuthoredWord1);
        Assert.Equal(0x5566_7788u, semantics.AuthoredWord2);
    }

    [Fact]
    public void MissingTextureInfoUsesRetailDefaultWrapAndCreationSelectorZero()
    {
        EnbW3dTextureSamplingSemantics semantics = EnbW3dTextureSemanticCompiler.Compile(null);

        Assert.False(semantics.AuthoredInfoPresent);
        Assert.Equal((byte)0, semantics.RetailCreationSelector);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, semantics.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, semantics.AddressV);
        Assert.False(semantics.LegacyCapabilityFormatRequested);
    }

    [Fact]
    public void W3dDecoderPreservesTextureInfoAndUsesLastRepeatedTextureChildren()
    {
        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, MeshHeader()),
            LeafChunk(0x0002, Vector3Payload(Vector3.UnitX)),
            LeafChunk(0x0028, UInt32Payload(0, 0, 0, 1)),
            ContainerChunk(
                0x0030,
                ContainerChunk(
                    0x0031,
                    LeafChunk(0x0032, Encoding.ASCII.GetBytes("FIRST.TGA\0")),
                    LeafChunk(0x0033, UInt32Payload(0x0000_0008u, 1, 2)),
                    LeafChunk(0x0032, Encoding.ASCII.GetBytes("FINAL.TGA\0")),
                    LeafChunk(0x0033, UInt32Payload(0x0000_00D0u, 0xAABB_CCDDu, 0xEEFF_0011u)))));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dMesh mesh = Assert.Single(model.Meshes);
        Assert.Equal(["FINAL.TGA"], mesh.TextureNames);
        EnbW3dTextureInfo? authoredInfo = Assert.Single(mesh.TextureInfos);
        Assert.True(authoredInfo.HasValue);
        EnbW3dTextureInfo info = authoredInfo.Value;
        Assert.Equal(0x0000_00D0u, info.Attributes);
        Assert.Equal(0xAABB_CCDDu, info.Word1);
        Assert.Equal(0xEEFF_0011u, info.Word2);
    }


    [Fact]
    public void RenderResourceCarriesDecodedTextureSamplingSemanticsIntoBindings()
    {
        byte[] shader =
        [
            0x03, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02,
        ];
        byte[] vertices = Vector3Payload(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        byte[] normals = Vector3Payload(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, MeshHeader(vertexCount: 3, triangleCount: 1)),
            LeafChunk(0x0002, vertices),
            LeafChunk(0x0003, normals),
            LeafChunk(0x0020, TrianglePayload()),
            LeafChunk(0x0028, UInt32Payload(1, 1, 1, 1)),
            LeafChunk(0x0029, shader),
            ContainerChunk(0x002A, VertexMaterial()),
            ContainerChunk(
                0x0030,
                ContainerChunk(
                    0x0031,
                    LeafChunk(0x0032, Encoding.ASCII.GetBytes("BOUND.TGA\0")),
                    LeafChunk(0x0033, UInt32Payload(0x0000_0058u, 0x0102_0304u, 0x0506_0708u)))),
            ContainerChunk(
                0x0038,
                LeafChunk(0x0039, UInt32Payload(0)),
                LeafChunk(0x003A, UInt32Payload(0)),
                ContainerChunk(0x0048, LeafChunk(0x0049, UInt32Payload(0)))));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        string archivePath = Path.Combine(Path.GetTempPath(), "r4h_art.mix");
        var source = new EnbResolvedW3dResource(
            "R4H.W3D",
            "R4H_ROOT",
            new EnbMixResourceLocation("R4H.W3D", archivePath, 1024, bytes.Length),
            model,
            [new EnbResolvedTextureReference(
                "BOUND.TGA",
                new EnbMixResourceLocation("BOUND.DDS", archivePath, 4096, 128))]);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh renderMesh = Assert.Single(resource.Meshes);
        EnbW3dRenderMaterialPass renderPass = Assert.Single(renderMesh.MaterialPasses);
        EnbW3dRenderTextureStage renderStage = Assert.Single(renderPass.TextureStages);
        EnbW3dRenderTextureBinding binding = Assert.Single(renderStage.Textures);
        Assert.Equal((byte)2, binding.Sampling.RetailCreationSelector);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, binding.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, binding.Sampling.AddressV);
        Assert.Equal(0x0102_0304u, binding.Sampling.AuthoredWord1);
        Assert.Equal(0x0506_0708u, binding.Sampling.AuthoredWord2);
    }

    [Fact]
    public void W3dDecoderRejectsTextureInfoShorterThanRetailTwelveByteRead()
    {
        byte[] bytes = ContainerChunk(
            0x0000,
            LeafChunk(0x001F, MeshHeader()),
            LeafChunk(0x0002, Vector3Payload(Vector3.UnitX)),
            LeafChunk(0x0028, UInt32Payload(0, 0, 0, 1)),
            ContainerChunk(
                0x0030,
                ContainerChunk(
                    0x0031,
                    LeafChunk(0x0032, Encoding.ASCII.GetBytes("BAD.TGA\0")),
                    LeafChunk(0x0033, new byte[8]))));

        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    private static byte[] MeshHeader(uint vertexCount = 1, uint triangleCount = 0)
    {
        byte[] bytes = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(bytes.AsSpan(8, 16), "TEXTURE_TEST");
        WriteFixedAscii(bytes.AsSpan(24, 16), string.Empty);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), triangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44, 4), vertexCount);
        WriteSingle(bytes.AsSpan(112, 4), 1.0f);
        return bytes;
    }

    private static byte[] Vector3Payload(params Vector3[] values)
    {
        byte[] bytes = new byte[values.Length * 12];
        for (int index = 0; index < values.Length; index++)
        {
            int offset = index * 12;
            WriteSingle(bytes.AsSpan(offset, 4), values[index].X);
            WriteSingle(bytes.AsSpan(offset + 4, 4), values[index].Y);
            WriteSingle(bytes.AsSpan(offset + 8, 4), values[index].Z);
        }
        return bytes;
    }

    private static byte[] TrianglePayload()
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 2);
        WriteSingle(bytes.AsSpan(24, 4), 1.0f);
        return bytes;
    }

    private static byte[] VertexMaterial()
    {
        byte[] info = new byte[32];
        new byte[] { 255, 255, 255, 0 }.CopyTo(info, 4);
        new byte[] { 255, 255, 255, 0 }.CopyTo(info, 8);
        WriteSingle(info.AsSpan(24, 4), 1.0f);
        return ContainerChunk(
            0x002B,
            LeafChunk(0x002C, Encoding.ASCII.GetBytes("MAT0\0")),
            LeafChunk(0x002D, info));
    }

    private static byte[] UInt32Payload(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
        return bytes;
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

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(), destination);
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));
}
