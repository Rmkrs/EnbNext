using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI map window. The same map (markers, labels, planets, routes, notes, selection and plotting) in a
/// Modern UI window instead of the retail TSI26A frame: the map area (see-through like the retail map, or solid) and
/// a side panel with the title, a find box, the name and coordinate choices and the route planner. The window can be
/// moved and resized. The sector map never leaves its sector: the furthest zoom out is the whole sector. The galaxy
/// map is its own window, in 3D or as a flat schematic. The retail map is unchanged.
/// </summary>
internal sealed partial class EnbNavigationMapUi
{
    private bool _modern;
    /// <summary>Next Modern UI: the map in its own Modern UI window. Changing it closes and refits the map.</summary>
    internal bool Modern
    {
        get => _modern;
        set
        {
            if (_modern == value) return;
            CloseForPopup(); _modern = value;
            _projection = new(); _sectorProjection = new(); _openedOnce = false; _modernChrome = [];
        }
    }
    /// <summary>Saved top-left of the window as a fraction of the screen; null places it.</summary>
    internal Point? ModernMapPosition { get; set; }
    internal Action<Point>? ModernMapMoved { get; set; }
    /// <summary>Saved size of the map area as a fraction of the screen; null uses the default.</summary>
    internal Point? ModernMapSize { get; set; }
    internal Action<Point>? ModernMapResized { get; set; }
    /// <summary>Marker kinds whose names the sector map shows (bit per <see cref="ModernKinds"/> entry).</summary>
    internal int ModernMapNames { get; set; } = 7;
    internal bool ModernMapPointerCoordinates { get; set; }
    internal bool ModernMapSeeThrough { get; set; } = true;
    internal bool ModernGalaxySchematic { get; set; }
    /// <summary>Called when a legend choice changes (names, coordinates, see-through, galaxy view), to keep them.</summary>
    internal Action? ModernMapSettingsChanged { get; set; }
    /// <summary>The host's text box factory (maximum length, key handler), for the find box.</summary>
    internal Func<int, Func<ushort, Task<bool>>, EnbUiTextInput>? ModernTextInputFactory { get; set; }
    internal bool ModernFindEditing => _modernFind is { IsVisible: true, IsFocused: true };

    private static readonly (string Label, string Glyph)[] ModernKinds =
        [("Planets", "m_planet.w3d"), ("Stations", "m_sbase.w3d"), ("Gates", "m_gate.w3d"), ("Navigation points", "m_deco.w3d"), ("Players", "m_pc.w3d"), ("Other", "m_npc.w3d")];

    private Rect _modernWindow, _modernMapRect, _modernContentLogical, _modernContent, _modernPanel;
    private readonly List<(string Id, Rect Bounds, string Tip)> _modernHits = [];
    private ImmutableArray<EnbUiModelDraw> _modernChrome = [];
    private ImmutableArray<EnbMapMarker> _modernLegendMarkers = [];
    private readonly List<(string Text, float Pixels, EnbRgb Color, EnbUiRaster Image)> _modernText = [];
    private readonly List<(int Index, double Width, double Height, EnbAuthoredUiAsset Asset)> _modernFrames = [];
    private Point _modernDragOrigin, _modernResizeOrigin;
    private EnbUiTextInput? _modernFind;
    private bool _modernFindOpen, _modernFindGalaxy, _modernFindResetting;
    /// <summary>Find results: sector-map objects (game ids) or galaxy sectors/systems (hash ids, systems flagged).</summary>
    private readonly List<(uint Id, bool System, string Name)> _modernMatches = [];
    private uint? _modernFound;
    private uint? _modernFoundSector;
    /// <summary>Find: the found item pulses for a moment; the galaxy map's item (a sector or a system).</summary>
    private long _modernPulseStart;
    private uint _modernPulseId;
    private bool _modernPulseSystem;
    private static long s_modernRaise;
    /// <summary>Next Modern UI: the sector and galaxy map windows can both be open; the one used last lies on top.</summary>
    internal long Raised { get; private set; }
    private void Raise() => Raised = Interlocked.Increment(ref s_modernRaise);
    /// <summary>The galaxy map window of this sector map (Next Modern UI): opened from the route planner.</summary>
    internal Action? ModernGalaxyRequested { get; set; }
    /// <summary>A sector's Net-7 Wiki page was asked for (its name): the wiki window opens on it.</summary>
    internal Action<string>? WikiRequested { get; set; }
    /// <summary>The galaxy map window opens offset from the sector map window by default.</summary>
    internal bool GalaxyWindow { get; init; }

    /// <summary>The Modern UI micro-menu map buttons: the sector map or the galaxy map, each its own window.</summary>
    /// <summary>Brings an open window to the top.</summary>
    internal void RaiseModern() { if (!IsOpen) return; Raise(); Publish(); }

    internal bool ToggleModern(bool galaxy)
    {
        if (!ToggleMode(galaxy)) return false;
        InteractionSoundRequested?.Invoke(IsOpen ? "SR_scifielectronic40.wav" : "SR_scifielectronic42.wav");
        return true;
    }

    /// <summary>The Modern UI window's controls (probe receipts).</summary>
    internal IReadOnlyList<(string Id, Rect Bounds, string Tip)> ModernControls => _modernHits;
    /// <summary>The Modern UI map area on screen (pre-layout logical pixels) and the window.</summary>
    internal Rect ModernMapArea => _modernContentLogical;
    internal Rect ModernWindowArea => _modernWindow;
    internal EnbNavigationMapProjection Projection => _projection;
    internal Vector2 ProjectionViewport => _frame is null ? default : Viewport;
    internal IReadOnlyList<(uint Id, bool System, string Name)> ModernFindMatches => _modernMatches;
    internal EnbUiTextInput? ModernFindInput => _modernFind;

    private void ModernTip(string id, string tip)
    {
        int index = _modernHits.FindLastIndex(h => h.Id == id);
        if (index >= 0) _modernHits[index] = (_modernHits[index].Id, _modernHits[index].Bounds, tip);
    }
    private double ModernUnit => Math.Max(.75, _size.Height / 1080);
    /// <summary>The map area in authored 640x480 units: the Modern UI window's, or the retail frame's.</summary>
    private Rect Content => _modern && _modernContent.Width > 0 ? _modernContent : _frame!.ContentBounds;
    /// <summary>The whole map window in authored units (it blocks flight input).</summary>
    private Rect Background => _modern && _modernWindow.Width > 0 ? ToDesign(_modernWindow) : _frame!.BackgroundBounds;
    private Rect ToDesign(Rect r) => new(r.X * 640 / _size.Width, r.Y * 480 / _size.Height, r.Width * 640 / _size.Width, r.Height * 480 / _size.Height);
    private double Snap(double v) => Math.Round(v * _scaling) / _scaling;
    private Point ModernPointer => UiLayout?.Inverse(EnbUiGroup.Map, _pointer) ?? _pointer;
    private static string ModernName(string name) => name.Replace("â\u0080\u0099", "'").Replace("’", "'");

    /// <summary>The Modern UI kind of a map object (an index into <see cref="ModernKinds"/>); players include the ship itself.</summary>
    private static int ModernKind(IEnbRenderObject obj) => obj.IsLocalShip ? 4 : obj.ObjectType switch { 3 => 0, 12 => 1, 11 => 2, 0x25 => 3, 1 => 4, _ => 5 };
    // Next Modern UI: your own name stays on the map while your rescue beacon is on (retail always labels you).
    private bool ModernShowsName(IEnbRenderObject obj) => (ModernMapNames & 1 << ModernKind(obj)) != 0 || obj.IsLocalShip && obj.IsRescueBeaconActive;

    /// <summary>The map area (left) and the side panel (right); the map area keeps its saved or default size.</summary>
    private void LayoutModern()
    {
        if (!_modern || _size.Width <= 0 || _size.Height <= 0) return;
        double u = ModernUnit, panelWidth = Math.Round(250 * u), minHeight = Math.Round(430 * u), minWidth = Math.Round(320 * u);
        double height = ModernMapSize is { } size ? size.Y * _size.Height : Math.Round(_size.Height * .48);
        double width = ModernMapSize is { } saved ? saved.X * _size.Width : Math.Round(height * 1.3);
        width = Math.Round(Math.Clamp(width, minWidth, Math.Max(minWidth, _size.Width - panelWidth - 8 * u)));
        height = Math.Round(Math.Clamp(height, minHeight, Math.Max(minHeight, _size.Height - 8 * u)));
        double total = width + panelWidth, x, y;
        if (ModernMapPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - total) / 2 + (GalaxyWindow ? 40 * u : 0); y = (_size.Height - height) * .4 + (GalaxyWindow ? 40 * u : 0); }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - total))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernWindow = new(x, y, total, height);
        _modernMapRect = new(x, y, width, height);
        double inset = Math.Round(3 * u);
        _modernContentLogical = new(Snap(x + inset), Snap(y + inset), width - 2 * inset, height - 2 * inset);
        _modernContent = ToDesign(_modernContentLogical);
        _modernPanel = new(x + width, y, panelWidth, height);
    }

    /// <summary>The control under a point (pre-layout logical coordinates), if any.</summary>
    private string? ModernHit(Point p)
    {
        var q = UiLayout?.Inverse(EnbUiGroup.Map, p) ?? p;
        for (int i = _modernHits.Count - 1; i >= 0; i--) if (_modernHits[i].Bounds.Contains(q)) return _modernHits[i].Id;
        // The panel body and the map border move the window, like the other Modern UI windows.
        return _modernWindow.Contains(q) && !_modernContentLogical.Contains(q) ? "mmap/title" : null;
    }

    private void ActivateModern(string id)
    {
        if (_state is null) return;
        string sound = "TextClick03.wav";
        if (id == "mmap/close") { if (Close()) InteractionSoundRequested?.Invoke(sound); return; }
        if (id == "mmap/find") { BeginModernFind(); return; }
        if (id.StartsWith("mmap/names/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(11), out int kind))
        { ModernMapNames ^= 1 << kind; _labelsInvalidated = true; ModernMapSettingsChanged?.Invoke(); }
        else if (id == "mmap/coords") { ModernMapPointerCoordinates = !ModernMapPointerCoordinates; ModernMapSettingsChanged?.Invoke(); }
        else if (id == "mmap/seethrough") { ModernMapSeeThrough = !ModernMapSeeThrough; ModernMapSettingsChanged?.Invoke(); }
        else if (id is "mmap/view/3d" or "mmap/view/schematic")
        {
            bool schematic = id == "mmap/view/schematic";
            if (schematic == ModernGalaxySchematic) return;
            ModernGalaxySchematic = schematic; _schematicFitted = false; ModernMapSettingsChanged?.Invoke();
        }
        else if (id == "mmap/marker") ToggleAddMarker();
        else if (id == "mmap/home") { if (ModernGalaxySchematic) CentreSchematicOnHome(); else FitGalaxy(); }
        else if (id == "mmap/togalaxy") ModernGalaxyRequested?.Invoke();
        else if (id == "mmap/clearroute") Guidance.Choose(null);
        else if (id.StartsWith("mmap/wiki/", StringComparison.Ordinal) && uint.TryParse(id.AsSpan(10), out uint wikiSector) && WikiSector(wikiSector) is { } wikiName) WikiRequested?.Invoke(wikiName);
        else if (id == "mmap/plotfound" && _modernFoundSector is uint found && _scene is not null) { Guidance.Choose(found); Guidance.Update(GalaxyMapAccessor?.Invoke(), _scene); }
        else if (id.StartsWith("mmap/match/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(11), out int match) && match < _modernMatches.Count) GoToMatch(_modernMatches[match]);
        else return;
        InteractionSoundRequested?.Invoke(sound);
    }

    /// <summary>Moves the window with the panel or the map border; the place is kept when the drag ends.</summary>
    private void DragModern(Point point)
    {
        double dx = point.X - _pressPoint.X, dy = point.Y - _pressPoint.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragged = true;
        double x = Math.Clamp(_modernDragOrigin.X + dx, 0, Math.Max(0, _size.Width - _modernWindow.Width));
        double y = Math.Clamp(_modernDragOrigin.Y + dy, 0, Math.Max(0, _size.Height - _modernWindow.Height));
        ModernMapPosition = new(x / _size.Width, y / _size.Height);
        LayoutModern();
    }

    /// <summary>Resizes the map area with the corner grip; the size is kept when the drag ends.</summary>
    private void ResizeModern(Point point)
    {
        double dx = point.X - _pressPoint.X, dy = point.Y - _pressPoint.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 3) return;
        _dragged = true;
        // The window's corner follows the pointer: its top-left stays put.
        ModernMapPosition ??= new(_modernWindow.X / _size.Width, _modernWindow.Y / _size.Height);
        ModernMapSize = new(Math.Max(1, _modernResizeOrigin.X + dx) / _size.Width, Math.Max(1, _modernResizeOrigin.Y + dy) / _size.Height);
        LayoutModern();
        ModernMapSize = new(_modernMapRect.Width / _size.Width, _modernMapRect.Height / _size.Height);
    }

    private void BeginModernFind()
    {
        if (ModernTextInputFactory is not { } create) return;
        _modernFind ??= CreateModernFind(create);
        _modernFind.IsVisible = true; _modernFind.Focus(); _modernFindOpen = true;
        Publish();
    }

    private EnbUiTextInput CreateModernFind(Func<int, Func<ushort, Task<bool>>, EnbUiTextInput> create)
    {
        EnbUiTextInput? input = null;
        input = create(40, key =>
        {
            if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return Task.FromResult(false);
            // Enter goes to the best match; Escape clears the box. Either way the keyboard returns to the game.
            if (key == EnbInventoryKeys.Enter && _modernMatches.Count > 0) GoToMatch(_modernMatches[0]);
            if (key == EnbInventoryKeys.Escape && input is not null) { input.Text = ""; _modernFindOpen = false; }
            if (input is not null) input.IsVisible = false;
            Publish(); return Task.FromResult(true);
        });
        input.Changed += (_, _) => { if (_modernFindResetting) return; _modernFindOpen = true; Publish(); };
        input.LostFocus += (_, _) => Publish();
        return input;
    }

    private void EndModernFind() { if (_modernFind is { IsFocused: true }) _modernFind.IsVisible = false; }

    /// <summary>Find results for the box's text: names that start with it first, then names that contain it.</summary>
    private void RefreshModernMatches()
    {
        _modernMatches.Clear();
        string text = (_modernFind?.Text ?? "").Trim();
        if (text.Length == 0 || _scene is null) return;
        var found = new List<(uint Id, bool System, string Name)>();
        if (_galaxy)
        {
            var galaxy = GalaxyMapAccessor?.Invoke();
            if (galaxy is null) return;
            foreach (var sector in EnbGalaxyMapPresentation.VisibleSectors(galaxy)) found.Add((sector.SectorHashId, false, ModernName(sector.Name)));
            foreach (var system in galaxy.Systems) found.Add((system.SystemHashId, true, ModernName(system.Name) + " (system)"));
        }
        else if (_scene.ActiveStarbaseId is null)
            foreach (var obj in _scene.Objects)
                if (MapAdmits(obj) && !obj.IsLocalShip && !_mapLabelUnknown.GetValueOrDefault(obj.RenderIdentity) &&
                    EnbCurrentRetailRadarCompatibilityAdapter.ResolveContactName(obj) is { Length: > 0 } name)
                    found.Add((obj.GameId, false, name));
        _modernMatches.AddRange(found.Where(f => f.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(found.Where(f => !f.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) && f.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(f => (f.Id, f.System)).Take(6));
    }

    /// <summary>Shows a find result without changing the zoom: a result already in view pulses; otherwise the map
    /// first centres on it (the sector map on the object, the galaxy map on the sector or system).</summary>
    private void GoToMatch((uint Id, bool System, string Name) match)
    {
        _modernFindOpen = false; EndModernFind();
        if (!_galaxy)
        {
            if (_scene?.Objects.FirstOrDefault(o => o.GameId == match.Id)?.Spatial?.Transform is not { } t) return;
            _modernFound = match.Id;
            if (ModernFoundPoint() is not { } shown || !ModernInView(shown)) _projection.CenterAt(t.Position);
        }
        else
        {
            _modernFoundSector = match.System ? null : match.Id;
            _modernPulseId = match.Id; _modernPulseSystem = match.System;
            if (ModernFoundPoint() is not { } shown || !ModernInView(shown))
            {
                if (ModernGalaxySchematic) CentreSchematicOn(match.Id, match.System, zoom: false);
                else if (match.System && _galaxySystemSpheres.TryGetValue(match.Id, out var sphere)) _galaxyCamera.Glide(new(sphere.X, sphere.Y, sphere.Z));
                else if (!match.System && _galaxySectors.TryGetValue(match.Id, out var position)) _galaxyCamera.Glide(position);
            }
        }
        _modernPulseStart = Environment.TickCount64;
    }

    /// <summary>Where the found item is on screen now (pre-layout logical pixels), if it is on the map.</summary>
    private Point? ModernFoundPoint()
    {
        if (!_galaxy)
            return _modernFound is uint id && _markers.TryGetValue(id, out var marker) ? Logical(marker.Bounds.Center) : null;
        if (Schematic)
        {
            Rect r = _modernPulseSystem ? _schematicSystems.FirstOrDefault(s => s.System == _modernPulseId).Block : _schematicSectors.FirstOrDefault(s => s.Sector == _modernPulseId).Box;
            return r.Width > 0 ? SchematicScreen(r).Center : null;
        }
        Vector3 at;
        if (_modernPulseSystem && _galaxySystemSpheres.TryGetValue(_modernPulseId, out var sphere)) at = new(sphere.X, sphere.Y, sphere.Z);
        else if (!_modernPulseSystem && _galaxySectors.TryGetValue(_modernPulseId, out var position)) at = position;
        else return null;
        var p = ProjectGalaxy(at);
        return float.IsFinite(p.X) && float.IsFinite(p.Y) ? Logical(new Point(p.X, p.Y)) : null;
    }

    /// <summary>Whether a point is well inside the map area (not at its very edge).</summary>
    private bool ModernInView(Point p)
    {
        double margin = 24 * ModernUnit; var r = _modernContentLogical;
        return p.X >= r.X + margin && p.X <= r.Right - margin && p.Y >= r.Y + margin && p.Y <= r.Bottom - margin;
    }

    /// <summary>The find pulse: three rings that grow and fade around the found item for a moment.</summary>
    private void AddModernPulse(ImmutableArray<EnbUiTextDraw>.Builder images)
    {
        if (_modernPulseStart == 0) return;
        double t = (Environment.TickCount64 - _modernPulseStart) / 1000.0;
        if (t > 2.2) { _modernPulseStart = 0; return; }
        if (ModernFoundPoint() is not { } at) return;
        double u = ModernUnit;
        for (int ring = 0; ring < 3; ring++)
        {
            double p = (t - ring * .4) / 1.4;
            if (p is < 0 or > 1) continue;
            int pixels = (int)Math.Round((14 + 58 * p) * u * _scaling / 4) * 4;
            double size = pixels / _scaling;
            images.Add(new(EnbModernIcons.Ring(pixels, Math.Max(1.5f, (float)(2 * u * _scaling))), new(Snap(at.X - size / 2), Snap(at.Y - size / 2), size, size)) { Opacity = (float)(1 - p) });
        }
    }

    /// <summary>The name of a galaxy sector, for the route list.</summary>
    private string SectorName(Net7.Protocol.Net7GalaxyMapData? galaxy, uint sector)
        => galaxy?.Sectors.FirstOrDefault(s => s.SectorHashId == sector)?.Name is { Length: > 0 } name ? ModernName(name) : "Unknown sector";

    /// <summary>A sector's name as its Net-7 Wiki page has it (null: no wiki, or the sector is unknown).</summary>
    private string? WikiSector(uint sector)
        => WikiRequested is not null && GalaxyMapAccessor?.Invoke()?.Sectors.FirstOrDefault(s => s.SectorHashId == sector)?.Name is { Length: > 0 } name ? ModernName(name) : null;

    /// <summary>Builds the Modern UI window: map area backing, side panel and their input areas.</summary>
    private void BuildModernChrome(EnbRetailMapCoordinates? coordinates)
    {
        _modernHits.Clear();
        if (!_modern || _assets is null || _state is null || _scene is null) { _modernChrome = []; _modernLegendMarkers = []; return; }
        // The find box belongs to the map it was typed in: switching between the sector and galaxy maps clears it.
        if (_modernFindGalaxy != _galaxy)
        {
            _modernFindGalaxy = _galaxy; _modernFindOpen = false; _modernFound = null; _modernFoundSector = null;
            if (_modernFind is not null) { _modernFindResetting = true; try { _modernFind.Text = ""; } finally { _modernFindResetting = false; } _modernFind.IsVisible = false; }
        }
        RefreshModernMatches();
        double u = ModernUnit, pad = Math.Round(10 * u), head = Math.Round(26 * u), text = 14 * u, small = 12 * u, title = 16 * u, row = Math.Round(22 * u), box = Math.Round(15 * u), line = Math.Round(18 * u);
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var under = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var legendMarkers = ImmutableArray.CreateBuilder<EnbMapMarker>();
        var panel = _modernPanel; var map = _modernMapRect;
        var galaxy = GalaxyMapAccessor?.Invoke();
        double left = panel.X + pad, width = panel.Width - pad * 2, cy = panel.Y + pad;
        double helpTop = panel.Bottom - pad - 2 * Math.Round(16 * u);

        // Title: the map's name and the close button. The current sector is named under the sector map's title.
        var close = new Rect(Snap(panel.Right - pad - head + 2 * u), Snap(cy), head - 2 * u, head - 2 * u);
        Text(_galaxy ? "Galaxy Map" : "Sector Map", new(left, cy, close.X - left - 4 * u, head - 2 * u), new(1, .85f, .3f), title);
        int closePixels = (int)Math.Round(close.Height * _scaling);
        images.Add(new(EnbModernIcons.CloseButton(closePixels, _hovered == "mmap/close"), new(close.X, close.Y, closePixels / _scaling, closePixels / _scaling)));
        _modernHits.Add(("mmap/close", close, "Close"));
        cy += head;
        if (!_galaxy && _scene.Environment?.SectorNum is uint current && galaxy is not null)
        { Text(SectorName(galaxy, current), new(left, cy, width, line), new(.75f, .82f, .95f), small); cy += line; }
        cy += 4 * u;

        // Find: a text box; matches show under it while there is text.
        var find = new Rect(left, Snap(cy), width, Math.Round(24 * u));
        bool editing = ModernFindEditing;
        string query = _modernFind?.Text ?? "";
        under.Add(new(EnbModernIcons.Fill(new(.02f, .03f, .08f), 210), find));
        Border(find, editing || _hovered == "mmap/find" ? new(.6f, .75f, 1) : new(.3f, .38f, .6f));
        string shown = editing ? query.Insert(Math.Clamp(_modernFind!.CaretIndex, 0, query.Length), "|") : query;
        Text(shown.Length > 0 ? shown : _galaxy ? "Find a sector or system" : "Find a nav, station, gate or planet", new(find.X + 6 * u, find.Y, find.Width - 12 * u, find.Height),
            shown.Length > 0 ? new(1, 1, 1) : new(.5f, .55f, .7f), text);
        _modernHits.Add(("mmap/find", find, "Type a name; Enter goes to the first match."));
        if (_modernFind is not null) _modernFind.Bounds = UiLayout?.Apply(EnbUiGroup.Map, find) ?? find;
        cy += find.Height + 2 * u;
        if (_modernFindOpen && query.Trim().Length > 0)
        {
            if (_modernMatches.Count == 0) { Text("No match", new(left + 6 * u, cy, width, row), new(.6f, .65f, .8f), small); cy += row; }
            for (int i = 0; i < _modernMatches.Count; i++)
            {
                var r = new Rect(left, Snap(cy), width, row);
                if (_hovered == "mmap/match/" + i) under.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 60), r));
                Text(_modernMatches[i].Name, new(r.X + 6 * u, r.Y, r.Width - 12 * u, r.Height), _hovered == "mmap/match/" + i ? new(1, 1, .3f) : new(.9f, .93f, 1), text);
                _modernHits.Add(("mmap/match/" + i, r, "")); cy += row;
            }
        }
        if (_galaxy && _modernFoundSector is uint foundSector && RoutePlannerEnabled && Guidance.Destination != foundSector)
        { cy += 2 * u; Button("mmap/plotfound", "Plot route to " + SectorName(galaxy, foundSector), "Plot a route to the sector you found."); }
        // The found sector's wiki page (stations, ore fields, mobs), without plotting a route to it.
        if (_galaxy && _modernFoundSector is uint wikiFound && WikiSector(wikiFound) is { } foundName)
        { cy += 2 * u; Button("mmap/wiki/" + wikiFound, foundName + " on the wiki", "Open the Net-7 Wiki page of " + foundName + "."); }
        cy += 8 * u;

        if (!_galaxy)
        {
            Header("Names");
            for (int i = 0; i < ModernKinds.Length && Room(row); i++)
            {
                var r = Option("mmap/names/" + i, ModernKinds[i].Label, (ModernMapNames & 1 << i) != 0, false, 26 * u);
                if (Glyph(ModernKinds[i].Glyph) is { } glyph)
                {
                    var at = ToDesign(new(r.X + box + 6 * u, r.Y, 18 * u, r.Height)).Center;
                    legendMarkers.Add(new(glyph, new((float)at.X, (float)at.Y), 0, new(.8f, .88f, 1)));
                }
                ModernTip("mmap/names/" + i, "Show the names of " + ModernKinds[i].Label.ToLowerInvariant() + " on the map.\nThe pointer, your target and a found object always show their name.");
            }
            cy += 6 * u;
            if (Room(row)) { Option("mmap/coords", "Show coordinates", ModernMapPointerCoordinates, false); ModernTip("mmap/coords", "Show the position under the pointer (or of the object there) beside the pointer."); }
        }
        else
        {
            Header("View");
            if (Room(row)) { Option("mmap/view/3d", "3D", !ModernGalaxySchematic, true); ModernTip("mmap/view/3d", "The galaxy in 3D: drag to turn it, wheel to zoom."); }
            if (Room(row)) { Option("mmap/view/schematic", "Schematic", ModernGalaxySchematic, true); ModernTip("mmap/view/schematic", "Every sector laid out flat with its name and its gate connections."); }
        }
        if (Room(row)) { Option("mmap/seethrough", "See-through", ModernMapSeeThrough, false); ModernTip("mmap/seethrough", "Show the game through the map, or give it a solid background."); }
        cy += 4 * u;
        if (_galaxy) Button("mmap/home", "Centre on home sector", "Centre the map on the sector you are in.");
        else if (NotesActive) Button("mmap/marker", _addingMarker ? "Click the map to place it" : "Add map marker", MarkerButtonTooltip, _addingMarker);
        // The sector you are in, on the wiki (the sector map; the galaxy map finds any sector).
        if (!_galaxy && _scene.Environment?.SectorNum is uint hereSector && WikiSector(hereSector) is { } hereName)
        { cy += 4 * u; Button("mmap/wiki/" + hereSector, hereName + " on the wiki", "Open the Net-7 Wiki page of " + hereName + "."); }

        // Route planner: the destination, the route and its notices, and the way to clear it (on the galaxy map also
        // while docked: a route starts from the station's sector).
        if (RoutePlannerEnabled && (_galaxy || _scene.ActiveStarbaseId is null))
        {
            cy += 8 * u;
            Header("Route");
            if (Guidance.Destination is not uint destination)
            {
                foreach (string wrapped in Wrap(_galaxy ? "Click a sector to plot a route to it." : "Plot a route to another sector on the galaxy map.", width, small))
                    if (Room(line)) { Text(wrapped, new(left, cy, width, line), new(.65f, .7f, .85f), small); cy += line; }
                if (!_galaxy) { cy += 4 * u; Button("mmap/togalaxy", "Open the galaxy map", "Switch to the galaxy map to plot a route."); }
            }
            else
            {
                if (Guidance.Status.Length > 0)
                    foreach (string wrapped in Wrap(Guidance.Status, width, small))
                        if (Room(line)) { Text(wrapped, new(left, cy, width, line), new(1, .85f, .3f), small); cy += line; }
                var route = Guidance.Route;
                double clearSpace = Math.Round(24 * u) + 6 * u, notice = Guidance.RestrictionNotice.Length > 0 ? line * 2 : 0;
                // The route's sectors, numbered from here; when they do not all fit, the last line says how many follow.
                int rows = Math.Max(0, (int)((helpTop - clearSpace - notice - cy) / line));
                int listed = route.Count <= rows ? route.Count : Math.Max(0, rows - 1);
                for (int i = 0; i < listed; i++)
                {
                    bool here = i == 0, next = i == 1;
                    EnbRgb color = here ? new(.4f, 1, .45f) : next ? new(1, .85f, .3f) : new(.8f, .85f, .95f);
                    Text(i == 0 ? "-" : i.ToString(System.Globalization.CultureInfo.InvariantCulture), new(left, cy, 14 * u, line), color, small);
                    Text(SectorName(galaxy, route[i]) + (here ? " (here)" : Guidance.WormholeInto(route[i]) is not null ? " (wormhole)" : ""), new(left + 18 * u, cy, width - 18 * u, line), here || next ? color : new(.8f, .85f, .95f), small);
                    cy += line;
                }
                if (listed < route.Count && rows > 0)
                { Text("... " + (route.Count - listed) + " more to " + SectorName(galaxy, route[^1]), new(left + 18 * u, cy, width - 18 * u, line), new(.65f, .7f, .85f), small); cy += line; }
                if (Guidance.RestrictionNotice.Length > 0)
                    foreach (string wrapped in Wrap(Guidance.RestrictionNotice, width, small))
                        if (Room(line)) { Text(wrapped, new(left, cy, width, line), new(1, .55f, .3f), small); cy += line; }
                cy += 4 * u;
                Button("mmap/clearroute", "Clear route", "Stop guiding to " + SectorName(galaxy, destination) + ".");
                if (_galaxy && WikiSector(destination) is { } destinationName)
                { cy += 4 * u; Button("mmap/wiki/" + destination, destinationName + " on the wiki", "Open the Net-7 Wiki page of " + destinationName + "."); }
            }
        }

        // How to use it, at the bottom of the panel (the retail help button is gone).
        string[] help = _galaxy
            ? ModernGalaxySchematic ? ["Drag to move, wheel to zoom.", "Click a sector to plot a route."] : ["Drag to turn, wheel to zoom.", "Click a sector to plot a route."]
            : ["Drag to move, wheel to zoom.", "Click to target, right-click to centre."];
        for (int i = 0; i < help.Length; i++)
            Text(help[i], new(left, panel.Bottom - pad - (help.Length - i) * Math.Round(16 * u), width, Math.Round(16 * u)), new(.6f, .66f, .82f), 11 * u);

        // The resize grip in the window's corner.
        double gripSize = Math.Round(16 * u);
        var grip = new Rect(Snap(panel.Right - gripSize - 2 * u), Snap(panel.Bottom - gripSize - 2 * u), gripSize, gripSize);
        int gripPixels = (int)Math.Round(gripSize * _scaling);
        images.Add(new(EnbModernIcons.Grip(gripPixels, _hovered == "mmap/resize"), new(grip.X, grip.Y, gripPixels / _scaling, gripPixels / _scaling)));
        _modernHits.Add(("mmap/resize", grip, "Drag to resize the map."));

        // The map area: see-through (a dark tint with a thin border, like the retail map) or a solid panel.
        var chrome = ImmutableArray.CreateBuilder<EnbUiModelDraw>();
        var backing = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        if (ModernMapSeeThrough)
        {
            backing.Add(new(EnbModernIcons.Fill(new(.01f, .02f, .06f), 150), map));
            double edge = 1 / _scaling;
            var rim = EnbModernIcons.Fill(new(.45f, .58f, .9f), 200);
            backing.Add(new(rim, new(map.X, map.Y, map.Width, edge))); backing.Add(new(rim, new(map.X, map.Bottom - edge, map.Width, edge)));
            backing.Add(new(rim, new(map.X, map.Y, edge, map.Height)));
        }
        else if (Frame(1, map) is { } solid) chrome.Add(solid);
        if (Frame(0, panel) is { } panelDraw) chrome.Add(panelDraw);
        if (chrome.Count == 0) return;
        chrome[^1] = chrome[^1] with { Images = backing.ToImmutable().AddRange(under).AddRange(images) };
        _modernChrome = chrome.ToImmutable();
        _modernLegendMarkers = legendMarkers.ToImmutable();

        bool Room(double h) => cy + h <= helpTop;
        void Header(string label)
        {
            if (!Room(row)) return;
            Text(label, new(left, cy, width, row), new(1, .85f, .3f), text); cy += row;
        }
        Rect Option(string id, string label, bool on, bool round, double indent = 0)
        {
            var r = new Rect(left, Snap(cy), width, row);
            bool hot = _hovered == id;
            int pixels = (int)Math.Round(box * _scaling);
            images.Add(new(EnbModernIcons.Check(pixels, on, hot, round), new(r.X, Snap(r.Center.Y - box / 2), pixels / _scaling, pixels / _scaling)));
            Text(label, new(r.X + box + 8 * u + indent, r.Y, r.Width - box - 8 * u - indent, r.Height), hot ? new(1, 1, .3f) : on ? new(1, 1, 1) : new(.7f, .74f, .85f), text);
            _modernHits.Add((id, r, "")); cy += row;
            return r;
        }
        void Button(string id, string label, string tip, bool active = false)
        {
            if (!Room(Math.Round(24 * u))) return;
            var button = new Rect(left, Snap(cy), width, Math.Round(24 * u));
            bool hot = _hovered == id;
            images.Add(new(EnbModernIcons.Button((int)Math.Round(button.Width * _scaling), (int)Math.Round(button.Height * _scaling), hot || active), button));
            Text(label, button, hot || active ? new(1, 1, .3f) : new(1, 1, 1), text, center: true);
            _modernHits.Add((id, button, tip)); cy += button.Height;
        }
        void Border(Rect r, EnbRgb color)
        {
            double edge = 1 / _scaling; var rim = EnbModernIcons.Fill(color, 230);
            images.Add(new(rim, new(r.X, r.Y, r.Width, edge))); images.Add(new(rim, new(r.X, r.Bottom - edge, r.Width, edge)));
            images.Add(new(rim, new(r.X, r.Y, edge, r.Height))); images.Add(new(rim, new(r.Right - edge, r.Y, edge, r.Height)));
        }
        void Text(string value, Rect area, EnbRgb color, double size, bool center = false)
        {
            if (ModernTextImage(value, area, color, size, center) is { } image) images.Add(image);
        }
        EnbMapGlyph? Glyph(string name)
        {
            if (_missingGlyphs.Contains(name)) return null;
            if (_glyphs.TryGetValue(name, out var glyph)) return glyph;
            try { return _glyphs[name] = EnbMapGlyph.Load(_assets!, name); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { _missingGlyphs.Add(name); return null; }
        }
        EnbUiModelDraw? Frame(int index, Rect rect)
        {
            var art = _modernFrames.FirstOrDefault(f => f.Index == index && f.Width == rect.Width && f.Height == rect.Height).Asset;
            if (art is null)
            {
                var source = EnbRenderSession.Of(_assets!).Confirmations.Frames[index];
                art = source.Asset with { Geometry = source.Resize.Geometry((float)(rect.Width / u), (float)(rect.Height / u)) };
                if (_modernFrames.Count >= 16) _modernFrames.RemoveAt(0);
                _modernFrames.Add((index, rect.Width, rect.Height, art));
            }
            var min = art.Geometry!.Positions.Aggregate(Vector3.Min); var max = art.Geometry.Positions.Aggregate(Vector3.Max);
            return new(art, 0, rect, false) { LayoutGroup = EnbUiGroup.Map, DesignBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y) };
        }
    }

    /// <summary>One line of Modern UI text in an area (shortened with an ellipsis rather than squeezed).</summary>
    private EnbUiTextDraw? ModernTextImage(string value, Rect area, EnbRgb color, double size, bool center = false)
    {
        if (value.Length == 0 || EnbModernText.Renderer is not { } m) return null;
        float px = (float)(size * _scaling);
        if (m.Measure(value, px) / _scaling > area.Width)
        {
            while (value.Length > 1 && m.Measure(value + "…", px) / _scaling > area.Width) value = value[..^1];
            value = value.TrimEnd() + "…";
        }
        int cached = _modernText.FindIndex(t => t.Pixels == px && t.Color == color && t.Text == value);
        EnbUiRaster image;
        if (cached >= 0) image = _modernText[cached].Image;
        else
        {
            if (m.Render(value, px, color) is not { } rendered) return null;
            if (_modernText.Count >= 384) _modernText.RemoveRange(0, 96);
            _modernText.Add((value, px, color, image = rendered));
        }
        double padding = m.Padding(px) / _scaling, iw = image.Width / _scaling, ih = image.Height / _scaling;
        double x = center ? area.X + (area.Width - (iw - 2 * padding)) / 2 : area.X;
        double top = area.Center.Y - ih / 2 - m.InkCenterOffset("H", px) / _scaling;
        return new(image, new(Snap(x - padding), Snap(top), iw, ih));
    }

    private double ModernMeasure(string value, double size)
        => EnbModernText.Renderer is { } m ? m.Measure(value, (float)(size * _scaling)) / _scaling : value.Length * size * .55;

    private List<string> Wrap(string value, double width, double size)
    {
        var lines = new List<string>();
        foreach (string paragraph in value.Replace("\r", "").Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && ModernMeasure(next, size) > width) { lines.Add(current); current = word; }
                else current = next;
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>The position beside the pointer, while it is over the sector map and the choice is on.</summary>
    private ImmutableArray<EnbUiTextDraw> ModernPointerCoordinates(EnbRetailMapCoordinates? coordinates)
    {
        if (!ModernMapPointerCoordinates || _galaxy || coordinates is not { } c || !_modernContentLogical.Contains(ModernPointer) || _gesture) return [];
        double u = ModernUnit;
        string value = c.X.Trim() + "   " + c.Y.Trim() + "   " + c.Z.Trim();
        double width = ModernMeasure(value, 12 * u) + 12 * u, height = Math.Round(20 * u);
        var p = ModernPointer;
        double x = p.X + 16 * u, y = p.Y + 18 * u;
        if (x + width > _modernContentLogical.Right) x = p.X - 10 * u - width;
        if (y + height > _modernContentLogical.Bottom) y = p.Y - 10 * u - height;
        var back = new Rect(Snap(x), Snap(y), Math.Ceiling(width), height);
        var text = ModernTextImage(value, new(back.X + 6 * u, back.Y, back.Width - 6 * u, back.Height), c.Color, 12 * u);
        return text is null ? [new(EnbModernIcons.Fill(new(0, 0, 0), 170), back)] : [new(EnbModernIcons.Fill(new(0, 0, 0), 170), back), text];
    }

    /// <summary>The retail coordinate readout in its pointer mode (object under the pointer, else the pointer's place).</summary>
    private EnbRetailMapCoordinates? ResolveModernCoordinates()
    {
        if (_state is null) return null;
        int kept = _state.CoordinateMode;
        _state.SetCoordinateMode(2);
        try { return ResolveCoordinates(); }
        finally { _state.SetCoordinateMode(kept); }
    }

    /// <summary>Hands the Modern UI window to the presentation in place of the retail frame.</summary>
    private void ApplyModern(EnbRetailMapCoordinates? coordinates)
    {
        if (!_modern || Presentation is not { } presentation) return;
        BuildModernChrome(coordinates);
        var content = presentation.ContentImages.ToBuilder();
        AddModernPulse(content);
        content.AddRange(ModernPointerCoordinates(coordinates));
        Presentation = presentation with { Packets = [], Chrome = _modernChrome, LegendMarkers = _modernLegendMarkers,
            ContentImages = content.ToImmutable(), Order = Raised };
    }
}
