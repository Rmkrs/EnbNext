using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next-only map marker editor: a small modal dialog built from the game's own dialog art
/// (DIALOG_BOX_WINDOW/INFO/OPTION). It shows the marker's sector and coordinates and edits its
/// name and note. Enter saves, Escape cancels, Tab moves between the two fields.
/// </summary>
internal sealed class EnbMapNoteEditor : IDisposable
{
    public const int NameLength = 32, NoteLength = 80;
    public EnbUiTextInput NameInput { get; } = new(new EnbRetailTextEditor(NameLength)) { Bounds = new(0, 0, 1, 1) };
    public EnbUiTextInput NoteInput { get; } = new(new EnbRetailTextEditor(NoteLength)) { Bounds = new(0, 0, 1, 1) };
    public EnbRetailAssetBundle? Assets { get => assets; set { if (ReferenceEquals(assets, value)) return; assets = value; frames.Clear(); Invalidate(); if (value is null) Close(); } }
    public bool IsOpen { get; private set; }
    public Guid? NoteId { get; private set; }
    public uint Sector { get; private set; }
    public string SectorName { get; private set; } = "";
    public Vector3 Position { get; private set; }
    /// <summary>Opened from the temporary waypoint pin: saving keeps the arrow on the new marker.</summary>
    public bool FromWaypoint { get; private set; }
    public string Name => NameInput.Text;
    public string Note => NoteInput.Text;
    /// <summary>Offer the "Waypoint" button (the waypoint arrow option is on).</summary>
    public bool ShowWaypoint { get; set; }
    /// <summary>Save (and, when true, set the waypoint arrow to the marker).</summary>
    public Action<EnbMapNoteEditor, bool>? Committed { get; set; }
    public Action<Guid>? DeleteRequested { get; set; }
    public event Action? Changed;
    public EnbUiRect Bounds { get; private set; }
    private EnbRetailAssetBundle? assets;
    private readonly List<EnbRetailText> labels = [];
    private readonly List<(int Index, double Width, double Height, EnbAuthoredUiAsset Asset)> frames = [];
    private readonly List<(string Key, EnbUiRect Rect)> hits = [];
    private readonly List<object> owners = [];
    private ImmutableList<EnbUiModelDraw>? cached;
    private EnbUiSize cachedSize; private double cachedScale;
    private EnbUiPoint pointer = new(-100000, -100000);
    private static readonly EnbUiRaster Caret = EnbUiRaster.FromPixels(1, 1, [new EnbColor32(255, 255, 255, 255)]);
    private static readonly EnbRgb White = new(1, 1, 1), Yellow = new(1, 1, .1f), Green = new(.1f, 1, .1f), Grey = new(.7f, .7f, .7f);

    public EnbMapNoteEditor()
    {
        foreach (var input in new[] { NameInput, NoteInput })
        {
            input.Changed += (_, _) => Invalidate();
            input.GotFocus += (_, _) => Invalidate();
            input.KeyDown += (_, e, handled) =>
            {
                // Every key belongs to the dialog while a field has focus: none may fly the ship.
                handled?.Invoke();
                if (e.Code == 13) Commit(false);
                else if (e.Code == 27) Close();
                else if (e.Code == 9) (ReferenceEquals(input, NameInput) ? NoteInput : NameInput).Focus();
            };
        }
    }

    public void Open(uint sector, string sectorName, Vector3 position, EnbMapNote? note, string name = "", bool fromWaypoint = false)
    {
        NoteId = note?.Id; Sector = sector; SectorName = sectorName; Position = position; FromWaypoint = fromWaypoint && note is null;
        NameInput.Text = note?.Name ?? name; NoteInput.Text = note?.Note ?? "";
        NameInput.CaretIndex = NameInput.Text.Length; NoteInput.CaretIndex = NoteInput.Text.Length;
        IsOpen = true; NameInput.IsVisible = NoteInput.IsVisible = true; NameInput.Focus();
        Invalidate();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false; NameInput.IsVisible = NoteInput.IsVisible = false; hits.Clear(); Bounds = default;
        Invalidate();
    }

    private void Commit(bool waypoint)
    {
        if (!IsOpen) return;
        Committed?.Invoke(this, waypoint); Close();
    }

    public void Invalidate() { cached = null; Changed?.Invoke(); }

    public EnbUiRect? FindHit(string key) { foreach (var h in hits) if (h.Key == key) return h.Rect; return null; }

    private string? Hovered() { foreach (var h in hits) if (h.Rect.Contains(pointer)) return h.Key; return null; }

    /// <summary>The dialog is modal: while open it owns every pointer event.</summary>
    public bool Pointer(EnbUiPoint p)
    {
        if (!IsOpen) return false;
        var before = Hovered(); pointer = p;
        if (Hovered() != before) Invalidate();
        return true;
    }

    public bool Press(EnbUiPoint p)
    {
        if (!IsOpen) return false;
        pointer = p;
        switch (Hovered())
        {
            case "name": NameInput.Focus(); break;
            case "note": NoteInput.Focus(); break;
            case "save": Commit(false); break;
            case "waypoint": Commit(true); break;
            case "delete": if (NoteId is Guid id) { DeleteRequested?.Invoke(id); Close(); } break;
            case "cancel": Close(); break;
        }
        Invalidate();
        return true;
    }

    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize size, double scale)
    {
        if (!IsOpen || assets is null) { hits.Clear(); Bounds = default; return []; }
        if (cached is not null && cachedSize == size && cachedScale == scale) return cached;
        if (cachedSize != size || cachedScale != scale) frames.Clear();
        cachedSize = size; cachedScale = scale;
        var session = EnbRenderSession.Of(assets);
        var resources = session.Confirmations;
        var font = session.OptionsPanels.Font("FONT_SMALL", size, scale); if (font is null) return [];
        hits.Clear(); int labelIndex = 0, ownerIndex = 0;
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>(); var texts = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double kx = size.Width / 640, ky = size.Height / 480;
        double text = font.CellHeight * font.Scale, pad = 8 * kx, gap = 4 * ky, row = text + 6 * ky;
        double Measure(string s) => font.MeasureWidth(s) * font.Scale;
        string where = $"{SectorName}   {EnbWayCommand.Format(Position)} km";
        string[] buttons = [.. new[] { "save", ShowWaypoint && !FromWaypoint ? "waypoint" : null, NoteId is null ? null : "delete", "cancel" }.OfType<string>()];
        static string Caption(string key) => key switch { "save" => "Save", "waypoint" => "Waypoint", "delete" => "Delete", _ => "Cancel" };
        double buttonW = Math.Max(54 * kx, buttons.Max(b => Measure(Caption(b))) + 14 * kx);
        double captionW = Math.Max(Measure("Name"), Measure("Note")) + 6 * kx;
        double width = Math.Max(Math.Max(260 * kx, Measure(where) + pad * 2), buttons.Length * (buttonW + gap) - gap + pad * 2);
        double height = pad * 2 + row * 4 + gap * 4 + row;
        Bounds = new(Math.Round((size.Width - width) / 2), Math.Round(size.Height * .42 - height / 2), width, height);
        Frame(0, Bounds);
        double y = Bounds.Y + pad, left = Bounds.X + pad, inner = width - pad * 2;
        Text(NoteId is not null ? "Edit map marker" : FromWaypoint ? "Save waypoint as marker" : "New map marker", left, new(left, y, inner, row), Green); y += row + gap;
        Text(where, left, new(left, y, inner, row), Grey); y += row + gap;
        Field("name", "Name", NameInput, y); y += row + gap;
        Field("note", "Note", NoteInput, y); y += row + gap * 2;
        double bx = Bounds.X + (width - (buttons.Length * (buttonW + gap) - gap)) / 2;
        foreach (string key in buttons)
        {
            var rect = new EnbUiRect(bx, y, buttonW, row + 2 * ky); bx += buttonW + gap;
            bool hot = rect.Contains(pointer);
            Frame(2, rect);
            string caption = Caption(key);
            Text(caption, rect.Center.X - Measure(caption) / 2, rect, hot ? Yellow : White);
            hits.Add((key, rect));
        }
        if (draws.Count > 0) draws[^1] = draws[^1] with { Images = texts.ToImmutable() };
        return cached = draws.ToImmutable();

        void Field(string key, string caption, EnbUiTextInput input, double top)
        {
            var line = new EnbUiRect(left, top, inner, row);
            Text(caption, left, line, White);
            var box = new EnbUiRect(left + captionW, top, inner - captionW, row);
            Frame(1, box);
            hits.Add((key, box));
            string value = input.Text; int caret = Math.Clamp(input.CaretIndex, 0, value.Length);
            double room = box.Width - 8 * kx; int start = 0;
            while (start < caret && Measure(value[start..caret]) > room) start++;
            int end = value.Length; while (end > caret && Measure(value[start..end]) > room) end--;
            string shown = value[start..end]; double x = box.X + 4 * kx;
            if (shown.Length > 0) Text(shown, x, box, White);
            if (input.IsFocused)
            {
                double cx = x + Measure(value[start..caret]);
                texts.Add(new(Caret, new(Math.Round(cx), box.Center.Y - text / 2, Math.Max(1, kx), text)));
            }
        }
        void Frame(int index, EnbUiRect rect)
        {
            var art = frames.FirstOrDefault(f => f.Index == index && f.Width == rect.Width && f.Height == rect.Height).Asset;
            if (art is null) { var source = resources.Frames[index]; art = source.Asset with { Geometry = source.Resize.Geometry((float)rect.Width, (float)rect.Height) }; frames.Add((index, rect.Width, rect.Height, art)); }
            var min = art.Geometry!.Positions.Aggregate(Vector3.Min); var max = art.Geometry.Positions.Aggregate(Vector3.Max);
            draws.Add(new(art, 0, rect, index == 2 && rect.Contains(pointer)) { DesignBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y), GadgetOwner = Owner() });
        }
        object Owner() { if (ownerIndex == owners.Count) owners.Add(new()); return owners[ownerIndex++]; }
        void Text(string value, double x, EnbUiRect rect, EnbRgb color)
        {
            if (labelIndex == labels.Count) labels.Add(new()); var label = labels[labelIndex++]; label.SetText(value, font, color); label.SetDisplayScale(1, 1);
            if (label.Raster is { } raster) texts.Add(new(raster, new(x, rect.Center.Y - label.Height / 2, label.Width, label.Height)));
        }
    }

    public void Dispose() { Close(); foreach (var label in labels) label.Dispose(); labels.Clear(); }
}
