using System.Collections.Immutable;

namespace EnbNext.Rendering;

// Static asset geometry: positions are camera-relative world directions; point-star
// offsets are expanded in view space, after rotation. No camera or viewport cache.
public readonly record struct EnbBackdropVertex(
    System.Numerics.Vector3 Position,
    System.Numerics.Vector2 ViewOffset,
    System.Numerics.Vector2 Uv0,
    System.Numerics.Vector2 Uv1,
    float Intensity);

public sealed record EnbBackdropGeometry(
    ImmutableArray<EnbBackdropVertex> Vertices,
    EnbNext.Assets.EnbDdsTexture? Stage0,
    EnbNext.Assets.EnbDdsTexture? Stage1)
{
    public static EnbBackdropGeometry Empty { get; } = new([], null, null);

    public ImmutableArray<EnbBackdropGeometry> AdditiveLayers { get; init; } = [];
    public bool WriteFarDepth { get; init; }
    public EnbStarTexture? StarTexture { get; init; }
    public bool IsUntextured { get; init; }
    public EnbNext.Assets.EnbTextureImage? ResolvedStage0 { get; init; }
}

public sealed record EnbBackdropFrame(
    EnbBackdropGeometry Environment,
    ImmutableArray<EnbBackdropGeometry> Stars);
