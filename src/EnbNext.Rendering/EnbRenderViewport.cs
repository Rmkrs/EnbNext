namespace EnbNext.Rendering;

/// <summary>
/// Physical render-surface dimensions. Host logical/DIP dimensions are
/// converted exactly once when the surface creates these metrics.
/// </summary>
public readonly record struct EnbRenderViewport(
    int PixelWidth,
    int PixelHeight,
    double RenderScale)
{
    public double AspectRatio => (double)PixelWidth / PixelHeight;
    public double LogicalWidth => PixelWidth / RenderScale;
    public double LogicalHeight => PixelHeight / RenderScale;

    public double ToLogicalX(double pixelX) => pixelX / RenderScale;
    public double ToLogicalY(double pixelY) => pixelY / RenderScale;

    public static bool TryFromLogicalSize(
        double logicalWidth,
        double logicalHeight,
        double renderScale,
        out EnbRenderViewport viewport)
    {
        viewport = default;
        if (!double.IsFinite(logicalWidth) || !double.IsFinite(logicalHeight) ||
            !double.IsFinite(renderScale) ||
            logicalWidth <= 0 || logicalHeight <= 0 || renderScale <= 0)
        {
            return false;
        }

        double physicalWidth = logicalWidth * renderScale;
        double physicalHeight = logicalHeight * renderScale;
        if (!double.IsFinite(physicalWidth) || !double.IsFinite(physicalHeight) ||
            physicalWidth < 1 || physicalHeight < 1 ||
            physicalWidth > int.MaxValue || physicalHeight > int.MaxValue)
        {
            return false;
        }

        int pixelWidth = Math.Max(1, (int)Math.Round(physicalWidth, MidpointRounding.AwayFromZero));
        int pixelHeight = Math.Max(1, (int)Math.Round(physicalHeight, MidpointRounding.AwayFromZero));
        viewport = new EnbRenderViewport(pixelWidth, pixelHeight, renderScale);
        return true;
    }
}
