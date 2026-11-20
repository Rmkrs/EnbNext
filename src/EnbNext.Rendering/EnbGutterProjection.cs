using System.Numerics;

namespace EnbNext.Rendering;

public readonly record struct EnbGutterProjection(
    double LogicalX,
    double LogicalY,
    bool IsClampedToGutter,
    EnbGutterEdge Edge,
    float IconScale,
    double ReticleRadiusNormalized,
    bool IsInFront)
{
    public double ReticleLogicalX { get; init; }
    public double ReticleLogicalY { get; init; }
    public double HoverRadiusNormalized { get; init; }
    public double ObjectRadiusNormalized { get; init; }
    public double ObjectLogicalX { get; init; }
    public double ObjectLogicalY { get; init; }
    public double ObjectLogicalRadiusX { get; init; }
    public double ObjectLogicalRadiusY { get; init; }
}

public enum EnbGutterEdge
{
    None,
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>
/// Current-client gutter placement recovered from
/// GutterRadarViewUpdateObjectPresentation @ 0x007B7040,
/// FUN_007B71D0, FUN_007B75E0 and FUN_007B5BF0. Positions use Retail's
/// normalized 640x480 gutter limits; rear objects retain their camera-space
/// vertical bearing instead of being assigned to an arbitrary side.
/// </summary>
public static class EnbGutterProjectionRuntime
{
    private const double HorizontalLimit = 0.490625;
    private const double VerticalLimit = 0.4875;
    private const double IconBoundsLimit = 0.4875;
    private const double MinimumProjectedRadius = 0.017677668;
    private const double MinimumClampedReticleRadius = 0.0125;
    private const double MaximumProjectedRadius = 0.15;
    private const float MinimumScaleDistance = 1000f;
    private const float MaximumScaleDistance = 6000f;
    private const float MinimumIconScale = 0.5f;

    public static bool TryResolve(
        EnbResolvedRenderCamera camera,
        Vector3 worldPosition,
        float worldRadius,
        float surfaceDistance,
        out EnbGutterProjection projection)
    {
        projection = default;
        if (!IsFinite(worldPosition) || !float.IsFinite(worldRadius) || worldRadius < 0f ||
            !float.IsFinite(surfaceDistance) || surfaceDistance < 0f)
            return false;

        Vector3 delta = worldPosition - camera.Position;
        float right = Vector3.Dot(delta, camera.Basis.Right);
        float up = Vector3.Dot(delta, camera.Basis.Up);
        float forward = Vector3.Dot(delta, camera.Basis.Forward);
        if (!float.IsFinite(right) || !float.IsFinite(up) || !float.IsFinite(forward))
            return false;

        if (camera.Viewport.PixelWidth <= 0 || camera.Viewport.PixelHeight <= 0)
            return false;

        // Retail view space is -Z forward. Its near-zero replacement is
        // +0.0001, which is -0.0001 in EnbNext's +forward convention.
        double divisor = Math.Abs(forward) <= 0.0001 ? -0.0001 : forward;
        double offsetX = right * camera.Projection.Matrix.M11 * 0.5 / divisor;
        double offsetY = -up * camera.Projection.Matrix.M22 * 0.5 / divisor;
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            return false;

        double objectPhysicalX = (0.5 + offsetX) * camera.Viewport.PixelWidth;
        double objectPhysicalY = (0.5 + offsetY) * camera.Viewport.PixelHeight;
        double projectedRadiusX = Math.Abs(worldRadius * camera.Projection.Matrix.M11 * 0.5 / divisor);
        double projectedRadiusY = Math.Abs(worldRadius * camera.Projection.Matrix.M22 * 0.5 / divisor);

        double reticleRadius = projectedRadiusX;
        double rawOffsetX = offsetX;
        double rawOffsetY = offsetY;

        bool clamped = Math.Abs(offsetX) >= HorizontalLimit ||
                       Math.Abs(offsetY) >= VerticalLimit ||
                       forward <= 0f;
        EnbGutterEdge edge = EnbGutterEdge.None;
        if (clamped)
        {
            // The edge is selected from camera-space bearing, not from the
            // perspective-divided rear coordinate (which reverses at -Z).
            double directionX = right * camera.Projection.Matrix.M11;
            double directionY = -up * camera.Projection.Matrix.M22;
            if (Math.Abs(directionX) <= 1e-12 && Math.Abs(directionY) <= 1e-12)
            {
                // FUN_007B71D0 takes its vertical branch for the exact 180°
                // degeneracy and selects the lower edge for zero camera Y.
                directionY = 1.0;
            }

            double factorX = Math.Abs(directionX) <= 1e-12
                ? double.PositiveInfinity
                : HorizontalLimit / Math.Abs(directionX);
            double factorY = Math.Abs(directionY) <= 1e-12
                ? double.PositiveInfinity
                : VerticalLimit / Math.Abs(directionY);
            double factor = Math.Min(factorX, factorY);
            edge = factorX <= factorY
                ? directionX < 0.0 ? EnbGutterEdge.Left : EnbGutterEdge.Right
                : directionY < 0.0 ? EnbGutterEdge.Top : EnbGutterEdge.Bottom;
            offsetX = directionX * factor;
            offsetY = directionY * factor;
            reticleRadius = ResolveClampedReticleRadius(rawOffsetX, rawOffsetY, reticleRadius);
        }
        double reticlePhysicalX = Math.Clamp(0.5 + offsetX, 0.0, 1.0) * camera.Viewport.PixelWidth;
        double reticlePhysicalY = Math.Clamp(0.5 + offsetY, 0.0, 1.0) * camera.Viewport.PixelHeight;

        double iconOffsetX = offsetX;
        double iconOffsetY = offsetY;
        double hoverRadius = MinimumProjectedRadius;
        if (!clamped)
        {
            // FUN_007B5BF0 applies the object's projected-radius separation
            // only when FUN_007B71D0 reports an on-screen contact.  A gutter
            // contact stays at the exact six-design-pixel edge center; moving
            // it by the object radius was the source of the inward drift seen
            // in the live comparison.
            double normalizedX = 0.5 + iconOffsetX;
            double normalizedY = 0.5 + iconOffsetY;
            hoverRadius = Math.Min(
                Math.Max(reticleRadius - MinimumProjectedRadius, MinimumProjectedRadius),
                MaximumProjectedRadius);
            hoverRadius = ConstrainRadius(normalizedX, hoverRadius);
            hoverRadius = ConstrainRadius(normalizedY, hoverRadius);

            double directionLength = Math.Sqrt(iconOffsetX * iconOffsetX + iconOffsetY * iconOffsetY);
            if (directionLength > 1e-12)
            {
                normalizedX += iconOffsetX / directionLength * hoverRadius;
                normalizedY += iconOffsetY / directionLength * hoverRadius;
            }
            iconOffsetX = normalizedX - 0.5;
            iconOffsetY = normalizedY - 0.5;
        }

        double physicalX = Math.Clamp(0.5 + iconOffsetX, 0.0, 1.0) * camera.Viewport.PixelWidth;
        double physicalY = Math.Clamp(0.5 + iconOffsetY, 0.0, 1.0) * camera.Viewport.PixelHeight;
        projection = new EnbGutterProjection(
            camera.Viewport.ToLogicalX(physicalX),
            camera.Viewport.ToLogicalY(physicalY),
            clamped,
            edge,
            ResolveIconScale(surfaceDistance),
            reticleRadius,
            forward > 0f)
        {
            ReticleLogicalX = camera.Viewport.ToLogicalX(reticlePhysicalX),
            ReticleLogicalY = camera.Viewport.ToLogicalY(reticlePhysicalY),
            HoverRadiusNormalized = hoverRadius,
            ObjectRadiusNormalized = projectedRadiusX,
            ObjectLogicalX = camera.Viewport.ToLogicalX(objectPhysicalX),
            ObjectLogicalY = camera.Viewport.ToLogicalY(objectPhysicalY),
            ObjectLogicalRadiusX = camera.Viewport.ToLogicalX(projectedRadiusX * camera.Viewport.PixelWidth),
            ObjectLogicalRadiusY = camera.Viewport.ToLogicalY(projectedRadiusY * camera.Viewport.PixelHeight),
        };
        return true;
    }

    public static float ResolveIconScale(float surfaceDistance)
    {
        if (!float.IsFinite(surfaceDistance) || surfaceDistance <= MinimumScaleDistance)
            return 1f;
        if (surfaceDistance >= MaximumScaleDistance)
            return MinimumIconScale;
        return 1f - (1f - MinimumIconScale) *
            ((surfaceDistance - MinimumScaleDistance) /
             (MaximumScaleDistance - MinimumScaleDistance));
    }

    private static double ConstrainRadius(double coordinate, double radius)
    {
        double minimum = 0.5 - IconBoundsLimit;
        double maximum = 0.5 + IconBoundsLimit;
        if (radius + coordinate <= maximum)
        {
            if (coordinate - radius < minimum)
                radius = coordinate - minimum;
        }
        else
        {
            radius = maximum - coordinate;
        }
        return radius;
    }

    private static double ResolveClampedReticleRadius(double offsetX, double offsetY, double radius)
    {
        double overflowX = ResolveRetailRadiusOverflow(offsetX);
        double overflowY = ResolveRetailRadiusOverflow(offsetY);
        double maximumOverflow = Math.Max(overflowX, overflowY);
        double reducibleRadius = radius - MinimumClampedReticleRadius;
        double reduction = maximumOverflow < reducibleRadius ? maximumOverflow : reducibleRadius;
        return reduction > 0 ? radius - reduction : radius;
    }

    private static double ResolveRetailRadiusOverflow(double offset)
    {
        if (offset < 0)
            return -offset;
        return offset > 1 ? offset - 1 : 0;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
