using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail scanner camera: fitted 1.8 radii back from the target sphere, with clip planes
/// spanning the sphere as seen from the camera. Planes are only reset when the far plane moves.</summary>
public sealed class EnbRetailScannerCameraState
{
    public readonly record struct Result(Matrix4x4 Transform, float Near, float Far);
    // Clip planes retail sets when the scanner has no target, and its far-plane change limit.
    private const float EmptyNear = 0.0001f, EmptyFar = 0.099999994f;
    private const double FarTolerance = 9.999999747378752E-05;
    private Matrix4x4 transform = Matrix4x4.Identity;
    private float near = 5, far = 5000;

    public Result Update(Vector3 observer, Vector3 objectPosition, Vector3 center, float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "The scanner requires a resolved positive render bound.");
        transform = EnbRetailViewCamera.ScannerFit(observer, center, radius);
        float dx = transform.M41 - objectPosition.X, dy = transform.M42 - objectPosition.Y, dz = transform.M43 - objectPosition.Z;
        float distance = MathF.Sqrt((dz * dz + dy * dy) + dx * dx);
        float diameter = radius + radius, newFar = diameter + distance;
        if (MathF.Abs(far - newFar) > FarTolerance)
        {
            float newNear = distance - diameter;
            near = 1f <= newNear || float.IsNaN(newNear) ? newNear : 1f;
            far = newFar;
        }
        return new(transform, near, far);
    }

    public Result Clear()
    {
        near = EmptyNear; far = EmptyFar;
        return new(transform, near, far);
    }
}
