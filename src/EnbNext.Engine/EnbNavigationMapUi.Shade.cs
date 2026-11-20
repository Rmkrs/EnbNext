using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Point = EnbNext.Presentation.EnbUiPoint;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    /// <summary>Next option: darken the map outside the sector's extent (the server's sector bounds).</summary>
    public bool ShadeOutsideSector { get; set; }
    private static readonly EnbUiRaster ShadeRaster = EnbUiRaster.FromPixels(1, 1, [new EnbNext.Assets.EnbColor32(0, 0, 0, 255)]);
    private const float ShadeOpacity = .45f;

    private ImmutableArray<EnbUiTextDraw> SectorShade()
    {
        if (!ShadeOutsideSector || _galaxy || _frame is null || !_projection.IsInitialized || _scene?.Environment?.SectorExtent is not var (minimum, maximum)) return [];
        var viewport = Viewport; var content = Content;
        var a = _projection.Project(new(minimum.X, maximum.Y, 0), viewport);
        var b = _projection.Project(new(maximum.X, minimum.Y, 0), viewport);
        double left = Math.Clamp(Math.Min(a.X, b.X), 0, viewport.X), right = Math.Clamp(Math.Max(a.X, b.X), 0, viewport.X);
        double top = Math.Clamp(Math.Min(a.Y, b.Y), 0, viewport.Y), bottom = Math.Clamp(Math.Max(a.Y, b.Y), 0, viewport.Y);
        var bands = ImmutableArray.CreateBuilder<EnbUiTextDraw>(4);
        void Band(double x0, double y0, double x1, double y1)
        {
            if (x1 - x0 < .01 || y1 - y0 < .01) return;
            var tl = Logical(content.TopLeft + new Point(x0, y0)); var br = Logical(content.TopLeft + new Point(x1, y1));
            bands.Add(new(ShadeRaster, new(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y)) { Opacity = ShadeOpacity });
        }
        Band(0, 0, viewport.X, top);
        Band(0, bottom, viewport.X, viewport.Y);
        Band(0, top, left, bottom);
        Band(right, top, viewport.X, bottom);
        return bands.ToImmutable();
    }
}
