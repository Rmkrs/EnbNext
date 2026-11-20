using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Resolves the current-retail RenderObject world sphere used by OrbitalManager
/// visibility and LIGHTRADIUS fallback. The caller owns BaseAsset/RSL/W3D root
/// materialization because that work depends on the installed asset bundle.
/// </summary>
public delegate bool EnbRetailOrbitalWorldSphereResolver(
    EnbRetailOrbitalDefinition orbital,
    Matrix4x4 orbitalWorldTransform,
    out EnbW3dWorldBounds worldSphere,
    out string? diagnostic);

public readonly record struct EnbRetailOrbitalFogEvaluation(
    EnbW3dSceneFogState SceneFog,
    Vector3 FogRgb,
    uint GlobalAmbientRgb24,
    int OrbitalCount,
    int EmittingOrbitalCount,
    int OccludedSampleCount)
{
    public EnbRetailTerrainLightState? TerrainLight { get; init; }
    public EnbRetailTerrainLightState? DominantLight { get; init; }
    public int DominantOrbitalIndex { get; init; } = -1;
    public Vector3 AggregateLightRgb { get; init; }
    public Vector3? SpecialMaterialAmbient { get; init; }
}

public sealed record EnbRetailTerrainLightState(Vector3 Diffuse, Vector3 Direction,
    float Elevation, float Radius, Vector3 Ambient, bool Enabled);

/// <summary>
/// Current retail MainView/OrbitalManager ordinary-world environment semantics
/// used. Packet-derived range/enable state stays in the immutable
/// presentation snapshot; camera-dependent orbital colour is evaluated only at
/// the render boundary where the resolved camera and installed BaseAsset geometry
/// exist. This additionally reproduces the MainView+0x10C global ambient source.
/// </summary>
public static class EnbRetailOrbitalFogEvaluator
{
    public const float CurrentRetailHoursPerMillisecond = 2.777778e-7f;
    public const int CurrentRetailVisibilitySampleCount = 16;
    public const float CurrentRetailVisibilitySphereScale = 0.6f;

    private const float Pi = MathF.PI;
    private const float TwoPi = 2f * MathF.PI;
    private const float CurrentRetailInverseTwoPi = 0.15915494f;

    public static bool TryResolveWorldFogEnabled(
        EnbSceneEnvironmentSnapshot environment,
        out bool enabled,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(environment);
        diagnostic = null;
        switch (environment.PresentationMode)
        {
            case 0:
                enabled = environment.FogFar - environment.FogNear > 10f;
                return true;
            case 1:
            case 5:
                enabled = true;
                return true;
            default:
                enabled = false;
                diagnostic = environment.PresentationMode is null
                    ? "Retail presentation mode has not materialized yet."
                    : $"Retail presentation mode {environment.PresentationMode.Value} is outside the proven MainView fog modes 0/1/5.";
                return false;
        }
    }

    public static bool TryEvaluate(
        EnbSceneEnvironmentSnapshot environment,
        long clientTimeMilliseconds,
        Vector3 cameraPosition,
        EnbRetailOrbitalSceneDefinition scene,
        EnbRetailScalarSplineDatabase splines,
        Vector3 cachedSkyEmissiveRgb,
        EnbRetailOrbitalWorldSphereResolver resolveWorldSphere,
        out EnbRetailOrbitalFogEvaluation evaluation,
        out string? diagnostic,
        bool evaluateModeZeroLight = false)
    {
        if (!TryEvaluateFog(
                environment,
                clientTimeMilliseconds,
                cameraPosition,
                scene,
                splines,
                resolveWorldSphere,
                out EnbRetailOrbitalFogEvaluation fogEvaluation,
                out diagnostic, evaluateModeZeroLight))
        {
            evaluation = default;
            return false;
        }

        if (!TryComputeCurrentRetailGlobalAmbient(
                environment.PresentationMode,
                fogEvaluation.FogRgb,
                cachedSkyEmissiveRgb,
                out Vector3 ambientRgb,
                out diagnostic))
        {
            evaluation = default;
            return false;
        }

        evaluation = fogEvaluation with
        {
            GlobalAmbientRgb24 = PackCurrentRetailAmbientRgb24(ambientRgb),
            SpecialMaterialAmbient = ambientRgb,
            // 0050F58E..59A stores the same sky-derived ambient passed to both
            // scenes in MainView+198. 0046DA70 copies it to Land+1B8..1C0.
            // The orbital light sum at OrbitalManager+40 is not this input.
            TerrainLight = fogEvaluation.TerrainLight is { } terrainLight
                ? terrainLight with { Ambient = ambientRgb } : null,
        };
        return true;
    }

    /// <summary>
    /// Current MainView reloads the orbital manager by resetting it first and
    /// only calling OrbitalManagerLoadIni when orbital%04d.ini was loaded.
    /// Therefore a missing sector orbital INI is not an unresolved environment:
    /// it is the exact reset state, with no orbitals, black fog RGB and black
    /// cached SKYEMISSIVE.
    /// </summary>
    public static bool TryEvaluateMissingOrbitalIniResetState(
        EnbSceneEnvironmentSnapshot environment,
        out EnbRetailOrbitalFogEvaluation evaluation,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(environment);
        evaluation = default;
        diagnostic = null;

        if (!TryResolveWorldFogEnabled(environment, out bool enabled, out diagnostic))
            return false;
        if (!float.IsFinite(environment.FogNear) || !float.IsFinite(environment.FogFar))
        {
            diagnostic = "Server Parameters fog range contains a non-finite value.";
            return false;
        }

        Vector3 resetRgb = Vector3.Zero;
        if (!TryComputeCurrentRetailGlobalAmbient(
                environment.PresentationMode,
                resetRgb,
                resetRgb,
                out Vector3 ambientRgb,
                out diagnostic))
        {
            return false;
        }

        evaluation = new EnbRetailOrbitalFogEvaluation(
            new EnbW3dSceneFogState(enabled, 0u, environment.FogNear, environment.FogFar),
            resetRgb,
            PackCurrentRetailAmbientRgb24(ambientRgb),
            0,
            0,
            0) { SpecialMaterialAmbient=ambientRgb };
        return true;
    }

    public static bool TryEvaluateFog(
        EnbSceneEnvironmentSnapshot environment,
        long clientTimeMilliseconds,
        Vector3 cameraPosition,
        EnbRetailOrbitalSceneDefinition scene,
        EnbRetailScalarSplineDatabase splines,
        EnbRetailOrbitalWorldSphereResolver resolveWorldSphere,
        out EnbRetailOrbitalFogEvaluation evaluation,
        out string? diagnostic,
        bool evaluateModeZeroLight = false)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(splines);
        ArgumentNullException.ThrowIfNull(resolveWorldSphere);

        evaluation = default;
        diagnostic = null;
        if (!TryResolveWorldFogEnabled(environment, out bool enabled, out diagnostic))
            return false;

        if (!float.IsFinite(environment.FogNear) || !float.IsFinite(environment.FogFar))
        {
            diagnostic = "Server Parameters fog range contains a non-finite value.";
            return false;
        }

        if (!enabled && !evaluateModeZeroLight)
        {
            evaluation = new EnbRetailOrbitalFogEvaluation(
                new EnbW3dSceneFogState(false, 0u, environment.FogNear, environment.FogFar),
                Vector3.Zero,
                0u,
                scene.Orbitals.Count,
                scene.Orbitals.Count(static orbital => orbital.EmitsLight),
                0);
            return true;
        }

        if (!IsFinite(cameraPosition))
        {
            diagnostic = "Resolved render-camera position is non-finite.";
            return false;
        }

        float timeHours = clientTimeMilliseconds * CurrentRetailHoursPerMillisecond;
        if (!float.IsFinite(timeHours))
        {
            diagnostic = "Current client time could not be represented in retail orbital hours.";
            return false;
        }

        var frames = new OrbitalFrame[scene.Orbitals.Count];
        for (int i = 0; i < scene.Orbitals.Count; i++)
        {
            EnbRetailOrbitalDefinition orbital = scene.Orbitals[i];
            if (!TryBuildOrbitalFrame(orbital, timeHours, cameraPosition, out OrbitalFrame frame, out diagnostic))
            {
                diagnostic = $"Orbital {orbital.Index}: {diagnostic}";
                return false;
            }

            if (!resolveWorldSphere(orbital, frame.WorldTransform, out EnbW3dWorldBounds sphere, out string? sphereDiagnostic) ||
                !sphere.IsValid)
            {
                diagnostic = $"Orbital {orbital.Index} BaseAsset {orbital.BaseAsset}: {sphereDiagnostic ?? "current-retail world sphere is unavailable"}";
                return false;
            }

            frames[i] = frame with { WorldSphere = sphere };
        }

        Vector3 fogRgb = Vector3.Zero;
        int emitting = 0;
        int occludedSamples = 0;
        bool modeOne = environment.PresentationMode == 1;
        EnbRetailTerrainLightState? terrainLight = null;
        float dominantScore = -1;
        int dominantIndex = -1;
        Vector3 aggregateLight = Vector3.Zero;

        for (int i = 0; i < frames.Length; i++)
        {
            OrbitalFrame frame = frames[i];
            EnbRetailOrbitalDefinition orbital = frame.Definition;
            if (!orbital.EmitsLight)
                continue;
            emitting++;

            float activation = 1f;
            if (modeOne)
            {
                float lightRadius = orbital.LightRadius;
                if (lightRadius == 0f)
                    lightRadius = MathF.Atan(frame.WorldSphere.Radius / orbital.Distance);

                float denominator = lightRadius + lightRadius;
                activation = (frame.ElevationAngle + lightRadius) / denominator;
                if (activation > 1f)
                    activation = 1f;

                // Current OrbitalManager sets visibility to zero when the mode-1
                // activation is below zero and only accumulates a light when the
                // final activation is strictly positive.
                if (!(activation > 0f))
                    continue;
            }

            if (!TrySampleRgb(splines, scene.SkyColorSpline, frame.SplineT, out Vector3 skyColor, out diagnostic))
            {
                diagnostic = $"Orbital {orbital.Index} scene SkyColor '{scene.SkyColorSpline}': {diagnostic}";
                return false;
            }
            if (!TrySampleRgb(splines, orbital.SkyIntensitySpline, frame.SplineT, out Vector3 skyIntensity, out diagnostic))
            {
                diagnostic = $"Orbital {orbital.Index} SkyIntensity '{orbital.SkyIntensitySpline}': {diagnostic}";
                return false;
            }

            float visibility = ComputeVisibility(cameraPosition, i, frames, ref occludedSamples);
            fogRgb += skyColor * skyIntensity * (activation * visibility);
            if (modeOne || evaluateModeZeroLight)
            {
                // 004F45A0 selects by intensity squared * activation * visibility,
                // retains the unattenuated dominant RGB.
                if (!TrySampleRgb(splines, scene.LightColorSpline, frame.SplineT, out var color, out diagnostic) ||
                    !TrySampleRgb(splines, orbital.LightIntensitySpline, frame.SplineT, out var intensity, out diagnostic))
                    return false;
                Vector3 light = color * intensity;
                float score = intensity.LengthSquared() * activation * visibility;
                aggregateLight += light * activation * visibility;
                if (terrainLight is null || score > dominantScore)
                {
                    dominantScore = score;
                    dominantIndex = orbital.Index;
                    float radius = orbital.LightRadius != 0 ? orbital.LightRadius :
                        MathF.Atan(frame.WorldSphere.Radius / orbital.Distance);
                    terrainLight = new(Vector3.Clamp(light, Vector3.Zero, Vector3.One),
                        frame.Direction, frame.ElevationAngle, radius, Vector3.Zero, true);
                }
            }
        }

        fogRgb = Vector3.Clamp(fogRgb, Vector3.Zero, Vector3.One);
        uint rgb24 = PackCurrentRetailFogRgb24(fogRgb);
        evaluation = new EnbRetailOrbitalFogEvaluation(
            new EnbW3dSceneFogState(enabled, rgb24, environment.FogNear, environment.FogFar),
            fogRgb,
            0u,
            frames.Length,
            emitting,
            occludedSamples)
        {
            TerrainLight = modeOne ? (terrainLight ?? new(Vector3.Zero, Vector3.Zero, 0, 0, Vector3.Zero, false)) : null,
            DominantLight = terrainLight,
            DominantOrbitalIndex = dominantIndex,
            AggregateLightRgb = Vector3.Clamp(aggregateLight, Vector3.Zero, Vector3.One),
        };
        return true;
    }

    public static bool TrySampleSkyEmissiveAtLoad(
        EnbRetailOrbitalSceneDefinition scene,
        EnbRetailScalarSplineDatabase splines,
        out Vector3 rgb,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(splines);
        if (!TrySampleRgb(splines, scene.SkyEmissiveSpline, 0f, out rgb, out diagnostic))
        {
            diagnostic = $"scene SkyEmissive '{scene.SkyEmissiveSpline}': {diagnostic}";
            return false;
        }
        return true;
    }

    public static bool TryComputeCurrentRetailGlobalAmbient(
        int? presentationMode,
        Vector3 fogRgb,
        Vector3 cachedSkyEmissiveRgb,
        out Vector3 ambientRgb,
        out string? diagnostic)
    {
        ambientRgb = default;
        diagnostic = null;
        if (!IsFinite(fogRgb) || !IsFinite(cachedSkyEmissiveRgb))
        {
            diagnostic = "Current-retail ambient input contains a non-finite RGB component.";
            return false;
        }

        switch (presentationMode)
        {
            case 0:
            case 5:
                ambientRgb = cachedSkyEmissiveRgb;
                return true;
            case 1:
                // MainViewPrepareCameraForRender @ 0050F1B0, ordinary
                // param_2==0 pass: add the already-clamped OrbitalManager fog
                // accumulator to cached SKYEMISSIVE, then clamp only the upper
                // side of each channel to 1.0. There is no lower clamp here.
                Vector3 sum = fogRgb + cachedSkyEmissiveRgb;
                ambientRgb = new Vector3(
                    MathF.Min(1f, sum.X),
                    MathF.Min(1f, sum.Y),
                    MathF.Min(1f, sum.Z));
                return true;
            default:
                diagnostic = presentationMode is null
                    ? "Retail presentation mode has not materialized yet."
                    : $"Retail presentation mode {presentationMode.Value} is outside the proven MainView ambient modes 0/1/5.";
                return false;
        }
    }

    public static uint PackCurrentRetailAmbientRgb24(Vector3 rgb)
    {
        if (!IsFinite(rgb))
            throw new ArgumentOutOfRangeException(nameof(rgb));

        // WW3DRenderScene @ 00917C80 uses the same temporary x87 truncate
        // control word as the fog setter, but performs no pre-pack RGB clamp.
        // Preserve the signed integer shift/OR behavior before masking to the
        // D3DRS_AMBIENT RGB24 payload.
        int r = (int)MathF.Truncate(rgb.X * 255f);
        int g = (int)MathF.Truncate(rgb.Y * 255f);
        int b = (int)MathF.Truncate(rgb.Z * 255f);
        return unchecked((uint)((r << 16) | (g << 8) | b)) & 0x00FF_FFFFu;
    }

    public static uint PackCurrentRetailFogRgb24(Vector3 clampedRgb)
    {
        if (!IsFinite(clampedRgb))
            throw new ArgumentOutOfRangeException(nameof(clampedRgb));

        Vector3 rgb = Vector3.Clamp(clampedRgb, Vector3.Zero, Vector3.One);
        // FUN_00965460 temporarily switches the x87 control word to truncate
        // before FISTP, so this must not use round-to-nearest conversion.
        uint r = (uint)MathF.Truncate(rgb.X * 255f);
        uint g = (uint)MathF.Truncate(rgb.Y * 255f);
        uint b = (uint)MathF.Truncate(rgb.Z * 255f);
        return (r << 16) | (g << 8) | b;
    }

    public static bool TryBuildOrbitalWorldTransform(
        EnbRetailOrbitalDefinition orbital,
        float timeHours,
        Vector3 cameraPosition,
        out Matrix4x4 worldTransform,
        out Vector3 direction,
        out float elevationAngle,
        out float splineT)
    {
        ArgumentNullException.ThrowIfNull(orbital);
        worldTransform = default;
        direction = default;
        elevationAngle = 0f;
        splineT = 0f;

        if (!float.IsFinite(timeHours) || !IsFinite(cameraPosition) ||
            !float.IsFinite(orbital.StartAngle) || !float.IsFinite(orbital.Rx) || !float.IsFinite(orbital.Rz) ||
            !float.IsFinite(orbital.Distance) || !float.IsFinite(orbital.OrbitsPerHour))
        {
            return false;
        }

        float rz = orbital.Rz * Pi;
        float rx = orbital.Rx * Pi;
        // OrbitalObject::Update @ 004F2CB0 uses ORBITSPERHOUR here.
        // PHASEPERHOUR is consumed later by OrbitalManager, after fog
        // accumulation/clamping, by a planet-specific presentation helper; it
        // does not alter the orbital world transform used by fog visibility.
        float phase = -(2f * timeHours * orbital.OrbitsPerHour + orbital.StartAngle) * Pi;

        float cosRz = MathF.Cos(rz);
        float sinRz = MathF.Sin(rz);
        float cosRx = MathF.Cos(rx);
        float sinRx = MathF.Sin(rx);
        float cosPhase = MathF.Cos(phase);
        float sinPhase = MathF.Sin(phase);

        direction = new Vector3(
            -cosRz * sinPhase - sinRz * sinRx * cosPhase,
            -sinRz * sinPhase + cosRz * sinRx * cosPhase,
            -cosRx * cosPhase);

        Vector3 axis = new(
            cosPhase * cosRz - sinRz * sinRx * sinPhase,
            sinRz * cosPhase + cosRz * sinRx * sinPhase,
            -cosRx * sinPhase);
        Vector3 negDirection = -direction;
        Vector3 cross = Vector3.Cross(axis, negDirection);
        Vector3 position = cameraPosition + orbital.Distance * direction;

        // Retail stores a row-major 3x4 basis with columns {-direction,
        // axis×-direction, axis}. System.Numerics transforms row vectors, so the
        // 3x3 basis is transposed and translation occupies M41..M43.
        worldTransform = new Matrix4x4(
            negDirection.X, negDirection.Y, negDirection.Z, 0f,
            cross.X, cross.Y, cross.Z, 0f,
            axis.X, axis.Y, axis.Z, 0f,
            position.X, position.Y, position.Z, 1f);

        elevationAngle = (Pi * 0.5f) - MathF.Acos(direction.Z);
        splineT = elevationAngle / Pi + 0.5f;
        return IsFinite(worldTransform) && IsFinite(direction) && float.IsFinite(elevationAngle) && float.IsFinite(splineT);
    }

    /// <summary>
    /// Current client FUN_0075B8F0 phase used by AdjustDayNight for ordinary
    /// render objects whose names begin with "plt". The camera-to-object
    /// vector is normalized, the cross magnitude is signed by cross.z, the
    /// wrapped atan2 angle is converted to one turn, and retail stores
    /// 1-angle/(2*pi) as the PLT_DN mapper U offset.
    /// </summary>
    public static bool TryComputeOrdinarySceneDayNightPhase(
        Vector3 renderObjectPosition,
        Vector3 cameraPosition,
        Vector3 dominantLightDirection,
        out float phase)
    {
        phase = 0f;
        if (!IsFinite(renderObjectPosition) ||
            !IsFinite(cameraPosition) ||
            !IsFinite(dominantLightDirection))
        {
            return false;
        }

        Vector3 view = cameraPosition - renderObjectPosition;
        float lengthSquared = view.LengthSquared();
        if (!float.IsFinite(lengthSquared))
            return false;
        if (lengthSquared != 0f)
            view *= 1f / MathF.Sqrt(lengthSquared);

        Vector3 cross = Vector3.Cross(view, dominantLightDirection);
        float signedCrossLength = cross.Length();
        if (cross.Z < 0f)
            signedCrossLength = -signedCrossLength;
        float dot = Vector3.Dot(view, dominantLightDirection);
        if (!float.IsFinite(signedCrossLength) || !float.IsFinite(dot))
            return false;

        // Retail keeps FPATAN and the following multiply/subtract in x87
        // extended precision, loading 2π and 1/(2π) from 32-bit constants,
        // then stores the final phase as float. Use double for that interval
        // rather than prematurely rounding atan2 to a float.
        double angle = Math.Atan2(signedCrossLength, dot);
        if (angle < 0d)
            angle += TwoPi;
        phase = (float)(1d - (angle * CurrentRetailInverseTwoPi));
        return float.IsFinite(phase);
    }

    private static bool TryBuildOrbitalFrame(
        EnbRetailOrbitalDefinition orbital,
        float timeHours,
        Vector3 cameraPosition,
        out OrbitalFrame frame,
        out string? diagnostic)
    {
        frame = default;
        diagnostic = null;
        if (!TryBuildOrbitalWorldTransform(
                orbital,
                timeHours,
                cameraPosition,
                out Matrix4x4 world,
                out Vector3 direction,
                out float elevationAngle,
                out float splineT))
        {
            diagnostic = "orbital transform/elevation is non-finite.";
            return false;
        }

        frame = new OrbitalFrame(orbital, world, direction, elevationAngle, splineT, default);
        return true;
    }

    private static bool TrySampleRgb(
        EnbRetailScalarSplineDatabase splines,
        string baseName,
        float time,
        out Vector3 rgb,
        out string? diagnostic)
    {
        rgb = default;
        diagnostic = null;
        if (!splines.TrySample(baseName, 'R', time, out float r))
        {
            diagnostic = $"missing scalar spline {baseName}_R.";
            return false;
        }
        if (!splines.TrySample(baseName, 'G', time, out float g))
        {
            diagnostic = $"missing scalar spline {baseName}_G.";
            return false;
        }
        if (!splines.TrySample(baseName, 'B', time, out float b))
        {
            diagnostic = $"missing scalar spline {baseName}_B.";
            return false;
        }
        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
        {
            diagnostic = $"scalar spline {baseName} produced a non-finite RGB component.";
            return false;
        }

        rgb = new Vector3(r, g, b);
        return true;
    }

    private static float ComputeVisibility(
        Vector3 cameraPosition,
        int sourceIndex,
        IReadOnlyList<OrbitalFrame> orbitals,
        ref int totalOccludedSamples)
    {
        return ComputeVisibility(cameraPosition, sourceIndex,
            orbitals.Select(o => o.WorldSphere).ToArray(), ref totalOccludedSamples);
    }

    /// <summary>OrbitalManager visibility shared by sky fog and its dazzle provider.</summary>
    public static float ComputeVisibility(Vector3 cameraPosition, int sourceIndex,
        IReadOnlyList<EnbW3dWorldBounds> orbitals, ref int totalOccludedSamples)
    {
        EnbW3dWorldBounds source = orbitals[sourceIndex];
        float sampleRadius = source.Radius * CurrentRetailVisibilitySphereScale;
        int blocked = 0;

        for (int n = 0; n < CurrentRetailVisibilitySampleCount; n++)
        {
            float u = Halton(n, 2);
            float v = Halton(n, 3);
            float radial = 2f * MathF.Sqrt((1f - v) * v) * sampleRadius;
            float phi = u * TwoPi;
            Vector3 sample = new(
                source.Center.X + MathF.Cos(phi) * radial,
                source.Center.Y + MathF.Sin(phi) * radial,
                source.Center.Z + (1f - 2f * v) * sampleRadius);

            bool occluded = false;
            for (int other = 0; other < orbitals.Count; other++)
            {
                if (other == sourceIndex)
                    continue;
                if (SegmentIntersectsSphere(cameraPosition, sample, orbitals[other]))
                {
                    occluded = true;
                    break;
                }
            }

            if (occluded)
                blocked++;
        }

        totalOccludedSamples += blocked;
        return (CurrentRetailVisibilitySampleCount - blocked) * (1f / CurrentRetailVisibilitySampleCount);
    }

    // 004F28A0 initializes +6C to one; 004F45A0 updates it only for
    // emitters, suppressing below-horizon mode-1 owners. 004F5090 returns
    // this value to 0075DEE0, independently of light accumulation.
    public static float ComputeDazzleVisibility(int mode, EnbRetailOrbitalDefinition orbital,
        float elevation, Vector3 cameraPosition, int sourceIndex,
        IReadOnlyList<EnbW3dWorldBounds> spheres)
    {
        if (!orbital.EmitsLight) return 1f;
        float radius = orbital.LightRadius != 0 ? orbital.LightRadius :
            MathF.Atan(spheres[sourceIndex].Radius / orbital.Distance);
        if (mode == 1 && (elevation + radius) / (radius + radius) < 0f) return 0f;
        int blocked = 0;
        return ComputeVisibility(cameraPosition, sourceIndex, spheres, ref blocked);
    }

    private static bool SegmentIntersectsSphere(Vector3 start, Vector3 end, EnbW3dWorldBounds sphere)
    {
        Vector3 delta = end - start;
        float length = delta.Length();
        if (!(length > 0f) || !float.IsFinite(length))
            return false;
        Vector3 direction = delta / length;
        Vector3 toCenter = sphere.Center - start;
        float projection = Vector3.Dot(toCenter, direction);
        float discriminant = sphere.Radius * sphere.Radius - (toCenter.LengthSquared() - projection * projection);
        if (discriminant < 0f)
            return false;

        float root = MathF.Sqrt(discriminant);
        float distance = projection - root;
        if (distance < 0f)
        {
            distance = projection + root;
            if (distance < 0f)
                return false;
        }

        // Current FUN_008C9370 divides the hit distance by the finite ray
        // length and only accepts a hit fraction strictly below the result's
        // initial 1.0 value. An intersection exactly at the sample is not an
        // occluder.
        return distance / length < 1f;
    }

    private static float Halton(int index, int basis)
    {
        float result = 0f;
        float fraction = 1f / basis;
        int value = index;
        while (value > 0)
        {
            result += fraction * (value % basis);
            value /= basis;
            fraction /= basis;
        }
        return result;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private readonly record struct OrbitalFrame(
        EnbRetailOrbitalDefinition Definition,
        Matrix4x4 WorldTransform,
        Vector3 Direction,
        float ElevationAngle,
        float SplineT,
        EnbW3dWorldBounds WorldSphere);
}
