using System.Numerics;

namespace EnbNext.Rendering;

// Sphere +1BC override 0090D480. Not the generic/ring affine-center path.
// Caller supplies the current authored/animated scale, including explicit
// prototype scale operations; matrix basis scale is not used by this override.
public static class EnbRetailSphereBounds
{
    public static EnbW3dWorldBounds World(Vector3 authoredCenter,Vector3 authoredExtent,
        Vector3 scaleMultiplier,Matrix4x4 world)
        => new(authoredCenter+world.Translation,(authoredExtent*scaleMultiplier).Length());

    // Local sphere virtual +F8 (0090D0D0) excludes the runtime multiplier.
    public static EnbW3dWorldBounds Local(Vector3 authoredCenter,Vector3 authoredExtent)
        => new(authoredCenter,authoredExtent.Length());
}
