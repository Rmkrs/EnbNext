using System.Buffers.Binary;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4EW3dTextureCombinerTests
{
    [Fact]
    public void R4EOperationAndArgumentValuesMatchCurrentRetailD3DFormatterNames()
    {
        Assert.Equal(1, (byte)EnbW3dTextureOperation.Disabled);
        Assert.Equal(2, (byte)EnbW3dTextureOperation.SelectArgument1);
        Assert.Equal(3, (byte)EnbW3dTextureOperation.SelectArgument2);
        Assert.Equal(4, (byte)EnbW3dTextureOperation.Modulate);
        Assert.Equal(5, (byte)EnbW3dTextureOperation.Modulate2X);
        Assert.Equal(7, (byte)EnbW3dTextureOperation.Add);
        Assert.Equal(8, (byte)EnbW3dTextureOperation.AddSigned);
        Assert.Equal(9, (byte)EnbW3dTextureOperation.AddSigned2X);
        Assert.Equal(10, (byte)EnbW3dTextureOperation.Subtract);
        Assert.Equal(11, (byte)EnbW3dTextureOperation.AddSmooth);
        Assert.Equal(13, (byte)EnbW3dTextureOperation.BlendTextureAlpha);
        Assert.Equal(16, (byte)EnbW3dTextureOperation.BlendCurrentAlpha);
        Assert.Equal(18, (byte)EnbW3dTextureOperation.ModulateAlphaAddColor);
        Assert.Equal(22, (byte)EnbW3dTextureOperation.BumpEnvironmentMap);
        Assert.Equal(23, (byte)EnbW3dTextureOperation.BumpEnvironmentMapLuminance);

        Assert.Equal(0, (byte)EnbW3dTextureArgument.Diffuse);
        Assert.Equal(1, (byte)EnbW3dTextureArgument.Current);
        Assert.Equal(2, (byte)EnbW3dTextureArgument.Texture);
    }

    [Theory]
    [InlineData(0, EnbW3dTextureOperation.SelectArgument1, EnbW3dTextureOperation.SelectArgument1)]
    [InlineData(1, EnbW3dTextureOperation.Modulate, EnbW3dTextureOperation.Modulate)]
    [InlineData(2, EnbW3dTextureOperation.Add, EnbW3dTextureOperation.Modulate)]
    [InlineData(3, EnbW3dTextureOperation.BumpEnvironmentMap, EnbW3dTextureOperation.Disabled)]
    [InlineData(4, EnbW3dTextureOperation.BumpEnvironmentMapLuminance, EnbW3dTextureOperation.Disabled)]
    [InlineData(5, EnbW3dTextureOperation.Modulate2X, EnbW3dTextureOperation.Modulate)]
    [InlineData(6, EnbW3dTextureOperation.Modulate, EnbW3dTextureOperation.Modulate)]
    [InlineData(7, EnbW3dTextureOperation.Modulate, EnbW3dTextureOperation.Modulate)]
    public void R4EStage0SelectorsMapToPreferredCurrentRetailOperations(
        byte selector,
        EnbW3dTextureOperation expectedColor,
        EnbW3dTextureOperation expectedAlpha)
    {
        EnbW3dTextureCombinerSemantics semantics = Compile(selector, true, 0, 0);

        Assert.Equal(expectedColor, semantics.Stage0.Color.Operation);
        Assert.Equal(expectedAlpha, semantics.Stage0.Alpha.Operation);
        Assert.Equal(EnbW3dTextureArgument.Texture, semantics.Stage0.Color.Argument1);

        if (selector is 3 or 4)
            Assert.Equal(EnbW3dTextureArgument.Diffuse, semantics.Stage0.Color.Argument2);
    }

    [Fact]
    public void R4EDisabledTextureStatePreservesRetailStage0DistinctionAndDisablesStage1()
    {
        EnbW3dTextureCombinerSemantics selectorZero = Compile(0, false, 12, 3);
        Assert.Equal(EnbW3dTextureOperation.Disabled, selectorZero.Stage0.Color.Operation);
        Assert.Equal(EnbW3dTextureOperation.Disabled, selectorZero.Stage0.Alpha.Operation);
        Assert.Equal(EnbW3dTextureOperation.Disabled, selectorZero.Stage1.Color.Operation);
        Assert.Equal(EnbW3dTextureOperation.Disabled, selectorZero.Stage1.Alpha.Operation);

        EnbW3dTextureCombinerSemantics nonZero = Compile(1, false, 12, 3);
        Assert.Equal(EnbW3dTextureOperation.SelectArgument2, nonZero.Stage0.Color.Operation);
        Assert.Equal(EnbW3dTextureArgument.Diffuse, nonZero.Stage0.Color.Argument2);
        Assert.Equal(EnbW3dTextureOperation.SelectArgument2, nonZero.Stage0.Alpha.Operation);
        Assert.Equal(EnbW3dTextureArgument.Diffuse, nonZero.Stage0.Alpha.Argument2);
        Assert.Equal(EnbW3dTextureOperation.Disabled, nonZero.Stage1.Color.Operation);
    }

    [Theory]
    [InlineData(0, EnbW3dTextureOperation.Disabled)]
    [InlineData(1, EnbW3dTextureOperation.SelectArgument1)]
    [InlineData(2, EnbW3dTextureOperation.Modulate)]
    [InlineData(3, EnbW3dTextureOperation.AddSmooth)]
    [InlineData(4, EnbW3dTextureOperation.Add)]
    [InlineData(5, EnbW3dTextureOperation.Subtract)]
    [InlineData(6, EnbW3dTextureOperation.Subtract)]
    [InlineData(7, EnbW3dTextureOperation.BlendTextureAlpha)]
    [InlineData(8, EnbW3dTextureOperation.BlendCurrentAlpha)]
    [InlineData(9, EnbW3dTextureOperation.AddSigned)]
    [InlineData(10, EnbW3dTextureOperation.AddSigned2X)]
    [InlineData(11, EnbW3dTextureOperation.Modulate2X)]
    [InlineData(12, EnbW3dTextureOperation.ModulateAlphaAddColor)]
    [InlineData(13, EnbW3dTextureOperation.Disabled)]
    [InlineData(14, EnbW3dTextureOperation.Disabled)]
    [InlineData(15, EnbW3dTextureOperation.Disabled)]
    public void R4EStage1ColorSelectorTableIsFiniteAndExact(byte selector, EnbW3dTextureOperation expected)
    {
        EnbW3dTextureCombinerSemantics semantics = Compile(1, true, selector, 0);

        EnbW3dTextureOperation actual = semantics.Stage1.Color.Operation;
        if (selector != 0 && expected != EnbW3dTextureOperation.Disabled)
            Assert.Equal(expected, actual);
        else
            Assert.Equal(EnbW3dTextureOperation.Disabled, actual);

        if (selector == 6)
        {
            Assert.Equal(EnbW3dTextureArgument.Current, semantics.Stage1.Color.Argument1);
            Assert.Equal(EnbW3dTextureArgument.Texture, semantics.Stage1.Color.Argument2);
        }
        else if (selector == 12)
        {
            Assert.Equal(EnbW3dTextureArgument.Current, semantics.Stage1.Color.Argument1);
            Assert.Equal(EnbW3dTextureArgument.Texture, semantics.Stage1.Color.Argument2);
        }
        else if (expected != EnbW3dTextureOperation.Disabled)
        {
            Assert.Equal(EnbW3dTextureArgument.Texture, semantics.Stage1.Color.Argument1);
            Assert.Equal(EnbW3dTextureArgument.Current, semantics.Stage1.Color.Argument2);
        }
    }

    [Theory]
    [InlineData(0, EnbW3dTextureOperation.Disabled)]
    [InlineData(1, EnbW3dTextureOperation.SelectArgument1)]
    [InlineData(2, EnbW3dTextureOperation.Modulate)]
    [InlineData(3, EnbW3dTextureOperation.AddSmooth)]
    [InlineData(4, EnbW3dTextureOperation.Disabled)]
    [InlineData(7, EnbW3dTextureOperation.Disabled)]
    public void R4EStage1AlphaSelectorTableIsFinite(byte selector, EnbW3dTextureOperation expected)
    {
        EnbW3dTextureCombinerSemantics semantics = Compile(1, true, 0, selector);

        Assert.Equal(expected, semantics.Stage1.Alpha.Operation);
        if (expected != EnbW3dTextureOperation.Disabled)
        {
            Assert.Equal(EnbW3dTextureOperation.SelectArgument2, semantics.Stage1.Color.Operation);
            Assert.Equal(EnbW3dTextureArgument.Current, semantics.Stage1.Color.Argument2);
        }
    }

    [Fact]
    public void R4EStage1PassesCurrentThroughForInactiveHalfOfActiveStage()
    {
        EnbW3dTextureCombinerSemantics colorOnly = Compile(1, true, 4, 0);
        Assert.Equal(EnbW3dTextureOperation.Add, colorOnly.Stage1.Color.Operation);
        Assert.Equal(EnbW3dTextureOperation.SelectArgument2, colorOnly.Stage1.Alpha.Operation);
        Assert.Equal(EnbW3dTextureArgument.Current, colorOnly.Stage1.Alpha.Argument2);

        EnbW3dTextureCombinerSemantics alphaOnly = Compile(1, true, 0, 2);
        Assert.Equal(EnbW3dTextureOperation.SelectArgument2, alphaOnly.Stage1.Color.Operation);
        Assert.Equal(EnbW3dTextureArgument.Current, alphaOnly.Stage1.Color.Argument2);
        Assert.Equal(EnbW3dTextureOperation.Modulate, alphaOnly.Stage1.Alpha.Operation);
    }

    [Fact]
    public void R4EBumpEnvironmentDefaultsMatchCurrentRetailDeviceInitialization()
    {
        EnbW3dTextureCombinerSemantics semantics = Compile(3, true, 0, 0);
        Assert.Equal(new System.Numerics.Vector4(1.0f, 0.0f, 0.0f, 1.0f), semantics.BumpEnvironment.Matrix2X2);
        Assert.Equal(1.0f, semantics.BumpEnvironment.LuminanceScale);
        Assert.Equal(0.0f, semantics.BumpEnvironment.LuminanceOffset);
    }

    [Fact]
    public void R4EMarsFixturesCarryActualStageProgramsRatherThanRawSelectorOnlyKeys()
    {
        EnbW3dFixedFunctionState body = Decode(
            0x03, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02);
        Assert.Equal(EnbW3dTextureOperation.Modulate, body.TextureCombinerSemantics.Stage0.Color.Operation);
        Assert.Equal(EnbW3dTextureOperation.Modulate, body.TextureCombinerSemantics.Stage1.Color.Operation);
        Assert.Equal(EnbW3dTextureOperation.SelectArgument2, body.TextureCombinerSemantics.Stage1.Alpha.Operation);

        EnbW3dFixedFunctionState detail = Decode(
            0x03, 0x00, 0x00, 0x02, 0x02, 0x01, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02);
        Assert.Equal(EnbW3dTextureOperation.Modulate, detail.TextureCombinerSemantics.Stage0.Color.Operation);
        Assert.Equal(EnbW3dTextureOperation.Disabled, detail.TextureCombinerSemantics.Stage1.Color.Operation);
    }

    [Fact]
    public void R4ESemanticPermutationCollapsesRetailSelectorAliasesWhileRawSelectorsRemainVisible()
    {
        EnbW3dRenderVertexMaterial material = Material();

        EnbW3dCompiledMaterialSemantics selector1 = CompileMaterial(1, material);
        EnbW3dCompiledMaterialSemantics selector6 = CompileMaterial(6, material);
        EnbW3dCompiledMaterialSemantics selector7 = CompileMaterial(7, material);

        Assert.Equal((byte)1, selector1.FixedFunction.TextureCombiners.Selector0);
        Assert.Equal((byte)6, selector6.FixedFunction.TextureCombiners.Selector0);
        Assert.Equal((byte)7, selector7.FixedFunction.TextureCombiners.Selector0);

        Assert.Equal(selector1.FixedFunction.TextureCombinerSemantics, selector6.FixedFunction.TextureCombinerSemantics);
        Assert.Equal(selector1.FixedFunction.TextureCombinerSemantics, selector7.FixedFunction.TextureCombinerSemantics);
        Assert.Equal(selector1.PermutationKey, selector6.PermutationKey);
        Assert.Equal(selector1.PermutationKey, selector7.PermutationKey);
    }

    [Fact]
    public void R4ERemainsBackendNeutral()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dMaterialSemantics.cs"));

        Assert.Contains("CompileTextureCombinerSemantics", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.DirectX", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SharpDX", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Veldrid", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silk.NET", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", source, StringComparison.OrdinalIgnoreCase);
    }


    private static EnbW3dCompiledMaterialSemantics CompileMaterial(byte selector0, EnbW3dRenderVertexMaterial material)
    {
        EnbW3dRenderShaderRecord shader = Shader(
            0x03, 0x01, 0x00, 0x00, 0x02, selector0, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02, 0x00, 0x02, 0x00, 0x02);
        Assert.True(RetailTestShims.TryCompileMaterial(shader, material, 2, out EnbW3dCompiledMaterialSemantics compiled, out string? diagnostic), diagnostic);
        return compiled;
    }

    private static EnbW3dRenderVertexMaterial Material()
        => new(
            0,
            "R4E",
            0,
            new EnbRenderPackedRgb8(1, 2, 3, 0),
            new EnbRenderPackedRgb8(4, 5, 6, 0),
            new EnbRenderPackedRgb8(7, 8, 9, 0),
            new EnbRenderPackedRgb8(10, 11, 12, 0),
            1.0f,
            1.0f,
            0.0f,
            null,
            null);

    private static EnbW3dTextureCombinerSemantics Compile(byte selector0, bool enabled, byte selector1, byte selector2)
        => EnbW3dMaterialCompiler.CompileTextureCombinerSemantics(
            new EnbW3dTextureCombinerSelectors(selector0, enabled, selector1, selector2));

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
