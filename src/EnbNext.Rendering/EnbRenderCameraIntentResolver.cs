using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Validated normal-camera intent derived from the immutable presentation
/// snapshot. Mode geometry remains a pure lookup/scaling operation; active
/// first/third-person selection is owned by <see cref="EnbNormalCameraController"/>.
/// </summary>
public readonly record struct EnbChaseCameraModeGeometry(
    int ModeIndex,
    float HeadingRadians,
    float ScaledDistance,
    float TiltRadians,
    float HorizontalFieldOfViewRadians,
    float ElevationOffsetRadians);

public static class EnbRenderCameraIntentResolver
{
    public static bool TryResolveModeGeometry(
        EnbSceneCameraStateSnapshot cameraState,
        int modeIndex,
        out EnbChaseCameraModeGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        geometry = default;

        EnbSceneCameraModeSnapshot? mode = cameraState.Modes
            .FirstOrDefault(candidate => candidate.Index == modeIndex);
        if (mode is null ||
            !float.IsFinite(cameraState.ChaseDistanceScale) ||
            cameraState.ChaseDistanceScale <= 0f ||
            !IsFinite(mode.HeadingRadians) ||
            !IsFinite(mode.Distance) ||
            !IsFinite(mode.TiltRadians) ||
            !IsFinite(mode.HorizontalFieldOfViewRadians) ||
            !IsFinite(mode.ElevationOffsetRadians))
        {
            return false;
        }

        float scaledDistance = mode.Distance * cameraState.ChaseDistanceScale;
        if (!float.IsFinite(scaledDistance) ||
            mode.HorizontalFieldOfViewRadians <= 0f ||
            mode.HorizontalFieldOfViewRadians >= MathF.PI)
        {
            return false;
        }

        geometry = new EnbChaseCameraModeGeometry(
            mode.Index,
            mode.HeadingRadians,
            scaledDistance,
            mode.TiltRadians,
            mode.HorizontalFieldOfViewRadians,
            mode.ElevationOffsetRadians);
        return true;
    }

    public static bool TryResolveNormalClipRange(
        EnbSceneCameraStateSnapshot cameraState,
        out EnbRenderClipRange clipRange)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        clipRange = new EnbRenderClipRange(
            cameraState.NormalClipRange.NearPlane,
            cameraState.NormalClipRange.FarPlane);
        return clipRange.IsValid;
    }

    private static bool IsFinite(float value) => float.IsFinite(value);
}
