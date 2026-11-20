using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail W3D mesh presentation classes encoded in MeshHeader3.Attributes.
/// Retail converts the authored high-word geometry selector into distinct MeshModel
/// runtime flags and reconstructs camera-relative world orientation at submit time
/// for ALIGNED/ORIENTED rigid meshes. Retail Create.Scale is owned by mutable
/// MeshModel geometry, while the final camera-relative draw uses only the RenderObj
/// translation and a fresh rigid look-at matrix. EnbNext keeps mesh geometry
/// immutable, so that model scale is carried explicitly and reapplied here.
/// </summary>
public enum EnbCurrentRetailW3dGeometryClass : byte
{
    Normal = 0,
    CameraAligned = 1,
    Skin = 2,
    CameraOriented = 3,
    Unsupported = 255,
}

/// <summary>
/// Current-retail physical-pixel projection contract for one authored planar
/// CameraOriented mesh. The destination crop comes from the actual authored
/// vertices that survive the current camera clip range. Pixel-to-source
/// mapping is the inverse plane homography derived directly from the retail
/// presentation/view/projection matrices, so it remains valid even when the
/// mesh bounding square crosses the camera plane.
/// </summary>
public readonly record struct EnbCurrentRetailProjectedPlanarMesh(
    float MinimumX,
    float MaximumX,
    float MinimumY,
    float MaximumY,
    float MinimumDepth,
    float MaximumDepth,
    double PixelToSourceM11,
    double PixelToSourceM12,
    double PixelToSourceM13,
    double PixelToSourceM21,
    double PixelToSourceM22,
    double PixelToSourceM23,
    double PixelToSourceM31,
    double PixelToSourceM32,
    double PixelToSourceM33)
{
    public bool TryMapPixelToSource(double pixelX, double pixelY, out double u, out double v)
    {
        u = 0.0;
        v = 0.0;
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY))
            return false;

        double w = (PixelToSourceM31 * pixelX) + (PixelToSourceM32 * pixelY) + PixelToSourceM33;
        if (!double.IsFinite(w) || Math.Abs(w) <= 1e-12)
            return false;

        u = ((PixelToSourceM11 * pixelX) + (PixelToSourceM12 * pixelY) + PixelToSourceM13) / w;
        v = ((PixelToSourceM21 * pixelX) + (PixelToSourceM22 * pixelY) + PixelToSourceM23) / w;
        return double.IsFinite(u) && double.IsFinite(v);
    }
}

public static class EnbCurrentRetailCameraGeometryRuntime
{
    public const uint GeometryTypeMask = 0x00FF_0000u;
    public const uint NormalGeometry = 0x0000_0000u;
    public const uint CameraAlignedGeometry = 0x0001_0000u;
    public const uint SkinGeometry = 0x0002_0000u;
    public const uint CameraOrientedGeometry = 0x0006_0000u;

    public static EnbCurrentRetailW3dGeometryClass Classify(uint authoredAttributes)
        => (authoredAttributes & GeometryTypeMask) switch
        {
            NormalGeometry => EnbCurrentRetailW3dGeometryClass.Normal,
            CameraAlignedGeometry => EnbCurrentRetailW3dGeometryClass.CameraAligned,
            SkinGeometry => EnbCurrentRetailW3dGeometryClass.Skin,
            CameraOrientedGeometry => EnbCurrentRetailW3dGeometryClass.CameraOriented,
            _ => EnbCurrentRetailW3dGeometryClass.Unsupported,
        };

    /// <summary>
    /// Resolves the model-to-world matrix used by current retail's final rigid-mesh
    /// presentation boundary. Normal and skin/deformed packets retain their packet
    /// transform. Camera-aligned and camera-oriented meshes discard the packet's
    /// entire ordinary matrix basis, retain only its RenderObj translation, and
    /// rebuild roll-zero orientation through retail Obj_Look_At semantics. Retail's
    /// separately-owned mutable MeshModel scale is explicitly reapplied because
    /// EnbNext intentionally keeps decoded geometry immutable.
    ///
    /// ALIGNED points local +X along the camera transform's +Z vector. EnbNext's
    /// canonical camera is +X-forward/+Y-left/+Z-up while its canonical view uses
    /// -Z forward, so camera +Z at this handoff is -Basis.Forward.
    /// ORIENTED points local +X from the mesh world position toward the camera.
    /// </summary>
    public static bool TryResolvePresentationWorldTransform(
        EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera,
        out Matrix4x4 worldTransform,
        out EnbCurrentRetailW3dGeometryClass geometryClass,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(submission.Packet);
        return TryResolvePresentationWorldTransform(
            submission.Packet.Mesh.Attributes,
            submission.Packet.Mesh.Name,
            submission.VertexSource,
            submission.Packet.DeformedGeometry is not null,
            submission.Packet.CurrentRetailMeshModelScale,
            submission.WorldTransform,
            camera,
            out worldTransform,
            out geometryClass,
            out diagnostic);
    }

    public static bool TryResolvePresentationWorldTransform(
        uint authoredAttributes,
        string meshName,
        EnbW3dSubmissionVertexSource vertexSource,
        bool hasDeformedGeometry,
        float currentRetailMeshModelScale,
        Matrix4x4 authoredWorldTransform,
        EnbResolvedRenderCamera camera,
        out Matrix4x4 worldTransform,
        out EnbCurrentRetailW3dGeometryClass geometryClass,
        out string? diagnostic)
    {
        worldTransform = default;
        diagnostic = null;
        geometryClass = Classify(authoredAttributes);

        switch (geometryClass)
        {
            case EnbCurrentRetailW3dGeometryClass.Normal:
                worldTransform = authoredWorldTransform;
                if (!IsFinite(worldTransform))
                {
                    diagnostic = $"Mesh '{meshName}' has a non-finite ordinary world transform.";
                    return false;
                }
                return true;

            case EnbCurrentRetailW3dGeometryClass.Skin:
                // Retail's influenced-vertex boundary is already met: CPU
                // deformation produces world-space vertices and identity world.
                if (vertexSource != EnbW3dSubmissionVertexSource.CpuDeformed ||
                    !authoredWorldTransform.Equals(Matrix4x4.Identity))
                {
                    diagnostic = $"Mesh '{meshName}' is authored SKIN but is not on the closed CPU-deformed identity-world path.";
                    return false;
                }
                worldTransform = authoredWorldTransform;
                return true;

            case EnbCurrentRetailW3dGeometryClass.CameraAligned:
            case EnbCurrentRetailW3dGeometryClass.CameraOriented:
                break;

            default:
                diagnostic = $"Mesh '{meshName}' uses unsupported current-retail W3D geometry selector 0x{(authoredAttributes & GeometryTypeMask):X6}.";
                return false;
        }

        if (vertexSource != EnbW3dSubmissionVertexSource.RigidMesh || hasDeformedGeometry)
        {
            diagnostic = $"Mesh '{meshName}' combines camera-relative geometry with influenced/deformed submission; that current-retail combination is not closed.";
            return false;
        }

        if (!float.IsFinite(currentRetailMeshModelScale) || currentRetailMeshModelScale < 0f)
        {
            diagnostic = $"Mesh '{meshName}' camera-relative presentation has invalid current-retail MeshModel scale {currentRetailMeshModelScale}.";
            return false;
        }
        if (!IsFinite(authoredWorldTransform))
        {
            diagnostic = $"Mesh '{meshName}' camera-relative presentation received a non-finite RenderObj world transform.";
            return false;
        }

        // Current client deferred renderer 0x0097A230 reads only the RenderObj
        // Matrix3D translation for ALIGNED/ORIENTED and constructs a fresh rigid
        // Obj_Look_At matrix. Any basis rotation/scale/shear in the packet world
        // is therefore not presentation authority at this boundary.
        Vector3 meshPosition = new(
            authoredWorldTransform.M41,
            authoredWorldTransform.M42,
            authoredWorldTransform.M43);
        if (!IsFinite(meshPosition))
        {
            diagnostic = $"Mesh '{meshName}' camera-relative presentation received a non-finite RenderObj translation.";
            return false;
        }

        //97A3B0..97A3EF spills meshPosition + camera column to float before
        //8C29B0 subtracts the two points. Preserve that rounding at large
        //coordinates, and its exact zero/vertical branches (no epsilon).
        Vector3 target = geometryClass == EnbCurrentRetailW3dGeometryClass.CameraAligned
            ? meshPosition - camera.Basis.Forward
            : camera.Position;
        Matrix4x4 lookAt = EnbRetailCameraLookAt.BetweenPoints(meshPosition, target);
        if (!IsFinite(lookAt))
        {
            diagnostic = geometryClass == EnbCurrentRetailW3dGeometryClass.CameraAligned
                ? $"Mesh '{meshName}' could not resolve the current-retail camera +Z alignment vector."
                : $"Mesh '{meshName}' camera-oriented look-at produced a non-finite matrix.";
            return false;
        }

        // Retail has already applied Create.Scale to mutable MeshModel vertices.
        // EnbNext represents the same ownership by applying that explicit scalar
        // ahead of the fresh rigid presentation transform.
        worldTransform = Matrix4x4.CreateScale(currentRetailMeshModelScale) * lookAt;
        if (!IsFinite(worldTransform))
        {
            diagnostic = $"Mesh '{meshName}' produced a non-finite current-retail camera-relative world matrix.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the exact perspective mapping of an authored planar
    /// CameraOriented mesh into the current-retail gameplay camera. The crop is
    /// determined from the real mesh vertices, never from bounding-square
    /// corners. The inverse projective map is derived from homogeneous clip
    /// coordinates before perspective division, so source-space square corners
    /// are allowed to cross the camera plane without invalidating the visible
    /// authored mesh.
    /// </summary>
    public static bool TryProjectCameraOrientedPlanarMesh(
        EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera,
        out EnbCurrentRetailProjectedPlanarMesh projection,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(submission.Packet);
        projection = default;
        diagnostic = null;

        if (!TryResolvePresentationWorldTransform(
                submission,
                camera,
                out Matrix4x4 presentationWorld,
                out EnbCurrentRetailW3dGeometryClass geometryClass,
                out diagnostic))
        {
            return false;
        }

        if (geometryClass != EnbCurrentRetailW3dGeometryClass.CameraOriented)
        {
            diagnostic = $"Mesh '{submission.Packet.Mesh.Name}' is {geometryClass}, not CameraOriented planar presentation.";
            return false;
        }

        return TryProjectCameraOrientedPlanarMeshCore(
            submission.Packet.Mesh.Name,
            submission.Positions,
            submission.Packet.Mesh.BoundingSphereCenter,
            submission.Packet.Mesh.BoundingRadius,
            presentationWorld,
            camera,
            out projection,
            out diagnostic);
    }

    public static bool TryProjectCameraOrientedPlanarMesh(
        uint authoredAttributes,
        string meshName,
        IReadOnlyList<Vector3> positions,
        Vector3 boundingSphereCenter,
        float boundingRadius,
        float currentRetailMeshModelScale,
        Matrix4x4 authoredWorldTransform,
        EnbResolvedRenderCamera camera,
        out EnbCurrentRetailProjectedPlanarMesh projection,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(positions);
        projection = default;
        diagnostic = null;
        if (!TryResolvePresentationWorldTransform(
                authoredAttributes,
                meshName,
                EnbW3dSubmissionVertexSource.RigidMesh,
                hasDeformedGeometry: false,
                currentRetailMeshModelScale,
                authoredWorldTransform,
                camera,
                out Matrix4x4 presentationWorld,
                out EnbCurrentRetailW3dGeometryClass geometryClass,
                out diagnostic))
        {
            return false;
        }

        if (geometryClass != EnbCurrentRetailW3dGeometryClass.CameraOriented)
        {
            diagnostic = $"Mesh '{meshName}' is {geometryClass}, not CameraOriented planar presentation.";
            return false;
        }

        return TryProjectCameraOrientedPlanarMeshCore(
            meshName,
            positions,
            boundingSphereCenter,
            boundingRadius,
            presentationWorld,
            camera,
            out projection,
            out diagnostic);
    }

    private static bool TryProjectCameraOrientedPlanarMeshCore(
        string meshName,
        IReadOnlyList<Vector3> positions,
        Vector3 center,
        float radius,
        Matrix4x4 presentationWorld,
        EnbResolvedRenderCamera camera,
        out EnbCurrentRetailProjectedPlanarMesh projection,
        out string? diagnostic)
    {
        projection = default;
        diagnostic = null;
        if (!IsFinite(center) || !float.IsFinite(radius) || radius <= 0f || positions.Count < 3)
        {
            diagnostic = $"Mesh '{meshName}' has no finite positive authored planar extent.";
            return false;
        }

        float tolerance = MathF.Max(radius * 1e-5f, 1e-4f);
        foreach (Vector3 vertex in positions)
        {
            if (!IsFinite(vertex) || MathF.Abs(vertex.X - center.X) > tolerance)
            {
                diagnostic = $"Mesh '{meshName}' is not planar at authored local X={center.X:0.###}; planar Screen mapping is not valid.";
                return false;
            }
        }

        // The canonical planar source convention established projection
        // investigation is u=0/v=0 at local (+Y,+Z), u=1 at -Y and v=1 at -Z. Build the
        // source->pixel homography in homogeneous clip space. Crucially, these
        // basis samples do not need clip.W > 0; only the actual authored mesh
        // vertices must be visible. This avoids rejecting a huge off-axis disc
        // merely because its enclosing square extends across the camera plane.
        Vector3 local00 = new(center.X, center.Y + radius, center.Z + radius);
        Vector3 local10 = new(center.X, center.Y - radius, center.Z + radius);
        Vector3 local01 = new(center.X, center.Y + radius, center.Z - radius);
        if (!TryGetHomogeneousClip(camera, Vector3.Transform(local00, presentationWorld), out Vector4 clip00) ||
            !TryGetHomogeneousClip(camera, Vector3.Transform(local10, presentationWorld), out Vector4 clip10) ||
            !TryGetHomogeneousClip(camera, Vector3.Transform(local01, presentationWorld), out Vector4 clip01))
        {
            diagnostic = $"Mesh '{meshName}' produced a non-finite planar source homography.";
            return false;
        }

        Vector4 du = clip10 - clip00;
        Vector4 dv = clip01 - clip00;
        double halfWidth = camera.Viewport.PixelWidth * 0.5;
        double halfHeight = camera.Viewport.PixelHeight * 0.5;
        double a = halfWidth * (du.X + du.W);
        double b = halfWidth * (dv.X + dv.W);
        double c = halfWidth * (clip00.X + clip00.W);
        double d = halfHeight * (du.W - du.Y);
        double e = halfHeight * (dv.W - dv.Y);
        double f = halfHeight * (clip00.W - clip00.Y);
        double g = du.W;
        double h = dv.W;
        double i = clip00.W;
        if (!TryInvertProjectiveMap(
                a, b, c,
                d, e, f,
                g, h, i,
                out double m11, out double m12, out double m13,
                out double m21, out double m22, out double m23,
                out double m31, out double m32, out double m33))
        {
            diagnostic = $"Mesh '{meshName}' planar source homography has no finite inverse.";
            return false;
        }

        float minimumX = float.PositiveInfinity;
        float maximumX = float.NegativeInfinity;
        float minimumY = float.PositiveInfinity;
        float maximumY = float.NegativeInfinity;
        float minimumDepth = float.PositiveInfinity;
        float maximumDepth = float.NegativeInfinity;
        foreach (Vector3 vertex in positions)
        {
            Vector3 world = Vector3.Transform(vertex, presentationWorld);
            if (!TryProjectWorldPointWithoutViewportClip(camera, world, out Vector2 pixel, out float depth))
            {
                diagnostic = $"Mesh '{meshName}' authored planar vertex is outside the current-retail clip range or non-finite.";
                return false;
            }

            minimumX = MathF.Min(minimumX, pixel.X);
            maximumX = MathF.Max(maximumX, pixel.X);
            minimumY = MathF.Min(minimumY, pixel.Y);
            maximumY = MathF.Max(maximumY, pixel.Y);
            minimumDepth = MathF.Min(minimumDepth, depth);
            maximumDepth = MathF.Max(maximumDepth, depth);
        }

        if (!float.IsFinite(minimumX) || !float.IsFinite(maximumX) || minimumX >= maximumX ||
            !float.IsFinite(minimumY) || !float.IsFinite(maximumY) || minimumY >= maximumY ||
            !float.IsFinite(minimumDepth) || !float.IsFinite(maximumDepth))
        {
            diagnostic = $"Mesh '{meshName}' authored planar projection has no finite screen extent.";
            return false;
        }

        projection = new EnbCurrentRetailProjectedPlanarMesh(
            minimumX,
            maximumX,
            minimumY,
            maximumY,
            minimumDepth,
            maximumDepth,
            m11, m12, m13,
            m21, m22, m23,
            m31, m32, m33);
        return true;
    }

    private static bool TryGetHomogeneousClip(
        EnbResolvedRenderCamera camera,
        Vector3 worldPosition,
        out Vector4 clip)
    {
        clip = default;
        if (!IsFinite(worldPosition))
            return false;

        Vector3 viewPoint = camera.WorldToCanonicalView(worldPosition);
        if (!IsFinite(viewPoint))
            return false;

        clip = Vector4.Transform(new Vector4(viewPoint, 1f), camera.Projection.Matrix);
        return float.IsFinite(clip.X) && float.IsFinite(clip.Y) &&
               float.IsFinite(clip.Z) && float.IsFinite(clip.W);
    }

    private static bool TryInvertProjectiveMap(
        double a, double b, double c,
        double d, double e, double f,
        double g, double h, double i,
        out double m11, out double m12, out double m13,
        out double m21, out double m22, out double m23,
        out double m31, out double m32, out double m33)
    {
        m11 = m12 = m13 = m21 = m22 = m23 = m31 = m32 = m33 = 0.0;
        double c11 = (e * i) - (f * h);
        double c12 = -((d * i) - (f * g));
        double c13 = (d * h) - (e * g);
        double c21 = -((b * i) - (c * h));
        double c22 = (a * i) - (c * g);
        double c23 = -((a * h) - (b * g));
        double c31 = (b * f) - (c * e);
        double c32 = -((a * f) - (c * d));
        double c33 = (a * e) - (b * d);
        double determinant = (a * c11) + (b * c12) + (c * c13);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) <= 1e-12)
            return false;

        double inv = 1.0 / determinant;
        m11 = c11 * inv; m12 = c21 * inv; m13 = c31 * inv;
        m21 = c12 * inv; m22 = c22 * inv; m23 = c32 * inv;
        m31 = c13 * inv; m32 = c23 * inv; m33 = c33 * inv;
        return double.IsFinite(m11) && double.IsFinite(m12) && double.IsFinite(m13) &&
               double.IsFinite(m21) && double.IsFinite(m22) && double.IsFinite(m23) &&
               double.IsFinite(m31) && double.IsFinite(m32) && double.IsFinite(m33);
    }

    private static bool TryProjectWorldPointWithoutViewportClip(
        EnbResolvedRenderCamera camera,
        Vector3 worldPosition,
        out Vector2 pixel,
        out float depth)
    {
        pixel = default;
        depth = 0f;
        if (!IsFinite(worldPosition))
            return false;

        Vector3 relative = worldPosition - camera.Position;
        depth = Vector3.Dot(relative, camera.Basis.Forward);
        EnbRenderClipRange clip = camera.Projection.ClipRange;
        if (!float.IsFinite(depth) || depth <= clip.NearPlane || depth >= clip.FarPlane)
            return false;

        Vector3 viewPoint = camera.WorldToCanonicalView(worldPosition);
        if (!camera.Projection.TryProjectViewPoint(viewPoint, out Vector3 ndc))
            return false;

        double pixelX = (ndc.X * 0.5 + 0.5) * camera.Viewport.PixelWidth;
        double pixelY = (0.5 - ndc.Y * 0.5) * camera.Viewport.PixelHeight;
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY) ||
            pixelX < float.MinValue || pixelX > float.MaxValue ||
            pixelY < float.MinValue || pixelY > float.MaxValue)
        {
            return false;
        }

        pixel = new Vector2((float)pixelX, (float)pixelY);
        return true;
    }

    /// <summary>
    /// Matrix3D::Obj_Look_At(p, p + direction, 0) reduced into System.Numerics'
    /// row-vector convention. Local +X is forward, +Y is left and +Z is up.
    /// The horizontal-zero branch matches retail's deterministic yaw fallback.
    /// </summary>
    public static bool TryBuildRetailRollZeroLookAt(
        Vector3 position,
        Vector3 direction,
        out Matrix4x4 transform)
    {
        transform = default;
        if (!IsFinite(position) || !IsFinite(direction))
            return false;

        transform = EnbRetailCameraLookAt.BetweenPoints(position, position + direction);
        return IsFinite(transform);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
