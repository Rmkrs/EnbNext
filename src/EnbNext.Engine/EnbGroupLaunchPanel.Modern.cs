using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI "My characters" panel: the multibox group's characters in the Modern UI style (the clear font, the
/// dialog frame, compact rows coloured by state) with no buttons. A right-click on a character opens its menu (Launch,
/// Invite to group); on the title the panel's own (Invite all, Collapse or Expand). Each entry runs what the retail
/// panel's button or menu entry runs. The title drags the panel; the place is kept.
/// </summary>
internal sealed partial class EnbGroupLaunchPanel
{
    private bool modern;
    /// <summary>Next Modern UI: the Modern UI style and menus instead of the buttons.</summary>
    public bool Modern { get => modern; set { if (modern == value) return; modern = value; menuMember = -1; Invalidate(); } }
    /// <summary>Where the player dragged the panel (before its layout placement); null for its default place.</summary>
    public EnbUiPoint? ModernPosition { get; set; }
    /// <summary>The panel was dragged to a new place (Next preferences keep it).</summary>
    public Action<EnbUiPoint>? ModernMoved { get; set; }
    private EnbUiPoint modernGrab;
    /// <summary>A press on the title starts dragging the panel, as the other Modern UI windows are dragged.</summary>
    private bool ModernClick(EnbUiPoint point)
    {
        if (hits.FirstOrDefault(h => h.Key == "title").Rect.Contains(point)) { Dragging = true; modernGrab = new(point.X - position.X, point.Y - position.Y); }
        Invalidate(); return Contains(point);
    }
    /// <summary>Opens a Modern UI menu (heading, entries) at a screen point.</summary>
    public Action<string?, IReadOnlyList<EnbModernMenuItem>, EnbUiPoint>? ModernMenuRequested { get; set; }

    private bool ModernRightClick(EnbUiPoint point)
    {
        if (!Contains(point)) return false;
        var row = hits.FirstOrDefault(h => h.Key.StartsWith("row/", StringComparison.Ordinal) && h.Rect.Contains(point));
        var screen = Layout?.Placement(EnbUiGroup.Multibox).Apply(point, cachedSize) ?? point;
        var items = new List<EnbModernMenuItem>();
        string? heading;
        if (row.Key is not null && int.Parse(row.Key.AsSpan(4)) is int i && i < Members.Length)
        {
            heading = Members[i].Name;
            items.Add(new("Launch", () => LaunchRequested?.Invoke(i), Enabled: CanLaunch(i)));
            if (InviteRequested is { } invite) items.Add(new("Invite to group", () => invite(i), Enabled: (uint)i < (uint)InviteEnabled.Length && InviteEnabled[i]));
        }
        else
        {
            heading = GroupName;
            if (InviteRequested is { } invite) items.Add(new("Invite all", () => invite(-1), Enabled: InviteEnabled.Any(v => v)));
            items.Add(new(Collapsed ? "Expand" : "Collapse", () => { Collapsed = !Collapsed; Invalidate(); }));
        }
        ModernMenuRequested?.Invoke(heading, items, screen);
        return true;
    }

    private ImmutableList<EnbUiModelDraw> DrawModern(EnbUiSize size, double scale, bool editing)
    {
        hits.Clear();
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        if (EnbModernText.Renderer is not { } text) return [];
        double z = Math.Max(1, size.Height / 760), pad = Math.Round(6 * z), row = Math.Round(19 * z), title = Math.Round(22 * z);
        float namePx = (float)(13 * z * scale), titlePx = (float)(13.5 * z * scale);
        double Measure(string value, float px) => text.Measure(value, px) / scale;
        int count = editing ? 6 : Members.Length;
        (string Name, string State, string Error) Row(int i) => i < Members.Length ? Members[i] : ("Character " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "Online", "");
        double width = Measure(GroupName, titlePx);
        for (int i = 0; i < count; i++) width = Math.Max(width, Measure(Row(i).Name, namePx));
        width = Math.Ceiling(Math.Max(120 * z, width + pad * 2 + 6 * z));
        bool collapsed = !editing && Collapsed;
        double height = pad + title + (collapsed ? 0 : count * row) + pad - (collapsed ? 0 : 2 * z);
        // By default just inside the screen-edge gutter, as the Modern UI frames are; where the player dragged it otherwise.
        position = ModernPosition ?? new(EnbModernHud.GutterDesign * size.Width / 640, Math.Round(size.Height * .26));
        position = new(Math.Clamp(position.X, 0, Math.Max(0, size.Width - width)), Math.Clamp(position.Y, 0, Math.Max(0, size.Height - height)));
        var outer = new EnbUiRect(position.X, position.Y, width, height); panelBounds = outer;
        Frame(outer);
        var titleRect = new EnbUiRect(outer.X + pad + 2 * z, outer.Y + pad, width - pad * 2 - 2 * z, title);
        Label(GroupName, titleRect, new(1, .85f, .3f), titlePx);
        hits.Add(("title", new(outer.X, outer.Y, width, pad + title)));
        if (!collapsed)
            for (int i = 0; i < count; i++)
            {
                var m = Row(i);
                var r = new EnbUiRect(outer.X + 3 * z, outer.Y + pad + title + i * row, width - 6 * z, row);
                // Hover lights the row up; the frame stays as it is.
                if (r.Contains(pointer)) draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, r, false) { ImagesOnly = true, Images = [new(EnbModernIcons.Fill(new(.6f, .75f, 1), 30), r)] });
                EnbRgb color = m.State switch { "Online" => new(.5f, 1, .5f), "Offline" or "Failed" => new(1, .45f, .42f), "Starting" => new(1, .9f, .4f), _ => new(.72f, .75f, .82f) };
                Label(m.Name, new(r.X + pad - 3 * z + 2 * z, r.Y, r.Width - pad * 2, r.Height), color, namePx);
                hits.Add(("row/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), r));
            }
        return cached = draws.Select(d => d with { LayoutGroup = EnbUiGroup.Multibox }).ToImmutableList();

        void Frame(EnbUiRect rect)
        {
            var asset = frames.FirstOrDefault(f => f.Index == 0 && f.Width == rect.Width && f.Height == rect.Height).Asset;
            if (asset is null) { var source = resources.Frames[0]; asset = source.Asset with { Geometry = source.Resize.Geometry((float)rect.Width, (float)rect.Height) }; frames.Add((0, rect.Width, rect.Height, asset)); }
            var min = asset.Geometry!.Positions.Aggregate(System.Numerics.Vector3.Min); var max = asset.Geometry.Positions.Aggregate(System.Numerics.Vector3.Max);
            draws.Add(new(asset, 0, rect, false) { DesignBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y) });
        }
        void Label(string value, EnbUiRect rect, EnbRgb color, float px)
        {
            if (value.Length == 0 || text.Render(value, px, color) is not { } image) return;
            double w = image.Width / scale, h = image.Height / scale, p = text.Padding(px) / scale;
            double x = Math.Round((rect.X - p) * scale) / scale, y = Math.Round((rect.Center.Y - h / 2 - text.InkCenterOffset("H", px) / scale) * scale) / scale;
            draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, rect, false) { ImagesOnly = true, Images = [new(image, new(x, y, w, h))] });
        }
    }
}
