using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI damage meter: the same totals (dealt or taken, this encounter or the session, all targets or the
/// current one) in the Modern UI style with no buttons. The title says what is shown and how long the fight lasted;
/// each character's row has a bar for their share of the largest number. A right-click opens the meter's menu (the
/// three choices and Reset); the title drags the meter, and the place is kept. Hovering a row shows its damage types
/// beside the meter, as the retail-styled meter does.
/// </summary>
internal sealed partial class EnbDamageMeterPanel
{
    private bool modern, dragging;
    private EnbUiPoint grab, origin;
    public bool Modern { get => modern; set { if (modern == value) return; modern = value; menu = false; cached = null; } }
    /// <summary>Opens a Modern UI menu (heading, entries) at a screen point.</summary>
    public Action<string?, IReadOnlyList<EnbModernMenuItem>, EnbUiPoint>? ModernMenuRequested { get; set; }
    /// <summary>Where the player dragged the meter (before its layout placement); null for its default place.</summary>
    public EnbUiPoint? ModernPosition { get; set; }
    public Action<EnbUiPoint>? ModernMoved { get; set; }
    public bool Dragging => dragging;
    public void EndDrag() { if (dragging) ModernMoved?.Invoke(ModernPosition ?? origin); dragging = false; }

    private bool ModernRightClick(EnbUiPoint p)
    {
        if (!Bounds.Contains(p)) return false;
        var screen = Layout?.Placement(EnbUiGroup.DamageMeter).Apply(p, cachedSize) ?? p;
        ModernMenuRequested?.Invoke("Damage meter",
        [
            new(incoming ? "Show damage dealt" : "Show damage taken", () => { incoming = !incoming; cached = null; }),
            new(session ? "Show this encounter" : "Show the whole session", () => { session = !session; cached = null; }),
            new(target ? "Show all targets" : "Show the current target only", () => { target = !target; cached = null; }),
            EnbModernMenuItem.Heading(""),
            new("Reset", () => { model.Reset(now); cached = null; }),
        ], screen);
        return true;
    }

    private bool ModernClick(EnbUiPoint p)
    {
        if (FindHit("title") is { } title && title.Contains(p)) { dragging = true; grab = new(p.X - Bounds.X, p.Y - Bounds.Y); }
        cached = null; return Bounds.Contains(p);
    }

    private void ModernPointer(EnbUiPoint p) { if (dragging) ModernPosition = new(p.X - grab.X, p.Y - grab.Y); }

    private ImmutableList<EnbUiModelDraw> DrawModern(EnbUiSize size, double scale, bool editing)
    {
        hits.Clear();
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        if (EnbModernText.Renderer is not { } text) return [];
        double z = Math.Max(1, size.Height / 760), pad = Math.Round(7 * z), row = Math.Round(19 * z), head = Math.Round(20 * z), sub = Math.Round(15 * z);
        float body = (float)(12.5 * z * scale), title = (float)(13.5 * z * scale), small = (float)(11 * z * scale);
        double Measure(string value, float px) => text.Measure(value, px) / scale;

        // The rows: each character (edit mode previews six), then the total when there is more than one.
        var rows = new List<(string Key, string Name, double Value)>();
        if (editing) for (int i = 0; i < 6; i++) rows.Add(("", "Character " + (i + 1).ToString(CultureInfo.InvariantCulture), (6 - i) * 1000));
        else foreach (var pair in model.Members) rows.Add((pair.Key.ToString(), pair.Value.Name, Values(pair.Key, pair.Value).Total));
        double total = 0, largest = 0; foreach (var r in rows) { total += r.Value; largest = Math.Max(largest, r.Value); }
        double seconds = model.Duration(session, now) / 1000d;
        bool showTotal = rows.Count > 1;

        double nameWidth = Measure("Character 6", body);
        foreach (var r in rows) nameWidth = Math.Max(nameWidth, Measure(r.Name, body));
        double number = Measure("999.9M", body) + 6 * z, rate = Measure("999.9M/s", body) + 6 * z;
        double width = Math.Ceiling(Math.Max(Math.Max(210 * z, Measure("Damage dealt  00:00:00", title) + pad * 2), nameWidth + number + rate + pad * 2 + 10 * z));
        double height = pad + head + sub + 4 * z + rows.Count * row + (showTotal ? row + 3 * z : 0) + pad;
        var position = ModernPosition ?? new(EnbModernHud.GutterDesign * size.Width / 640, Math.Round(size.Height * .26 + 175 * z)); // under the My characters panel, clear of the chat
        origin = new(Math.Clamp(position.X, 0, Math.Max(0, size.Width - width)), Math.Clamp(position.Y, 0, Math.Max(0, size.Height - height)));
        Bounds = new(origin.X, origin.Y, width, height);
        Frame(Bounds);
        double x = Bounds.X + pad, content = width - pad * 2, y = Bounds.Y + pad;

        // Title: what is shown and for how long; under it the period and scope.
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        string time = span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        Label(incoming ? "Damage taken" : "Damage dealt", new(x, y, content, head), new(1, .85f, .3f), title);
        Label(time, new(x, y, content, head), new(.75f, .8f, .9f), body, true);
        Label((session ? "Session" : "Encounter") + ", " + (target ? "current target" : "all targets"), new(x, y + head, content, sub), new(.6f, .68f, .82f), small);
        hits.Add(("title", new(Bounds.X, Bounds.Y, width, pad + head + sub)));
        y += head + sub + 4 * z;

        for (int i = 0; i < rows.Count; i++)
        {
            var (key, name, value) = rows[i];
            var r = new EnbUiRect(x - 2 * z, y + i * row, content + 4 * z, row);
            // The character's share of the largest number, as a bar behind the row; hover lights the row.
            if (largest > 0 && value > 0) Image(EnbModernIcons.Fill(new(.32f, .48f, .95f), 70), new(r.X, r.Y + 1 * z, r.Width * value / largest, r.Height - 2 * z));
            bool hot = r.Contains(pointer) && !editing;
            if (hot) Image(EnbModernIcons.Fill(new(.6f, .75f, 1), 30), r);
            Columns(name, value, r, new(.92f, .94f, 1));
            if (key.Length > 0) hits.Add((key, r));
        }
        if (showTotal)
        {
            double ty = y + rows.Count * row + 3 * z;
            Image(EnbModernIcons.Fill(new(.35f, .45f, .75f), 130), new(x, Math.Round((ty - 2 * z) * scale) / scale, content, Math.Max(1, Math.Round(z * scale)) / scale));
            var r = new EnbUiRect(x - 2 * z, ty, content + 4 * z, row);
            if (r.Contains(pointer) && !editing) Image(EnbModernIcons.Fill(new(.6f, .75f, 1), 30), r);
            Columns("Total", total, r, new(1, .93f, .6f));
            if (!editing) hits.Add(("total", r));
        }
        if (!editing) ModernDetail();
        return cached = draws.Select(d => d with { LayoutGroup = EnbUiGroup.DamageMeter }).ToImmutableList();

        void Columns(string name, double value, EnbUiRect r, EnbRgb color)
        {
            var inner = new EnbUiRect(r.X + 4 * z, r.Y, r.Width - 8 * z, r.Height);
            Label(name, new(inner.X, inner.Y, inner.Width - number - rate, inner.Height), color, body);
            Label(Number(value, total), new(inner.Right - rate - number, inner.Y, number, inner.Height), new(1, 1, 1), body, true);
            Label(Number(seconds > 0 ? value / seconds : 0, seconds > 0 ? total / seconds : 0) + "/s", new(inner.Right - rate, inner.Y, rate, inner.Height), new(.72f, .78f, .9f), body, true);
        }

        // The hovered row's damage types, in their retail damage-number colours, in a pane beside the meter.
        void ModernDetail()
        {
            int index = hits.FindIndex(h => h.Rect.Contains(pointer) && (h.Key == "total" || Guid.TryParse(h.Key, out _)));
            if (index < 0) return;
            var hovered = hits[index].Rect; var values = new EnbCombatTotals(); string name = "Total";
            if (hits[index].Key == "total") { foreach (var pair in model.Members) { var part = Values(pair.Key, pair.Value); for (int i = 0; i < 7; i++) values.Types[i] += part.Types[i]; } }
            else { Guid id = Guid.Parse(hits[index].Key); if (model.Find(id) is not { } member) return; name = member.Name; values = Values(id, member); }
            var shown = Enumerable.Range(0, 7).Where(i => i != 4 && values.Types[i] > 0).ToArray(); bool emp = values.Types[4] > 0;
            string heading = name + (incoming ? ", taken" : ", dealt");
            double sum = values.Total, typeWidth = Measure("Explosive", body), amount = Measure("999.9M", body) + 6 * z, share = Measure("100%", body) + 6 * z;
            double paneWidth = Math.Ceiling(Math.Max(Measure(heading, title), typeWidth + amount + share + 8 * z) + pad * 2);
            double paneHeight = pad + head + (shown.Length + 1 + (emp ? 1 : 0)) * row + pad;
            double px = Bounds.Right + 3 * z + paneWidth <= size.Width ? Bounds.Right + 3 * z : Bounds.X - paneWidth - 3 * z;
            double py = Math.Clamp(hovered.Y - pad - head, 0, Math.Max(0, size.Height - paneHeight));
            var pane = new EnbUiRect(px, py, paneWidth, paneHeight);
            Frame(pane);
            double cx = pane.X + pad, cw = paneWidth - pad * 2, cy = pane.Y + pad;
            Label(heading, new(cx, cy, cw, head), new(1, .85f, .3f), title);
            cy += head;
            foreach (int i in shown)
            {
                var color = TypeColor(i);
                Label(TypeNames[i], new(cx, cy, cw - amount - share, row), color, body);
                Label(Number(values.Types[i], sum), new(cx + cw - amount - share, cy, amount, row), color, body, true);
                Label((sum > 0 ? values.Types[i] / sum * 100 : 0).ToString("0", CultureInfo.InvariantCulture) + "%", new(cx + cw - share, cy, share, row), color, body, true);
                cy += row;
            }
            Label("Total", new(cx, cy, cw - amount - share, row), new(1, .93f, .6f), body);
            Label(Number(sum, sum), new(cx + cw - amount - share, cy, amount, row), new(1, 1, 1), body, true);
            if (emp)
            {
                // EMP drains energy and is not part of the damage total: it follows the total line.
                cy += row; var color = TypeColor(4);
                Label(TypeNames[4], new(cx, cy, cw - amount - share, row), color, body);
                Label(Number(values.Types[4], Math.Max(sum, values.Types[4])), new(cx + cw - amount - share, cy, amount, row), color, body, true);
            }
        }
        void Frame(EnbUiRect rect)
        {
            var art = frames.FirstOrDefault(f => f.Index == 0 && f.Width == rect.Width && f.Height == rect.Height).Asset;
            if (art is null) { var source = resources.Frames[0]; art = source.Asset with { Geometry = source.Resize.Geometry((float)rect.Width, (float)rect.Height) }; frames.Add((0, rect.Width, rect.Height, art)); }
            var min = art.Geometry!.Positions.Aggregate(System.Numerics.Vector3.Min); var max = art.Geometry.Positions.Aggregate(System.Numerics.Vector3.Max);
            draws.Add(new(art, 0, rect, false) { DesignBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y) });
        }
        void Image(EnbUiRaster image, EnbUiRect rect) => draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, rect, false) { ImagesOnly = true, Images = [new(image, rect)] });
        void Label(string value, EnbUiRect rect, EnbRgb color, float px, bool right = false)
        {
            if (value.Length == 0) return;
            if (Measure(value, px) > rect.Width)
            {
                while (value.Length > 1 && Measure(value + "...", px) > rect.Width) value = value[..^1];
                value = value.TrimEnd() + "...";
            }
            if (text.Render(value, px, color) is not { } image) return;
            double w = image.Width / scale, h = image.Height / scale, p = text.Padding(px) / scale;
            double left = right ? rect.Right - (w - p * 2) - p : rect.X - p;
            double top = Math.Round((rect.Center.Y - h / 2 - text.InkCenterOffset("H", px) / scale) * scale) / scale;
            draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, rect, false) { ImagesOnly = true, Images = [new(image, new(Math.Round(left * scale) / scale, top, w, h))] });
        }
    }
}
