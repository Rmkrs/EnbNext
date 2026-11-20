using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Orthographic source projection (0093DBF0, 00965130, 0097F810).
/// Matrices use System.Numerics row-vector convention.</summary>
public sealed record EnbCurrentRetailShadowProjection(
    Matrix4x4 WorldFromProjector, Matrix4x4 ProjectorFromWorld,
    Vector4 Bounds, float Near, float Far, Vector3 VolumeCenter, Vector3 VolumeHalfExtent,
    Matrix4x4 TextureFromWorld, Matrix4x4 GradientFromWorld)
{
    /// <summary>0093E0D0 capture camera. Submit into the one-pixel inset of
    /// the square target; the texture mapper above retains the full target size.</summary>
    public bool TryCreateCaptureCamera(int textureSize, out EnbResolvedRenderCamera camera)
    {
        camera = default;
        if (textureSize <= 2) return false;
        Vector3 right = new(WorldFromProjector.M11, WorldFromProjector.M12, WorldFromProjector.M13);
        Vector3 up = new(WorldFromProjector.M21, WorldFromProjector.M22, WorldFromProjector.M23);
        Vector3 back = new(WorldFromProjector.M31, WorldFromProjector.M32, WorldFromProjector.M33);
        // Recenter the asymmetric native bounds into the canonical symmetric
        // camera without changing any world point's projected coordinates.
        Vector3 position = WorldFromProjector.Translation +
            right * ((Bounds.X + Bounds.Y) * .5f) + up * ((Bounds.Z + Bounds.W) * .5f);
        var rotation = new Matrix4x4(-back.X, -back.Y, -back.Z, 0,
            -right.X, -right.Y, -right.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        return EnbResolvedRenderCamera.TryCreateOrthographic(position,
            Quaternion.CreateFromRotationMatrix(rotation), new(textureSize - 2, textureSize - 2, 1),
            (Bounds.Y - Bounds.X) * .5, (Bounds.W - Bounds.Z) * .5,
            new(.01f, Far), out camera);
    }

    public static EnbCurrentRetailShadowProjection Create(Vector3 localCenter, Vector3 localHalfExtent,
        Matrix4x4 worldFromModel, Vector3 direction, int textureSize,
        float near = -1, float far = -1)
    {
        Vector3 worldCenter = Vector3.Transform(localCenter, worldFromModel);
        Vector3 worldExtent = TransformExtent(localHalfExtent, worldFromModel);
        Vector3 eye = worldCenter - direction * (2 * worldExtent.Length());
        Vector3 delta = worldCenter - eye;
        // 008C2780: the vertical-axis yaw is explicitly zero, not whichever
        // alternate up vector a general-purpose LookAt happens to choose.
        float horizontal = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        float length = delta.Length();
        float sinYaw = horizontal == 0 ? 0 : delta.Y / horizontal;
        float cosYaw = horizontal == 0 ? 1 : delta.X / horizontal;
        float sinPitch = length == 0 ? 0 : delta.Z / length;
        float cosPitch = length == 0 ? 1 : horizontal / length;
        Vector3 right = new(sinYaw, -cosYaw, 0);
        Vector3 up = new(-cosYaw * sinPitch, -sinYaw * sinPitch, cosPitch);
        Vector3 back = new(-cosYaw * cosPitch, -sinYaw * cosPitch, -sinPitch);
        var pose = new Matrix4x4(right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0, back.X, back.Y, back.Z, 0, eye.X, eye.Y, eye.Z, 1);
        // Native 008C2BD0 is a rigid inverse, not an arbitrary affine inverse.
        var view = new Matrix4x4(right.X, up.X, back.X, 0,
            right.Y, up.Y, back.Y, 0, right.Z, up.Z, back.Z, 0,
            -Vector3.Dot(eye, right), -Vector3.Dot(eye, up), -Vector3.Dot(eye, back), 1);
        Matrix4x4 modelToView = worldFromModel * view;
        Vector3 center = Vector3.Transform(localCenter, modelToView);
        Vector3 extent = TransformExtent(localHalfExtent, modelToView);
        // Keep the actual 0093DE18..0093DE6A compare sequence. The final
        // comparison only replaces the middle operand; it does not swap it.
        float largest = MathF.Abs(localHalfExtent.X), middle = MathF.Abs(localHalfExtent.Y);
        float remaining = MathF.Abs(localHalfExtent.Z);
        if (largest < middle) (largest, middle) = (middle, largest);
        if (largest < remaining) (largest, remaining) = (remaining, largest);
        if (middle < remaining) middle = remaining;
        float projectionNear = near == -1 ? -center.Z : near - center.Z;
        float projectionFar = far == -1 ? (center.Z -
            (largest + middle * .34375f + remaining * .25f)) * -2 : far - center.Z;
        var bounds = new Vector4(center.X - extent.X, center.X + extent.X,
            center.Y - extent.Y, center.Y + extent.Y);
        // 00965130 uses the fixed .1 near for texture depth, but the supplied
        // near for the culling volume. Capture camera separately uses .01.
        var projection = new Matrix4x4(
            2 / (bounds.Y - bounds.X), 0, 0, 0,
            0, 2 / (bounds.W - bounds.Z), 0, 0,
            0, 0, -2 / (projectionFar - .1f), 0,
            -(bounds.X + bounds.Y) / (bounds.Y - bounds.X),
            -(bounds.Z + bounds.W) / (bounds.W - bounds.Z),
            -(projectionFar + .1f) / (projectionFar - .1f), 1);
        float scale = (textureSize - 2f) * .5f / textureSize;
        var texture = new Matrix4x4(scale, 0, 0, 0, 0, -scale, 0, 0,
            0, 0, 0, 0, scale, scale, 0, 1);
        // Mapper type 2: constant U=.5, V=.5*(clip.W+clip.Z), COUNT2.
        var gradient = new Matrix4x4(0, 0, 0, 0, 0, 0, 0, 0,
            0, .5f, 0, 0, .5f, .5f, 0, 1);
        return new(pose, view, bounds, projectionNear, projectionFar,
            Vector3.Transform(new(center.X, center.Y, -(projectionNear + projectionFar) * .5f), pose),
            new(extent.X, extent.Y, (projectionFar - projectionNear) * .5f),
            view * projection * texture, view * projection * gradient);
    }

    private static Vector3 TransformExtent(Vector3 e, Matrix4x4 m) => new(
        MathF.Abs(m.M11 * e.X) + MathF.Abs(m.M21 * e.Y) + MathF.Abs(m.M31 * e.Z),
        MathF.Abs(m.M12 * e.X) + MathF.Abs(m.M22 * e.Y) + MathF.Abs(m.M32 * e.Z),
        MathF.Abs(m.M13 * e.X) + MathF.Abs(m.M23 * e.Y) + MathF.Abs(m.M33 * e.Z));
}
