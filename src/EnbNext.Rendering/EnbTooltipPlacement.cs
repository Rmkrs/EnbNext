using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Next "fixed tooltip" option (opt-in): instead of following the pointer, a tooltip stands in a fixed area the player
/// places in edit mode. It keeps to the area's corner nearest the screen edges and grows inwards from there, and never
/// leaves the screen.
/// </summary>
public static class EnbTooltipPlacement
{
    public static EnbUiRect Fixed(EnbUiRect tooltip, EnbUiRect area, EnbUiSize viewport)
    {
        bool right = area.Center.X > viewport.Width / 2, bottom = area.Center.Y > viewport.Height / 2;
        double x = right ? area.Right - tooltip.Width : area.X, y = bottom ? area.Bottom - tooltip.Height : area.Y;
        return tooltip with { X = Math.Clamp(x, 0, Math.Max(0, viewport.Width - tooltip.Width)), Y = Math.Clamp(y, 0, Math.Max(0, viewport.Height - tooltip.Height)) };
    }
}
