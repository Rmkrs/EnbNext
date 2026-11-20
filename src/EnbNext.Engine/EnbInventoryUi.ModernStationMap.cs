using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI station map (retail has no station map): in a station the map button opens
/// it instead of the sector map. The whole station as one picture (EnbStationMap: the Main Deck in the middle with its
/// door to the Hangar down, every room joined hall to hall) with the terminals, vendors and characters labelled, the
/// doors marked, the players in the avatar's room and the avatar itself. A click on a place walks there (as a
/// click on its Local row); a click on a door goes through to that room.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernStationMap;
    private int _mstationZ;
    private Rect _modernStationMapBounds;
    private EnbStationMap? _stationMap;

    /// <summary>The station as the map draws it (null until drawn, and outside a station).</summary>
    internal EnbStationMap? ModernStationMap
    {
        get => _stationMap;
        set
        {
            if (ReferenceEquals(_stationMap, value)) return;
            _stationMap = value;
            if (_modernStationMap) Publish();
        }
    }
    /// <summary>The avatar's room and place, while it stands in the station.</summary>
    internal (uint Room, Vector2 At)? ModernStationMapSelf { get; set; }
    /// <summary>The station's name, sector and system (the last handoff's, as retail's sector map shows them docked).</summary>
    internal (string Station, string Sector, string System)? ModernStationMapPlace
    {
        get => _stationMapPlace;
        set { if (_stationMapPlace == value) return; _stationMapPlace = value; if (_modernStationMap) Publish(); }
    }
    private (string Station, string Sector, string System)? _stationMapPlace;
    internal EnbUiPoint? ModernStationMapPosition { get; set; }
    internal Action<EnbUiPoint>? ModernStationMapMoved { get; set; }
    public bool ModernStationMapOpen => _modernStationMap;

    /// <summary>The map button inside a station.</summary>
    public bool ToggleModernStationMap()
    {
        if (!_interactive || !ModernUi || !LocalInStation) return false;
        _modernStationMap = !_modernStationMap;
        if (_modernStationMap) { RaiseModern("mstation"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke(_modernStationMap ? "SR_scifielectronic40.wav" : "SR_scifielectronic42.wav");
        Publish(); return true;
    }

    private static string StationMapKindName(EnbStationRosterKind kind) => kind switch
    {
        EnbStationRosterKind.Player => "Player", EnbStationRosterKind.Vendor => "Vendor", EnbStationRosterKind.Terminal => "Terminal",
        EnbStationRosterKind.Door => "Door", _ => "Character",
    };
    private static EnbRgb StationMapColor(EnbStationRosterKind kind) => kind switch
    {
        EnbStationRosterKind.Player => new(.6f, .7f, 1), EnbStationRosterKind.Vendor => new(1, .85f, .45f),
        EnbStationRosterKind.Terminal => new(.55f, .9f, .9f), EnbStationRosterKind.Door => new(.72f, .76f, .86f), _ => new(.92f, .94f, 1),
    };

    private void ModernStationMapWindow()
    {
        var map = _stationMap;
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 8 * u, pad = 10 * u, head = 24 * u;
        // The whole station in one picture, as large as fits (the text keeps its size).
        double aspect = map is null ? 1.4 : map.Floor.Width / (double)map.Floor.Height;
        double maxWidth = Math.Min(1100 * u, _size.Width - 40 * u - pad * 2), maxHeight = Math.Min(820 * u, _size.Height - 40 * u - pad * 2 - head - gap);
        double mapWidth = Math.Max(200 * u, Math.Min(maxWidth, maxHeight * aspect)), mapHeight = mapWidth / aspect;
        double width = pad * 2 + mapWidth, height = pad * 2 + head + gap + mapHeight;
        double x, y;
        if (ModernStationMapPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) / 2; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernStationMapBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernStationMapBounds, "mstation/frame");
        _tips.Remove("mstation/frame");
        Flush();
        double cx = x + pad, cy = y + pad, contentWidth = width - pad * 2;
        // The station by name, then its sector and system; the room the avatar is in at the right.
        var self = ModernStationMapSelf;
        string inRoom = self is { } me ? "You are in the " + EnbWorldRenderer.StationRoomName(me.Room) : "";
        double roomWidth = inRoom.Length > 0 ? ModernMeasure(inRoom, text) + gap : 0;
        string station = _stationMapPlace is { Station.Length: > 0 } named ? named.Station : "Station Map";
        double titleRoom = contentWidth - head - gap - roomWidth;
        ModernText("mstation/title", ModernEllipsis(station, titleRoom, title), new(cx, cy, titleRoom, head), new(1, .85f, .3f), title);
        if (_stationMapPlace is { Sector.Length: > 0 } place)
        {
            string where = place.System.Length > 0 ? place.Sector + ", " + place.System : place.Sector;
            double after = Math.Min(titleRoom, ModernMeasure(station, title)) + gap * 1.5;
            if (titleRoom - after > 40 * u)
                ModernText("mstation/sector", ModernEllipsis(where, titleRoom - after, text), new(cx + after, cy, titleRoom - after, head), new(.65f, .7f, .85f), text);
        }
        if (inRoom.Length > 0)
            ModernText("mstation/where", inRoom, new(cx, cy, contentWidth - head - gap, head), new(.65f, .7f, .85f), text, 2);
        ModernCloseButton("mstation/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;
        var panel = new Rect(cx, cy, mapWidth, mapHeight);
        _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 70), panel));
        if (map is null)
        {
            ModernText("mstation/none", "Drawing the station...", new(cx + 8 * u, cy, contentWidth, head), new(.75f, .8f, .92f), text);
            return;
        }
        _images.Add(new(map.Floor, panel));
        var span = map.Max - map.Min;
        EnbUiPoint Place(Vector2 at) => new(panel.X + (at.X - map.Min.X) / span.X * panel.Width, panel.Y + (map.Max.Y - at.Y) / span.Y * panel.Height);
        var placed = new List<Rect>();
        // Each room's name in the middle of its floor.
        foreach (var room in map.Rooms)
        {
            bool here = self?.Room == room.Room.Room;
            string name = EnbWorldRenderer.StationRoomName(room.Room.Room);
            var p = Place(room.LabelAt);
            double w = ModernMeasure(name, title) + 2, h = Math.Round(22 * u);
            var label = new Rect(p.X - w / 2, p.Y - h / 2, w, h);
            placed.Add(label);
            ModernText($"mstation/room/{room.Room.Room}", name, label, here ? new(1, .85f, .3f) : new(.62f, .68f, .82f), title, 1);
        }
        void Mark(EnbStationMapPlacement room, string key, uint target, string name, EnbStationRosterKind kind, Vector2 local, bool own = false)
        {
            var p = Place(room.ToMap(local));
            if (p.X < panel.X || p.Y < panel.Y || p.X > panel.Right || p.Y > panel.Bottom) return;
            double size = Math.Round((own ? 14 : 10) * u * _scale) / _scale;
            var dot = new Rect(Snap(p.X - size / 2), Snap(p.Y - size / 2), size, size);
            string id = "mstation/mark/" + room.Room.Room + "/" + key;
            var color = own ? new EnbRgb(.45f, 1, .55f) : StationMapColor(kind);
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round((size + 8 * u) * _scale), (int)Math.Round((size + 8 * u) * _scale)),
                new(dot.X - 4 * u, dot.Y - 4 * u, size + 8 * u, size + 8 * u)));
            _images.Add(new(EnbModernIcons.Dot((int)Math.Round(size * _scale), color, own), dot));
            var area = dot;
            // The label beside the mark where it has room (right, left, below, above); otherwise its tip names it.
            // Doors go unlabelled: the halls show where they lead.
            double lw = ModernMeasure(name, text) + 2, lh = Math.Round(18 * u), g = 3 * u;
            if (kind != EnbStationRosterKind.Door)
            foreach (var label in (ReadOnlySpan<Rect>)[new(dot.Right + g, p.Y - lh / 2, lw, lh), new(dot.X - g - lw, p.Y - lh / 2, lw, lh),
                new(p.X - lw / 2, dot.Bottom + g / 2, lw, lh), new(p.X - lw / 2, dot.Y - g / 2 - lh, lw, lh)])
            {
                if (label.X < panel.X || label.Right > panel.Right || label.Y < panel.Y || label.Bottom > panel.Bottom) continue;
                if (placed.Exists(o => o.X < label.Right && label.X < o.Right && o.Y < label.Bottom && label.Y < o.Bottom)) continue;
                placed.Add(label);
                ModernText(id + "/name", name, label, _hover == id ? new(1, 1, .3f) : color, text);
                area = new(Math.Min(dot.X, label.X), Math.Min(dot.Y, label.Y), Math.Max(dot.Right, label.Right) - Math.Min(dot.X, label.X),
                    Math.Max(dot.Bottom, label.Bottom) - Math.Min(dot.Y, label.Y));
                break;
            }
            placed.Add(dot);
            Hit(id, FromPixels(area));
            if (own) { _tips[id] = "You"; return; }
            _tips[id] = kind == EnbStationRosterKind.Door ? "Door to the " + EnbWorldRenderer.StationRoomName(target) + "\nClick to go there"
                : name + "\n" + StationMapKindName(kind) + " in the " + EnbWorldRenderer.StationRoomName(room.Room.Room) + "\nClick to walk there";
        }
        // The avatar and its room's players first (their labels win), then the places, the doors last.
        foreach (var room in map.Rooms)
        {
            if (self?.Room != room.Room.Room) continue;
            Mark(room, "self", room.Room.Room, "You", EnbStationRosterKind.Player, self.Value.At, own: true);
            if (_localStation is { } roster)
                foreach (var entry in roster)
                    if (entry.Kind == EnbStationRosterKind.Player && entry.Position is { } stands) Mark(room, entry.Key, room.Room.Room, entry.Name, entry.Kind, new(stands.X, stands.Y));
        }
        foreach (var kind in (ReadOnlySpan<EnbStationRosterKind>)[EnbStationRosterKind.Terminal, EnbStationRosterKind.Vendor, EnbStationRosterKind.Character, EnbStationRosterKind.Door])
            foreach (var room in map.Rooms)
                foreach (var mark in room.Room.Marks)
                    if (mark.Kind == kind)
                        Mark(room, mark.Key, kind == EnbStationRosterKind.Door && uint.TryParse(mark.Key.AsSpan(mark.Key.LastIndexOf('/') + 1), out uint to) ? to : room.Room.Room,
                            mark.Name, mark.Kind, mark.At);
    }

    private bool ActivateModernStationMap(string id, bool right)
    {
        if (!id.StartsWith("mstation/", StringComparison.Ordinal)) return false;
        if (right) return true;
        if (id == "mstation/close") { _modernStationMap = false; InteractionSoundRequested?.Invoke("TextClick01.wav"); }
        // A mark's id is mstation/mark/{room}/{key}: walk to it, or through a door (door/{room}/{to}) to its room.
        else if (id.StartsWith("mstation/mark/", StringComparison.Ordinal) && id[14..].Split('/', 2) is [var roomText, var key] &&
            uint.TryParse(roomText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint room) && key != "self")
        {
            InteractionSoundRequested?.Invoke("fontclick.wav");
            if (key.StartsWith("door/", StringComparison.Ordinal) && uint.TryParse(key.AsSpan(key.LastIndexOf('/') + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint to))
                ModernLocalWalkRequested?.Invoke("room/" + to, to);
            else ModernLocalWalkRequested?.Invoke(key, room);
        }
        return true;
    }
}
