using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbBuffIcon(int Slot, EnbAuthoredUiAsset Asset, Matrix4x4 Attachment, bool Hovered)
{
    public float Opacity { get; init; } = 1;
    public float HighlightOpacity { get; init; } = 1;
    public object? GadgetOwner { get; init; }
}
public sealed record EnbBuffDraw(EnbAuthoredUiAsset Frame, ImmutableArray<EnbBuffIcon> Icons,
    ImmutableArray<EnbUiTextDraw> Counts);
