using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Bounded current-retail mutations applied to the special type-0x0A
/// environment object created from BaseAsset 311.
///
/// FUN_007E87C0 writes the Between/SkyBoxColor vector through FUN_0092EF70.
/// Material backing +0x30..+0x38 is Emissive RGB, so the
/// color mutation changes only the emissive channel. When the special object's
/// immediate render child is a mesh, retail additionally replaces the current
/// packed shader state with (state &amp; 0xFFFFE7FF) | 0x00000400 before handing
/// that state back to the mesh.
/// </summary>
public readonly record struct EnbCurrentRetailInfluenceSkyPostLoadMaterialMutation(
    EnbW3dMaterialUniforms Material,
    bool ReprocessColor1);

public static class EnbCurrentRetailInfluenceSkyRuntime
{
    public const uint DirectMeshShaderClearMask = 0xFFFF_E7FFu;
    public const uint DirectMeshShaderSetMask = 0x0000_0400u;

    /// <summary>
    /// Current retail writes SkyBoxColor after the W3D load-finalization pass
    /// has already folded material emissive into descriptor COLO. The setter
    /// marks the material instance-local and updates emissive RGB, but it does
    /// not rerun FUN_009475E0. Consumers must therefore preserve the already
    /// preprocessed vertex colors rather than tinting them a second time.
    /// </summary>
    public static EnbCurrentRetailInfluenceSkyPostLoadMaterialMutation ApplySkyBoxColorAfterLoad(
        EnbW3dMaterialUniforms material,
        Vector3 skyBoxColor)
    {
        if (!IsFinite(skyBoxColor))
            throw new ArgumentOutOfRangeException(nameof(skyBoxColor), "SkyBoxColor must be finite.");

        return new EnbCurrentRetailInfluenceSkyPostLoadMaterialMutation(
            material with { Emissive = skyBoxColor },
            ReprocessColor1: false);
    }

    public static uint ApplyDirectMeshPackedShaderMutation(uint packedRetailState)
        => (packedRetailState & DirectMeshShaderClearMask) | DirectMeshShaderSetMask;

    public static bool TryResolveDirectMeshFixedFunctionState(
        EnbW3dRenderShaderRecord authoredShader,
        out EnbW3dFixedFunctionState effectiveState,
        out uint authoredPackedRetailState,
        out string? diagnostic)
    {
        effectiveState = default;
        authoredPackedRetailState = 0u;
        if (!EnbW3dMaterialCompiler.TryDecodeShader(
                authoredShader,
                out EnbW3dFixedFunctionState authoredState,
                out diagnostic))
        {
            return false;
        }

        authoredPackedRetailState = authoredState.PackedRetailState;
        uint mutated = ApplyDirectMeshPackedShaderMutation(authoredPackedRetailState);
        return EnbW3dMaterialCompiler.TryDecodePackedRetailState(
            mutated,
            out effectiveState,
            out diagnostic);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
