using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Scanner bounds: the root sphere grown to enclose the particle-buffer sphere of every
/// immediate emitter child (class 15), in child order.</summary>
public static class EnbRetailScannerBounds
{
    public readonly record struct Child(uint ClassId, EnbW3dWorldBounds BufferSphere);
    public readonly record struct Result(EnbW3dWorldBounds Sphere, int EmitterCount);
    private const uint EmitterClass = 15;

    public static Result Resolve(EnbW3dWorldBounds root, IReadOnlyList<Child> children)
    {
        Vector3 center = root.Center; float radius = root.Radius; int emitters = 0;
        foreach (var child in children)
        {
            if (child.ClassId != EmitterClass) continue;
            emitters++;
            var other = child.BufferSphere;
            if (other.Radius == 0 || float.IsNaN(other.Radius)) continue;
            float dx = other.Center.X - center.X, dy = other.Center.Y - center.Y, dz = other.Center.Z - center.Z;
            float distance = MathF.Sqrt((dx * dx + dy * dy) + dz * dz);
            if (distance == 0 || float.IsNaN(distance))
            {
                if (!(radius > other.Radius)) radius = other.Radius;
                continue;
            }
            float merged = ((other.Radius + radius) + distance) * 0.5f;
            if (!(merged >= radius)) continue;
            if (merged < other.Radius) { center = other.Center; radius = other.Radius; continue; }
            float k = (merged - radius) / distance;
            center = new(dx * k + center.X, dy * k + center.Y, dz * k + center.Z);
            radius = merged;
        }
        return new(new(center, radius), emitters);
    }
}
