using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbFrontendDraw(string Key, EnbW3dRenderResource Resource, EnbW3dRenderMesh Mesh,
    Matrix4x4 Transform, EnbW3dDeformedGeometry? Geometry, EnbResolvedRenderCamera Camera, uint Time)
{
    public EnbNext.Presentation.EnbSceneAvatarSnapshot? Avatar { get; init; }
    public IReadOnlyList<EnbNext.Presentation.EnbSceneColorizationSnapshot>? ShipColors { get; init; }
    public IReadOnlyList<EnbNext.Presentation.EnbSceneDecalSnapshot>? ShipDecals { get; init; }
    public EnbNext.Presentation.EnbSceneNameDecalSnapshot? ShipNameDecal { get; init; }
    public int ShipColorRegion { get; init; }
    public string? TextureOverride { get; init; }
    public bool Highlight { get; init; }
    public float HighlightStrength { get; init; } = 1f;
    public bool Pressed { get; init; }
    public bool Disabled { get; init; }
}
