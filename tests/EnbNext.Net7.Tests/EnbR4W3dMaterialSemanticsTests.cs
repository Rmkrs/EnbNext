using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4W3dMaterialSemanticsTests
{
    [Fact]
    public void R4BRetailShaderTranslationMatchesProvenMarsBodyAtmosphereAndDetailStates()
    {
        EnbW3dFixedFunctionState body = Decode(
            0x03, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02);
        Assert.Equal(0x0029_441Bu, body.PackedRetailState);
        Assert.Equal(EnbRenderDepthCompare.LessOrEqual, body.DepthCompare);
        Assert.True(body.DepthWriteEnabled);
        Assert.Equal(EnbRenderCullMode.Clockwise, body.CullMode);
        Assert.Equal(EnbRenderBlendFactor.One, body.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.Zero, body.DestinationBlend);
        Assert.False(body.BlendEnabled);
        Assert.False(body.AlphaTestEnabled);

        EnbW3dFixedFunctionState atmosphere = Decode(
            0x03, 0x00, 0x00, 0x03, 0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02);
        Assert.Equal(0x0029_4473u, atmosphere.PackedRetailState);
        Assert.Equal(EnbRenderDepthCompare.LessOrEqual, atmosphere.DepthCompare);
        Assert.False(atmosphere.DepthWriteEnabled);
        Assert.Equal(EnbRenderCullMode.Clockwise, atmosphere.CullMode);
        Assert.Equal(EnbRenderBlendFactor.One, atmosphere.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.InverseSourceColor, atmosphere.DestinationBlend);
        Assert.True(atmosphere.BlendEnabled);

        EnbW3dFixedFunctionState detail = Decode(
            0x03, 0x00, 0x00, 0x02, 0x02, 0x01, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02);
        Assert.Equal(0x0009_0453u, detail.PackedRetailState);
        Assert.Equal(EnbRenderDepthCompare.LessOrEqual, detail.DepthCompare);
        Assert.False(detail.DepthWriteEnabled);
        Assert.Equal(EnbRenderCullMode.Clockwise, detail.CullMode);
        Assert.Equal(EnbRenderBlendFactor.Zero, detail.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.SourceColor, detail.DestinationBlend);
        Assert.True(detail.BlendEnabled);
    }

    [Fact]
    public void R4BShaderCompilerUsesRetailBaselineAndRejectsUninitializedDestinationBlendSelectors()
    {
        Assert.Equal(0x0008_441Bu, EnbW3dMaterialCompiler.RetailW3dShaderBaseline);

        EnbW3dRenderShaderRecord unsupported = Shader(
            0x03, 0x01, 0x00, 0x06, 0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02);

        Assert.False(EnbW3dMaterialCompiler.TryDecodeShader(unsupported, out _, out string? diagnostic));
        string actual = diagnostic ?? throw new Xunit.Sdk.XunitException("Expected an invalid selector diagnostic.");
        Assert.Contains("destination-blend selector 6", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void R4BMaterialUniformsNormalizeRetailColorsAndPreserveMapperMillisecondRates()
    {
        var material = new EnbW3dRenderVertexMaterial(
            Ordinal: 0,
            Name: "MATERIAL",
            Attributes: 0,
            Ambient: new EnbRenderPackedRgb8(255, 128, 0, 0),
            Diffuse: new EnbRenderPackedRgb8(64, 32, 16, 0),
            Specular: new EnbRenderPackedRgb8(132, 56, 0, 0),
            Emissive: new EnbRenderPackedRgb8(1, 2, 3, 0),
            Power: 4.5f,
            Opacity: 0.75f,
            UnknownFloat: 0.25f,
            Mapper0: new EnbW3dRenderMapper(
                EnbW3dMapperTypeSelector.Mode04,
                "UPerSec=.006",
                6.0f,
                2.0f,
                0.006f,
                -0.1f,
                0.25f,
                -0.5f,
                true),
            Mapper1: null);

        EnbW3dRenderShaderRecord shader = Shader(
            0x03, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02);

        Assert.True(RetailTestShims.TryCompileMaterial(shader, material, 2, out EnbW3dCompiledMaterialSemantics compiled, out string? diagnostic), diagnostic);
        AssertVector(new Vector3(1.0f, 128.0f / 255.0f, 0.0f), compiled.Uniforms.Ambient);
        AssertVector(new Vector3(64.0f / 255.0f, 32.0f / 255.0f, 16.0f / 255.0f), compiled.Uniforms.Diffuse);
        AssertVector(new Vector3(132.0f / 255.0f, 56.0f / 255.0f, 0.0f), compiled.Uniforms.Specular);
        Assert.Equal(4.5f, compiled.Uniforms.Power, 4);
        Assert.Equal(0.75f, compiled.Uniforms.DiffuseAlpha, 4);
        Assert.Equal(new Vector2(6.0f, 2.0f), compiled.Uniforms.Mapper0.Scale);
        Assert.InRange(MathF.Abs(compiled.Uniforms.Mapper0.ScrollPerMillisecond.X - (0.006f * -0.001f)), 0f, 1e-9f);
        Assert.InRange(MathF.Abs(compiled.Uniforms.Mapper0.ScrollPerMillisecond.Y - (-0.1f * -0.001f)), 0f, 1e-9f);
        Assert.Equal(new Vector2(0.25f, -0.5f), compiled.Uniforms.Mapper0.Offset);
        Assert.True(compiled.Uniforms.Mapper0.ClampFix);
        Assert.True(compiled.Uniforms.Mapper0.HasClosedBaseParameters);
        Assert.Equal(EnbW3dMapperUniforms.Identity, compiled.Uniforms.Mapper1);
        Assert.True(compiled.PermutationKey.Mapper0.Enabled);
        Assert.Equal(EnbW3dMapperTypeSelector.Mode04, compiled.PermutationKey.Mapper0.TypeSelector);
        Assert.False(compiled.PermutationKey.Mapper1.Enabled);
    }

    [Fact]
    public void R4BPermutationKeySeparatesRetailBodyAtmosphereAndDetailPipelinesWithoutMaterialValueExplosion()
    {
        var materialA = Material(power: 1.0f, opacity: 1.0f);
        var materialB = Material(power: 99.0f, opacity: 0.25f);

        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(0x03, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x01, 0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02),
            materialA,
            2,
            out EnbW3dCompiledMaterialSemantics bodyA,
            out _));
        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(0x03, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x01, 0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02),
            materialB,
            2,
            out EnbW3dCompiledMaterialSemantics bodyB,
            out _));
        Assert.Equal(bodyA.PermutationKey, bodyB.PermutationKey);
        Assert.NotEqual(bodyA.Uniforms.Power, bodyB.Uniforms.Power);

        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(0x03, 0x00, 0x00, 0x03, 0x02, 0x01, 0x00, 0x01, 0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02),
            materialA,
            2,
            out EnbW3dCompiledMaterialSemantics atmosphere,
            out _));
        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(0x03, 0x00, 0x00, 0x02, 0x02, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02),
            materialA,
            1,
            out EnbW3dCompiledMaterialSemantics detail,
            out _));

        Assert.NotEqual(bodyA.PermutationKey, atmosphere.PermutationKey);
        Assert.NotEqual(bodyA.PermutationKey, detail.PermutationKey);
        Assert.NotEqual(atmosphere.PermutationKey, detail.PermutationKey);
    }

    [Fact]
    public void R4BMaterialCompilerRemainsBackendIndependentAndUsesFiniteSelectors()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dMaterialSemantics.cs"));

        Assert.Contains("RetailW3dShaderBaseline", source, StringComparison.Ordinal);
        Assert.Contains("EnbW3dShaderPermutationKey", source, StringComparison.Ordinal);
        Assert.Contains("DestinationBlendSelectorTable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Veldrid", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnbNext.Net7", source, StringComparison.OrdinalIgnoreCase);
    }

    private static EnbW3dFixedFunctionState Decode(params byte[] raw)
    {
        Assert.True(EnbW3dMaterialCompiler.TryDecodeShader(Shader(raw), out EnbW3dFixedFunctionState state, out string? diagnostic), diagnostic);
        return state;
    }

    private static EnbW3dRenderShaderRecord Shader(params byte[] raw)
    {
        if (raw.Length != 16)
            throw new Xunit.Sdk.XunitException($"Expected exactly 16 shader bytes, got {raw.Length}.");
        return new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4)));
    }

    private static EnbW3dRenderVertexMaterial Material(float power, float opacity)
        => new(
            0,
            "M",
            0,
            new EnbRenderPackedRgb8(1, 2, 3, 0),
            new EnbRenderPackedRgb8(4, 5, 6, 0),
            new EnbRenderPackedRgb8(7, 8, 9, 0),
            new EnbRenderPackedRgb8(10, 11, 12, 0),
            power,
            opacity,
            0,
            null,
            null);

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate EnbNext repository root.");
    }
}
