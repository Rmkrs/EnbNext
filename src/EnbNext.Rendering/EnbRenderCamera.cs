using System.Numerics;

namespace EnbNext.Rendering;

public readonly record struct EnbRenderClipRange(float NearPlane, float FarPlane)
{
    public bool IsValid =>
        float.IsFinite(NearPlane) &&
        float.IsFinite(FarPlane) &&
        NearPlane > 0f &&
        FarPlane > NearPlane;
}

/// <summary>
/// Camera basis expressed in EnbNext's canonical right-handed world semantics:
/// +X forward, +Y left, +Z up. Screen right is therefore local -Y.
/// </summary>
public readonly record struct EnbRenderCameraBasis(
    Vector3 Forward,
    Vector3 Right,
    Vector3 Up)
{
    public static bool TryCreate(Quaternion worldOrientation, out EnbRenderCameraBasis basis)
    {
        basis = default;
        if (!TryNormalize(worldOrientation, out Quaternion normalized))
            return false;

        Vector3 forward = Vector3.Transform(Vector3.UnitX, normalized);
        Vector3 right = Vector3.Transform(-Vector3.UnitY, normalized);
        Vector3 up = Vector3.Transform(Vector3.UnitZ, normalized);
        if (!TryNormalize(forward, out forward) ||
            !TryNormalize(right, out right) ||
            !TryNormalize(up, out up))
        {
            return false;
        }

        basis = new EnbRenderCameraBasis(forward, right, up);
        return true;
    }

    private static bool TryNormalize(Quaternion value, out Quaternion normalized)
    {
        normalized = default;
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
            return false;

        normalized = Quaternion.Normalize(value);
        return IsFinite(normalized);
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        normalized = default;
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
            return false;

        normalized = Vector3.Normalize(value);
        return IsFinite(normalized);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>
/// Backend-independent horizontal-FOV projection. Canonical camera view space
/// is +X screen-right, +Y screen-up, -Z forward with NDC depth 0..1.
/// A concrete GPU backend converts from this one canonical boundary if needed.
/// </summary>
public readonly record struct EnbPerspectiveProjection(
    double HorizontalFieldOfViewRadians,
    double VerticalFieldOfViewRadians,
    double AspectRatio,
    EnbRenderClipRange ClipRange,
    Matrix4x4 Matrix)
{
    /// <summary>Authoritative float words stored by the retail projection
    /// setter. Kept separately from the backend projection matrix.</summary>
    public Vector4? StoredProjectionBounds { get; init; }
    public bool IsOrthographic => Matrix.M34 == 0f && Matrix.M44 == 1f;

    public double OrthographicHalfWidth => IsOrthographic ? 1.0 / Matrix.M11 : double.NaN;

    public double OrthographicHalfHeight => IsOrthographic ? 1.0 / Matrix.M22 : double.NaN;

    public static bool TryCreateHorizontalFov(
        EnbRenderViewport viewport,
        double horizontalFieldOfViewRadians,
        EnbRenderClipRange clipRange,
        out EnbPerspectiveProjection projection)
        => TryCreateHorizontalFov(
            viewport,
            horizontalFieldOfViewRadians,
            viewport.AspectRatio,
            clipRange,
            out projection);

    public static bool TryCreateHorizontalFov(
        EnbRenderViewport viewport,
        double horizontalFieldOfViewRadians,
        double projectionAspectRatio,
        EnbRenderClipRange clipRange,
        out EnbPerspectiveProjection projection)
    {
        projection = default;
        if (viewport.PixelWidth <= 1 || viewport.PixelHeight <= 1 ||
            !double.IsFinite(horizontalFieldOfViewRadians) ||
            horizontalFieldOfViewRadians <= 0 || horizontalFieldOfViewRadians >= Math.PI ||
            !double.IsFinite(projectionAspectRatio) || projectionAspectRatio <= 0 ||
            !clipRange.IsValid)
        {
            return false;
        }

        double aspect = projectionAspectRatio;
        double horizontalTangent = Math.Tan(horizontalFieldOfViewRadians * 0.5);
        if (!double.IsFinite(horizontalTangent) || horizontalTangent <= 0)
        {
            return false;
        }

        double verticalTangent = horizontalTangent / aspect;
        double verticalFieldOfViewRadians = 2.0 * Math.Atan(verticalTangent);
        double xScale = 1.0 / horizontalTangent;
        double yScale = 1.0 / verticalTangent;
        if (!double.IsFinite(verticalFieldOfViewRadians) ||
            !double.IsFinite(xScale) || !double.IsFinite(yScale))
        {
            return false;
        }

        if(!EnbCurrentRetailCameraFieldOfView.TryCompute((float)horizontalFieldOfViewRadians,-1f,
            (float)aspect,out Vector4 storedBounds,out _))return false;

        Matrix4x4 matrix=EnbRetailProjectionMatrix.ComputeBackend(storedBounds,clipRange,false);

        projection = new EnbPerspectiveProjection(
            horizontalFieldOfViewRadians,
            verticalFieldOfViewRadians,
            aspect,
            clipRange,
            matrix) {StoredProjectionBounds=storedBounds};
        return true;
    }

    public static bool TryCreateOrthographicBounds(
        EnbRenderViewport viewport,
        double halfWidth,
        double halfHeight,
        EnbRenderClipRange clipRange,
        out EnbPerspectiveProjection projection)
    {
        projection = default;
        if (viewport.PixelWidth <= 1 || viewport.PixelHeight <= 1 ||
            !double.IsFinite(halfWidth) || halfWidth <= 0 ||
            !double.IsFinite(halfHeight) || halfHeight <= 0 ||
            !clipRange.IsValid)
        {
            return false;
        }

        var storedBounds=new Vector4(-(float)halfWidth,-(float)halfHeight,(float)halfWidth,(float)halfHeight);
        Matrix4x4 matrix=EnbRetailProjectionMatrix.ComputeBackend(storedBounds,clipRange,true);

        projection = new EnbPerspectiveProjection(
            0.0,
            0.0,
            halfWidth / halfHeight,
            clipRange,
            matrix) {StoredProjectionBounds=storedBounds};
        return true;
    }

    public bool TryProjectViewPoint(Vector3 viewPoint, out Vector3 normalizedDeviceCoordinate)
    {
        normalizedDeviceCoordinate = default;
        if (!IsFinite(viewPoint))
            return false;

        Vector4 clip = Vector4.Transform(new Vector4(viewPoint, 1f), Matrix);
        if (!IsFinite(clip) || clip.W <= 0f)
            return false;

        Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        if (!IsFinite(ndc))
            return false;

        normalizedDeviceCoordinate = ndc;
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);
}

public readonly record struct EnbRenderProjectionResult(
    double PixelX,
    double PixelY,
    float Depth,
    float Distance,
    Vector3 NormalizedDeviceCoordinate);

public readonly record struct EnbRenderSphereProjectionResult(
    double PixelX,
    double PixelY,
    double PixelRadiusX,
    double PixelRadiusY,
    float Depth);

/// <summary>
/// Backend-independent resolved render camera for one presentation frame.
/// Camera controllers own pose/state selection; this type owns the canonical
/// world-to-view basis and projection contract consumed by rendering.
/// </summary>
public readonly record struct EnbResolvedRenderCamera(
    Vector3 Position,
    Quaternion WorldOrientation,
    EnbRenderCameraBasis Basis,
    EnbRenderViewport Viewport,
    EnbPerspectiveProjection Projection)
{
    /// <summary>Stored camera viewport coordinates consumed by00941AF0.
    /// The full camera surface uses the proven943B70 initialization.</summary>
    public Vector4 CurrentRetailDetailViewport { get; init; }=new(0,0,1,1);
    public static bool TryCreate(
        Vector3 position,
        Quaternion worldOrientation,
        EnbRenderViewport viewport,
        double horizontalFieldOfViewRadians,
        EnbRenderClipRange clipRange,
        out EnbResolvedRenderCamera camera)
        => TryCreate(
            position,
            worldOrientation,
            viewport,
            horizontalFieldOfViewRadians,
            viewport.AspectRatio,
            clipRange,
            out camera);

    public static bool TryCreate(
        Vector3 position,
        Quaternion worldOrientation,
        EnbRenderViewport viewport,
        double horizontalFieldOfViewRadians,
        double projectionAspectRatio,
        EnbRenderClipRange clipRange,
        out EnbResolvedRenderCamera camera)
    {
        camera = default;
        if (!IsFinite(position) ||
            !EnbRenderCameraBasis.TryCreate(worldOrientation, out EnbRenderCameraBasis basis) ||
            !EnbPerspectiveProjection.TryCreateHorizontalFov(
                viewport,
                horizontalFieldOfViewRadians,
                projectionAspectRatio,
                clipRange,
                out EnbPerspectiveProjection projection))
        {
            return false;
        }

        Quaternion normalized = Quaternion.Normalize(worldOrientation);
        camera = new EnbResolvedRenderCamera(position, normalized, basis, viewport, projection);
        return true;
    }

    public static bool TryCreateOrthographic(
        Vector3 position,
        Quaternion worldOrientation,
        EnbRenderViewport viewport,
        double halfWidth,
        double halfHeight,
        EnbRenderClipRange clipRange,
        out EnbResolvedRenderCamera camera)
    {
        camera = default;
        if (!IsFinite(position) ||
            !EnbRenderCameraBasis.TryCreate(worldOrientation, out EnbRenderCameraBasis basis) ||
            !EnbPerspectiveProjection.TryCreateOrthographicBounds(
                viewport,
                halfWidth,
                halfHeight,
                clipRange,
                out EnbPerspectiveProjection projection))
        {
            return false;
        }

        Quaternion normalized = Quaternion.Normalize(worldOrientation);
        camera = new EnbResolvedRenderCamera(position, normalized, basis, viewport, projection);
        return true;
    }

    public Vector3 WorldToCanonicalView(Vector3 worldPosition)
    {
        Vector3 relative = worldPosition - Position;
        float depth = Vector3.Dot(relative, Basis.Forward);
        return new Vector3(
            Vector3.Dot(relative, Basis.Right),
            Vector3.Dot(relative, Basis.Up),
            -depth);
    }

    public bool TryProjectWorldPoint(
        Vector3 worldPosition,
        double viewportMargin,
        out EnbRenderProjectionResult result)
    {
        result = default;
        if (!IsFinite(worldPosition) || !double.IsFinite(viewportMargin) || viewportMargin < 0)
            return false;

        Vector3 relative = worldPosition - Position;
        float distance = relative.Length();
        if (!float.IsFinite(distance))
            return false;

        float depth = Vector3.Dot(relative, Basis.Forward);
        EnbRenderClipRange clip = Projection.ClipRange;
        if (!float.IsFinite(depth) || depth <= clip.NearPlane || depth >= clip.FarPlane)
            return false;

        Vector3 viewPoint = WorldToCanonicalView(worldPosition);
        if (!Projection.TryProjectViewPoint(viewPoint, out Vector3 ndc))
            return false;

        double pixelX = (ndc.X * 0.5 + 0.5) * Viewport.PixelWidth;
        double pixelY = (0.5 - ndc.Y * 0.5) * Viewport.PixelHeight;
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY))
            return false;

        if (pixelX < viewportMargin || pixelX > Viewport.PixelWidth - viewportMargin ||
            pixelY < viewportMargin || pixelY > Viewport.PixelHeight - viewportMargin)
        {
            return false;
        }

        result = new EnbRenderProjectionResult(pixelX, pixelY, depth, distance, ndc);
        return true;
    }

    public bool TryProjectWorldDirection(
        Vector3 worldDirection,
        out EnbRenderProjectionResult result)
    {
        result = default;
        if (!TryNormalize(worldDirection, out Vector3 direction))
            return false;

        float depth = Vector3.Dot(direction, Basis.Forward);
        if (!float.IsFinite(depth) || depth <= 0f)
            return false;

        Vector3 viewPoint = new(
            Vector3.Dot(direction, Basis.Right),
            Vector3.Dot(direction, Basis.Up),
            -depth);
        if (!Projection.TryProjectViewPoint(viewPoint, out Vector3 ndc))
            return false;

        double pixelX = (ndc.X * 0.5 + 0.5) * Viewport.PixelWidth;
        double pixelY = (0.5 - ndc.Y * 0.5) * Viewport.PixelHeight;
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY) ||
            pixelX < 0 || pixelX > Viewport.PixelWidth ||
            pixelY < 0 || pixelY > Viewport.PixelHeight)
        {
            return false;
        }

        result = new EnbRenderProjectionResult(pixelX, pixelY, depth, 1f, ndc);
        return true;
    }

    public bool TryGetWorldRayDirectionFromNdc(
        double normalizedDeviceX,
        double normalizedDeviceY,
        out Vector3 worldDirection)
    {
        worldDirection = default;
        if (!double.IsFinite(normalizedDeviceX) || !double.IsFinite(normalizedDeviceY) ||
            normalizedDeviceX < -1.0 || normalizedDeviceX > 1.0 ||
            normalizedDeviceY < -1.0 || normalizedDeviceY > 1.0)
        {
            return false;
        }

        if (Projection.IsOrthographic)
        {
            worldDirection = Basis.Forward;
            return true;
        }

        float xScale = Projection.Matrix.M11;
        float yScale = Projection.Matrix.M22;
        if (!float.IsFinite(xScale) || !float.IsFinite(yScale) ||
            MathF.Abs(xScale) <= 1e-12f || MathF.Abs(yScale) <= 1e-12f)
        {
            return false;
        }

        Vector3 direction =
            Basis.Forward +
            (Basis.Right * (float)(normalizedDeviceX / xScale)) +
            (Basis.Up * (float)(normalizedDeviceY / yScale));
        return TryNormalize(direction, out worldDirection);
    }

    public bool TryProjectWorldSphere(
        Vector3 worldPosition,
        float worldRadius,
        out EnbRenderSphereProjectionResult result)
    {
        result = default;
        if (!IsFinite(worldPosition) || !float.IsFinite(worldRadius) || worldRadius <= 0f)
            return false;

        Vector3 relative = worldPosition - Position;
        float depth = Vector3.Dot(relative, Basis.Forward);
        EnbRenderClipRange clip = Projection.ClipRange;
        if (!float.IsFinite(depth) || depth <= clip.NearPlane || depth >= clip.FarPlane)
            return false;

        float localX = Vector3.Dot(relative, Basis.Right);
        float localY = Vector3.Dot(relative, Basis.Up);
        if (!float.IsFinite(localX) || !float.IsFinite(localY))
            return false;

        double focalPixelsX = (Viewport.PixelWidth * 0.5) * Projection.Matrix.M11;
        double focalPixelsY = (Viewport.PixelHeight * 0.5) * Projection.Matrix.M22;
        double projectionDivisor = Projection.IsOrthographic ? 1.0 : depth;
        double pixelX = (Viewport.PixelWidth * 0.5) + ((localX / projectionDivisor) * focalPixelsX);
        double pixelY = (Viewport.PixelHeight * 0.5) - ((localY / projectionDivisor) * focalPixelsY);
        double pixelRadiusX = (worldRadius / projectionDivisor) * focalPixelsX;
        double pixelRadiusY = (worldRadius / projectionDivisor) * focalPixelsY;
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY) ||
            !double.IsFinite(pixelRadiusX) || pixelRadiusX <= 0 ||
            !double.IsFinite(pixelRadiusY) || pixelRadiusY <= 0)
        {
            return false;
        }

        if (pixelX + pixelRadiusX < 0 || pixelX - pixelRadiusX > Viewport.PixelWidth ||
            pixelY + pixelRadiusY < 0 || pixelY - pixelRadiusY > Viewport.PixelHeight)
        {
            return false;
        }

        result = new EnbRenderSphereProjectionResult(pixelX, pixelY, pixelRadiusX, pixelRadiusY, depth);
        return true;
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        normalized = default;
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f)
            return false;

        normalized = Vector3.Normalize(value);
        return IsFinite(normalized);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
