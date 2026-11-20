using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbInterruptionGauge(EnbAuthoredUiAsset Asset, Matrix4x4 Transform,
    float Progress, float Activation, ulong Generation);

public sealed record EnbGaugeDraw(EnbAuthoredTargetPanel Asset, float Hull, float Shield)
{
    public uint? TargetId { get; init; }
    public EnbInterruptionGauge? Interruption { get; init; }
}
