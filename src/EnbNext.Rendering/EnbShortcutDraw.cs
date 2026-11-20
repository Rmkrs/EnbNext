using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbShortcutDraw(EnbAuthoredUiAsset Asset,
    int LeftGroup, int RightGroup, bool LeftEnabled, bool RightEnabled,
    ImmutableArray<EnbUiTextDraw> Icons, ImmutableArray<EnbUiTextDraw> Labels)
{ public ImmutableArray<EnbCombatIcon> Combat { get; init; } = [];
  public ImmutableArray<EnbUiTextDraw> Drag { get; init; } = []; }
