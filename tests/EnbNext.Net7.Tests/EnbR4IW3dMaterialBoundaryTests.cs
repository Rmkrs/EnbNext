using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4IW3dMaterialBoundaryTests
{
    [Fact]
    public void AuthoredOpacityCompilesAsDiffuseAlpha()
    {
        var material = new EnbW3dRenderVertexMaterial(
            0,
            "MATERIAL",
            0,
            new EnbRenderPackedRgb8(255, 255, 255, 0),
            new EnbRenderPackedRgb8(64, 128, 192, 0),
            new EnbRenderPackedRgb8(0, 0, 0, 0),
            new EnbRenderPackedRgb8(0, 0, 0, 0),
            0f,
            0.375f,
            0f,
            null,
            null);

        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(BaseShader()),
            material,
            1,
            out EnbW3dCompiledMaterialSemantics compiled,
            out string? diagnostic), diagnostic);

        Assert.Equal(0.375f, compiled.Uniforms.DiffuseAlpha, 6);
        Assert.Equal(64f / 255f, compiled.Uniforms.DiffuseRgba.X, 6);
        Assert.Equal(128f / 255f, compiled.Uniforms.DiffuseRgba.Y, 6);
        Assert.Equal(192f / 255f, compiled.Uniforms.DiffuseRgba.Z, 6);
        Assert.Equal(0.375f, compiled.Uniforms.DiffuseRgba.W, 6);
    }

    [Theory]
    [InlineData((byte)0x08)]
    [InlineData((byte)0x10)]
    [InlineData((byte)0x18)]
    public void CompactW3dShaderCannotAuthorGenericFogState(byte authoredFogSourceBits)
    {
        byte[] baselineBytes = BaseShader();
        byte[] authoredFogBytes = (byte[])baselineBytes.Clone();
        authoredFogBytes[3] = authoredFogSourceBits;

        Assert.True(EnbW3dMaterialCompiler.TryDecodeShader(
            Shader(baselineBytes),
            out EnbW3dFixedFunctionState baseline,
            out string? baselineDiagnostic), baselineDiagnostic);
        Assert.True(EnbW3dMaterialCompiler.TryDecodeShader(
            Shader(authoredFogBytes),
            out EnbW3dFixedFunctionState authoredFog,
            out string? authoredFogDiagnostic), authoredFogDiagnostic);

        Assert.Equal(baseline, authoredFog);
        Assert.Equal(0u, authoredFog.PackedRetailState & 0x0000_0300u);
    }

    [Fact]
    public void NPatchRequestComesFromAuthoredMeshAttributeAndRuntimeEligibilityStaysExplicit()
    {
        Assert.Equal(0x2000_0000u, EnbW3dNPatchSemantics.AuthoredRequestAttributeMask);
        Assert.Equal(0x0001_0000u, EnbW3dNPatchSemantics.CurrentRetailRuntimeRequestFlagMask);
        Assert.False(EnbW3dNPatchSemantics.FromAuthoredAttributes(0).Requested);

        EnbW3dNPatchSemantics request =
            EnbW3dNPatchSemantics.FromAuthoredAttributes(0x2000_0000u);
        Assert.True(request.Requested);

        var mesh = new EnbW3dRenderMesh(
            0,
            "NPATCH",
            "ROOT",
            1f,
            Array.Empty<Vector3>(),
            Array.Empty<Vector3>(),
            Array.Empty<ushort>(),
            Array.Empty<uint>(),
            Array.Empty<EnbW3dRenderTriangleMetadata>(),
            Array.Empty<EnbW3dRenderVertexMaterial>(),
            Array.Empty<EnbW3dRenderMaterialPass>())
        {
            Attributes = 0x2000_0000u,
        };

        Assert.True(mesh.NPatch.Requested);
    }

    private static byte[] BaseShader() =>
    [
        0x03, 0x01, 0x00, 0x00,
        0x02, 0x01, 0x00, 0x01,
        0x01, 0x02, 0x00, 0x02,
        0x00, 0x02, 0x00, 0x02,
    ];

    private static EnbW3dRenderShaderRecord Shader(byte[] raw)
    {
        Assert.Equal(16, raw.Length);
        return new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4)));
    }
}
