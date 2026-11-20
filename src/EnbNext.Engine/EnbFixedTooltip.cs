using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next "Tooltips in a fixed position" (Quality of Life, opt-in, classic and Modern UI): the in-game tooltips stand in
/// a placeable area (the Tooltip element in edit mode) instead of at the pointer. Edit mode shows a sample tooltip there.
/// </summary>
internal sealed class EnbFixedTooltip : IDisposable
{
    private readonly EnbRetailText[] labels = [new(), new(), new(), new()];
    private EnbUiTooltipDraw? preview;
    private EnbUiSize previewSize;
    private double previewScale;
    internal bool Enabled { get; set; }
    internal EnbUiLayoutProfiles? Layout { get; set; }

    /// <summary>The area before its placement: right of the screen, above the lower HUD.</summary>
    internal static EnbUiRect DefaultArea(EnbUiSize viewport) => new(viewport.Width - 16 - 200, viewport.Height * .7 - 60, 200, 60);
    internal EnbUiRect? Area(EnbUiSize viewport) => Enabled ? Layout?.Apply(EnbUiGroup.Tooltip, DefaultArea(viewport)) ?? DefaultArea(viewport) : null;

    /// <summary>Edit mode's sample tooltip, so the area can be placed.</summary>
    internal EnbUiTooltipDraw? Preview(EnbRetailAssetBundle assets, EnbUiSize viewport, double scale)
    {
        if (!Enabled || Layout?.Editing != true) return null;
        if (preview is not null && previewSize == viewport && previewScale == scale) return preview;
        var session = EnbRenderSession.Of(assets);
        if (session.TooltipResources is not { Frame: { } frame, Definition: { } definition } || session.OptionsPanels.Font(definition.FontId, viewport, scale) is not { } font) return null;
        previewSize = viewport; previewScale = scale;
        return preview = EnbTooltipContent.Create(frame, font, "Tooltip\nTooltips show here", viewport, scale, new(.1f, 1, .1f), default, i => labels[Math.Min(i, labels.Length - 1)]);
    }

    public void Dispose() { foreach (var label in labels) label.Dispose(); }
}
