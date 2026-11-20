using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Bind-pose MainView model-gadget query, 0066C4C0 -> 00496960 ->
/// 009671B0. This camera is the interface camera, not the world/preview camera.</summary>
public static class EnbUiModelQuery
{
    // 0065B810 sets MainView type zero. 0065AB10 configures its +20 camera;
    // 0050B7A0 supplies that same camera in space and station modes.
    private static readonly EnbResolvedRenderCamera MainViewCamera = CreateCamera();

    public static EnbRetailMeshPick BindMainView(EnbW3dRenderMesh mesh, Matrix4x4 transform)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var ray = OwnerCache.Get(ref mesh.DerivedUiModelRay, mesh,
            static source => new EnbRetailDazzleMeshRay(source, 1, hidden: false, queryMask: 1));
        return new(ray, transform, MainViewCamera);
    }

    private static EnbResolvedRenderCamera CreateCamera()
    {
        var viewport = new EnbRenderViewport(640, 480, 1);
        var clip = new EnbRenderClipRange(300, 1000);
        if (!EnbPerspectiveProjection.TryCreateHorizontalFov(viewport, Math.PI / 2, 4d / 3, clip, out var projection))
            throw new InvalidOperationException("Invalid MainView interface camera.");
        // Retain the native setter words and identity camera axes. Do not
        // round-trip either through the GPU projection or a quaternion.
        projection = projection with { StoredProjectionBounds = new(-1, -.75f, 1, .75f) };
        return new(new(0, 0, 320), Quaternion.Identity,
            new(-Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), viewport, projection);
    }
}
