using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbCockpitDraw(EnbAuthoredUiAsset Asset, EnbUiSize Size,
    ImmutableArray<EnbUiTextDraw> Text, float Energy, float Hull, float Shield,
    ulong Owner, int Thrust, string? Hovered)
{
    public EnbShortcutDraw? Shortcuts { get; init; }
    public EnbBuffDraw? Buffs { get; init; }
    public float WarpFill { get; init; }
    public float WarpRed { get; init; } = 1;
    public Vector3? WarpButtonEmission { get; init; }
    public bool WarpEngaged { get; init; }
    public bool WarpEnabled { get; init; }
}
