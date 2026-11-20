using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Immutable bridge state submitted together with its world frame.</summary>
public sealed record EnbBridgeRenderFrame(EnbRetailBridgeResource Asset, Matrix4x4 ModelTransform,
    EnbBridgeCameraComposition Cameras)
{
    public EnbW3dAnimationSample? Animation { get; init; }
    public Vector3 AvatarSteeringRate { get; init; }
}
