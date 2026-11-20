using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Backend-neutral current-retail planet presentation mutations that occur
/// after authored W3D material construction. The W3D remains authority for
/// geometry, texture names, mapper classes and all non-PLT_DN mapper state.
/// </summary>
public static class EnbCurrentRetailPlanetRuntime
{
    private const string PlanetRenderObjectPrefix = "plt";
    private const string DayNightTexturePrefix = "plt_dn";
    /// <summary>
    /// Produces the per-apply mapper evaluation for the same retail mutation
    /// without changing the immutable authored material. This is the preferred
    /// GPU handoff because the phase changes with camera/light geometry while
    /// cached mesh/texture resources remain stable.
    /// </summary>
    public static bool TryEvaluateDayNightMapperOverride(
        EnbW3dRenderTextureBinding? texture,
        EnbW3dMapperRuntimeProgram authoredProgram,
        float phase,
        out EnbW3dMapperRuntimeEvaluation evaluation,
        out bool applied,
        out string? diagnostic)
    {
        evaluation = default;
        applied = false;
        diagnostic = null;

        if (!float.IsFinite(phase))
        {
            diagnostic = "Current-retail PLT_DN phase is non-finite.";
            return false;
        }

        if (texture is null || texture.IsUnbound ||
            string.IsNullOrWhiteSpace(texture.LogicalName) ||
            !texture.LogicalName.StartsWith(DayNightTexturePrefix, StringComparison.OrdinalIgnoreCase) ||
            authoredProgram.Kind != EnbW3dMapperRuntimeProgramKind.Linear)
        {
            return true;
        }

        evaluation = new EnbW3dMapperRuntimeEvaluation(
            authoredProgram.BaseUniforms.Scale,
            Vector2.Zero,
            new Vector2(phase, 0f),
            new Vector4(1f, 0f, 0f, 1f),
            ConfiguresBumpEnvironment: false);
        applied = true;
        return true;
    }

}
