using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>The local ship as one rendered frame presents it.</summary>
public readonly record struct EnbPresentedShip(uint Sector, Vector3 Position, Vector3 Forward);

/// <summary>
/// Next waypoint arrow (TomTom style): one 3D arrow pointing at the active waypoint relative to the
/// ship's heading and tilt. It moves by dragging anywhere on it (no edit mode) and a right click
/// clears it. It keeps clear of nothing on purpose: the player places it where it suits them.
/// </summary>
internal sealed class EnbWaypointArrow(EnbRetailAssetBundle assets) : IDisposable
{
    public static readonly EnbUiPoint DefaultCenter = new(.5, .2);
    private const double ArrowSize = 56, ArrivedDistance = 1000;
    private readonly EnbOptionsPanelResources fonts = EnbRenderSession.Of(assets).OptionsPanels;
    private readonly EnbRetailText title = new(), detail = new(), heightLine = new();
    private double scaleFactor = 1; private bool showDistance = true, showHeight, levelHovered;
    private EnbUiRect levelBounds;
    public EnbUiRect LevelBounds => levelBounds;
    private EnbWaypoint? target;
    private float? yaw; // radians, counterclockwise; null when not in the target's sector
    private float pitch, off; // elevation relative to the ship's tilt; full 3D angle off the nose
    private double distance, height;
    private bool hidden;
    private EnbUiPoint pointer, dragStart, centerStart;
    private bool dragging, moved;
    private ImmutableList<EnbUiModelDraw>? cached;
    private EnbUiSize cachedSize; private double cachedScale;
    public EnbUiPoint Center { get; set; } = DefaultCenter;
    public EnbUiRect Bounds { get; private set; }
    public bool Dragging => dragging;
    public event Action? Cleared;
    public event Action<EnbUiPoint>? Moved;

    /// <summary>Recomputes the direction from the local ship, as this frame presents it, to the waypoint.</summary>
    public void Update(EnbWaypoint? waypoint, EnbPresentedShip? ship)
    {
        float? nextYaw = null; float nextPitch = 0, nextOff = 0; double nextDistance = 0, nextHeight = 0;
        bool nextHidden = waypoint is null || ship is null;
        if (!nextHidden && ship!.Value.Sector == waypoint!.Sector)
        {
            var s = ship.Value; var delta = waypoint.Position - s.Position;
            nextDistance = delta.Length(); nextHeight = delta.Z;
            var forward = s.Forward.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Forward) : Vector3.UnitX;
            var flatForward = new Vector2(forward.X, forward.Y); var flatToward = new Vector2(delta.X, delta.Y);
            // Ships turn about the sector's Z axis and tilt within limits: bearing in the XY plane,
            // elevation relative to the ship's current tilt.
            nextYaw = flatForward.LengthSquared() > 1e-8f && flatToward.LengthSquared() > 1e-8f
                ? MathF.Atan2(flatForward.X * flatToward.Y - flatForward.Y * flatToward.X, Vector2.Dot(flatForward, flatToward)) : 0;
            float shipPitch = MathF.Asin(Math.Clamp(forward.Z, -1, 1));
            float targetPitch = MathF.Atan2(delta.Z, flatToward.Length());
            nextPitch = Math.Clamp(targetPitch - shipPitch, -MathF.PI / 2, MathF.PI / 2);
            nextOff = nextDistance > 1e-3 ? MathF.Acos(Math.Clamp(Vector3.Dot(forward, delta / (float)nextDistance), -1, 1)) : 0;
        }
        // Fine buckets: one cached raster per 2 degrees of bearing and 3 of elevation.
        if (nextYaw is float y) { nextYaw = MathF.Round(y / YawStep) * YawStep; nextPitch = MathF.Round(nextPitch / PitchStep) * PitchStep; }
        nextDistance = Math.Round(nextDistance / 10) * 10; nextHeight = Math.Round(nextHeight / 100) * 100;
        if (Equals(target, waypoint) && yaw == nextYaw && pitch == nextPitch && distance == nextDistance && height == nextHeight && hidden == nextHidden) return;
        target = waypoint; yaw = nextYaw; pitch = nextPitch; off = nextOff; distance = nextDistance; height = nextHeight; hidden = nextHidden; cached = null;
    }

    public bool Contains(EnbUiPoint p) => !hidden && target is not null && Bounds.Contains(p);
    /// <summary>Options: arrow size factor, distance line, height difference always (else on hover).</summary>
    public void Configure(double size, bool distanceLine, bool heightAlways)
    {
        if (size == scaleFactor && distanceLine == showDistance && heightAlways == showHeight) return;
        scaleFactor = size; showDistance = distanceLine; showHeight = heightAlways; cached = null;
    }
    public void Pointer(EnbUiPoint p)
    {
        pointer = p;
        bool hover = !dragging && levelBounds.Width > 0 && levelBounds.Contains(p);
        if (hover != levelHovered) { levelHovered = hover; cached = null; }
        if (!dragging) return;
        var delta = p - dragStart;
        if (!moved && Math.Abs(delta.X) + Math.Abs(delta.Y) < 3) return;
        moved = true;
        Center = new(Math.Clamp(centerStart.X + delta.X / Math.Max(1, cachedSize.Width), 0, 1), Math.Clamp(centerStart.Y + delta.Y / Math.Max(1, cachedSize.Height), 0, 1));
        cached = null;
    }
    public bool Press(EnbUiPoint p, bool right)
    {
        if (!Contains(p)) return false;
        if (right) { Cleared?.Invoke(); return true; }
        dragging = true; moved = false; dragStart = p; centerStart = Center; return true;
    }
    public bool Release()
    {
        if (!dragging) return false;
        dragging = false; if (moved) Moved?.Invoke(Center); return true;
    }

    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size, double scale)
    {
        if (hidden || target is null) { Bounds = levelBounds = default; return []; }
        if (cached is not null && cachedSize == size && cachedScale == scale) return cached;
        cachedSize = size; cachedScale = scale;
        var font = fonts.Font("FONT_SMALL", size, scale); if (font is null) return [];
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double cx = Center.X * size.Width, cy = Center.Y * size.Height, arrowSize = ArrowSize * scaleFactor;
        bool arrived = yaw is not null && distance < ArrivedDistance, pointing = yaw is not null && !arrived;
        var arrow = new EnbUiRect(cx - arrowSize / 2, cy - arrowSize / 2, arrowSize, arrowSize);
        if (yaw is float angle && !arrived) images.Add(new(EnbWaypointArrowRaster.Get(angle, pitch), arrow));
        string name = target.Label.Length > 0 ? target.Label : "Waypoint";
        title.SetText(name, font, new(1, 1, 1)); title.SetDisplayScale(1, 1);
        double y = pointing ? arrow.Bottom + 2 : cy - title.Height;
        var titleRect = new EnbUiRect(cx - title.Width / 2, y, title.Width, title.Height);
        if (title.Raster is { } t) images.Add(new(t, titleRect));
        double left = Math.Min(arrow.X, titleRect.X), right = Math.Max(arrow.Right, titleRect.Right), bottom = titleRect.Bottom;
        void Add(EnbUiRaster raster, EnbUiRect rect) { images.Add(new(raster, rect)); left = Math.Min(left, rect.X); right = Math.Max(right, rect.Right); bottom = Math.Max(bottom, rect.Bottom); }
        levelBounds = default;
        // One compact line: total distance, then a level mark (line = level, ^ = climb, v = descend).
        string? line = yaw is null ? "in " + target.SectorName : arrived ? "Arrived" : showDistance ? Distance(distance) : null;
        double lineHeight = font.CellHeight * font.Scale;
        double markHeight = Math.Round(lineHeight * .7), markWidth = Math.Round(markHeight * 1.5), gap = pointing && line is not null ? 6 : 0;
        double lineWidth = 0;
        if (line is not null) { detail.SetText(line, font, arrived ? new(.3f, 1, .3f) : new(1, .93f, 0)); detail.SetDisplayScale(1, 1); lineWidth = detail.Width; }
        if (pointing) lineWidth += gap + markWidth;
        double x = cx - lineWidth / 2;
        if (line is not null && detail.Raster is { } d) { Add(d, new(x, titleRect.Bottom, detail.Width, detail.Height)); x += detail.Width + gap; }
        if (pointing)
        {
            levelBounds = new(x, titleRect.Bottom + (Math.Max(lineHeight, detail.Height) - markHeight) / 2, markWidth, markHeight);
            Add(LevelMark(Level(height, distance)), levelBounds);
            // The height difference: always (option) or while the mark is hovered.
            if (showHeight || levelHovered)
            {
                int level = Level(height, distance);
                string text = level == 0 ? "Level" : Distance(Math.Abs(height)) + (level > 0 ? " above" : " below");
                heightLine.SetText(text, font, new(1, 1, 1)); heightLine.SetDisplayScale(1, 1);
                if (heightLine.Raster is { } h) Add(h, new(cx - heightLine.Width / 2, bottom, heightLine.Width, heightLine.Height));
            }
        }
        double top = pointing ? arrow.Y : titleRect.Y;
        Bounds = new(left, top, right - left, bottom - top);
        var asset = fonts.Main.Asset;
        cached = [new EnbUiModelDraw(asset, 0, Bounds, false) { ImagesOnly = true, Images = images.ToImmutable(), LayoutGroup = EnbUiGroup.None }];
        return cached;
    }

    /// <summary>+1 target above (climb), -1 below (descend), 0 within a few degrees or 500 units of level.</summary>
    public static int Level(double dz, double distance)
    {
        double horizontal = Math.Sqrt(Math.Max(0, distance * distance - dz * dz));
        if (Math.Abs(dz) < 500 || Math.Atan2(Math.Abs(dz), horizontal) < 3 * Math.PI / 180) return 0;
        return dz > 0 ? 1 : -1;
    }

    private static readonly EnbUiRaster[] LevelMarks = [BuildMark(-1), BuildMark(0), BuildMark(1)];
    private static EnbUiRaster LevelMark(int level) => LevelMarks[level + 1];
    /// <summary>Green bar for level; amber chevron pointing the way to climb or descend.</summary>
    private static EnbUiRaster BuildMark(int level)
    {
        const int w = 24, h = 16, samples = 4; var pixels = new EnbColor32[w * h];
        Vector3 colour = level == 0 ? new(.3f, 1, .3f) : new(1, .78f, .2f);
        float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1); return Vector2.Distance(p, a + ab * t);
        }
        float Shape(Vector2 p) => level == 0 ? Segment(p, new(4, 8), new(20, 8))
            : level > 0 ? Math.Min(Segment(p, new(4, 12), new(12, 4)), Segment(p, new(12, 4), new(20, 12)))
            : Math.Min(Segment(p, new(4, 4), new(12, 12)), Segment(p, new(12, 12), new(20, 4)));
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float fill = 0, rim = 0;
            for (int sy = 0; sy < samples; sy++)
            for (int sx = 0; sx < samples; sx++)
            {
                float d = Shape(new(x + (sx + .5f) / samples, y + (sy + .5f) / samples));
                if (d <= 1.9f) fill++; else if (d <= 3.2f) rim++;
            }
            fill /= samples * samples; rim /= samples * samples;
            float a = fill + rim * .45f; if (a <= 0) continue;
            var c = colour * (fill / a);
            pixels[y * w + x] = new((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255), (byte)(Math.Min(1, a) * 255));
        }
        return EnbUiRaster.FromPixels(w, h, pixels);
    }

    public static string Distance(double world) => world < 1000
        ? ((int)Math.Round(world)).ToString(CultureInfo.InvariantCulture)
        : (world / 1000).ToString(world < 100000 ? "0.0" : "0", CultureInfo.InvariantCulture) + "k";

    private const float YawStep = MathF.PI / 90, PitchStep = MathF.PI / 60;
    public void Dispose() { title.Dispose(); detail.Dispose(); heightLine.Dispose(); }
}
