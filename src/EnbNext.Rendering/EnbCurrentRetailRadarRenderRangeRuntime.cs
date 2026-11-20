using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail radar-owned attachment gate for navigation-backed render objects.
/// RadarSystemRefreshObjectPresentations (client.exe 0x007BCA70) finds navigation
/// metadata before it removes/adds the object's main RenderObject according to
/// RadarObjectContextIsTargetable (0x007BE3A0).
/// </summary>
public static class EnbCurrentRetailRadarRenderRangeRuntime
{
    public const byte DecorationObjectType = 0x25;

    /// <summary>
    /// Returns whether EnbNext should submit an object whose main RenderObject is
    /// controlled by current retail's radar attachment branch.
    /// Current retail first reads player ScanRange in
    /// RadarSystemRefreshObjectPresentations and then lets
    /// RadarObjectContextIsTargetable add it to non-decoration navigation
    /// signatures. Type 0x25 decorations use their authored Decoration.RadarRange.
    /// </summary>
    public static bool ShouldAttachRadarGatedRenderObject(
        IEnbRenderObject? observer,
        IEnbRenderObject obj,
        int? scanRange,
        IReadOnlyDictionary<ulong, float>? distanceRadii = null,float? observerRadius=null,float? objectRadius=null)
    {
        // CanClientObjectBeTargeted gates type 0x25 on AppearsInRadar. When that
        // gate is false, retail skips this refresh branch rather than removing
        // the RenderObject. Navigation metadata is likewise required before the
        // add/remove branch is entered.
        if (!ShouldEnterRadarAttachmentGate(obj))
        {
            return true;
        }

        if (observer?.Spatial?.Transform is not { } observerTransform ||
            obj.Spatial?.Transform is not { } objectTransform ||
            !IsFinite(observerTransform.Position) ||
            !IsFinite(objectTransform.Position))
        {
            // Retail does not enter the add/remove branch without usable finite
            // spatial state. Preserve the already materialized object in that
            // unresolved state instead of inventing a detach decision.
            return true;
        }

        return EnbCurrentRetailRadarCompatibilityAdapter.IsTargetable(observer, obj, scanRange, distanceRadii,observerRadius,objectRadius);
    }

    /// <summary>
    /// Exact TestObjectWithinTargetableRange formula from client.exe 0x00831F90.
    /// Retail uses a strict comparison at the combined-radius boundary.
    /// </summary>
    public static bool IsWithinTargetableRange(
        Vector3 observerPosition,
        float observerRadius,
        Vector3 objectPosition,
        float objectRadius,
        float targetRange)
    {
        float range = observerRadius + objectRadius + targetRange;
        Vector3 delta = observerPosition - objectPosition;
        float squaredDistance = delta.Y * delta.Y + delta.Z * delta.Z + delta.X * delta.X;
        return squaredDistance < range * range;
    }

    public static float ResolveWorldRadius(IEnbRenderObject obj)
    {
        if (obj.ClientBaseAssetReference is not { } geometry ||
            !float.IsFinite(obj.Scale) ||
            obj.Scale <= 0f)
        {
            return 0f;
        }

        float adjustedRadius = geometry.Radius + geometry.RadiusAdjust;
        if (!float.IsFinite(adjustedRadius) || adjustedRadius <= 0f)
            return 0f;

        float worldRadius = adjustedRadius * obj.Scale;
        return float.IsFinite(worldRadius) && worldRadius > 0f ? worldRadius : 0f;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool ShouldEnterRadarAttachmentGate(IEnbRenderObject obj)
    {
        // 7BCA70 -> 7BE350/6F5980 -> 7BE1C0: eligibility and existing
        // navigation metadata, with no spatial-provider restriction. 73A600
        // creates that metadata for the packet's object independently of type.
        // 7BCA70 uses zero when the player's ScanRange attribute is absent.
        return obj.Radar.Navigation is not null &&
            EnbCurrentRetailRadarCompatibilityAdapter.CanClientObjectBeTargeted(obj);
    }
}
