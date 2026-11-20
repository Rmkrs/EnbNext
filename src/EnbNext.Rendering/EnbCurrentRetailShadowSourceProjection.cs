using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retained source projection owned by one RSL RenderObj wrapper.
/// 0045FFB0 starts dirty; 00461580/004615C0 mark transform writes dirty;
/// 00460BF0 updates only when dirty or the current state has RS_INSTANCE_DATA.</summary>
public sealed class EnbCurrentRetailShadowSourceProjection
{
    public bool TransformDirty { get; private set; } = true;
    public EnbCurrentRetailShadowProjection? Projection { get; private set; }

    // A native setter invalidates even when assigned an identical matrix.
    // Do not infer this event by comparing positions or by observing light changes.
    public void MarkTransformDirty() => TransformDirty = true;

    /// <summary>006F3D3C..006F3D84 propagates the normal space object's
    /// transform on every update, even if its value did not change. Type 29
    /// returns before that write. This is a simulation update notification,
    /// not a render/camera refresh or a replacement-object notification.</summary>
    public void NotifySpaceObjectUpdate(byte objectType, bool hasRenderObject)
    {
        if (objectType != 0x29 && hasRenderObject) MarkTransformDirty();
    }

    public bool Update(EnbW3dAxisAlignedBounds? currentObjectBounds,
        Matrix4x4 worldFromModel, Vector3 managerDirection, int textureSize,
        bool currentStateHasInstanceData)
    {
        if ((!TransformDirty && !currentStateHasInstanceData) || currentObjectBounds is not { } bounds)
            return false;

        Projection = EnbCurrentRetailShadowProjection.Create(bounds.Center, bounds.Extents,
            worldFromModel, managerDirection, textureSize);
        TransformDirty = false;
        return true;
    }
}
