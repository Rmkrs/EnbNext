using EnbNext.Assets;
using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect = EnbNext.Presentation.EnbUiRect;
using Point = EnbNext.Presentation.EnbUiPoint;

namespace EnbNext.Engine;

/// <summary>One entry of a Next Modern UI menu: an action, a further menu that opens beside it, or (Section) a label
/// over the entries after it, which groups them without a menu of their own.</summary>
internal sealed record EnbModernMenuItem(string Label, Action? Run = null, IReadOnlyList<EnbModernMenuItem>? Children = null, bool Enabled = true, bool Section = false)
{
    /// <summary>A tick box: the entry toggles in place and the menu stays open.</summary>
    public bool? Checked { get; init; }
    public bool Opens => Children is { Count: > 0 };
    public static EnbModernMenuItem Heading(string label) => new(label, Enabled: false, Section: true);
}

/// <summary>
/// Next Modern UI pop-up menu (the help button's menu, the player portrait's menu): a dark panel with a title and its
/// entries; an entry with an arrow opens its own menu beside it when the pointer rests on it. A click on an entry runs
/// it and closes the menu; a click elsewhere or Escape closes it. It lies above every window.
/// </summary>
internal sealed class EnbModernMenu(EnbAuthoredUiAsset canvas)
{
    private readonly List<(string Text, float Pixels, EnbRgb Color, EnbUiRaster Image)> texts = [];
    private readonly List<(Rect Box, IReadOnlyList<EnbModernMenuItem> Items, List<Rect> Rows, int Open)> levels = [];
    private IReadOnlyList<EnbModernMenuItem> items = [];
    // Rebuilds the entries after a tick box changed (their ticks and labels follow the owner's state).
    private Func<IReadOnlyList<EnbModernMenuItem>>? source;
    private string? title;
    private Point anchor;
    private bool above;
    private Point pointer = new(-100000, -100000);
    private ImmutableList<EnbUiModelDraw>? cached;
    private EnbUiSize cachedSize;
    private double cachedScale;

    public bool Open { get; private set; }
    /// <summary>What opened the menu (a button's key, or "portrait"), so pressing that button again closes it.</summary>
    public string? Owner { get; private set; }
    /// <summary>The button the menu belongs to (screen space); a press there is the button's own.</summary>
    public Rect? Button { get; private set; }
    public event Action? Changed;
    public event Action<string>? SoundRequested;

    /// <summary>Opens the menu at <paramref name="at"/>: growing up from it when <paramref name="upward"/>, down otherwise.</summary>
    public void Show(string owner, string? heading, IReadOnlyList<EnbModernMenuItem> entries, Point at, bool upward, Rect? button = null,
        Func<IReadOnlyList<EnbModernMenuItem>>? refresh = null)
    {
        Owner = owner; title = heading; items = entries; anchor = at; above = upward; Button = button; source = refresh;
        Open = entries.Count > 0; levels.Clear(); cached = null; Changed?.Invoke();
    }
    public void Close()
    {
        if (!Open) return;
        Open = false; Owner = null; Button = null; levels.Clear(); cached = null; Changed?.Invoke();
    }
    /// <summary>The entries on screen (every open level), for probes.</summary>
    internal IEnumerable<(string Label, Rect Bounds)> Entries => levels.SelectMany(l => l.Items.Zip(l.Rows, (item, row) => (item.Label, row)));
    public bool Contains(Point p) => Open && levels.Exists(l => l.Box.Contains(p));

    /// <summary>Tracks the pointer: resting on an entry that opens a menu opens it (and closes deeper ones).</summary>
    public bool Pointer(Point p)
    {
        pointer = p;
        if (!Open) return false;
        for (int level = levels.Count - 1; level >= 0; level--)
        {
            var (box, list, rows, open) = levels[level];
            if (!box.Contains(p)) continue;
            int row = rows.FindIndex(r => r.Contains(p));
            if (row >= 0 && row != open && (list[row].Opens || open >= 0))
            {
                levels[level] = (box, list, rows, list[row].Opens ? row : -1);
                if (levels.Count > level + 1) levels.RemoveRange(level + 1, levels.Count - level - 1);
            }
            break;
        }
        cached = null; Changed?.Invoke(); return true;
    }

    /// <summary>A left or right press: an entry runs and the menu closes; anywhere else on the menu does nothing.
    /// Returns false when the press is not on the menu.</summary>
    public bool Press(Point p)
    {
        if (!Contains(p)) return false;
        for (int level = levels.Count - 1; level >= 0; level--)
        {
            var (box, list, rows, _) = levels[level];
            if (!box.Contains(p)) continue;
            int row = rows.FindIndex(r => r.Contains(p));
            if (row < 0) return true;
            var item = list[row];
            if (item.Opens) { Pointer(p); return true; }
            if (!item.Enabled || item.Run is not { } run) return true;
            if (item.Checked is not null)
            {
                SoundRequested?.Invoke("TextClick03.wav");
                run();
                if (source is not null) items = source();
                cached = null; Changed?.Invoke();
                return true;
            }
            Close(); SoundRequested?.Invoke("TextClick03.wav");
            run();
            return true;
        }
        return true;
    }

    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size, double scale)
    {
        if (!Open || EnbModernText.Renderer is not { } font) { if (!Open) levels.Clear(); return []; }
        if (cached is not null && cachedSize == size && cachedScale == scale) return cached;
        if (cachedScale != scale) texts.Clear();
        cachedSize = size; cachedScale = scale;
        double u = Math.Max(.75, size.Height / 1080), text = 14 * u, pad = Math.Round(6 * u), row = Math.Round(24 * u), arrow = Math.Round(14 * u);
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        // Level 0 at the anchor; each open entry's menu beside its row (to the right, or the left at the screen edge).
        var opened = levels.Select(l => l.Open).ToList();
        levels.Clear();
        var list = items; Rect? parentRow = null; Rect? parentBox = null;
        for (int level = 0; list.Count > 0; level++)
        {
            string? heading = level == 0 ? title : null;
            double labelWidth = list.Max(i => font.Measure(i.Label, Pixels(text, scale)) / scale + (i.Checked is null ? 0 : Math.Round(12 * u) + pad));
            if (heading is not null) labelWidth = Math.Max(labelWidth, font.Measure(heading, Pixels(text, scale)) / scale - arrow - pad);
            double width = Math.Ceiling(Math.Max(140 * u, pad * 3 + labelWidth + arrow + pad));
            double headHeight = heading is null ? 0 : row;
            double height = pad * 2 + headHeight + list.Count * row;
            double x, y;
            if (parentRow is { } pr && parentBox is { } pb)
            {
                x = pb.Right + width <= size.Width ? pb.Right - 1 : pb.X - width + 1;
                y = pr.Y - pad - headHeight;
            }
            else { x = anchor.X; y = above ? anchor.Y - height : anchor.Y; }
            // A small margin keeps the menu off the screen edge.
            double margin = Math.Round(4 * u);
            x = Snap(Math.Clamp(x, margin, Math.Max(margin, size.Width - width - margin)), scale); y = Snap(Math.Clamp(y, margin, Math.Max(margin, size.Height - height - margin)), scale);
            var box = new Rect(x, y, width, height);
            images.Add(new(EnbModernIcons.Fill(new(.03f, .05f, .12f), 230), box));
            Border(box, new(.35f, .45f, .75f));
            if (heading is not null)
            {
                Label(heading, new(box.X + pad * 1.5, box.Y + pad, box.Width - pad * 3, row), new(1, .85f, .3f));
                images.Add(new(EnbModernIcons.Fill(new(.35f, .45f, .75f), 160), new(box.X + pad, Snap(box.Y + pad + row - Math.Max(1, u), scale), box.Width - pad * 2, Math.Max(1 / scale, Math.Round(u * scale) / scale))));
            }
            var rows = new List<Rect>();
            int open = level < opened.Count ? opened[level] : -1;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var r = new Rect(box.X + pad / 2, Snap(box.Y + pad + headHeight + i * row, scale), box.Width - pad, row);
                rows.Add(r);
                if (item.Section)
                {
                    // A section label: muted, with a thin rule above it (not at the top of the menu).
                    if (i > 0) images.Add(new(EnbModernIcons.Fill(new(.35f, .45f, .75f), 130), new(r.X + pad / 2, Snap(r.Y + 2 * u, scale), r.Width - pad, Math.Max(1 / scale, Math.Round(u * scale) / scale))));
                    Label(item.Label, new(r.X + pad, r.Y + 2 * u, r.Width - pad * 2, r.Height), new(.6f, .72f, 1));
                    continue;
                }
                bool hot = r.Contains(pointer) || i == open;
                if (hot && item.Enabled) images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 70), r));
                EnbRgb color = !item.Enabled ? new(.45f, .5f, .6f) : hot ? new(1, 1, .45f) : new(.9f, .93f, 1);
                double labelX = r.X + pad;
                if (item.Checked is bool on)
                {
                    // A tick box before the label: its rim, filled when ticked.
                    double mark = Math.Round(12 * u);
                    var tick = new Rect(labelX, Snap(r.Center.Y - mark / 2, scale), mark, mark);
                    images.Add(new(EnbModernIcons.Fill(new(.08f, .11f, .22f), 255), tick));
                    Border(tick, new(.35f, .45f, .75f));
                    if (on) images.Add(new(EnbModernIcons.Fill(new(.45f, 1, .45f), 255), new(tick.X + mark * .25, tick.Y + mark * .25, mark * .5, mark * .5)));
                    labelX = tick.Right + pad;
                }
                Label(item.Label, new(labelX, r.Y, r.Right - pad - arrow - labelX, r.Height), color);
                if (item.Opens && EnbModernSymbols.Get("submenu", (int)Math.Round(arrow * scale), hot) is { } chevron)
                    images.Add(new(chevron, new(r.Right - pad / 2 - arrow, Snap(r.Center.Y - arrow / 2, scale), arrow, arrow)));
            }
            levels.Add((box, list, rows, open));
            if (open < 0 || open >= list.Count || list[open].Children is not { Count: > 0 } children) break;
            parentRow = rows[open]; parentBox = box; list = children;
        }
        var bounds = levels[0].Box;
        foreach (var l in levels) bounds = bounds.Union(l.Box);
        return cached = [new EnbUiModelDraw(canvas, 0, bounds, false) { ImagesOnly = true, Images = images.ToImmutable() }];

        void Label(string value, Rect area, EnbRgb color)
        {
            float px = Pixels(text, scale);
            int index = texts.FindIndex(t => t.Text == value && t.Pixels == px && t.Color == color);
            EnbUiRaster? image = index >= 0 ? texts[index].Image : font.Render(value, px, color);
            if (image is null) return;
            if (index < 0) texts.Add((value, px, color, image));
            double pad_ = font.Padding(px) / scale, w = image.Width / scale, h = image.Height / scale;
            images.Add(new(image, new(Snap(area.X - pad_, scale), Snap(area.Center.Y - h / 2, scale), w, h)));
        }
        void Border(Rect r, EnbRgb color)
        {
            double t = Math.Max(1, Math.Round(u * scale)) / scale;
            var fill = EnbModernIcons.Fill(color, 255);
            images.Add(new(fill, new(r.X, r.Y, r.Width, t))); images.Add(new(fill, new(r.X, r.Bottom - t, r.Width, t)));
            images.Add(new(fill, new(r.X, r.Y, t, r.Height))); images.Add(new(fill, new(r.Right - t, r.Y, t, r.Height)));
        }
    }

    private static float Pixels(double size, double scale) => (float)(size * scale);
    private static double Snap(double v, double scale) => Math.Round(v * scale) / scale;
}
