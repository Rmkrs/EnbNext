using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Local window (Eve style): every object the radar can target around the ship, with
/// a search box, a kind filter (several kinds at once), and sorting by name or distance. Its rows are the radar's contacts
/// (EnbCurrentRetailRadarCompatibilityAdapter: retail eligibility 006F5980 and range 007BE3A0, the retail display
/// name 006FF7A0 and the surface distance 00831E90), coloured as the radar colours them. A click on a row is the
/// ordinary target request (0017, as a click on the map). The window itself is a Next feature; retail has no Local
/// list (research/retail-traces/03-local-list.md). Inside a station it lists the room instead (EnbStationRosterEntry:
/// its players, characters, vendors and terminals), with filters of its own.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernLocal, _localFilterOpen, _localByName, _localDescending;
    /// <summary>The kinds shown (bit n for LocalFilters[n], 1 to 6); none set shows everything.</summary>
    private int _localKinds, _localScroll, _mlocalZ;
    private EnbUiTextInput? _localSearch;
    private Rect _modernLocalBounds;
    private EnbRadarFrame? _localFrame;
    private long _localPublished;
    private readonly List<EnbRadarContact> _localRows = [];
    private static readonly string[] LocalFilters = ["Everything", "Ships", "Players", "Resources", "Corpses", "Navigation", "Other"];
    private IReadOnlyList<EnbStationRosterEntry>? _localStation;
    /// <summary>The kinds shown inside a station (bit n for LocalStationFilters[n]); kept apart from the space choice.</summary>
    private int _localStationKinds;
    private readonly List<EnbStationRosterEntry> _localStationRows = [];
    private static readonly string[] LocalStationFilters = ["Everything", "Players", "Characters", "Vendors", "Terminals"];
    private bool LocalInStation => _localStation is not null;
    private string[] LocalKindNames => LocalInStation ? LocalStationFilters : LocalFilters;
    private ref int LocalKinds => ref (LocalInStation ? ref _localStationKinds : ref _localKinds);
    /// <summary>Inside a station: the room's roster (null in space); the window lists it instead of the radar's contacts.</summary>
    internal IReadOnlyList<EnbStationRosterEntry>? ModernLocalStation
    {
        get => _localStation;
        set
        {
            if (ReferenceEquals(_localStation, value)) return;
            bool modeChanged = (_localStation is null) != (value is null);
            _localStation = value;
            if (modeChanged) { _localScroll = 0; _localFilterOpen = false; }
            // The station map belongs to the station.
            if (value is null) _modernStationMap = false;
            if (_modernLocal || _modernStationMap) Publish();
        }
    }
    /// <summary>The room rows shown inside a station, in their order.</summary>
    internal IReadOnlyList<EnbStationRosterEntry> ModernLocalStationRows => _localStationRows;

    /// <summary>The radar's contacts this frame; an open window refreshes its distances a few times a second.</summary>
    internal EnbRadarFrame? ModernLocalFrame
    {
        get => _localFrame;
        set
        {
            _localFrame = value;
            if (!_modernLocal) return;
            long now = Environment.TickCount64;
            if (now - _localPublished >= 250) { _localPublished = now; Publish(); }
        }
    }
    /// <summary>A contact's radar colour (its gutter category's normal colour).</summary>
    internal Func<EnbRadarContact, EnbRgb>? ModernLocalColor { get; set; }
    /// <summary>Selects a contact as the target (the ordinary target request).</summary>
    internal Func<uint, Task>? ModernLocalTargetRequested { get; set; }
    /// <summary>A station row was clicked: walk there (the row's key and room).</summary>
    internal Action<string, uint>? ModernLocalWalkRequested { get; set; }
    /// <summary>An action on a row that needs the target's server verbs: "loot" (a corpse) or "approach" (Follow).</summary>
    internal Func<uint, string, Task>? ModernLocalAction { get; set; }
    /// <summary>What a row waiting on such an action says (for example the Loot button's "Too far").</summary>
    internal Func<uint, string?>? ModernLocalStatus { get; set; }
    /// <summary>Whether a corpse was opened before (its credits are taken on the first opening; Next keeps track).</summary>
    internal Func<uint, bool>? ModernLocalOpened { get; set; }
    /// <summary>The opened corpse, if any.</summary>
    internal uint? LootHulkId => Data?.HulkId;
    /// <summary>Space and a station each keep their own place and size: the station list has rooms instead of the
    /// space columns, so it neither needs the space window's width nor belongs in its spot.</summary>
    internal EnbUiPoint? ModernLocalSpacePosition { get; set; }
    internal EnbUiPoint? ModernLocalStationPosition { get; set; }
    internal EnbUiPoint? ModernLocalSpaceSize { get; set; }
    internal EnbUiPoint? ModernLocalStationSize { get; set; }
    /// <summary>Whether the window lists a station's rooms (and so uses the station place and size).</summary>
    internal bool ModernLocalInStation => LocalInStation;
    /// <summary>The place of the window as it is now (space or station), as a fraction of the screen.</summary>
    internal EnbUiPoint? ModernLocalPosition
    {
        get => LocalInStation ? ModernLocalStationPosition : ModernLocalSpacePosition;
        set { if (LocalInStation) ModernLocalStationPosition = value; else ModernLocalSpacePosition = value; }
    }
    internal Action<EnbUiPoint>? ModernLocalMoved { get; set; }
    /// <summary>The size the player gave the window with its grip (space or station, as it is now): X its width in
    /// Modern UI units, Y its visible rows.</summary>
    internal EnbUiPoint? ModernLocalSize
    {
        get => LocalInStation ? ModernLocalStationSize : ModernLocalSpaceSize;
        set { if (LocalInStation) ModernLocalStationSize = value; else ModernLocalSpaceSize = value; }
    }
    internal Action<EnbUiPoint>? ModernLocalResized { get; set; }
    private EnbUiPoint _localResizeStart, _localResizeOrigin;
    private const double LocalDefaultWidth = 360, LocalMinimumWidth = 280, LocalMaximumWidth = 900;
    private const int LocalDefaultRows = 14, LocalMinimumRows = 5, LocalMaximumRows = 40;
    private (double Width, int Rows) LocalSize() => (Math.Clamp(ModernLocalSize?.X ?? LocalDefaultWidth, LocalMinimumWidth, LocalMaximumWidth),
        Math.Clamp((int)Math.Round(ModernLocalSize?.Y ?? LocalDefaultRows), LocalMinimumRows, LocalMaximumRows));
    /// <summary>A press on the grip: the size and point it started from.</summary>
    private void BeginLocalResize(EnbUiPoint point)
    {
        var (w, rows) = LocalSize(); _localResizeStart = point; _localResizeOrigin = new(w, rows);
        // The top left corner stays where it is while the grip moves (the default place keeps the right edge instead).
        if (ModernLocalPosition is null && _size.Width > 0 && _size.Height > 0)
        {
            ModernLocalPosition = new(_modernLocalBounds.X / _size.Width, _modernLocalBounds.Y / _size.Height);
            ModernLocalMoved?.Invoke(ModernLocalPosition.Value);
        }
    }
    /// <summary>Dragging the grip: the width follows the pointer, the height in whole rows.</summary>
    private void ResizeModernLocal(EnbUiPoint point)
    {
        double u = ModernUnit, rowHeight = Math.Round(22 * u);
        double width = _localResizeOrigin.X + (point.X - _localResizeStart.X) / u;
        int rows = (int)Math.Round(_localResizeOrigin.Y + (point.Y - _localResizeStart.Y) / rowHeight);
        // Never larger than the screen from where the window stands.
        double maxWidth = (_size.Width - _modernLocalBounds.X) / u - 20, maxRows = (_size.Height - _modernLocalBounds.Y - (_modernLocalBounds.Height - LocalSize().Rows * rowHeight)) / rowHeight;
        ModernLocalSize = new(Math.Clamp(width, LocalMinimumWidth, Math.Max(LocalMinimumWidth, Math.Min(LocalMaximumWidth, maxWidth))),
            Math.Clamp(rows, LocalMinimumRows, Math.Max(LocalMinimumRows, Math.Min(LocalMaximumRows, (int)maxRows))));
    }
    public bool ModernLocalOpen => _modernLocal;
    /// <summary>Next option: Escape does not close the Local window (it stays on screen until its close button).</summary>
    internal bool ModernLocalKeptOnEscape { get; set; }
    /// <summary>How solid the window's background is (1 = the Modern UI frame; less lets the world show through).</summary>
    internal double ModernLocalOpacity { get; set; } = 1;
    internal Action<double>? ModernLocalOpacityChanged { get; set; }
    private static readonly double[] LocalOpacities = [1, .8, .6, .4, .2];
    private bool LocalSeeThrough => ModernLocalOpacity < .999;
    /// <summary>Next columns shown after Distance (bit n for LocalColumns[n]); kept between sessions.</summary>
    internal int ModernLocalColumns { get; set; }
    internal Action<int>? ModernLocalColumnsChanged { get; set; }
    /// <summary>The columns that need the character's own combat record (all but Speed).</summary>
    internal const int LocalDamageColumns = 0b11110;
    /// <summary>This character's own hits per object (never group or multibox numbers), for the damage columns.</summary>
    internal EnbLocalCombatLedger? ModernLocalCombat { get; set; }
    /// <summary>The Next column the list is sorted by, or -1 for Name or Distance.</summary>
    private int _localSortColumn = -1;
    private static readonly (string Heading, string Menu, string Tip)[] LocalColumns =
    [
        ("Speed", "speed", "How fast it is moving, as your own speed gauge shows speed"),
        ("Your DPS", "your damage per second", "Damage per second you deal it, over all your time fighting it; shown while you fight it (a fight ends 30 seconds after the last hit while it is no longer attacking you; EMP not counted)"),
        ("Their DPS", "their damage per second", "Damage per second it deals you, over all your time fighting it; shown while you fight it (a fight ends 30 seconds after the last hit while it is no longer attacking you; EMP not counted)"),
        ("Your damage", "your total damage", "All the damage you have dealt it while it has been in Local (EMP not counted)"),
        ("Their damage", "their total damage", "All the damage it has dealt you while it has been in Local (EMP not counted)"),
    ];
    private bool LocalColumnShown(int column) => (ModernLocalColumns & (1 << column)) != 0;
    /// <summary>A Next column's value for a contact (sort value, and its text; empty while there is nothing to show).</summary>
    private (double Value, string Text) LocalColumnValue(EnbRadarContact contact, int column, long now)
    {
        if (column == 0)
            return contact.Speed is float speed ? (speed, ((int)Math.Round(speed)).ToString(CultureInfo.InvariantCulture)) : (-1, "");
        if (ModernLocalCombat?.TryRead(contact.GameId, now, out var combat) != true) return (-1, "");
        double value = column switch { 1 => combat.DealtPerSecond, 2 => combat.TakenPerSecond, 3 => combat.Dealt, _ => combat.Taken };
        // A low rate keeps one decimal (0.5, not 0); totals and larger rates read as the damage meter shows them.
        if (value <= 0) return (0, "");
        return (value, column is 1 or 2 && value < 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : EnbDamageMeterPanel.Number(value, value));
    }
    /// <summary>The window's right-click menu (anywhere but a row): how see-through its background is.</summary>
    private void LocalWindowMenu()
    {
        int level = Array.FindIndex(LocalOpacities, o => o <= ModernLocalOpacity + 1e-6);
        if (level < 0) level = LocalOpacities.Length - 1;
        void Set(int to) { ModernLocalOpacity = LocalOpacities[Math.Clamp(to, 0, LocalOpacities.Length - 1)]; ModernLocalOpacityChanged?.Invoke(ModernLocalOpacity); Publish(); }
        ModernMenuRequested?.Invoke("mlocal/window", "Local",
        [
            EnbModernMenuItem.Heading("Background " + Math.Round(LocalOpacities[level] * 100).ToString(CultureInfo.InvariantCulture) + "%"),
            new("More see-through", () => Set(level + 1), Enabled: level < LocalOpacities.Length - 1),
            new("Less see-through", () => Set(level - 1), Enabled: level > 0),
            new("Solid", () => Set(0), Enabled: level != 0),
            // The columns are the space list's; a station lists rooms.
            ..(LocalInStation ? [] : (IEnumerable<EnbModernMenuItem>)[EnbModernMenuItem.Heading("Columns"),
                ..LocalColumns.Select((c, n) => new EnbModernMenuItem((LocalColumnShown(n) ? "Hide " : "Show ") + c.Menu, () => ToggleLocalColumn(n)))]),
        ], _point);
    }
    private void ToggleLocalColumn(int column)
    {
        ModernLocalColumns ^= 1 << column;
        if (!LocalColumnShown(column) && _localSortColumn == column) { _localSortColumn = -1; _localByName = false; _localDescending = false; }
        ModernLocalColumnsChanged?.Invoke(ModernLocalColumns);
        Publish();
    }
    public bool ModernLocalSearchEditing => _localSearch is { IsVisible: true, IsFocused: true };
    internal EnbUiTextInput? ModernLocalSearchInput => _localSearch;
    /// <summary>The rows shown, in their order (after search, filter and sort).</summary>
    internal IReadOnlyList<EnbRadarContact> ModernLocalRows => _localRows;

    /// <summary>The player's own choice to have the window open (kept between sessions): it comes back after a jump,
    /// a dock or a restart, and only the player's closing (its button, the micro menu or Escape) clears it.</summary>
    internal bool ModernLocalWanted { get; set; }
    internal Action<bool>? ModernLocalWantedChanged { get; set; }
    private void WantModernLocal(bool wanted)
    {
        if (ModernLocalWanted == wanted) return;
        ModernLocalWanted = wanted; ModernLocalWantedChanged?.Invoke(wanted);
    }
    /// <summary>The player has hidden the whole interface: a wanted window waits until it is shown again (its reopening
    /// is not the player opening a window, which brings a hidden interface back).</summary>
    internal bool WorldInterfaceHidden { get; set; }
    /// <summary>Each update: a wanted window reopens (in space or a station) once the interface takes input again.</summary>
    private void RestoreModernLocal(IEnbSceneFrame scene)
    {
        if (ModernLocalWanted && !_modernLocal && _interactive && ModernUi && !WorldInterfaceHidden) _modernLocal = true;
    }

    public bool ToggleModernLocal()
    {
        if (!_interactive || !ModernUi) return false;
        _modernLocal = !_modernLocal;
        WantModernLocal(_modernLocal);
        _localFilterOpen = false; _localScroll = 0;
        if (_localSearch is not null) _localSearch.IsVisible = false;
        if (_modernLocal) { RaiseModern("mlocal"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }
    private void CloseModernLocal()
    {
        _modernLocal = _localFilterOpen = false;
        if (_localSearch is not null) _localSearch.IsVisible = false;
    }

    /// <summary>The filter a contact falls under (1 to 6; 0 is everything).</summary>
    private static int LocalCategory(EnbRadarContact contact) => contact.IsNavigation ? 5 : contact.ObjectType switch
    {
        0 or 2 or 0x23 => 1,
        1 => 2,
        0x22 or 0x26 => 3,
        0x19 => 4,
        3 or 0x0B or 0x0C or 0x18 => 5,
        _ => 6,
    };
    /// <summary>The filter button's label: Everything, the one kind chosen, or how many.</summary>
    private string LocalKindsLabel()
    {
        int kinds = LocalKinds;
        if (kinds == 0) return LocalKindNames[0];
        int count = System.Numerics.BitOperations.PopCount((uint)kinds);
        return count == 1 ? LocalKindNames[System.Numerics.BitOperations.TrailingZeroCount(kinds)] : count.ToString(CultureInfo.InvariantCulture) + " kinds";
    }
    private static string LocalName(EnbRadarContact contact) => string.IsNullOrWhiteSpace(contact.Name) ? "Unknown" : contact.Name!;

    private void ModernLocalWindow()
    {
        var contacts = LocalInStation ? [] : _localFrame?.Contacts ?? [];
        string query = (_localSearch?.Text ?? "").Trim();
        _localRows.Clear();
        _localStationRows.Clear();
        if (_localStation is { } room)
        {
            foreach (var entry in room)
            {
                if (_localStationKinds != 0 && (_localStationKinds & (1 << (int)entry.Kind)) == 0) continue;
                if (query.Length > 0 && !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                _localStationRows.Add(entry);
            }
            // By name, or by room: this room first, then the other rooms room by room, each by name.
            static int ByRoom(EnbStationRosterEntry a, EnbStationRosterEntry b)
            {
                int c = b.InRoom.CompareTo(a.InRoom);
                return c != 0 ? c : a.Room.CompareTo(b.Room);
            }
            Comparison<EnbStationRosterEntry> roomOrder = _localByName
                ? (a, b) => { int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : ByRoom(a, b); }
                : (a, b) => { int c = ByRoom(a, b); return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); };
            _localStationRows.Sort(_localDescending ? (a, b) => roomOrder(b, a) : roomOrder);
        }
        foreach (var contact in contacts)
        {
            if (contact.IsLocalShip) continue;
            if (_localKinds != 0 && (_localKinds & (1 << LocalCategory(contact))) == 0) continue;
            // Corpses are local only within scan range (beyond it nothing can be done with them), unless targeted or waiting on an action.
            if (contact.ObjectType == 0x19 && _localFrame?.ScanRange is int range && contact.SurfaceDistance > range && !contact.IsCurrentTarget
                && ModernLocalStatus?.Invoke(contact.GameId) is null) continue;
            if (query.Length > 0 && !LocalName(contact).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            _localRows.Add(contact);
        }
        // The damage columns forget objects that have left Local.
        long localNow = Environment.TickCount64;
        if (ModernLocalCombat is { Count: > 0 } ledger && !LocalInStation)
        {
            ledger.Retain(id => { foreach (var c in contacts) if (c.GameId == id) return true; return false; });
            // A contact the server lists as attacking us is still fighting us between its hits.
            foreach (var c in contacts) if (c.IsAttacking) ledger.MarkAttacking(c.GameId, localNow);
        }
        // Name: alphabetical, nearest first among equal names. Distance: nearest first, then by name.
        // A Next column: largest first (a second click: smallest first), then nearest first.
        int sortColumn = _localSortColumn >= 0 && LocalColumnShown(_localSortColumn) ? _localSortColumn : -1;
        Comparison<EnbRadarContact> order = sortColumn >= 0
            ? (a, b) => { int c = LocalColumnValue(b, sortColumn, localNow).Value.CompareTo(LocalColumnValue(a, sortColumn, localNow).Value); return c != 0 ? c : a.SurfaceDistance.CompareTo(b.SurfaceDistance); }
            : _localByName
            ? (a, b) => { int c = string.Compare(LocalName(a), LocalName(b), StringComparison.OrdinalIgnoreCase); return c != 0 ? c : a.SurfaceDistance.CompareTo(b.SurfaceDistance); }
            : (a, b) => { int c = a.SurfaceDistance.CompareTo(b.SurfaceDistance); return c != 0 ? c : string.Compare(LocalName(a), LocalName(b), StringComparison.OrdinalIgnoreCase); };
        _localRows.Sort(_localDescending ? (a, b) => order(b, a) : order);

        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u;
        double rowHeight = Math.Round(22 * u), distanceWidth = Math.Round(Math.Max(ModernMeasure("999.99k", text), ModernMeasure("Distance", text)) + 20 * u);
        // In a station the column also names other rooms, in full.
        if (LocalInStation) distanceWidth = Math.Max(distanceWidth, Math.Round(ModernMeasure("Main Deck", text) + 20 * u));
        double filterWidth = 0;
        foreach (string f in LocalKindNames) filterWidth = Math.Max(filterWidth, ModernMeasure(f, text));
        filterWidth = Math.Max(filterWidth, ModernMeasure("6 kinds", text));
        filterWidth = Math.Round(filterWidth + 24 * u);
        var (sizeWidth, visible) = LocalSize();
        // Next columns widen the window instead of narrowing the names. A station has no such columns (and its own size).
        Span<double> columnWidths = stackalloc double[LocalColumns.Length];
        double columnsWidth = 0;
        for (int n = 0; n < LocalColumns.Length; n++)
            if (LocalColumnShown(n)) columnsWidth += columnWidths[n] = Math.Round(Math.Max(ModernMeasure("9999.9K", text), ModernMeasure(LocalColumns[n].Heading, text)) + 16 * u);
        double contentWidth = Math.Round(sizeWidth * u) + (LocalInStation ? 0 : columnsWidth);
        double chrome = pad * 2 + head + gap + head + gap + head + gap;
        double width = pad * 2 + contentWidth, height = chrome + visible * rowHeight;
        double x, y;
        if (ModernLocalPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = _size.Width - width - 12 * u; y = _size.Height * .18; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernLocalBounds = new(x, y, width, height);
        if (!LocalSeeThrough) ModernFrame("DIALOG_BOX_WINDOW", _modernLocalBounds, "mlocal/frame");
        else
        {
            // See-through: a plain background in the frame's blue at the chosen strength, with a faint edge.
            Flush();
            // The window's own image-only draw carries the background (images ride on the latest draw, and there may be none yet).
            ModernFrame("DIALOG_BOX_WINDOW", _modernLocalBounds, "mlocal/frame", imagesOnly: true);
            _images.Add(new(EnbModernIcons.Fill(new(.03f, .05f, .14f), (byte)Math.Round(ModernLocalOpacity * 230)), Inset(_modernLocalBounds, 1)));
            // The edge stays visible at every strength, so the window (and its grip) can be found over open space.
            var edge = EnbModernIcons.Fill(new(.36f, .45f, .7f), (byte)Math.Round(80 + ModernLocalOpacity * 60));
            var b = _modernLocalBounds;
            _images.Add(new(edge, new(b.X, b.Y, b.Width, 1))); _images.Add(new(edge, new(b.X, b.Bottom - 1, b.Width, 1)));
            _images.Add(new(edge, new(b.X, b.Y + 1, 1, b.Height - 2))); _images.Add(new(edge, new(b.Right - 1, b.Y + 1, 1, b.Height - 2)));
        }
        _tips.Remove("mlocal/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mlocal/title", "Local", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        int total = LocalInStation ? _localStation!.Count : _localKinds == 0 && query.Length == 0 ? _localRows.Count : contacts.Count(c => !c.IsLocalShip && !(c.ObjectType == 0x19 && _localFrame?.ScanRange is int far && c.SurfaceDistance > far && !c.IsCurrentTarget));
        int shownCount = LocalInStation ? _localStationRows.Count : _localRows.Count;
        ModernText("mlocal/count", shownCount >= total ? shownCount + (LocalInStation ? " here" : " nearby") : shownCount + " of " + total,
            new(cx, cy, contentWidth - head - gap * 2, head), new(.65f, .7f, .85f), small, 2);
        // A see-through window lets its controls through too (never fainter than a third, so they can still be found).
        float controls = (float)Math.Max(ModernLocalOpacity, .35);
        ModernCloseButton("mlocal/close", new(x + width - pad - head, cy, head, head), controls);
        cy += head + gap;

        // Search and the kind filter.
        double searchWidth = contentWidth - filterWidth - gap;
        var searchRect = new Rect(cx, cy, searchWidth, head);
        if (!LocalSeeThrough) ModernFrame("DIALOG_BOX_INFO", searchRect, "mlocal/search");
        else
        {
            Flush(); ModernFrame("DIALOG_BOX_INFO", searchRect, "mlocal/search", imagesOnly: true);
            _images.Add(new(EnbModernIcons.Fill(new(.08f, .12f, .3f), (byte)Math.Round(controls * 200)), searchRect));
            _images.Add(new(EnbModernIcons.Fill(new(.36f, .45f, .7f), (byte)Math.Round(controls * 160)), new(searchRect.X, searchRect.Bottom - 1, searchRect.Width, 1)));
        }
        bool editing = ModernLocalSearchEditing;
        string search = _localSearch?.Text ?? "";
        var searchText = new Rect(cx + 8 * u, cy, searchWidth - 16 * u, head);
        if (search.Length == 0 && !editing) ModernText("mlocal/searchText", "Search", searchText, new(.55f, .6f, .7f), text);
        else ModernText("mlocal/searchText", editing ? search.Insert(Math.Clamp(_localSearch!.CaretIndex, 0, search.Length), "|") : search, searchText, new(1, 1, 1), text);
        if (_localSearch is not null) _localSearch.Bounds = searchRect;
        _tips["mlocal/search"] = LocalInStation ? "Find by name" : "Find objects by name";
        var filterRect = new Rect(cx + searchWidth + gap, cy, filterWidth, head);
        ModernButton("mlocal/filter", LocalKindsLabel(), filterRect, text, LocalInStation ? "Choose what to show (several at once)" : "Choose the kinds of object to show (several at once)", opacity: controls);
        cy += head + gap;

        // Column headings: a click sorts by that column; a second click reverses it.
        double shownColumns = LocalInStation ? 0 : columnsWidth;
        double nameWidth = contentWidth - distanceWidth - shownColumns - ModernScrollbarRoom;
        int sortedColumn = _localSortColumn >= 0 && LocalColumnShown(_localSortColumn) ? _localSortColumn : -1;
        double columnX = cx + nameWidth + distanceWidth;
        for (int n = 0; !LocalInStation && n < LocalColumns.Length; n++)
        {
            if (!LocalColumnShown(n)) continue;
            string id = "mlocal/sortColumn/" + n.ToString(CultureInfo.InvariantCulture);
            var area = new Rect(columnX, cy, columnWidths[n], head);
            Hit(id, FromPixels(area));
            bool sorted = sortedColumn == n, hot = _hover == id;
            ModernText(id + "/text", LocalColumns[n].Heading, new(area.X, area.Y, area.Width - 8 * u, area.Height),
                hot ? new(1, 1, .45f) : sorted ? new(1, .85f, .3f) : new(.7f, .75f, .85f), text, 2);
            _tips[id] = LocalColumns[n].Tip + (sorted ? (_localDescending ? "\nSorted smallest first. Click for largest first." : "\nSorted largest first. Click for smallest first.") : "\nClick to sort by it");
            columnX += columnWidths[n];
        }
        foreach (var (id, label, area, byName) in new[]
        {
            ("mlocal/sortName", "Name", new Rect(cx, cy, nameWidth, head), true),
            ("mlocal/sortDistance", LocalInStation ? "Room" : "Distance", new Rect(cx + nameWidth, cy, distanceWidth, head), false),
        })
        {
            Hit(id, FromPixels(area));
            bool sorted = sortedColumn < 0 && _localByName == byName, hot = _hover == id;
            // The sorted column says its direction in words (no arrow glyphs: the Modern font need not have them).
            string shown = !sorted ? label : byName ? (_localDescending ? "Name, Z to A" : "Name, A to Z")
                : LocalInStation ? (_localDescending ? "Here last" : "Here first") : _localDescending ? "Farthest" : "Nearest";
            // In space the Name heading starts over the names, past the level column.
            double indent = byName && !LocalInStation ? ModernMeasure("00", text) + 6 * u : 0;
            ModernText(id + "/text", shown, new(area.X + 8 * u + indent, area.Y, area.Width - 16 * u - indent, area.Height),
                hot ? new(1, 1, .45f) : sorted ? new(1, .85f, .3f) : new(.7f, .75f, .85f), text, byName ? 0 : 2);
            _tips[id] = sorted ? (byName ? (_localDescending ? "Sorted Z to A. Click for A to Z." : "Sorted A to Z. Click for Z to A.")
                                        : LocalInStation ? (_localDescending ? "Other rooms first, this room last. Click for this room first." : "This room first, then the other rooms. Click for this room last.")
                                        : (_localDescending ? "Farthest first. Click for nearest first." : "Nearest first. Click for farthest first."))
                               : "Sort by " + label.ToLowerInvariant();
        }
        cy += head + gap;
        Flush();

        var list = new Rect(cx, cy, contentWidth, visible * rowHeight);
        if (!LocalSeeThrough) ModernFrame("DIALOG_BOX_INFO", list);
        else _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), (byte)Math.Round(ModernLocalOpacity * 90)), list));
        Flush();
        Hit("mlocal/list", FromPixels(list));
        _localScroll = Math.Clamp(_localScroll, 0, Math.Max(0, shownCount - visible));
        if (shownCount == 0)
            ModernText("mlocal/none", LocalInStation ? (_localStation!.Count == 0 ? "Nothing here." : "Nothing matches.") : contacts.Length == 0 ? "Nothing in range." : "Nothing matches.",
                new(cx + 8 * u, cy, contentWidth - 16 * u, rowHeight), new(.75f, .8f, .92f), text);
        for (int n = 0; LocalInStation && n < visible && _localScroll + n < _localStationRows.Count; n++)
        {
            var entry = _localStationRows[_localScroll + n];
            var row = new Rect(cx, Snap(cy + n * rowHeight), contentWidth - ModernScrollbarRoom, rowHeight);
            string id = "mlocal/room/" + entry.Key;
            Hit(id, FromPixels(row));
            if ((_localScroll + n) % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            // Players in the station's player-name blue, vendors in the credits gold, terminals in a light teal.
            EnbRgb color = entry.Kind switch
            {
                EnbStationRosterKind.Player => new(.6f, .7f, 1), EnbStationRosterKind.Vendor => new(1, .85f, .45f),
                EnbStationRosterKind.Terminal => new(.55f, .9f, .9f), _ => new(.92f, .94f, 1),
            };
            ModernText(id + "/name", ModernEllipsis(entry.Name, nameWidth - 16 * u, text), new(cx + 8 * u, row.Y, nameWidth - 16 * u, row.Height), color, text);
            // Every row names its room.
            string where = EnbWorldRenderer.StationRoomName(entry.Room);
            ModernText(id + "/distance", ModernEllipsis(where, distanceWidth - 8 * u, text), new(cx + nameWidth, row.Y, distanceWidth - 8 * u, row.Height),
                entry.InRoom ? new(.85f, .88f, .95f) : new(.6f, .65f, .78f), text, 2);
            _tips[id] = entry.Name + "\n" + entry.Kind switch
            {
                EnbStationRosterKind.Player => "Player", EnbStationRosterKind.Vendor => "Vendor",
                EnbStationRosterKind.Terminal => "Terminal", _ => "Character",
            } + (entry.InRoom ? " in this room" : " in the " + EnbWorldRenderer.StationRoomName(entry.Room)) + "\nClick to walk there";
        }
        for (int n = 0; !LocalInStation && n < visible && _localScroll + n < _localRows.Count; n++)
        {
            var contact = _localRows[_localScroll + n];
            var row = new Rect(cx, Snap(cy + n * rowHeight), contentWidth - ModernScrollbarRoom, rowHeight);
            string id = "mlocal/row/" + contact.GameId.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(row));
            if ((_localScroll + n) % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (contact.IsCurrentTarget) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            // The radar's colour, lightened so the darker hues (players' blue, hostile red) stay readable on the window.
            // The contact's own colour (its hostility), not the radar's attacking colour: an attacker blinks in it instead.
            var radar = ModernLocalColor?.Invoke(contact.IsAttacking ? contact with { IsAttacking = false } : contact) ?? new EnbRgb(1, 1, 1);
            var color = new EnbRgb(radar.R * .6f + .4f, radar.G * .6f + .4f, radar.B * .6f + .4f);
            // An attacker's row lights up in that colour every other moment (its name stays readable).
            if (contact.IsAttacking && Environment.TickCount64 / 450 % 2 == 0)
                _images.Add(new(EnbModernIcons.Fill(new(radar.R, radar.G, radar.B), 70), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            // Corpses: one not opened yet stands out in purple (the credits colour); one opened before is greyed.
            bool opened = contact.ObjectType == 0x19 && ModernLocalOpened?.Invoke(contact.GameId) == true;
            if (contact.ObjectType == 0x19) color = opened ? new(.55f, .58f, .65f) : new(.82f, .55f, 1);
            string name = LocalName(contact);
            // A known level stands before the name, as on the Modern UI frames and the object labels. The column is
            // always kept, so names stay in one line whether or not a row has a level.
            string? level = contact.Level is int known ? known.ToString(CultureInfo.InvariantCulture) : null;
            double levelWidth = ModernMeasure("00", text), nameX = cx + 8 * u + levelWidth + 6 * u, nameRoom = nameWidth - 22 * u - levelWidth;
            if (level is not null) ModernText(id + "/level", level, new(cx + 8 * u, row.Y, levelWidth, row.Height), new(.85f, .88f, .95f), text, 2);
            ModernText(id + "/name", ModernEllipsis(name, nameRoom, text), new(nameX, row.Y, nameRoom, row.Height), color, text);
            // A row waiting on its loot or approach says so where its distance stands.
            string? status = ModernLocalStatus?.Invoke(contact.GameId);
            ModernText(id + "/distance", status ?? (EnbGutterUi.FormatDistance(contact.SurfaceDistance) is { Length: > 0 } d ? d : "0.00k"),
                new(cx + nameWidth - (status is null ? 0 : 40 * u), row.Y, distanceWidth - 8 * u + (status is null ? 0 : 40 * u), row.Height),
                status is null ? new(.85f, .88f, .95f) : new(1, .78f, .35f), text, 2);
            // Next columns: speed, then this character's own damage with it (yours in the damage numbers' gold, theirs in red).
            double cellX = cx + nameWidth + distanceWidth;
            for (int c = 0; c < LocalColumns.Length; c++)
            {
                if (!LocalColumnShown(c)) continue;
                string value = LocalColumnValue(contact, c, localNow).Text;
                if (value.Length > 0)
                    ModernText(id + "/column/" + c.ToString(CultureInfo.InvariantCulture), value, new(cellX, row.Y, columnWidths[c] - 8 * u, row.Height),
                        c == 0 ? new(.85f, .88f, .95f) : c is 1 or 3 ? new(1, .88f, .4f) : new(1, .55f, .5f), text, 2);
                cellX += columnWidths[c];
            }
            bool corpse = contact.ObjectType == 0x19;
            _tips[id] = name + (level is null ? "" : "\nLevel " + level) + (corpse ? (opened ? "\nOpened" : "\nNew") : contact.IsAttacking ? "\nAttacking" : "")
                + (contact.IsCurrentTarget && !corpse ? "\nYour target" : corpse ? (Data?.HulkId == contact.GameId ? "\nClick to close its loot" : "\nClick to open its loot") : "\nClick to target") + "\nRight-click for more";
        }
        ModernScrollbar("mlocal/list", list, shownCount, visible, _localScroll, v => _localScroll = v);
        // The grip at the bottom right corner resizes the window; the size is kept.
        int gripPixels = (int)Math.Round(14 * u * _scale);
        var grip = new Rect(Snap(x + width - gripPixels / _scale - 2 * u), Snap(y + height - gripPixels / _scale - 2 * u), gripPixels / _scale, gripPixels / _scale);
        Hit("mlocal/resize", FromPixels(grip));
        _images.Add(new(EnbModernIcons.Grip(gripPixels, _hover == "mlocal/resize" || _pressed == "mlocal/resize"), grip));
        _tips["mlocal/resize"] = "Drag to resize the window";
        Flush();
        if (_localFilterOpen)
        {
            double rowH = 22 * u;
            double dropWidth = Math.Max(filterRect.Width, ModernMeasure("Navigation", text) + 40 * u);
            var names = LocalKindNames; int kinds = LocalKinds;
            var drop = new Rect(filterRect.Right - dropWidth, filterRect.Bottom + gap, dropWidth, names.Length * rowH + gap * 2);
            ModernFrame("DIALOG_BOX_WINDOW", drop, "mlocal/filterFrame");
            for (int i = 0; i < names.Length; i++)
            {
                string id = "mlocal/filter/" + i.ToString(CultureInfo.InvariantCulture);
                var r = new Rect(drop.X + 2 * u, drop.Y + gap + i * rowH, drop.Width - 4 * u, rowH);
                Hit(id, FromPixels(r));
                if (_hover == id) ModernFrame("DIALOG_BOX_OPTION", r);
                // Everything clears the choice; the kinds below it are ticked on and off, several at once.
                bool on = i == 0 ? kinds == 0 : (kinds & (1 << i)) != 0;
                int box = (int)Math.Round(14 * u * _scale);
                _images.Add(new(EnbModernIcons.Check(box, on, _hover == id, round: i == 0), new(Snap(r.X + 6 * u), Snap(r.Y + (r.Height - box / _scale) / 2), box / _scale, box / _scale)));
                ModernText(id + "/text", names[i], new(r.X + 12 * u + box / _scale, r.Y, r.Width, r.Height), _hover == id || on ? new(1, 1, .3f) : new(1, 1, 1), text);
            }
            Flush();
        }
    }

    private async Task<bool> ActivateModernLocal(string id, bool right)
    {
        if (!id.StartsWith("mlocal/", StringComparison.Ordinal)) return false;
        if (id != "mlocal/search" && _localSearch is { IsFocused: true }) _localSearch.IsVisible = false;
        if (right)
        {
            if (id.StartsWith("mlocal/row/", StringComparison.Ordinal) && uint.TryParse(id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out uint rowId)
                && _localRows.Find(c => c.GameId == rowId) is { } row)
            {
                bool corpse = row.ObjectType == 0x19;
                var items = new List<EnbModernMenuItem> { new("Target", () => _ = ModernLocalTargetRequested?.Invoke(rowId)) };
                if (corpse) items.Add(new("Open loot", () => _ = ModernLocalAction?.Invoke(rowId, "loot")));
                items.Add(new(corpse ? "Approach and open loot" : "Approach", () => _ = ModernLocalAction?.Invoke(rowId, "approach")));
                ModernMenuRequested?.Invoke("mlocal/menu", LocalName(row), items, _point);
            }
            else if (!id.StartsWith("mlocal/filter", StringComparison.Ordinal) && id != "mlocal/search") LocalWindowMenu();
            return true;
        }
        if (id == "mlocal/close") { CloseModernLocal(); WantModernLocal(false); InteractionSoundRequested?.Invoke("TextClick01.wav"); }
        else if (id == "mlocal/search")
        {
            _localSearch ??= CreateLocalSearch();
            _localSearch.IsVisible = true; _localSearch.Focus();
        }
        else if (id == "mlocal/filter") { _localFilterOpen = !_localFilterOpen; InteractionSoundRequested?.Invoke("TextClick03.wav"); }
        else if (id.StartsWith("mlocal/filter/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(14), NumberStyles.None, CultureInfo.InvariantCulture, out int filter))
        {
            // Everything shows all kinds; a kind is ticked on or off and the list stays open for the next one.
            filter = Math.Clamp(filter, 0, LocalKindNames.Length - 1);
            LocalKinds = filter == 0 ? 0 : LocalKinds ^ (1 << filter);
            if (filter == 0) _localFilterOpen = false;
            _localScroll = 0; InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
        else if (id.StartsWith("mlocal/sortColumn/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(18), NumberStyles.None, CultureInfo.InvariantCulture, out int column))
        {
            if (_localSortColumn == column) _localDescending = !_localDescending; else { _localSortColumn = column; _localDescending = false; }
            _localScroll = 0; InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
        else if (id is "mlocal/sortName" or "mlocal/sortDistance")
        {
            bool byName = id == "mlocal/sortName";
            if (_localSortColumn < 0 && _localByName == byName) _localDescending = !_localDescending; else { _localSortColumn = -1; _localByName = byName; _localDescending = false; }
            _localScroll = 0; InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
        else if (id.StartsWith("mlocal/row/", StringComparison.Ordinal) && uint.TryParse(id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out uint target))
        {
            _localFilterOpen = false;
            InteractionSoundRequested?.Invoke("fontclick.wav");
            // A corpse opens its loot (target, then its Loot button as soon as the server offers it), and a click on the corpse
            // whose loot is open closes it again, as the loot window's own close button does; anything else is targeted.
            bool corpse = _localRows.Find(c => c.GameId == target) is { ObjectType: 0x19 };
            if (corpse && Data?.HulkId == target) await CloseLoot();
            else if (corpse && ModernLocalAction is { } act) await act(target, "loot");
            else if (ModernLocalTargetRequested is { } select) await select(target);
        }
        else if (id.StartsWith("mlocal/room/", StringComparison.Ordinal) && LocalInStation
            && _localStationRows.Find(e => e.Key == id[12..]) is { Key: not null } walk)
        {
            _localFilterOpen = false;
            InteractionSoundRequested?.Invoke("fontclick.wav");
            ModernLocalWalkRequested?.Invoke(walk.Key, walk.Room);
        }
        else if (id != "mlocal/filterFrame") _localFilterOpen = false;
        return true;
    }

    private EnbUiTextInput CreateLocalSearch()
    {
        var input = CreateTextInput(new(32), key =>
        {
            if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return Task.FromResult(false);
            // Escape clears the text; Enter and Tab keep it. Either way the keyboard returns to the game.
            if (key == EnbInventoryKeys.Escape && _localSearch is { } search) search.Text = "";
            if (_localSearch is not null) _localSearch.IsVisible = false;
            Publish(); return Task.FromResult(true);
        });
        input.Changed += (_, _) => { _localScroll = 0; Publish(); };
        input.LostFocus += (_, _) => Publish();
        return input;
    }

    /// <summary>The wheel over the list scrolls it.</summary>
    internal bool ScrollModernLocal(string id, double delta)
    {
        if (!_modernLocal || !id.StartsWith("mlocal/", StringComparison.Ordinal) || id.StartsWith("mlocal/filter", StringComparison.Ordinal) || delta == 0) return false;
        _localScroll = Math.Max(0, _localScroll + (delta < 0 ? 3 : -3));
        Publish(); return true;
    }
}
