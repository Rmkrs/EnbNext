using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Backend-independent comparison values matching the observable ordering of
/// the retail D3D8 depth-comparison contract. Numeric values intentionally match
/// the current retail values used by FUN_0093b510.
/// </summary>
public enum EnbRenderDepthCompare : byte
{
    Never = 1,
    Less = 2,
    Equal = 3,
    LessOrEqual = 4,
    Greater = 5,
    NotEqual = 6,
    GreaterOrEqual = 7,
    Always = 8,
}

/// <summary>
/// Blend factors reachable through the current retail W3D shader selector
/// tables. Numeric values match the current retail fixed-function values.
/// </summary>
public enum EnbRenderBlendFactor : byte
{
    Zero = 1,
    One = 2,
    SourceColor = 3,
    InverseSourceColor = 4,
    SourceAlpha = 5,
    InverseSourceAlpha = 6,
}

public enum EnbRenderCullMode : byte
{
    None = 1,
    Clockwise = 2,
    CounterClockwise = 3,
}

public enum EnbW3dMaterialColorSource : byte
{
    Material = 0,
    Color1 = 1,
    Color2 = 2,
}

/// <summary>
/// Material fog selector consumed by current retail FUN_0093B510. Authored
/// compact W3D shader records cannot directly produce these bits; current
/// retail derives them in the post-load shader optimizer.
/// </summary>
public enum EnbW3dFogMode : byte
{
    Disabled = 0,
    SceneColor = 1,
    Black = 2,
    White = 3,
}

/// <summary>
/// Frame/scene-owned fog inputs installed by current retail FUN_00965460 before
/// scene traversal. Current retail writes Start/End directly to the legacy
/// FOGSTART/FOGEND render states.
/// </summary>
public readonly record struct EnbW3dSceneFogState(
    bool Enabled,
    uint SceneColorRgb24,
    float Start,
    float End);

/// <summary>
/// Distance source used by current retail's fixed-function linear vertex fog.
/// The renderer selects camera-space Euclidean range only when the device
/// exposes the legacy range-fog capability; otherwise it uses |camera Z|.
/// </summary>
public enum EnbW3dFogDistanceSource : byte
{
    AbsoluteCameraZ = 0,
    CameraRange = 1,
}

/// <summary>
/// Backend capability/state that current retail installs globally for fog.
/// Pixel/table fog is disabled and vertex fog is fixed to LINEAR. The only
/// remaining device-dependent fork is whether range-based vertex fog is used.
/// </summary>
public readonly record struct EnbW3dFogPipelineSemantics(
    EnbW3dFogDistanceSource DistanceSource)
{
    public static EnbW3dFogPipelineSemantics FromCurrentRetailRangeCapability(
        bool supportsRangeBasedVertexFog)
        => new(supportsRangeBasedVertexFog
            ? EnbW3dFogDistanceSource.CameraRange
            : EnbW3dFogDistanceSource.AbsoluteCameraZ);
}

/// <summary>
/// Effective fog state after combining material mode, scene state and renderer
/// capability at the boundary current retail FUN_0093B510 owns. This also closes
/// the fixed-function distance/equation contract installed by FUN_00921880,
/// FUN_00921EF0 and FUN_00923570: table fog NONE, vertex fog LINEAR, and optional
/// range-based distance.
/// </summary>
public readonly record struct EnbW3dResolvedFogState(
    bool Enabled,
    uint ColorRgb24,
    float Start,
    float End,
    EnbW3dFogDistanceSource DistanceSource)
{
    public static EnbW3dResolvedFogState ResolveCurrentRetail(
        EnbW3dFogMode mode,
        EnbW3dSceneFogState scene,
        bool rendererSupportsFog,
        EnbW3dFogPipelineSemantics pipeline)
    {
        bool enabled = rendererSupportsFog && scene.Enabled && mode != EnbW3dFogMode.Disabled;
        uint color = mode switch
        {
            EnbW3dFogMode.Black => 0x000000u,
            EnbW3dFogMode.White => 0xFFFFFFu,
            _ => scene.SceneColorRgb24 & 0xFFFFFFu,
        };

        return new EnbW3dResolvedFogState(
            enabled,
            color,
            scene.Start,
            scene.End,
            pipeline.DistanceSource);
    }
}

/// <summary>
/// Material-source and lighting switches ultimately applied by current retail's
/// W3D material runtime. The active file-loaded path leaves specular sourced
/// from the material.
/// </summary>
public readonly record struct EnbW3dVertexLightingSemantics(
    bool LightingEnabled,
    EnbW3dMaterialColorSource AmbientSource,
    EnbW3dMaterialColorSource DiffuseSource,
    EnbW3dMaterialColorSource SpecularSource,
    EnbW3dMaterialColorSource EmissiveSource)
{
    public static EnbW3dVertexLightingSemantics RetailDefault => new(
        true,
        EnbW3dMaterialColorSource.Material,
        EnbW3dMaterialColorSource.Material,
        EnbW3dMaterialColorSource.Material,
        EnbW3dMaterialColorSource.Material);

    // 00930C10 is the draw consumer for an actual null material pointer.
    public static EnbW3dVertexLightingSemantics RetailNullMaterial => RetailDefault with { LightingEnabled = false };
}

/// <summary>
/// Neutral representation of the packed texture-combiner selectors consumed by
/// the current retail renderer. Their numeric behavior is bounded by the retail
/// state machine; deliberately does not invent prettier semantic names for
/// selectors whose business labels are not established by current evidence.
/// </summary>
public readonly record struct EnbW3dTextureCombinerSelectors(
    byte Selector0,
    bool TextureStateEnabled,
    byte Selector1,
    byte Selector2);

/// <summary>
/// D3DTOP values emitted by the current retail texture-stage applicator. The
/// numeric values are retained because the retail diagnostic formatter names
/// these exact values and a modern backend can implement the same finite
/// operations directly in shaders.
/// </summary>
public enum EnbW3dTextureOperation : byte
{
    Disabled = 1,
    SelectArgument1 = 2,
    SelectArgument2 = 3,
    Modulate = 4,
    Modulate2X = 5,
    Modulate4X = 6,
    Add = 7,
    AddSigned = 8,
    AddSigned2X = 9,
    Subtract = 10,
    AddSmooth = 11,
    BlendDiffuseAlpha = 12,
    BlendTextureAlpha = 13,
    BlendFactorAlpha = 14,
    BlendTextureAlphaPremultiplied = 15,
    BlendCurrentAlpha = 16,
    PreModulate = 17,
    ModulateAlphaAddColor = 18,
    ModulateColorAddAlpha = 19,
    ModulateInverseAlphaAddColor = 20,
    ModulateInverseColorAddAlpha = 21,
    BumpEnvironmentMap = 22,
    BumpEnvironmentMapLuminance = 23,
    DotProduct3 = 24,
    MultiplyAdd = 25,
    Lerp = 26,
}

/// <summary>
/// D3DTA values used by the current W3D material path. No complement or
/// alpha-replicate modifiers are emitted by FUN_0093B510 for these stages.
/// </summary>
public enum EnbW3dTextureArgument : byte
{
    Diffuse = 0,
    Current = 1,
    Texture = 2,
}

public readonly record struct EnbW3dTextureChannelSemantics(
    EnbW3dTextureOperation Operation,
    EnbW3dTextureArgument Argument1,
    EnbW3dTextureArgument Argument2);

public readonly record struct EnbW3dTextureStageSemantics(
    EnbW3dTextureChannelSemantics Color,
    EnbW3dTextureChannelSemantics Alpha);

/// <summary>
/// Global bump-environment defaults installed by the current retail renderer:
/// identity DU/DV matrix, luminance scale 1 and luminance offset 0.
/// </summary>
public readonly record struct EnbW3dBumpEnvironmentSemantics(
    Vector4 Matrix2X2,
    float LuminanceScale,
    float LuminanceOffset)
{
    public static EnbW3dBumpEnvironmentSemantics RetailDefault =>
        new(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), 1.0f, 0.0f);
}

/// <summary>
/// Capability-independent semantic form of the current retail texture-stage
/// program. This records the preferred D3DTOP operation selected by the W3D
/// selector rather than reproducing obsolete GPU-capability fallbacks.
/// </summary>
public readonly record struct EnbW3dTextureCombinerSemantics(
    EnbW3dTextureStageSemantics Stage0,
    EnbW3dTextureStageSemantics Stage1,
    EnbW3dBumpEnvironmentSemantics BumpEnvironment);

/// <summary>
/// Exact current-retail alpha-test state. The comparison enum intentionally
/// reuses D3D comparison numeric identity because FUN_0093B510 writes those
/// same values to D3DRS_ALPHAFUNC.
/// </summary>
public readonly record struct EnbW3dAlphaTestSemantics(
    bool Enabled,
    EnbRenderDepthCompare Compare,
    byte Reference)
{
    public static EnbW3dAlphaTestSemantics Disabled =>
        new(false, EnbRenderDepthCompare.Always, 0);
}

/// <summary>
/// Exact fixed-function behavior derived from one authored W3D shader record
/// after applying the retail 0x0008441B W3D baseline and FUN_009957E0's field
/// replacement rules.
/// </summary>
public readonly record struct EnbW3dFixedFunctionState(
    uint PackedRetailState,
    EnbRenderDepthCompare DepthCompare,
    bool DepthWriteEnabled,
    EnbRenderCullMode CullMode,
    EnbRenderBlendFactor SourceBlend,
    EnbRenderBlendFactor DestinationBlend,
    bool BlendEnabled,
    EnbW3dAlphaTestSemantics AlphaTest,
    bool SpecularEnabled,
    EnbW3dFogMode FogMode,
    EnbW3dTextureCombinerSelectors TextureCombiners,
    EnbW3dTextureCombinerSemantics TextureCombinerSemantics)
{
    public bool AlphaTestEnabled => AlphaTest.Enabled;
}

public readonly record struct EnbW3dMapperShaderKey(
    EnbW3dMapperTypeSelector TypeSelector,
    EnbW3dMapperRuntimeProgramKind ProgramKind,
    EnbW3dMapperCoordinateSource CoordinateSource,
    EnbW3dMapperTransformShape TransformShape,
    bool Enabled);

/// <summary>
/// Closed shared scale/linear parameters used by the finite mapper runtime
/// programs. Selector-specific rotation, atlas, projection, oscillation, random
/// and bump parameters live on EnbW3dMapperRuntimeProgram rather than being
/// flattened into a pretend generic scroll transform.
/// </summary>
public readonly record struct EnbW3dMapperUniforms(
    Vector2 Scale,
    Vector2 ScrollPerMillisecond,
    Vector2 Offset,
    bool ClampFix,
    bool HasClosedBaseParameters)
{
    public static EnbW3dMapperUniforms Identity => new(
        Vector2.One,
        Vector2.Zero,
        Vector2.Zero,
        ClampFix: false,
        HasClosedBaseParameters: false);
}

/// <summary>
/// Material channels required by the modern shader. RGB channels are normalized
/// exactly as retail FUN_00995690 (byte / 255). The authored W3D Opacity scalar
/// is the alpha member of retail's submitted diffuse RGBA material, so the
/// backend contract names it DiffuseAlpha rather than treating it as an
/// unrelated opacity convention.
/// </summary>
public readonly record struct EnbW3dMaterialUniforms(
    Vector3 Ambient,
    Vector3 Diffuse,
    Vector3 Specular,
    Vector3 Emissive,
    float Power,
    float DiffuseAlpha,
    EnbW3dMapperUniforms Mapper0,
    EnbW3dMapperUniforms Mapper1)
{
    public Vector4 DiffuseRgba => new(Diffuse, DiffuseAlpha);
}

/// <summary>
/// Finite modern shader key. Per-material colors, power, diffuse alpha and closed
/// mapper base parameters remain uniforms. This adds the current-retail mapper type
/// selector itself to the key because distinct mapper classes have distinct runtime
/// programs and must not collapse into one generic linear-scroll permutation.
/// </summary>
public readonly record struct EnbW3dShaderPermutationKey(
    EnbRenderDepthCompare DepthCompare,
    bool DepthWriteEnabled,
    EnbRenderCullMode CullMode,
    EnbRenderBlendFactor SourceBlend,
    EnbRenderBlendFactor DestinationBlend,
    bool BlendEnabled,
    EnbW3dAlphaTestSemantics AlphaTest,
    bool SpecularEnabled,
    EnbW3dFogMode FogMode,
    EnbW3dTextureCombinerSemantics TextureCombinerSemantics,
    byte TextureStageCount,
    EnbW3dMapperShaderKey Mapper0,
    EnbW3dMapperShaderKey Mapper1,
    EnbW3dVertexLightingSemantics VertexLighting)
{
    public bool AlphaTestEnabled => AlphaTest.Enabled;
}

public readonly record struct EnbW3dCompiledMaterialSemantics(
    EnbW3dFixedFunctionState FixedFunction,
    EnbW3dShaderPermutationKey PermutationKey,
    EnbW3dMaterialUniforms Uniforms,
    EnbW3dVertexLightingSemantics VertexLighting,
    EnbW3dMapperRuntimeProgram Mapper0Program,
    EnbW3dMapperRuntimeProgram Mapper1Program);

public enum EnbW3dRetailShaderOptimizationKind : byte
{
    GeneralPerShader = 0,
    TwoPassTextureHandoff = 1,
    TwoPassFogPair = 2,
}

/// <summary>
/// Closed current-retail FUN_009BCE00 result. The first two-pass branch also
/// transfers stage-0 texture ownership and the material's stage-0 texture-coordinate
/// index from pass 1 to pass 0. This closes and integrates those descriptor/resource
/// transfers into the backend-neutral render resource.
/// </summary>
public readonly record struct EnbW3dRetailTwoPassShaderOptimization(
    EnbW3dRetailShaderOptimizationKind Kind,
    uint Pass0PackedState,
    uint Pass1PackedState,
    bool CopyPass1Stage0TextureToPass0,
    bool CopyPass1Stage0TextureCoordinateIndexToPass0);

/// <summary>
/// Pure backend-neutral reduction of current retail shader post-processing.
/// FUN_0093B3C0 owns the general blend-to-fog rewrite. FUN_009BCE00 contains
/// two finite uniform two-pass special cases before falling back to that general
/// rewrite for every uniform/per-triangle shader.
/// </summary>
public static class EnbW3dRetailShaderOptimizer
{
    public static uint OptimizeGeneralFog(uint packedRetailState)
    {
        uint sourceSelector = (packedRetailState >> 14) & 0x3u;
        uint destinationSelector = (packedRetailState >> 5) & 0x7u;

        return sourceSelector switch
        {
            0 when destinationSelector == 2 => packedRetailState | 0x0000_0300u,
            1 when destinationSelector == 0 => ReplaceFogMode(packedRetailState, EnbW3dFogMode.SceneColor),
            1 when destinationSelector is 1 or 3 => ReplaceFogMode(packedRetailState, EnbW3dFogMode.Black),
            2 when destinationSelector == 5 => ReplaceFogMode(packedRetailState, EnbW3dFogMode.SceneColor),
            3 when destinationSelector == 4 => ReplaceFogMode(packedRetailState, EnbW3dFogMode.SceneColor),
            _ => packedRetailState,
        };
    }

    public static EnbW3dRetailTwoPassShaderOptimization OptimizeUniformTwoPass(
        uint pass0PackedState,
        uint pass1PackedState)
    {
        if (MatchesTextureHandoffPair(pass0PackedState, pass1PackedState))
        {
            uint pass0 = pass0PackedState | 0x0001_0000u;
            uint pass1 = (pass1PackedState & 0xFFFF_FF3Fu) | 0x0000_0020u;
            pass0 = ReplaceFogMode(pass0, EnbW3dFogMode.SceneColor);
            pass1 = ReplaceFogMode(pass1, EnbW3dFogMode.Black);
            return new EnbW3dRetailTwoPassShaderOptimization(
                EnbW3dRetailShaderOptimizationKind.TwoPassTextureHandoff,
                pass0,
                pass1,
                CopyPass1Stage0TextureToPass0: true,
                CopyPass1Stage0TextureCoordinateIndexToPass0: true);
        }

        if (MatchesFogPair(pass0PackedState, pass1PackedState))
        {
            return new EnbW3dRetailTwoPassShaderOptimization(
                EnbW3dRetailShaderOptimizationKind.TwoPassFogPair,
                ReplaceFogMode(pass0PackedState, EnbW3dFogMode.Black),
                ReplaceFogMode(pass1PackedState, EnbW3dFogMode.SceneColor),
                CopyPass1Stage0TextureToPass0: false,
                CopyPass1Stage0TextureCoordinateIndexToPass0: false);
        }

        return new EnbW3dRetailTwoPassShaderOptimization(
            EnbW3dRetailShaderOptimizationKind.GeneralPerShader,
            OptimizeGeneralFog(pass0PackedState),
            OptimizeGeneralFog(pass1PackedState),
            CopyPass1Stage0TextureToPass0: false,
            CopyPass1Stage0TextureCoordinateIndexToPass0: false);
    }

    private static bool MatchesTextureHandoffPair(uint pass0, uint pass1)
        => (pass0 & 0x0001_0000u) == 0 &&
           (pass0 & 0x0000_C000u) == 0x0000_4000u &&
           (pass0 & 0x0000_00E0u) == 0 &&
           (pass0 & 0x0000_1C00u) == 0x0000_0400u &&
           (pass0 & 0x0000_2000u) == 0 &&
           (pass1 & 0x0001_0000u) != 0 &&
           (pass1 & 0x0000_C000u) == 0x0000_8000u &&
           (pass1 & 0x0000_00E0u) == 0x0000_0040u;

    private static bool MatchesFogPair(uint pass0, uint pass1)
    {
        uint pass1Destination = pass1 & 0x0000_00E0u;
        return (pass0 & 0x0000_C000u) == 0x0000_4000u &&
               (pass0 & 0x0000_00E0u) == 0 &&
               (pass1 & 0x0000_C000u) == 0x0000_4000u &&
               (pass1Destination == 0x0000_0080u || pass1Destination == 0x0000_00A0u);
    }

    private static uint ReplaceFogMode(uint packedRetailState, EnbW3dFogMode mode)
        => (packedRetailState & 0xFFFF_FCFFu) | ((uint)mode << 8);
}

/// <summary>
/// Current-retail W3D fixed-function/material translator. This is intentionally
/// backend-independent: a concrete GPU backend maps these finite semantics to
/// pipeline state and shader constants rather than carrying D3D8 API calls into
/// the production renderer.
/// </summary>
public static class EnbW3dMaterialCompiler
{
    // Current W3D loader initializes every compact shader through
    // InitializeLineRenderableMaterial before FUN_009957E0 mutates it.
    public const uint RetailW3dShaderBaseline = 0x0008_441Bu;

    private static readonly EnbRenderBlendFactor[] SourceBlendSelectorTable =
    [
        EnbRenderBlendFactor.Zero,
        EnbRenderBlendFactor.One,
        EnbRenderBlendFactor.SourceAlpha,
        EnbRenderBlendFactor.InverseSourceAlpha,
    ];

    private static readonly EnbRenderBlendFactor[] DestinationBlendSelectorTable =
    [
        EnbRenderBlendFactor.Zero,
        EnbRenderBlendFactor.One,
        EnbRenderBlendFactor.SourceColor,
        EnbRenderBlendFactor.InverseSourceColor,
        EnbRenderBlendFactor.SourceAlpha,
        EnbRenderBlendFactor.InverseSourceAlpha,
    ];

    public static bool TryDecodeShader(
        EnbW3dRenderShaderRecord shader,
        out EnbW3dFixedFunctionState state,
        out string? diagnostic)
    {
        Span<byte> raw = stackalloc byte[16];
        shader.CopyTo(raw);

        uint packed = RetailW3dShaderBaseline;
        packed = (packed & 0xFFFF_FFF8u) | raw[0];
        packed = ((uint)(raw[1] | 2) << 3) | (packed & 0xFFFF_FFF7u);
        packed = ((uint)(raw[3] & 0xE7) << 5) | (packed & 0xFFFF_FC1Fu);
        packed = (packed & 0xFFFF_E3FFu) | ((uint)raw[5] << 10);
        packed = (packed & 0xFFFF_DFFFu) | ((uint)raw[6] << 13);
        packed = (packed & 0xFFFF_3FFFu) | ((uint)raw[7] << 14);
        packed = (packed & 0xFFFE_FFFFu) | ((uint)raw[8] << 16);
        packed = (packed & 0xFFFB_FFFFu) | ((uint)raw[12] << 18);
        packed = (packed & 0xFF0F_FFFFu) | ((uint)raw[9] << 20);
        packed = (packed & 0xF8FF_FFFFu) | ((uint)raw[10] << 24);

        return TryDecodePackedRetailState(packed, out state, out diagnostic);
    }

    /// <summary>
    /// Decodes an already-packed current-retail W3D fixed-function state. This
    /// is required both for legacy root 0x0015 shader bindings and for the later
    /// render-state fallback 0x0008441B, neither of which requires an authored
    /// 16-byte modern shader record. The primary descriptor's unbound conflict
    /// sentinel is separately proven to be zero.
    /// </summary>
    public static bool TryDecodePackedRetailState(
        uint packed,
        out EnbW3dFixedFunctionState state,
        out string? diagnostic)
    {
        int destinationBlendSelector = (int)((packed >> 5) & 0x7u);
        if ((uint)destinationBlendSelector >= (uint)DestinationBlendSelectorTable.Length)
        {
            state = default;
            diagnostic = $"W3D destination-blend selector {destinationBlendSelector} is outside the current retail selector table.";
            return false;
        }

        int sourceBlendSelector = (int)((packed >> 14) & 0x3u);
        EnbRenderBlendFactor sourceBlend = SourceBlendSelectorTable[sourceBlendSelector];
        EnbRenderBlendFactor destinationBlend = DestinationBlendSelectorTable[destinationBlendSelector];

        int depthSelector = (int)(packed & 0x7u) + 1;
        if (depthSelector < (int)EnbRenderDepthCompare.Never || depthSelector > (int)EnbRenderDepthCompare.Always)
        {
            state = default;
            diagnostic = $"W3D depth selector {depthSelector} is outside the current retail comparison range.";
            return false;
        }

        EnbRenderCullMode cullMode = (packed & 0x0008_0000u) != 0
            ? EnbRenderCullMode.Clockwise
            : EnbRenderCullMode.None;

        var textureCombiners = new EnbW3dTextureCombinerSelectors(
            (byte)((packed >> 10) & 0x7u),
            (packed & 0x0001_0000u) != 0,
            (byte)((packed >> 20) & 0xFu),
            (byte)((packed >> 24) & 0x7u));

        bool alphaTestEnabled = (packed & 0x0004_0000u) != 0;
        EnbW3dAlphaTestSemantics alphaTest = !alphaTestEnabled
            ? EnbW3dAlphaTestSemantics.Disabled
            : sourceBlend == EnbRenderBlendFactor.InverseSourceAlpha
                ? new EnbW3dAlphaTestSemantics(true, EnbRenderDepthCompare.LessOrEqual, 0x9F)
                : new EnbW3dAlphaTestSemantics(true, EnbRenderDepthCompare.GreaterOrEqual, 0x60);

        state = new EnbW3dFixedFunctionState(
            packed,
            (EnbRenderDepthCompare)depthSelector,
            (packed & 0x8u) != 0,
            cullMode,
            sourceBlend,
            destinationBlend,
            sourceBlend != EnbRenderBlendFactor.One || destinationBlend != EnbRenderBlendFactor.Zero,
            alphaTest,
            (packed & 0x0000_2000u) != 0,
            (EnbW3dFogMode)((packed >> 8) & 0x3u),
            textureCombiners,
            CompileTextureCombinerSemantics(textureCombiners));
        diagnostic = null;
        return true;
    }

    public static bool TryCompileWithVertexLighting(
        EnbW3dRenderShaderRecord shader,
        EnbW3dRenderVertexMaterial material,
        int textureStageCount,
        EnbW3dVertexLightingSemantics vertexLighting,
        out EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic)
    {
        if (textureStageCount is < 0 or > byte.MaxValue)
        {
            semantics = default;
            diagnostic = $"Texture-stage count {textureStageCount} cannot be represented by the finite R4 shader key.";
            return false;
        }

        if (!TryDecodeShader(shader, out EnbW3dFixedFunctionState fixedFunction, out diagnostic))
        {
            semantics = default;
            return false;
        }

        return TryCompileCore(fixedFunction, material, textureStageCount, vertexLighting, out semantics, out diagnostic);
    }

    /// <summary>
    /// Compiles the descriptor's implicit packed shader state without inventing
    /// an authored W3D shader record.
    /// </summary>
    public static bool TryCompilePackedRetailState(
        uint packedRetailState,
        EnbW3dRenderVertexMaterial material,
        int textureStageCount,
        out EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic)
    {
        if (textureStageCount is < 0 or > byte.MaxValue)
        {
            semantics = default;
            diagnostic = $"Texture-stage count {textureStageCount} cannot be represented by the finite R4 shader key.";
            return false;
        }

        if (!TryDecodePackedRetailState(packedRetailState, out EnbW3dFixedFunctionState fixedFunction, out diagnostic))
        {
            semantics = default;
            return false;
        }

        return TryCompileCore(
            fixedFunction,
            material,
            textureStageCount,
            EnbW3dVertexLightingSemantics.RetailDefault,
            out semantics,
            out diagnostic);
    }

    public static bool TryCompilePackedRetailState(
        uint packedRetailState,
        EnbW3dRenderVertexMaterial material,
        int textureStageCount,
        EnbW3dVertexLightingSemantics vertexLighting,
        out EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic)
    {
        if (textureStageCount is < 0 or > byte.MaxValue)
        {
            semantics = default;
            diagnostic = $"Texture-stage count {textureStageCount} cannot be represented by the finite R4 shader key.";
            return false;
        }

        if (!TryDecodePackedRetailState(packedRetailState, out EnbW3dFixedFunctionState fixedFunction, out diagnostic))
        {
            semantics = default;
            return false;
        }

        return TryCompileCore(fixedFunction, material, textureStageCount, vertexLighting, out semantics, out diagnostic);
    }

    private static bool TryCompileCore(
        EnbW3dFixedFunctionState fixedFunction,
        EnbW3dRenderVertexMaterial material,
        int textureStageCount,
        EnbW3dVertexLightingSemantics vertexLighting,
        out EnbW3dCompiledMaterialSemantics semantics,
        out string? diagnostic)
    {
        if (!EnbW3dMapperRuntimeProgramCompiler.TryCompile(material.Mapper0, out EnbW3dMapperRuntimeProgram mapper0Program, out diagnostic) ||
            !EnbW3dMapperRuntimeProgramCompiler.TryCompile(material.Mapper1, out EnbW3dMapperRuntimeProgram mapper1Program, out diagnostic))
        {
            semantics = default;
            return false;
        }

        EnbW3dMapperUniforms mapper0 = mapper0Program.BaseUniforms;
        EnbW3dMapperUniforms mapper1 = mapper1Program.BaseUniforms;
        EnbW3dMapperShaderKey mapper0Key = CompileMapperKey(material.Mapper0, mapper0Program);
        EnbW3dMapperShaderKey mapper1Key = CompileMapperKey(material.Mapper1, mapper1Program);

        EnbW3dRenderMaterialUniformOverride? currentRetailOverride = material.CurrentRetailUniformOverride;
        var uniforms = currentRetailOverride is EnbW3dRenderMaterialUniformOverride legacy
            ? new EnbW3dMaterialUniforms(
                legacy.Ambient,
                legacy.Diffuse,
                legacy.Specular,
                legacy.Emissive,
                legacy.Power,
                legacy.DiffuseAlpha,
                mapper0,
                mapper1)
            : new EnbW3dMaterialUniforms(
                DecodeMaterialRgb(material.Ambient),
                DecodeMaterialRgb(material.Diffuse),
                DecodeMaterialRgb(material.Specular),
                DecodeMaterialRgb(material.Emissive),
                material.Power,
                material.Opacity,
                mapper0,
                mapper1);

        var permutationKey = new EnbW3dShaderPermutationKey(
            fixedFunction.DepthCompare,
            fixedFunction.DepthWriteEnabled,
            fixedFunction.CullMode,
            fixedFunction.SourceBlend,
            fixedFunction.DestinationBlend,
            fixedFunction.BlendEnabled,
            fixedFunction.AlphaTest,
            fixedFunction.SpecularEnabled,
            fixedFunction.FogMode,
            fixedFunction.TextureCombinerSemantics,
            (byte)textureStageCount,
            mapper0Key,
            mapper1Key,
            vertexLighting);

        semantics = new EnbW3dCompiledMaterialSemantics(
            fixedFunction,
            permutationKey,
            uniforms,
            vertexLighting,
            mapper0Program,
            mapper1Program);
        diagnostic = null;
        return true;
    }

    /// <summary>
    /// Converts the four packed current-retail W3D combiner selectors into the
    /// preferred texture-stage program. FUN_0093B510 contains fallbacks for
    /// 2002-era devices that lack individual D3DTOP capabilities; a modern
    /// programmable backend implements the preferred operation directly.
    /// </summary>
    public static EnbW3dTextureCombinerSemantics CompileTextureCombinerSemantics(
        EnbW3dTextureCombinerSelectors selectors)
    {
        EnbW3dTextureStageSemantics stage0 = CompileStage0(selectors);
        EnbW3dTextureStageSemantics stage1 = selectors.TextureStateEnabled
            ? CompileStage1(selectors.Selector1, selectors.Selector2)
            : DisabledStage();

        return new EnbW3dTextureCombinerSemantics(
            stage0,
            stage1,
            EnbW3dBumpEnvironmentSemantics.RetailDefault);
    }

    private static EnbW3dTextureStageSemantics CompileStage0(EnbW3dTextureCombinerSelectors selectors)
    {
        if (!selectors.TextureStateEnabled)
        {
            if (selectors.Selector0 == 0)
            {
                return new EnbW3dTextureStageSemantics(
                    Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
                    Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current));
            }

            return new EnbW3dTextureStageSemantics(
                Channel(EnbW3dTextureOperation.SelectArgument2, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
                Channel(EnbW3dTextureOperation.SelectArgument2, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse));
        }

        return selectors.Selector0 switch
        {
            0 => new EnbW3dTextureStageSemantics(
                Channel(EnbW3dTextureOperation.SelectArgument1, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
                Channel(EnbW3dTextureOperation.SelectArgument1, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current)),
            2 => new EnbW3dTextureStageSemantics(
                Channel(EnbW3dTextureOperation.Add, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
                Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse)),
            3 => new EnbW3dTextureStageSemantics(
                Channel(EnbW3dTextureOperation.BumpEnvironmentMap, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
                Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current)),
            4 => new EnbW3dTextureStageSemantics(
                Channel(EnbW3dTextureOperation.BumpEnvironmentMapLuminance, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
                Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current)),
            5 => new EnbW3dTextureStageSemantics(
                Channel(EnbW3dTextureOperation.Modulate2X, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
                Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse)),
            _ => new EnbW3dTextureStageSemantics(
                Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse),
                Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Diffuse)),
        };
    }

    private static EnbW3dTextureStageSemantics CompileStage1(byte colorSelector, byte alphaSelector)
    {
        EnbW3dTextureChannelSemantics color = colorSelector switch
        {
            1 => Channel(EnbW3dTextureOperation.SelectArgument1, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            2 => Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            3 => Channel(EnbW3dTextureOperation.AddSmooth, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            4 => Channel(EnbW3dTextureOperation.Add, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            5 => Channel(EnbW3dTextureOperation.Subtract, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            6 => Channel(EnbW3dTextureOperation.Subtract, EnbW3dTextureArgument.Current, EnbW3dTextureArgument.Texture),
            7 => Channel(EnbW3dTextureOperation.BlendTextureAlpha, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            8 => Channel(EnbW3dTextureOperation.BlendCurrentAlpha, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            9 => Channel(EnbW3dTextureOperation.AddSigned, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            10 => Channel(EnbW3dTextureOperation.AddSigned2X, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            11 => Channel(EnbW3dTextureOperation.Modulate2X, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            12 => Channel(EnbW3dTextureOperation.ModulateAlphaAddColor, EnbW3dTextureArgument.Current, EnbW3dTextureArgument.Texture),
            _ => Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
        };

        EnbW3dTextureChannelSemantics alpha = alphaSelector switch
        {
            1 => Channel(EnbW3dTextureOperation.SelectArgument1, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            2 => Channel(EnbW3dTextureOperation.Modulate, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            3 => Channel(EnbW3dTextureOperation.AddSmooth, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            _ => Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
        };

        // Current retail prevents only one half of stage 1 from being disabled:
        // the inactive half passes CURRENT through with SELECTARG2.
        if (color.Operation == EnbW3dTextureOperation.Disabled && alpha.Operation != EnbW3dTextureOperation.Disabled)
        {
            color = Channel(EnbW3dTextureOperation.SelectArgument2, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current);
        }
        else if (color.Operation != EnbW3dTextureOperation.Disabled && alpha.Operation == EnbW3dTextureOperation.Disabled)
        {
            alpha = Channel(EnbW3dTextureOperation.SelectArgument2, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current);
        }

        return new EnbW3dTextureStageSemantics(color, alpha);
    }

    private static EnbW3dTextureStageSemantics DisabledStage()
        => new(
            Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current),
            Channel(EnbW3dTextureOperation.Disabled, EnbW3dTextureArgument.Texture, EnbW3dTextureArgument.Current));

    private static EnbW3dTextureChannelSemantics Channel(
        EnbW3dTextureOperation operation,
        EnbW3dTextureArgument argument1,
        EnbW3dTextureArgument argument2)
        => new(operation, argument1, argument2);

    internal static Vector3 DecodeMaterialRgb(EnbRenderPackedRgb8 color)
    {
        const float scale = 1.0f / 255.0f;
        return new Vector3(color.R * scale, color.G * scale, color.B * scale);
    }

    private static EnbW3dMapperShaderKey CompileMapperKey(
        EnbW3dRenderMapper? mapper,
        EnbW3dMapperRuntimeProgram program)
        => mapper is null
            ? default
            : new EnbW3dMapperShaderKey(
                mapper.TypeSelector,
                program.Kind,
                program.CoordinateSource,
                program.TransformShape,
                Enabled: true);


}
