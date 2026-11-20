using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbRetailMainViewLightingEvaluation(
    uint GlobalAmbientRgb24,
    ImmutableArray<EnbW3dLightSlot> DirectionalLights,
    bool AmbientOverridden)
{
    public Vector3? SpecialMaterialAmbient { get; init; }
    public int EnabledDirectionalLightCount => DirectionalLights.Count(static slot => slot.Enabled);
}

/// <summary>
/// Current-retail ordinary MainView mode-0 directional-light rig. Retail owns
/// four persistent directional LightClass objects and rewrites their diffuse,
/// intensity and direction from OrbitalManager plus light_sectorNNNN.ini before
/// adding them to the ordinary scene each frame.
/// </summary>
public static class EnbRetailMainViewLightingEvaluator
{
    private const float ConfigurationThreshold = 0.0001f;

    /// <summary>0050F1B0: every mode other than 0 rebuilds only light +0x144 from the
    /// OrbitalManager dominant colour/direction (0050F843, 0050FB5B..); mode 1 feeds the
    /// terrain-selected light, mode 5 the ordinary dominant orbital. Other modes keep the rig.</summary>
    public static EnbW3dLightEnvironment? EvaluateOrbitalRig(int? mode, uint ambientRgb24, EnbRetailOrbitalFogEvaluation fog)
        => mode == 1 ? fog.TerrainLight is { } planet ? EvaluateModeOne(ambientRgb24, planet) : null
         : mode == 5 ? EvaluateModeOne(ambientRgb24, fog.DominantLight ?? new(Vector3.Zero, Vector3.Zero, 0, 0, Vector3.Zero, false))
         : null;

    public static EnbW3dLightEnvironment EvaluateModeOne(uint ambientRgb24, EnbRetailTerrainLightState dominant)
    {
        // 0050F1B0 mode 1 removes the four ordinary lights, then installs only
        // the dominant orbital light with its negated surface-to-light direction.
        // The authored mode-0 KEY/REVERSE/CAMERA overrides are not used here.
        var direction = dominant.Direction.LengthSquared() > 0
            ? -Vector3.Normalize(dominant.Direction) : Vector3.Zero;
        return EnbW3dLightEnvironment.CreateCurrentRetail(ambientRgb24,
            [new EnbW3dLightSlot(dominant.Enabled, new EnbW3dCompiledLight(
                EnbW3dLightType.Directional, dominant.Diffuse, Vector3.Zero, Vector3.Zero,
                Vector3.Zero, direction, 100f, 1f, 1f, 0f, 0f, MathF.PI / 4f, 0.70685834f))]);
    }

    public static bool TryEvaluateModeZero(EnbResolvedRenderCamera camera,
        EnbRetailMainViewLightSectorDefinition authored,uint baseGlobalAmbientRgb24,
        EnbRetailTerrainLightState? dominant,out EnbRetailMainViewLightingEvaluation evaluation,out string? diagnostic)
    {
        evaluation=default!;diagnostic=null;
        bool hasDominantEmitter=dominant?.Enabled==true;
        Vector3 dominantDirection=hasDominantEmitter?dominant!.Direction:Vector3.UnitZ;
        Vector3 dominantRgb=hasDominantEmitter?dominant!.Diffuse:Vector3.Zero;
        EnbRetailMainViewLightEntry[] entries =
        [
            authored.Key,
            authored.ReverseKey,
            authored.Camera,
            authored.ReverseCamera,
        ];
        Vector3[] bases =
        [
            dominantDirection,
            -dominantDirection,
            // MainViewPrepareCameraForRender reads Matrix3D column 2 for
            // CAMERA and its negation for RCAMERA. Current retail's camera
            // looks down local -Z, so that column maps to the opposite of
            // EnbNext's canonical viewing-forward vector.
            -camera.Basis.Forward,
            camera.Basis.Forward,
        ];

        var lights = ImmutableArray.CreateBuilder<EnbW3dLightSlot>(4);
        for (int i = 0; i < entries.Length; i++)
        {
            EnbRetailMainViewLightEntry entry = entries[i];
            if (!IsFinite(entry.Rgb) || !float.IsFinite(entry.Intensity) || !IsFinite(entry.DirectionOffset))
            {
                diagnostic = $"MainView authored light entry {i} contains a non-finite component.";
                return false;
            }

            bool hasOverride = entry.HasColorOverride;
            Vector3 diffuse = i == 0 ? dominantRgb : Vector3.Zero;
            float intensity = 1f;
            if (hasOverride)
            {
                diffuse = entry.Rgb;
                intensity = entry.Intensity;
            }

            if (!TryAdjustDirection(bases[i], entry.DirectionOffset, out Vector3 direction))
            {
                diagnostic = $"MainView authored light entry {i} did not produce a finite direction.";
                return false;
            }

            bool enabled = i == 0 ? hasDominantEmitter : hasOverride;
            lights.Add(new EnbW3dLightSlot(
                enabled,
                new EnbW3dCompiledLight(
                    EnbW3dLightType.Directional,
                    diffuse * intensity,
                    Vector3.Zero,
                    Vector3.Zero,
                    Vector3.Zero,
                    direction,
                    100f,
                    1f,
                    1f,
                    0f,
                    0f,
                    MathF.PI / 4f,
                    0.70685834f)));
        }

        bool ambientOverridden = authored.Ambient.HasColorOverride;
        uint ambient = baseGlobalAmbientRgb24;
        if (ambientOverridden)
        {
            if (!IsFinite(authored.Ambient.Rgb) || !float.IsFinite(authored.Ambient.Intensity))
            {
                diagnostic = "MainView authored ambient entry contains a non-finite component.";
                return false;
            }
            ambient = EnbRetailOrbitalFogEvaluator.PackCurrentRetailAmbientRgb24(
                authored.Ambient.Rgb * authored.Ambient.Intensity);
        }

        evaluation = new EnbRetailMainViewLightingEvaluation(
            ambient,
            lights.MoveToImmutable(),
            ambientOverridden) { SpecialMaterialAmbient=ambientOverridden?authored.Ambient.Rgb*authored.Ambient.Intensity:null };
        return true;
    }

    private static bool TryAdjustDirection(Vector3 baseDirection, Vector3 authoredOffset, out Vector3 lightDirection)
    {
        lightDirection = default;
        if (!TryNormalize(baseDirection, out Vector3 basis))
            return false;

        Vector3 adjusted = basis;
        if (authoredOffset.LengthSquared() > ConfigurationThreshold)
        {
            Vector3 perpendicular = new(-basis.Y, basis.X, 0f);
            if (perpendicular.LengthSquared() < ConfigurationThreshold)
                perpendicular = new Vector3(basis.Z - basis.Y, basis.X, -basis.X);
            if (!TryNormalize(perpendicular, out perpendicular))
                return false;

            Vector3 cross = Vector3.Cross(basis, perpendicular);
            if (!TryNormalize(cross, out cross))
                return false;

            adjusted = basis * (1f + authoredOffset.X) +
                       perpendicular * authoredOffset.Y +
                       cross * authoredOffset.Z;
            if (!TryNormalize(adjusted, out adjusted))
                return false;
        }

        // MainView stores the negated adjusted vector in LightClass::Direction;
        // fixed-function evaluation negates D3DLIGHT_DIRECTIONAL.Direction again
        // to obtain the surface-to-light vector.
        lightDirection = -adjusted;
        return IsFinite(lightDirection);
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        normalized = default;
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared == 0f)
            return false;
        normalized = value / MathF.Sqrt(lengthSquared);
        return IsFinite(normalized);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
