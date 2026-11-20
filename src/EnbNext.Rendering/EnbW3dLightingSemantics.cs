using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// D3DLIGHT8 light types reached by the current retail renderer. Numeric values
/// deliberately match the legacy fixed-function API values written by
/// FUN_009261C0.
/// </summary>
public enum EnbW3dLightType : byte
{
    Point = 1,
    Spot = 2,
    Directional = 3,
}

/// <summary>
/// Backend-neutral form of one current-retail D3DLIGHT8 record. Light alpha is
/// intentionally absent because the fixed-function lighting contract ignores it.
/// </summary>
public readonly record struct EnbW3dCompiledLight(
    EnbW3dLightType Type,
    Vector3 Diffuse,
    Vector3 Specular,
    Vector3 Ambient,
    Vector3 Position,
    Vector3 Direction,
    float Range,
    float Falloff,
    float Attenuation0,
    float Attenuation1,
    float Attenuation2,
    float Theta,
    float Phi);

public readonly record struct EnbW3dLightSlot(
    bool Enabled,
    EnbW3dCompiledLight Light);

/// <summary>
/// Current retail retains and snapshots exactly four D3DLIGHT8 records plus four
/// enable bytes. The snapshot is part of deferred/sorted draw state, so an
/// EnbNext draw packet carries an immutable copy rather than consulting mutable
/// scene-global lights later during backend submission.
/// </summary>
public sealed record EnbW3dLightEnvironment
{
    public const int CurrentRetailLightSlotCount = 4;

    private EnbW3dLightEnvironment(uint globalAmbientRgb24, ImmutableArray<EnbW3dLightSlot> lights)
    {
        GlobalAmbientRgb24 = globalAmbientRgb24 & 0x00FF_FFFFu;
        Lights = lights;
    }

    public uint GlobalAmbientRgb24 { get; }
    public ImmutableArray<EnbW3dLightSlot> Lights { get; }
    public EnbRetailSpecialMaterialObjectLights? SpecialMaterialObjectLights { get; init; }
    // 985840 can produce IEEE NaNs when all selected direct strengths are zero.
    // 926460 -> 9254A0 submits those values unchanged (including ignored HRESULTs).
    // Only the proven station evaluator can mark that provenance.
    internal bool HasNativeStationArithmetic { get; init; }

    /// <summary>
    /// Explicit all-dark environment useful to callers that intentionally want
    /// no fixed-function scene illumination. It is not an implicit draw-packet
    /// default.
    /// </summary>
    public static EnbW3dLightEnvironment BlackNoLights { get; } = CreateCurrentRetail(
        globalAmbientRgb24: 0,
        Array.Empty<EnbW3dLightSlot>());

    public static EnbW3dLightEnvironment CreateCurrentRetail(
        uint globalAmbientRgb24,
        IEnumerable<EnbW3dLightSlot> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        EnbW3dLightSlot[] supplied = lights.ToArray();
        if (supplied.Length > CurrentRetailLightSlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lights),
                supplied.Length,
                $"Current retail snapshots at most {CurrentRetailLightSlotCount} light slots.");
        }

        var builder = ImmutableArray.CreateBuilder<EnbW3dLightSlot>(CurrentRetailLightSlotCount);
        builder.AddRange(supplied);
        while (builder.Count < CurrentRetailLightSlotCount)
            builder.Add(default);

        return new EnbW3dLightEnvironment(globalAmbientRgb24, builder.ToImmutable());
    }
}

/// <summary>
/// Device/render-state constants that define the current retail fixed-function
/// lighting program. The current image explicitly enables COLORVERTEX, leaves
/// LOCALVIEWER at its TRUE API default, leaves NORMALIZENORMALS at its FALSE API
/// default, and initializes SPECULARMATERIALSOURCE to MATERIAL. No current
/// application writer contradicts those retained values.
/// </summary>
public readonly record struct EnbW3dFixedFunctionLightingPipeline(
    bool ColorVertexEnabled,
    bool LocalViewerEnabled,
    bool NormalizeNormals,
    EnbW3dMaterialColorSource SpecularMaterialSource,
    int MaximumSnapshottedLights)
{
    public static EnbW3dFixedFunctionLightingPipeline CurrentRetail => new(
        ColorVertexEnabled: true,
        LocalViewerEnabled: true,
        NormalizeNormals: false,
        SpecularMaterialSource: EnbW3dMaterialColorSource.Material,
        MaximumSnapshottedLights: EnbW3dLightEnvironment.CurrentRetailLightSlotCount);
}

/// <summary>
/// Reference output of the current fixed-function vertex-lighting stage before
/// the texture cascade and fog blend. Diffuse alpha is the only lighting
/// alpha used for transparency; specular RGB remains a separate post-texture
/// add when the shader enables specular highlights.
/// </summary>
public readonly record struct EnbW3dVertexLightingOutput(
    Vector4 Diffuse,
    Vector3 Specular);
