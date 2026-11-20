using System.Buffers.Binary;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4KVertexMaterialMapperSelectorTests
{
    [Fact]
    public void R4KCurrentRetailSelectorInventoryLeavesMode05AndOutOfRangeValuesUninstantiated()
    {
        Assert.False(EnbW3dMapperTypeSelector.None.IsInstantiatedByCurrentRetail());
        Assert.True(EnbW3dMapperTypeSelector.Mode01.IsInstantiatedByCurrentRetail());
        Assert.True(EnbW3dMapperTypeSelector.Mode04.IsInstantiatedByCurrentRetail());
        Assert.False(EnbW3dMapperTypeSelector.Reserved05.IsInstantiatedByCurrentRetail());
        Assert.True(EnbW3dMapperTypeSelector.Mode06.IsInstantiatedByCurrentRetail());
        Assert.True(EnbW3dMapperTypeSelector.Mode14.IsInstantiatedByCurrentRetail());
        Assert.False(((EnbW3dMapperTypeSelector)0x15).IsInstantiatedByCurrentRetail());
        Assert.False(((EnbW3dMapperTypeSelector)0xFF).IsInstantiatedByCurrentRetail());
    }

    [Fact]
    public void R4KNonLinearMapperSelectorIsPartOfTheFiniteKeyAndIsNotCollapsedIntoLinearScroll()
    {
        EnbW3dRenderVertexMaterial material = Material(new EnbW3dRenderMapper(
            EnbW3dMapperTypeSelector.Mode08,
            "UScale=6\r\nVScale=2\r\nUPerSec=.5",
            6.0f,
            2.0f,
            0.5f,
            -0.25f,
            0.0f,
            0.0f,
            false));

        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(),
            material,
            1,
            out EnbW3dCompiledMaterialSemantics compiled,
            out string? diagnostic), diagnostic);

        Assert.True(compiled.PermutationKey.Mapper0.Enabled);
        Assert.Equal(EnbW3dMapperTypeSelector.Mode08, compiled.PermutationKey.Mapper0.TypeSelector);
        Assert.Equal(EnbW3dMapperRuntimeProgramKind.CenteredRotation, compiled.Mapper0Program.Kind);
    }

    [Fact]
    public void R4KStaticScaleSelectorCarriesOnlyItsProvenBaseScale()
    {
        EnbW3dRenderVertexMaterial material = Material(new EnbW3dRenderMapper(
            EnbW3dMapperTypeSelector.Mode06,
            "UScale=3\r\nVScale=4",
            3.0f,
            4.0f,
            9.0f,
            10.0f,
            11.0f,
            12.0f,
            true));

        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(),
            material,
            1,
            out EnbW3dCompiledMaterialSemantics compiled,
            out string? diagnostic), diagnostic);

        Assert.Equal(EnbW3dMapperTypeSelector.Mode06, compiled.PermutationKey.Mapper0.TypeSelector);
        Assert.Equal(new System.Numerics.Vector2(3.0f, 4.0f), compiled.Uniforms.Mapper0.Scale);
        Assert.Equal(System.Numerics.Vector2.Zero, compiled.Uniforms.Mapper0.ScrollPerMillisecond);
        Assert.Equal(System.Numerics.Vector2.Zero, compiled.Uniforms.Mapper0.Offset);
        Assert.False(compiled.Uniforms.Mapper0.ClampFix);
        Assert.True(compiled.Uniforms.Mapper0.HasClosedBaseParameters);
    }

    private static EnbW3dRenderVertexMaterial Material(EnbW3dRenderMapper mapper)
        => new(
            Ordinal: 0,
            Name: "R4K",
            Attributes: 0,
            Ambient: new EnbRenderPackedRgb8(255, 255, 255, 0),
            Diffuse: new EnbRenderPackedRgb8(255, 255, 255, 0),
            Specular: new EnbRenderPackedRgb8(0, 0, 0, 0),
            Emissive: new EnbRenderPackedRgb8(0, 0, 0, 0),
            Power: 1.0f,
            Opacity: 1.0f,
            UnknownFloat: 0.0f,
            Mapper0: mapper,
            Mapper1: null);

    private static EnbW3dRenderShaderRecord Shader()
    {
        byte[] raw =
        [
            0x03, 0x01, 0x00, 0x00,
            0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x02,
        ];

        return new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4)));
    }
}
