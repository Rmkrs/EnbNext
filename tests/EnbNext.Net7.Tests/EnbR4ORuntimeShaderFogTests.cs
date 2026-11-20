using System.Buffers.Binary;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4ORuntimeShaderFogTests
{
    [Theory]
    [InlineData(0, 2, EnbW3dFogMode.White)]
    [InlineData(1, 0, EnbW3dFogMode.SceneColor)]
    [InlineData(1, 1, EnbW3dFogMode.Black)]
    [InlineData(1, 3, EnbW3dFogMode.Black)]
    [InlineData(2, 5, EnbW3dFogMode.SceneColor)]
    [InlineData(3, 4, EnbW3dFogMode.SceneColor)]
    public void GeneralBlendToFogRewriteMatchesCurrentRetail(
        int sourceSelector,
        int destinationSelector,
        EnbW3dFogMode expectedFog)
    {
        uint authored = ReplaceBlend(EnbW3dMaterialCompiler.RetailW3dShaderBaseline, sourceSelector, destinationSelector);
        uint optimized = EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(authored);

        EnbW3dFixedFunctionState state = Decode(optimized);
        Assert.Equal(expectedFog, state.FogMode);
        Assert.Equal(
            EnbW3dRetailSortCompiler.ClassifyPackedShader(authored),
            EnbW3dRetailSortCompiler.ClassifyPackedShader(optimized));
    }

    [Fact]
    public void GeneralRewriteLeavesUnmatchedBlendPairAndExistingFogUntouched()
    {
        uint state = ReplaceBlend(EnbW3dMaterialCompiler.RetailW3dShaderBaseline, sourceSelector: 0, destinationSelector: 0)
            | 0x0000_0200u;

        Assert.Equal(state, EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(state));
    }

    [Fact]
    public void FirstUniformTwoPassBranchRewritesShadersAndSurfacesResourceHandoff()
    {
        uint pass0 = EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
        uint pass1 = ReplaceBlend(pass0, sourceSelector: 2, destinationSelector: 2) | 0x0001_0000u;

        EnbW3dRetailTwoPassShaderOptimization optimized =
            EnbW3dRetailShaderOptimizer.OptimizeUniformTwoPass(pass0, pass1);

        Assert.Equal(EnbW3dRetailShaderOptimizationKind.TwoPassTextureHandoff, optimized.Kind);
        Assert.True(optimized.CopyPass1Stage0TextureToPass0);
        Assert.True(optimized.CopyPass1Stage0TextureCoordinateIndexToPass0);
        Assert.NotEqual(0u, optimized.Pass0PackedState & 0x0001_0000u);
        Assert.Equal(0x0000_0020u, optimized.Pass1PackedState & 0x0000_00E0u);
        Assert.Equal(EnbW3dFogMode.SceneColor, Decode(optimized.Pass0PackedState).FogMode);
        Assert.Equal(EnbW3dFogMode.Black, Decode(optimized.Pass1PackedState).FogMode);
        Assert.Equal(
            EnbW3dRetailSortCompiler.ClassifyPackedShader(pass0),
            EnbW3dRetailSortCompiler.ClassifyPackedShader(optimized.Pass0PackedState));
        Assert.Equal(
            EnbW3dRetailSortCompiler.ClassifyPackedShader(pass1),
            EnbW3dRetailSortCompiler.ClassifyPackedShader(optimized.Pass1PackedState));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void SecondUniformTwoPassBranchAssignsBlackThenSceneFogWithoutResourceTransfer(int pass1DestinationSelector)
    {
        uint pass0 = EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
        uint pass1 = ReplaceBlend(pass0, sourceSelector: 1, destinationSelector: pass1DestinationSelector);

        EnbW3dRetailTwoPassShaderOptimization optimized =
            EnbW3dRetailShaderOptimizer.OptimizeUniformTwoPass(pass0, pass1);

        Assert.Equal(EnbW3dRetailShaderOptimizationKind.TwoPassFogPair, optimized.Kind);
        Assert.False(optimized.CopyPass1Stage0TextureToPass0);
        Assert.False(optimized.CopyPass1Stage0TextureCoordinateIndexToPass0);
        Assert.Equal(EnbW3dFogMode.Black, Decode(optimized.Pass0PackedState).FogMode);
        Assert.Equal(EnbW3dFogMode.SceneColor, Decode(optimized.Pass1PackedState).FogMode);
    }

    [Fact]
    public void NonSpecialUniformPairFallsBackToGeneralPerShaderRewrite()
    {
        uint pass0 = ReplaceBlend(EnbW3dMaterialCompiler.RetailW3dShaderBaseline, sourceSelector: 1, destinationSelector: 1);
        uint pass1 = ReplaceBlend(EnbW3dMaterialCompiler.RetailW3dShaderBaseline, sourceSelector: 0, destinationSelector: 2);

        EnbW3dRetailTwoPassShaderOptimization optimized =
            EnbW3dRetailShaderOptimizer.OptimizeUniformTwoPass(pass0, pass1);

        Assert.Equal(EnbW3dRetailShaderOptimizationKind.GeneralPerShader, optimized.Kind);
        Assert.Equal(EnbW3dFogMode.Black, Decode(optimized.Pass0PackedState).FogMode);
        Assert.Equal(EnbW3dFogMode.White, Decode(optimized.Pass1PackedState).FogMode);
        Assert.False(optimized.CopyPass1Stage0TextureToPass0);
        Assert.False(optimized.CopyPass1Stage0TextureCoordinateIndexToPass0);
    }

    [Fact]
    public void FogApplicatorCombinesMaterialModeWithSceneAndCapabilityState()
    {
        var scene = new EnbW3dSceneFogState(
            Enabled: true,
            SceneColorRgb24: 0x12_34_56u,
            Start: 125.5f,
            End: 9876.25f);

        EnbW3dFogPipelineSemantics pipeline =
            EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(false);
        EnbW3dResolvedFogState sceneColor = EnbW3dResolvedFogState.ResolveCurrentRetail(
            EnbW3dFogMode.SceneColor,
            scene,
            rendererSupportsFog: true,
            pipeline: pipeline);
        Assert.True(sceneColor.Enabled);
        Assert.Equal(0x12_34_56u, sceneColor.ColorRgb24);
        Assert.Equal(scene.Start, sceneColor.Start);
        Assert.Equal(scene.End, sceneColor.End);

        Assert.Equal(0u, EnbW3dResolvedFogState.ResolveCurrentRetail(
            EnbW3dFogMode.Black, scene, rendererSupportsFog: true, pipeline: pipeline).ColorRgb24);
        Assert.Equal(0xFF_FF_FFu, EnbW3dResolvedFogState.ResolveCurrentRetail(
            EnbW3dFogMode.White, scene, rendererSupportsFog: true, pipeline: pipeline).ColorRgb24);

        Assert.False(EnbW3dResolvedFogState.ResolveCurrentRetail(
            EnbW3dFogMode.SceneColor, scene with { Enabled = false }, rendererSupportsFog: true, pipeline: pipeline).Enabled);
        Assert.False(EnbW3dResolvedFogState.ResolveCurrentRetail(
            EnbW3dFogMode.SceneColor, scene, rendererSupportsFog: false, pipeline: pipeline).Enabled);
        Assert.False(EnbW3dResolvedFogState.ResolveCurrentRetail(
            EnbW3dFogMode.Disabled, scene, rendererSupportsFog: true, pipeline: pipeline).Enabled);
    }

    [Fact]
    public void CompactAuthoredShaderStillCannotDirectlyAuthorFogBits()
    {
        byte[] raw =
        [
            0x03, 0x01, 0x00, 0x18,
            0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x02,
        ];

        Assert.True(EnbW3dMaterialCompiler.TryDecodeShader(
            Shader(raw),
            out EnbW3dFixedFunctionState state,
            out string? diagnostic), diagnostic);
        Assert.Equal(EnbW3dFogMode.Disabled, state.FogMode);
    }

    [Fact]
    public void FogModeParticipatesInFiniteShaderPermutationKey()
    {
        uint packed = EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(
            ReplaceBlend(EnbW3dMaterialCompiler.RetailW3dShaderBaseline, sourceSelector: 1, destinationSelector: 0));
        EnbW3dRenderVertexMaterial material = new(
            0,
            "FOG",
            0,
            new EnbRenderPackedRgb8(255, 255, 255, 0),
            new EnbRenderPackedRgb8(255, 255, 255, 0),
            new EnbRenderPackedRgb8(0, 0, 0, 0),
            new EnbRenderPackedRgb8(0, 0, 0, 0),
            1f,
            1f,
            0f,
            null,
            null);

        Assert.True(EnbW3dMaterialCompiler.TryCompilePackedRetailState(
            packed,
            material,
            textureStageCount: 0,
            out EnbW3dCompiledMaterialSemantics semantics,
            out string? diagnostic), diagnostic);

        Assert.Equal(EnbW3dFogMode.SceneColor, semantics.FixedFunction.FogMode);
        Assert.Equal(EnbW3dFogMode.SceneColor, semantics.PermutationKey.FogMode);
    }

    private static uint ReplaceBlend(uint packedRetailState, int sourceSelector, int destinationSelector)
    {
        uint packed = packedRetailState & ~(0x0000_C000u | 0x0000_00E0u | 0x0000_0300u);
        packed |= (uint)sourceSelector << 14;
        packed |= (uint)destinationSelector << 5;
        return packed;
    }

    private static EnbW3dFixedFunctionState Decode(uint packedRetailState)
    {
        Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
            packedRetailState,
            out EnbW3dFixedFunctionState state,
            out string? diagnostic), diagnostic);
        return state;
    }

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
