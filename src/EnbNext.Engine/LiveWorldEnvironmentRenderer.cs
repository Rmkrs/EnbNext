using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Authored environment renderer for the selected current-retail backdrop.
/// The environment controller keeps the BaseAsset render object translated to
/// the live camera; this renderer therefore projects the authored sphere around
/// the camera while preserving its triangles, both W3D UV streams and DCG.
/// </summary>
internal sealed class LiveWorldEnvironmentRenderer : IDisposable
{
    private readonly EnbW3dEnvironmentBackdrop _backdrop;
    private readonly EnbDdsTexture _stage0Texture;
    private readonly EnbDdsTexture _stage1Texture;
    private readonly string _stage0ResolvedName;
    private readonly string _stage1ResolvedName;
    private readonly bool _influenceSky;
    private readonly uint _authoredPackedShader;
    private readonly uint _effectivePackedShader;
    private readonly Vector3? _influenceSkyEmissive;
    private LiveWorldEnvironmentRenderer(
        EnbW3dEnvironmentBackdrop backdrop,
        EnbDdsTexture stage0Texture,
        EnbDdsTexture stage1Texture,
        string stage0ResolvedName,
        string stage1ResolvedName,
        bool influenceSky,
        uint authoredPackedShader,
        uint effectivePackedShader,
        Vector3? influenceSkyEmissive)
    {
        _backdrop = backdrop;
        _stage0Texture = stage0Texture;
        _stage1Texture = stage1Texture;
        _stage0ResolvedName = stage0ResolvedName;
        _stage1ResolvedName = stage1ResolvedName;
        _influenceSky = influenceSky;
        _authoredPackedShader = authoredPackedShader;
        _effectivePackedShader = effectivePackedShader;
        _influenceSkyEmissive = influenceSkyEmissive;
        Geometry = BuildGeometry();
    }

    public EnbBackdropGeometry Geometry { get; }

    public string Summary
    {
        get
        {
            string influenceSuffix = _influenceSkyEmissive is Vector3 emissive
                ? $" · SkyBoxColor emissive {FormatVector(emissive)} post-load/COLOR1 retained"
                : string.Empty;
            return
                $"{(_influenceSky ? "R7B1_LIVE_CURRENT_RETAIL_INFLUENCE_SKY_ACTIVE" : "R6X_LIVE_ENVIRONMENT_AUTHORED_ACTIVE")} · {_backdrop.Mesh.Name} " +
                $"{_backdrop.Mesh.Vertices.Count}v/{_backdrop.Mesh.Triangles.Count}t · " +
                $"stage0 {_stage0ResolvedName} × stage1 {_stage1ResolvedName} · " +
                $"shader 0x{_authoredPackedShader:X8}->0x{_effectivePackedShader:X8}{influenceSuffix} · " +
                $"authored triangles/UV0/UV1/DCG · layers {1 + _backdrop.AdditiveLayers.Count} · GPU camera-centered";
        }
    }

    public static bool TryCreate(
        EnbW3dEnvironmentBackdrop backdrop,
        EnbDdsTexture stage0Texture,
        EnbDdsTexture stage1Texture,
        string stage0ResolvedName,
        string stage1ResolvedName,
        bool influenceSky,
        Vector3? influenceSkyColor,
        out LiveWorldEnvironmentRenderer renderer,
        out string? diagnostic)
    {
        renderer = default!;
        diagnostic = null;
        if (influenceSky && backdrop.IsLayered)
        {
            diagnostic = "layered influence sky is outside the bounded ordinary backdrop path";
            return false;
        }
        if (!TryDecodeShaderRecord(backdrop.Mesh.Shaders[0], out EnbW3dRenderShaderRecord shaderRecord, out diagnostic))
            return false;

        uint authoredPackedShader;
        EnbW3dFixedFunctionState state;
        if (influenceSky)
        {
            if (!EnbCurrentRetailInfluenceSkyRuntime.TryResolveDirectMeshFixedFunctionState(
                    shaderRecord,
                    out state,
                    out authoredPackedShader,
                    out diagnostic))
            {
                return false;
            }
        }
        else if (!EnbW3dMaterialCompiler.TryDecodeShader(shaderRecord, out state, out diagnostic))
        {
            return false;
        }
        else
        {
            authoredPackedShader = state.PackedRetailState;
        }

        Vector3? influenceSkyEmissive = null;
        if (influenceSky)
        {
            if (influenceSkyColor is not Vector3 skyBoxColor)
            {
                diagnostic = "selected influence environment is missing the current-retail Between/SkyBoxColor input.";
                return false;
            }
            if (!float.IsFinite(skyBoxColor.X) || !float.IsFinite(skyBoxColor.Y) || !float.IsFinite(skyBoxColor.Z))
            {
                diagnostic = "selected influence environment has a non-finite current-retail Between/SkyBoxColor input.";
                return false;
            }

            EnbW3dMaterialUniforms authoredMaterial = ToMaterialUniforms(backdrop.Mesh.VertexMaterials[0]);
            EnbCurrentRetailInfluenceSkyPostLoadMaterialMutation mutation =
                EnbCurrentRetailInfluenceSkyRuntime.ApplySkyBoxColorAfterLoad(authoredMaterial, skyBoxColor);
            if (mutation.ReprocessColor1)
            {
                diagnostic = "current-retail influence SkyBoxColor unexpectedly requested COLOR1 reprocessing.";
                return false;
            }

            // FUN_0092EF70 runs after W3D load-finalization. The material instance
            // receives the new emissive RGB, while the already-folded COLO/DCG
            // stream remains exactly as loaded and therefore remains the raster input.
            influenceSkyEmissive = mutation.Material.Emissive;
        }

        if (state.DepthCompare != EnbRenderDepthCompare.Always ||
            state.DepthWriteEnabled != backdrop.IsLayered ||
            state.CullMode != EnbRenderCullMode.Clockwise ||
            state.SourceBlend != EnbRenderBlendFactor.One ||
            state.DestinationBlend != EnbRenderBlendFactor.Zero ||
            state.BlendEnabled ||
            state.AlphaTest.Enabled ||
            state.FogMode != EnbW3dFogMode.Disabled ||
            state.TextureCombinerSemantics.Stage0.Color != new EnbW3dTextureChannelSemantics(
                EnbW3dTextureOperation.Modulate,
                EnbW3dTextureArgument.Texture,
                EnbW3dTextureArgument.Diffuse) ||
            state.TextureCombinerSemantics.Stage1.Color != new EnbW3dTextureChannelSemantics(
                EnbW3dTextureOperation.Modulate,
                EnbW3dTextureArgument.Texture,
                EnbW3dTextureArgument.Current))
        {
            diagnostic ??= influenceSky
                ? "selected influence environment shader no longer matches the bounded current-retail environment pass after the proven direct-mesh mutation."
                : "selected environment shader no longer matches the bounded current-retail environment pass.";
            return false;
        }

        renderer = new LiveWorldEnvironmentRenderer(
            backdrop,
            stage0Texture,
            stage1Texture,
            stage0ResolvedName,
            stage1ResolvedName,
            influenceSky,
            authoredPackedShader,
            state.PackedRetailState,
            influenceSkyEmissive);
        return true;
    }

    public void Dispose() { } // CPU asset data only; the GL owner retires its GPU resources.

    private EnbBackdropGeometry BuildGeometry()
    {
        return BuildLayer(_backdrop) with
        {
            WriteFarDepth = _backdrop.IsLayered,
            AdditiveLayers = _backdrop.AdditiveLayers.Select(BuildLayer).ToImmutableArray()
        };
    }

    private EnbBackdropGeometry BuildLayer(EnbW3dEnvironmentBackdrop layer)
    {
        var vertices = ImmutableArray.CreateBuilder<EnbBackdropVertex>(layer.Mesh.Triangles.Count * 3);
        foreach (EnbW3dTriangle triangle in layer.Mesh.Triangles)
        {
            Add(checked((int)triangle.Vertex0));
            Add(checked((int)triangle.Vertex1));
            Add(checked((int)triangle.Vertex2));
        }
        return new(vertices.MoveToImmutable(), _stage0Texture, _stage1Texture);

        void Add(int index) => vertices.Add(new(
            layer.Mesh.Vertices[index], Vector2.Zero,
            layer.Stage0.RuntimeTexCoords[index], Stage1Uv(index),
            layer.VertexIntensity[index]));

        Vector2 Stage1Uv(int index)
        {
            // Mode06 StaticScale/PassThrough consumes the already-imported UV.
            var mapper = layer.Mesh.VertexMaterials[0].Mapper1;
            return layer.Stage1.RuntimeTexCoords[index] * (mapper is null ? Vector2.One : new Vector2(mapper.UScale, mapper.VScale));
        }
    }

    private static EnbW3dMaterialUniforms ToMaterialUniforms(EnbW3dVertexMaterial material)
    {
        EnbW3dVertexMaterialInfo info = material.Info;
        return new EnbW3dMaterialUniforms(
            Normalize(info.Ambient),
            Normalize(info.Diffuse),
            Normalize(info.Specular),
            Normalize(info.Emissive),
            info.Power,
            info.Opacity,
            EnbW3dMapperUniforms.Identity,
            EnbW3dMapperUniforms.Identity);
    }

    private static Vector3 Normalize(EnbW3dPackedRgb color)
        => new(color.R / 255f, color.G / 255f, color.B / 255f);

    private static string FormatVector(Vector3 value)
        => $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})";

    private static bool TryDecodeShaderRecord(
        EnbW3dShader shader,
        out EnbW3dRenderShaderRecord record,
        out string? diagnostic)
    {
        ReadOnlySpan<byte> raw = shader.Raw.Span;
        if (raw.Length != 16)
        {
            record = default;
            diagnostic = "environment shader is not a 16-byte current W3D shader record.";
            return false;
        }

        record = new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(12, 4)));
        diagnostic = null;
        return true;
    }

}
