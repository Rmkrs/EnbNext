using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbUiTextDraw(EnbUiRaster Raster, EnbUiRect Bounds)
{
    public EnbUiRect? Clip { get; init; }
    public EnbUiRaster? Highlight { get; init; }
    public object? GadgetOwner { get; init; }
    public float Opacity { get; init; } = 1;
    public float Brightness { get; init; } = 1;
    public float VerticalScale { get; init; } = 1;
}
public sealed record EnbUiTooltipDraw(EnbAuthoredTooltipFrame Asset, EnbUiRect Bounds, EnbUiSize DesignSize)
{
    public bool FitFrameToBounds { get; init; }
    public EnbUiTextDraw? Title { get; init; }
    public EnbUiTextDraw? Detail { get; init; }
    public EnbUiTextDraw? Third { get; init; }
    public ImmutableArray<EnbUiTextDraw> Lines { get; init; } = [];
    /// <summary>The tooltip's text, pointer and gadget as asked for (EnbTooltipContent), so another style can lay it out again.</summary>
    public string? Text { get; init; }
    public EnbUiPoint Pointer { get; init; }
    public EnbUiRect? Gadget { get; init; }
    /// <summary>A drawn background at the tooltip's exact pixel size instead of the authored frame (Next Modern UI).</summary>
    public EnbUiRaster? Background { get; init; }
    /// <summary>A Next Modern UI rich tooltip for what is hovered (an item, a skill); the retail style ignores it.</summary>
    public EnbTooltipCard? Card { get; init; }
}
