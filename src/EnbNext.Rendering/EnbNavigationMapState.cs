using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Presentation-only starmap controls. No network or warp authority.</summary>
public sealed class EnbNavigationMapState
{
    public float MinimumFrame { get; }
    public float MaximumFrame { get; }
    public float Frame { get; private set; }
    public bool Maximized { get; private set; }
    public int LabelMode { get; private set; }
    public int CoordinateMode { get; private set; }

    public EnbNavigationMapState(EnbRetailPlayerOptions options)
    {
        // ConstructStarMapView 007C63D0 validates each endpoint, then orders them.
        float min = ValidFrame(options.StarmapMinFrame) ? options.StarmapMinFrame : 29;
        float max = ValidFrame(options.StarmapMaxFrame) ? options.StarmapMaxFrame : 59;
        MinimumFrame = Math.Min(min, max); MaximumFrame = Math.Max(min, max);
        Maximized = options.StarmapMaximized;
        Frame = Maximized ? MaximumFrame : MinimumFrame;
        LabelMode = options.StarmapLabel;
        CoordinateMode = options.StarmapShowCoords is > 0 and < 3 ? options.StarmapShowCoords : 0;
    }

    public void ToggleSize() => Maximized = !Maximized;
    public void CycleLabels() => LabelMode = LabelMode is >= 0 and < 3 ? LabelMode + 1 : 0;
    public void CycleCoordinates() => CoordinateMode = (CoordinateMode + 1) % 3;
    public void SetCoordinateMode(int mode) => CoordinateMode = Math.Clamp(mode, 0, 2);

    public void Advance(float milliseconds)
    {
        if (!float.IsFinite(milliseconds) || milliseconds <= 0) return;
        // StarMapViewUpdate 007C8980 / float 00B07288: frame step = elapsed ms / 15.
        float target = Maximized ? MaximumFrame : MinimumFrame;
        float step = milliseconds * .06666667f;
        Frame = Frame < target ? Math.Min(target, Frame + step) : Math.Max(target, Frame - step);
    }

    // StarMapRadarViewRefreshLabels 007C2850. Hover/destination labels are separate.
    public bool ShowsLabel(bool local, bool groupMember, bool player)
        => LabelMode switch { 1 => local || groupMember, 2 => player, 3 => true, _ => false };

    private static bool ValidFrame(float frame) => float.IsFinite(frame) && frame is >= 0 and <= 59;
}
