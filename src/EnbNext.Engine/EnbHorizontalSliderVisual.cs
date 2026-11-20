using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>00670300: one slider's current geometry, retained until its length changes.</summary>
internal sealed class EnbHorizontalSliderVisual(EnbHorizontalSliderResources resources)
{
    public EnbAuthoredUiAsset Asset { get; private set; } = resources.Thumb;
    public EnbUiRect Bounds { get; private set; } = resources.ThumbBounds;
    private double length = 16;

    public void Resize(double value)
    {
        value = Math.Max(16, value);
        if (length == value) return;
        (Asset, Bounds) = resources.Sample(value);
        length = value;
    }
}
