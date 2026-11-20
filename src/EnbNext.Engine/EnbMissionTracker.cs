using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>One tracked mission as the tracker shows it.</summary>
public readonly record struct EnbMissionTrackerEntry(int Id, string Name, string Step, string Timer);

/// <summary>
/// Next mission tracker: a compact, movable list of the missions the player tracks (name, current
/// step, timer). Steps show two lines; hovering a mission shows the game's framed tooltip with
/// the whole step and the click hints. Dragging moves the panel, a click points the waypoint
/// arrow at the step's target, a right click untracks.
/// </summary>
internal sealed class EnbMissionTracker(EnbRetailAssetBundle assets) : IDisposable
{
    public const double Width = 230, Pad = 6, EntryGap = 6;
    private const int CollapsedLines = 2;
    private readonly EnbOptionsPanelResources fonts = EnbRenderSession.Of(assets).OptionsPanels;
    private readonly List<EnbRetailText> labels = [];
    private readonly EnbRetailText[] tipLabels = Enumerable.Range(0, 20).Select(_ => new EnbRetailText()).ToArray();
    private readonly EnbCurrentRetailTooltipDelay delay = new();
    private EnbUiTooltipDraw? cachedTooltip;
    private readonly List<(int Id, EnbUiRect Rect)> hits = [];
    private ImmutableArray<EnbMissionTrackerEntry> entries = [];
    private ImmutableList<EnbUiModelDraw>? cached;
    private EnbUiSize cachedSize; private double cachedScale;
    private bool background = true, arrowHint, opensMission;
    private EnbUiPoint pointer = new(-100000, -100000), dragStart, positionStart;
    private bool dragging, moved;
    private int hovered = -1;
    /// <summary>Normalized top-left; null places it below the radar, clear of the gutter.</summary>
    public EnbUiPoint? Position { get; set; }
    public EnbUiRect Bounds { get; private set; }
    public bool Dragging => dragging;
    public event Action<int>? Clicked, Untracked;
    public event Action<EnbUiPoint>? Moved;

    /// <summary>The tracker's look and what a left-click does: point the waypoint arrow, or (Next Modern UI) show the
    /// mission's details in the missions window.</summary>
    public void Configure(bool withBackground, bool clickSetsArrow, bool clickOpensMission = false)
    {
        if (withBackground == background && clickSetsArrow == arrowHint && clickOpensMission == opensMission) return;
        background = withBackground; arrowHint = clickSetsArrow; opensMission = clickOpensMission; cached = null; cachedTooltip = null;
    }

    public void Update(ImmutableArray<EnbMissionTrackerEntry> next)
    {
        if (next.SequenceEqual(entries)) return;
        entries = next; cached = null; cachedTooltip = null;
    }

    public bool Contains(EnbUiPoint p) => !entries.IsEmpty && Bounds.Contains(p);
    private int EntryAt(EnbUiPoint p) { foreach (var h in hits) if (h.Rect.Contains(p)) return h.Id; return -1; }

    public void Pointer(EnbUiPoint p)
    {
        pointer = p;
        if (dragging)
        {
            var delta = p - dragStart;
            if (!moved && Math.Abs(delta.X) + Math.Abs(delta.Y) < 3) return;
            moved = true;
            Position = new(Math.Clamp(positionStart.X + delta.X / Math.Max(1, cachedSize.Width), 0, 1),
                Math.Clamp(positionStart.Y + delta.Y / Math.Max(1, cachedSize.Height), 0, 1));
            cached = null; return;
        }
        int now = Bounds.Contains(p) ? EntryAt(p) : -1;
        if (now != hovered) { hovered = now; cached = null; cachedTooltip = null; }
    }

    public bool Press(EnbUiPoint p, bool right)
    {
        if (!Contains(p)) return false;
        if (right) { if (EntryAt(p) is int id and >= 0) Untracked?.Invoke(id); return true; }
        dragging = true; moved = false; dragStart = p;
        positionStart = new(Bounds.X / Math.Max(1, cachedSize.Width), Bounds.Y / Math.Max(1, cachedSize.Height));
        return true;
    }

    public bool Release()
    {
        if (!dragging) return false;
        dragging = false;
        if (moved) Moved?.Invoke(Position!.Value);
        else if (EntryAt(dragStart) is int id and >= 0) Clicked?.Invoke(id);
        return true;
    }

    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size, double scale)
    {
        if (entries.IsEmpty) { Bounds = default; hits.Clear(); return []; }
        if (cached is not null && cachedSize == size && cachedScale == scale) return cached;
        cachedSize = size; cachedScale = scale;
        var font = fonts.Font("FONT_SMALL", size, scale); if (font is null) return [];
        hits.Clear(); int labelIndex = 0;
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double line = font.CellHeight * font.Scale;
        float Advance(char c) => font.CharacterAdvance(c) * font.Scale;
        double Measure(string s) => font.MeasureWidth(s) * font.Scale;
        double inner = Width - 2 * Pad;
        var origin = Position is { } p ? new EnbUiPoint(p.X * size.Width, p.Y * size.Height)
            : new EnbUiPoint(size.Width - EnbGutterUi.EdgeClearance * size.Width / 640 - Width, size.Height * .3);
        double x = Math.Clamp(origin.X, 0, Math.Max(0, size.Width - Width)), y = Math.Max(0, origin.Y), top = y;
        var texts = new List<(EnbRetailText Label, double X, double Y)>();
        y += Pad;
        foreach (var entry in entries)
        {
            double start = y;
            string timer = entry.Timer;
            double timerWidth = timer.Length > 0 ? Measure(timer) + 6 : 0;
            Add(Fit(entry.Name, inner - timerWidth), x + Pad, y, entry.Id == hovered ? new(1, 1, .3f) : new(1, .82f, .35f));
            if (timer.Length > 0) Add(timer, x + Width - Pad - Measure(timer), y, new(1, .93f, 0));
            y += line;
            var lines = EnbRetailDialogText.Wrap(entry.Step, (float)inner, Advance);
            int shown = Math.Min(CollapsedLines, lines.Length);
            for (int i = 0; i < shown; i++)
            {
                string text = lines[i];
                if (i == shown - 1 && lines.Length > shown) text = Fit(text + " ...", inner);
                Add(text, x + Pad, y, new(.85f, .85f, .85f)); y += line;
            }
            hits.Add((entry.Id, new(x, start - EntryGap / 2, Width, y - start + EntryGap)));
            y += EntryGap;
        }
        y += Pad - EntryGap;
        Bounds = new(x, top, Width, y - top);
        if (background) images.Add(new(Gradient(Bounds.Center.X > size.Width / 2), Bounds));
        foreach (var (label, lx, ly) in texts) if (label.Raster is { } raster) images.Add(new(raster, new(lx, ly, label.Width, label.Height)));
        cached = [new EnbUiModelDraw(fonts.Main.Asset, 0, Bounds, false) { ImagesOnly = true, Images = images.ToImmutable(), LayoutGroup = EnbUiGroup.None }];
        return cached;

        string Fit(string text, double room)
        {
            if (Measure(text) <= room) return text;
            while (text.Length > 1 && Measure(text + "...") > room) text = text[..^1];
            return text.TrimEnd() + "...";
        }
        void Add(string text, double lx, double ly, EnbRgb color)
        {
            if (labelIndex == labels.Count) labels.Add(new()); var label = labels[labelIndex++];
            label.SetText(text, font, color); label.SetDisplayScale(1, 1); texts.Add((label, lx, ly));
        }
    }

    private static readonly EnbUiRaster FadeLeft = BuildGradient(false), FadeRight = BuildGradient(true);
    /// <summary>Dark band, strongest toward the nearer screen edge and fading toward the centre.</summary>
    private static EnbUiRaster Gradient(bool strongRight) => strongRight ? FadeLeft : FadeRight;
    private static EnbUiRaster BuildGradient(bool strongLeft)
    {
        const int w = 64; var pixels = new EnbColor32[w];
        for (int i = 0; i < w; i++)
        {
            float t = i / (float)(w - 1); if (strongLeft) t = 1 - t;
            pixels[i] = new(0, 0, 0, (byte)(Math.Clamp(.08f + .55f * MathF.Pow(t, .8f), 0, 1) * 255));
        }
        return EnbUiRaster.FromPixels(w, 1, pixels);
    }

    /// <summary>Owns the tooltip while the pointer is over a mission (and no drag is running).</summary>
    public bool OwnsTooltip => !dragging && hovered >= 0 && Contains(pointer);

    /// <summary>
    /// The game's framed green tooltip (same frame, font, delay and screen-clamped placement as
    /// every retail tooltip): mission name, the whole current step, the timer and the click hints.
    /// </summary>
    public EnbUiTooltipDraw? Tooltip(EnbUiSize size, double scale)
    {
        if (!OwnsTooltip) { delay.Reset(); return null; }
        if (!delay.IsReady(hovered, Environment.TickCount64, EnbCurrentRetailTooltipDelay.Milliseconds(assets.ResolvePlayerOptions(null).Options.InterfaceTooltipDelay))) return null;
        if (cachedTooltip is not null) return cachedTooltip;
        EnbMissionTrackerEntry? found = null;
        foreach (var e in entries) if (e.Id == hovered) { found = e; break; }
        if (found is not { } entry) return null;
        var resources = EnbRenderSession.Of(assets).TooltipResources;
        if (resources.Frame is not { } frame || resources.Definition is not { } definition || fonts.Font(definition.FontId, size, scale) is not { } font) return null;
        var text = new System.Text.StringBuilder(entry.Name);
        if (entry.Timer.Length > 0) text.Append(" (").Append(entry.Timer).Append(')');
        // Keep the column narrow like the game's item tooltips: wrap the step at 170 of 640 units.
        float fx = (float)(font.Scale / (size.Width / 640));
        if (entry.Step.Length > 0) text.Append("\n\n").AppendJoin('\n', EnbRetailDialogText.Wrap(entry.Step, 170, c => font.CharacterAdvance(c) * fx));
        text.Append("\n");
        if (opensMission) text.Append("\nLeft-click: show its details");
        else if (arrowHint) text.Append("\nLeft-click: point the waypoint arrow at it");
        text.Append("\nRight-click: stop tracking\nDrag: move the tracker");
        return cachedTooltip = EnbTooltipContent.Create(frame, font, text.ToString(), size, scale, new(.1f, 1, .1f), pointer, i => tipLabels[i]);
    }

    public void Dispose() { foreach (var label in labels) label.Dispose(); labels.Clear(); foreach (var label in tipLabels) label.Dispose(); }
}
