using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI inventory: every cargo slot in one compact window (no pages), with search, the retail cargo
/// filter and sort, credits and slot use. It is another layout of the retail cargo: slots keep the retail item
/// requests, drop targets, tooltips, examine, drag and vendor quick-click, so only the presentation is new.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernInventory, _modernFilterOpen;
    private EnbUiTextInput? _modernSearch;
    /// <summary>The Modern UI micro menu on screen; the inventory opens just above it.</summary>
    internal Func<Rect?>? ModernInventoryAnchor { get; set; }
    public bool ModernInventoryOpen => _modernInventory;
    /// <summary>The search field has keyboard focus: typed keys are text, not game controls.</summary>
    public bool ModernSearchEditing => _modernSearch is { IsVisible: true, IsFocused: true };
    internal string ModernSearchText => _modernSearch?.Text ?? "";
    internal EnbUiTextInput? ModernSearchInput => _modernSearch;

    public bool ToggleModernInventory()
    {
        if (!_interactive || !ModernUi) return false;
        _modernInventory = !_modernInventory;
        if (!_modernInventory) CloseModernInventory(); else { RaiseModern("minv"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }
    private void CloseModernInventory()
    {
        _modernInventory = _modernFilterOpen = false;
        if (_modernSearch is not null) _modernSearch.IsVisible = false;
    }

    private Rect FromPixels(double x, double y, double w, double h) => new(x * 640 / _size.Width, y * 480 / _size.Height, w * 640 / _size.Width, h * 480 / _size.Height);
    private Rect FromPixels(Rect r) => FromPixels(r.X, r.Y, r.Width, r.Height);

    private bool ModernMatches(EnbSceneItemSnapshot item)
    {
        if (_filter > 0 && !EnbItemInformation.Matches(item, _filter)) return false;
        string search = ModernSearchText.Trim();
        return search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Modern UI drawing: the HUD's thin-bordered frames and its system-font text, sized by measurement. ----
    /// <summary>Saved top-left as a fraction of the screen; null keeps the default place above the micro menu.</summary>
    internal EnbUiPoint? ModernInventoryPosition { get; set; }
    /// <summary>Called with the new top-left (screen fraction) after the window was dragged.</summary>
    internal Action<EnbUiPoint>? ModernInventoryMoved { get; set; }
    private Rect _modernInventoryBounds;
    private EnbUiPoint _modernDragStart, _modernDragOrigin;
    private bool _modernDragging;
    private readonly Dictionary<(string Text, float Size, EnbRgb Color), EnbUiRaster> _modernText = [];
    private double ModernUnit => Math.Max(.75, _size.Height / 1080);
    private double Snap(double v) => Math.Round(v * _scale) / _scale;

    private double ModernMeasure(string text, double size)
    {
        if (EnbModernText.Renderer is { } m) return m.Measure(text, (float)(size * _scale)) / _scale;
        return Font("FONT_SMALL") is { } f ? f.MeasureWidth(text) * f.Scale : text.Length * size * .6;
    }
    /// <summary>One line at its natural size: never scaled to fit. Align 0 left, 1 centre, 2 right; centred vertically on its capitals.</summary>
    private void ModernText(string id, string text, Rect area, EnbRgb color, double size, int align = 0, bool inkCentered = false)
    {
        if (text.Length == 0) return;
        if (EnbModernText.Renderer is not { } m) { Text(id, text, FromPixels(area), color, "FONT_SMALL", align == 1); return; }
        float px = (float)(size * _scale);
        if (!_modernText.TryGetValue((text, px, color), out var image))
        {
            if (m.Render(text, px, color) is not { } rendered) return;
            if (_modernText.Count >= 128) _modernText.Clear();
            _modernText[(text, px, color)] = image = rendered;
        }
        double pad = m.Padding(px) / _scale, iw = image.Width / _scale, ih = image.Height / _scale, width = iw - 2 * pad;
        double x = align == 0 ? area.X : align == 1 ? area.X + (area.Width - width) / 2 : area.Right - width;
        double y = area.Center.Y - ih / 2 - m.InkCenterOffset(inkCentered ? text : "H", px) / _scale;
        _images.Add(new(image, new(Snap(x - pad), Snap(y), iw, ih)));
    }
    /// <summary>A Modern UI frame (dynawin DIALOG_BOX_*) at a screen rectangle, its border sized like the HUD's.</summary>
    private void ModernFrame(string name, Rect screen, string? hit = null, bool imagesOnly = false)
    {
        double u = ModernUnit;
        float w = (float)Math.Round(screen.Width / u, 1), h = (float)Math.Round(screen.Height / u, 1);
        string key = FormattableString.Invariant($"modern/{name}/{w}/{h}");
        if (!_art.ContainsKey(key))
        {
            if (!_frames.TryGetValue(name, out var frame)) return;
            var g = frame.Geometry(w, h);
            var min = g.Positions.Aggregate(System.Numerics.Vector3.Min); var max = g.Positions.Aggregate(System.Numerics.Vector3.Max);
            var source = EnbAuthoredUiAsset.Load(_assets!, frame.Definition.MeshFile + ".w3d") ?? throw new InvalidDataException("Dynamic window hierarchy unavailable.");
            _art[key] = new(new(frame.Resource, source.Hierarchy) { Geometry = g }, new(min.X, -max.Y, max.X - min.X, max.Y - min.Y));
        }
        // imagesOnly: the frame's draw carries the window's images without drawing the frame (a see-through window).
        if (imagesOnly) _draws.Add(new(_art[key].Asset, 0, Screen(FromPixels(screen)), false) { ImagesOnly = true, LayoutGroup = _layoutGroup });
        else Draw(key, FromPixels(screen));
        if (hit is not null) Hit(hit, FromPixels(screen));
    }
    private void ModernButton(string id, string text, Rect screen, double size, string tip, bool enabled = true, float opacity = 1)
    {
        // Drawn at its exact pixel size (EnbModernIcons.Button): no stretched dialog-frame pieces. A disabled button is dimmed.
        var face = new Rect(Snap(screen.X), Snap(screen.Y), Math.Round(screen.Width * _scale) / _scale, Math.Round(screen.Height * _scale) / _scale);
        Hit(id, FromPixels(face));
        bool hot = enabled && _hover == id;
        _images.Add(new(EnbModernIcons.Button((int)Math.Round(face.Width * _scale), (int)Math.Round(face.Height * _scale), hot), face) { Opacity = (enabled ? 1 : .55f) * opacity });
        ModernText(id + "/text", text, screen, hot ? new(1, 1, .3f) : enabled ? new(1, 1, 1) : new(.5f, .53f, .6f), size, 1);
        _tips[id] = tip;
    }
    /// <summary>The Modern UI close button (EnbModernIcons.CloseButton), drawn at its exact pixel size; highlighted on hover.</summary>
    private void ModernCloseButton(string id, Rect screen, float opacity = 1)
    {
        Hit(id, FromPixels(screen));
        int pixels = (int)Math.Round(screen.Height * _scale);
        _images.Add(new(EnbModernIcons.CloseButton(pixels, _hover == id), new(Snap(screen.X), Snap(screen.Y), pixels / _scale, pixels / _scale)) { Opacity = opacity });
        _tips[id] = "Close";
    }
    /// <summary>Modern UI slots light up under the pointer, filled or empty.</summary>
    private void ModernSlotHighlight(string id, Rect design)
    {
        if (_hover != id) return;
        var screen = Screen(design);
        _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(screen.Width * _scale), (int)Math.Round(screen.Height * _scale)), screen));
    }
    /// <summary>The ammunition loaded in a pressed Modern UI weapon slot (the ammo slot beside it), if any.</summary>
    private EnbInventoryItemRequest? ModernDragSubstitute(string? pressed)
        => pressed?.StartsWith("mequip/weapon", StringComparison.Ordinal) == true && _items.TryGetValue("mequip/ammo" + pressed["mequip/weapon".Length..], out var ammo) && ammo.AmmoLayer ? ammo : null;
    /// <summary>Modern UI window stacking: each window's last raise; the highest is in front (opened or pressed last).</summary>
    private int _modernZ = 5, _minvZ = 1, _mequipZ = 2, _mskillZ = 3, _mmissionZ = 4, _mfactionZ = 5, _mcommandZ = 0, _mlogZ = 0, _moptZ = 0;
    private void RaiseModern(string window)
    {
        int z = ++_modernZ;
        if (window == "mequip") _mequipZ = z; else if (window == "mskill") _mskillZ = z; else if (window == "mmission") _mmissionZ = z; else if (window == "mfaction") _mfactionZ = z; else if (window == "mcommand") _mcommandZ = z; else if (window == "mlocal") _mlocalZ = z; else if (window == "mstation") _mstationZ = z; else if (window is "mmusic" or "mwiki" or "mitems") { } else if (window == "mterm") { } else if (window == "mlog") _mlogZ = z; else if (window == "mopt") _moptZ = z; else if (window == "mvault") _mvaultZ = z; else if (window is "friends" or "guild" or "finder" or "talk" or "trade" or "loot" or "vendor") { } else _minvZ = z;
    }
    /// <summary>The Modern UI window a control id belongs to (minv, mequip or mskill), if any.</summary>
    private static string? ModernWindowOf(string id)
        => id.StartsWith("minv/", StringComparison.Ordinal) ? "minv" : id.StartsWith("mequip/", StringComparison.Ordinal) ? "mequip" : id.StartsWith("mskill/", StringComparison.Ordinal) ? "mskill" : id.StartsWith("mmission/", StringComparison.Ordinal) ? "mmission" : id.StartsWith("mfaction/", StringComparison.Ordinal) ? "mfaction" : id.StartsWith("mcommand/", StringComparison.Ordinal) ? "mcommand" : id.StartsWith("mlocal/", StringComparison.Ordinal) ? "mlocal" : id.StartsWith("mstation/", StringComparison.Ordinal) ? "mstation" : id.StartsWith("mmusic/", StringComparison.Ordinal) ? "mmusic" : id.StartsWith("mwiki/", StringComparison.Ordinal) ? "mwiki" : id.StartsWith("mitems/", StringComparison.Ordinal) ? "mitems" : id.StartsWith("mterm/", StringComparison.Ordinal) ? "mterm" : id.StartsWith("mlog/", StringComparison.Ordinal) ? "mlog" : id.StartsWith("mopt/", StringComparison.Ordinal) ? "mopt" : id.StartsWith("mvault/", StringComparison.Ordinal) ? "mvault" : id == "friends/frame" ? "friends" : id == "guild/frame" ? "guild" : id == "finder/frame" ? "finder" : id is "talkFrame/modern" or "recoveryFrame/modern" ? "talk" : id == "playerTradeFrame/modern" ? "trade" : id == "lootFrame/modern" ? "loot" : id == "vendorFrame/modern" ? "vendor" : null;
    private static Rect Inset(Rect r, double d) => new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    private static string FilterLabel(int filter) => filter <= 0 || filter >= EnbItemInformation.Filters.Length ? "All items" : EnbItemInformation.Filters[filter];
    private string SortLabel(int mode)
    {
        string name = _owner is uint owner && _assets is not null ? _assets.ResolvePlayerOptions(owner).Options.InventorySort(mode).Name : "";
        return name.Length > 0 ? "Sort: " + char.ToUpperInvariant(name[0]) + name[1..] : "Sort";
    }

    private void ModernInventory()
    {
        var data = Data;
        var keys = data?.CargoSlotKeys ?? [];
        var (columns, rows, slot, content, filterWidth, sortWidth) = ModernInventoryMetrics();
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, row = 24 * u, buttonPad = 12 * u;
        double searchWidth = content - filterWidth - sortWidth - gap * 2;
        double width = content + pad * 2, height = pad * 2 + row * 3 + gap * 3 + rows * slot + (rows - 1) * gap;
        // Placement: where the player left it, else on the right just above the micro menu. Always fully on screen.
        double x, y;
        if (ModernInventoryPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else
        {
            var anchor = ModernInventoryAnchor?.Invoke();
            double right = anchor is { Width: > 0 } a ? a.Right : _size.Width - 12 * u;
            double bottom = anchor is { Height: > 0 } b ? b.Y - 10 * u : _size.Height - 60 * u;
            x = right - width; y = bottom - height;
        }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernInventoryBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernInventoryBounds, "minv/frame");
        _tips.Remove("minv/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        // Title row: name and close.
        ModernText("minv/title", "Inventory", new(cx, cy, content - row, row), new(1, .85f, .3f), title);
        ModernCloseButton("minv/close", new(x + width - pad - row, cy, row, row));
        cy += row + gap;
        // Search, filter, sort.
        var searchRect = new Rect(cx, cy, searchWidth, row);
        ModernFrame("DIALOG_BOX_INFO", searchRect, "minv/search");
        string search = ModernSearchText; bool editing = ModernSearchEditing;
        var searchText = new Rect(cx + 8 * u, cy, searchWidth - 16 * u, row);
        if (search.Length == 0 && !editing) ModernText("minv/searchText", "Search", searchText, new(.55f, .6f, .7f), text);
        else
        {
            string shown = editing ? search.Insert(Math.Clamp(_modernSearch!.CaretIndex, 0, search.Length), "|") : search;
            ModernText("minv/searchText", shown, searchText, new(1, 1, 1), text);
        }
        if (_modernSearch is not null) _modernSearch.Bounds = searchRect;
        _tips["minv/search"] = "Search cargo by name";
        var filterRect = new Rect(cx + searchWidth + gap, cy, filterWidth, row);
        ModernButton("minv/filter", FilterLabel(_filter), filterRect, text, "Show one kind of item");
        ModernButton("minv/sort", SortLabel(_sort), new(filterRect.Right + gap, cy, sortWidth, row), text,
            "Left-click to change the cargo sort.\nRight-click to reverse the sort order.");
        cy += row + gap;
        Flush();
        // Every slot at once.
        var items = data?.Cargo ?? [];
        int used = 0;
        for (int index = 0; index < keys.Length; index++)
        {
            int key = keys[index];
            EnbSceneItemSnapshot? item = null;
            foreach (var candidate in items) if (candidate.Slot == key) { item = candidate; break; }
            if (item is not null) used++;
            int column = index % columns, line = index / columns;
            string id = "minv/slot/" + index.ToString(CultureInfo.InvariantCulture);
            var slotRect = FromPixels(Snap(cx + column * (slot + gap)), Snap(cy + line * (slot + gap)), slot, slot);
            Slot(id, slotRect, item, enabled: true, dim: item is not null && !ModernMatches(item), compact: true);
            ModernSlotHighlight(id, slotRect);
            _dropSlots[id] = (1, key);
            if (item is null) EmptyInventorySlotTooltip(id);
        }
        Flush();
        cy += rows * slot + (rows - 1) * gap + gap;
        // Bottom row: credits left, slot use right.
        var bottomRow = new Rect(cx, cy, content, row);
        ModernText("minv/credits", "Credits: " + (data?.Credits ?? 0).ToString("N0", CultureInfo.InvariantCulture), bottomRow, new(1, 1, 1), text);
        string usage = used.ToString(CultureInfo.InvariantCulture) + " / " + keys.Length.ToString(CultureInfo.InvariantCulture) + " slots";
        ModernText("minv/usage", usage, bottomRow, used >= keys.Length && keys.Length > 0 ? new(1, .55f, .4f) : new(.7f, .75f, .85f), text, 2);
        if (_message is not null && _terminal is null) ModernText("minv/message", _message, new(cx, cy - row - gap, content, row), new(1, .4f, .4f), text, 1);
        Flush();
        if (_modernFilterOpen)
        {
            // The list is exactly as wide as the filter button, which already fits the longest name.
            double rowHeight = 22 * u;
            var list = new Rect(filterRect.X, filterRect.Bottom + gap, filterRect.Width, EnbItemInformation.Filters.Length * rowHeight + gap * 2);
            ModernFrame("DIALOG_BOX_WINDOW", list, "minv/filterFrame");
            for (int i = 0; i < EnbItemInformation.Filters.Length; i++)
            {
                string id = "minv/filter/" + i.ToString(CultureInfo.InvariantCulture);
                var r = new Rect(list.X + 2 * u, list.Y + gap + i * rowHeight, list.Width - 4 * u, rowHeight);
                Hit(id, FromPixels(r));
                if (_hover == id) ModernFrame("DIALOG_BOX_OPTION", r);
                ModernText(id + "/text", EnbItemInformation.Filters[i], new(r.X + buttonPad - 2 * u, r.Y, r.Width, r.Height), _hover == id || _filter == i ? new(1, 1, .3f) : new(1, 1, 1), text);
            }
            Flush();
        }
    }

    /// <summary>
    /// An item dragged out of a Modern UI slot stays the size of that slot under the pointer (category bar, icon,
    /// numeral and count, as in the slot), instead of the retail cargo's large drag image.
    /// </summary>
    private void ModernDraggedItem(EnbSceneItemSnapshot item, Rect slot)
    {
        var p = _pressed is { } source ? LayoutOutput(source, _point) : _point;
        var r = new Rect(Snap(p.X - slot.Width / 2), Snap(p.Y - slot.Height / 2), slot.Width, slot.Height);
        Flush(); Draw("dragLayer", FromPixels(r));
        if (item.ItemType != uint.MaxValue && Texture("icon_" + SlotColor(item) + ".tga") is { } frame) _images.Add(new(frame, r));
        var d = FromPixels(r); var iconArea = new Rect(d.X + 1, d.Y + 2, d.Width - 2, d.Height - 4);
        if (Icon(item.IconBaseAsset) is { } icon) _images.Add(new(icon, Screen(iconArea)));
        Flush(); ItemRestriction(item, iconArea);
        var green = new EnbRgb(0, 1, 0);
        ModernText("drag/level", Roman(item.Level), new(r.X, r.Y, r.Width, r.Height * .2), green, r.Height * .22, 1);
        int count = item.Count;
        if (item.Inventory == 2 && item.IsWeapon) count = Data?.Ammo.FirstOrDefault(a => a.Slot == item.Slot)?.Count ?? 0;
        if (count > 1 || item.Inventory == 2 && item.IsWeapon && count > 0)
            ModernText("drag/count", count.ToString(CultureInfo.InvariantCulture), new(r.X, r.Bottom - r.Height * .3, r.Width, r.Height * .26), green, r.Height * .22, 1);
        Flush();
    }

    /// <summary>Dragging a Modern UI window's body (not a slot or button) moves it; the place is kept.</summary>
    private string? _modernDragWindow;
    private static bool IsModernWindowBody(string? id) => id is "minv/frame" or "mvault/frame" or "mequip/frame" or "mskill/frame" or "mmission/frame" or "mfaction/frame" or "mcommand/frame" or "mlocal/frame" or "mstation/frame" or "mmusic/frame" or "mwiki/frame" or "mitems/frame" or "mterm/frame" or "mlog/frame" or "mopt/frame" or "friends/frame" or "guild/frame" or "finder/frame" or "talkFrame/modern" or "recoveryFrame/modern" or "playerTradeFrame/modern" or "lootFrame/modern" or "vendorFrame/modern";
    private Rect ModernWindowBounds(string? window) => window == "mequip" ? _modernEquipmentBounds : window == "mskill" ? _modernSkillsBounds : window == "mmission" ? _modernMissionsBounds : window == "mfaction" ? _modernFactionsBounds : window == "mcommand" ? _modernCommandsBounds : window == "mlocal" ? _modernLocalBounds : window == "mstation" ? _modernStationMapBounds : window == "mmusic" ? _modernMusicBounds : window == "mwiki" ? _modernWikiBounds : window == "mitems" ? _modernItemsBounds : window == "mterm" ? _modernTerminalBounds : window == "friends" ? _modernFriendsBounds : window == "guild" ? _modernGuildBounds : window == "finder" ? _modernFinderBounds : window == "talk" ? _modernTalkBounds : window == "trade" ? _modernTradeBounds : window == "loot" ? _modernLootBounds : window == "vendor" ? _modernVendorBounds : window == "mlog" ? _modernLogBounds : window == "mopt" ? _modernOptionsBounds : window == "mvault" ? _modernVaultBounds : _modernInventoryBounds;
    private void DragModernInventory(EnbUiPoint p)
    {
        if (!_modernDragging && Math.Abs(p.X - _modernDragStart.X) + Math.Abs(p.Y - _modernDragStart.Y) < 4) return;
        _modernDragging = true;
        var bounds = ModernWindowBounds(_modernDragWindow);
        double x = Math.Clamp(_modernDragOrigin.X + p.X - _modernDragStart.X, 0, Math.Max(0, _size.Width - bounds.Width));
        double y = Math.Clamp(_modernDragOrigin.Y + p.Y - _modernDragStart.Y, 0, Math.Max(0, _size.Height - bounds.Height));
        var at = new EnbUiPoint(x / Math.Max(1, _size.Width), y / Math.Max(1, _size.Height));
        if (_modernDragWindow == "mequip") ModernEquipmentPosition = at; else if (_modernDragWindow == "mskill") ModernSkillsPosition = at; else if (_modernDragWindow == "mmission") ModernMissionsPosition = at; else if (_modernDragWindow == "mfaction") ModernFactionsPosition = at; else if (_modernDragWindow == "mcommand") ModernCommandsPosition = at; else if (_modernDragWindow == "mlocal") ModernLocalPosition = at; else if (_modernDragWindow == "mstation") ModernStationMapPosition = at; else if (_modernDragWindow == "mmusic") ModernMusicPosition = at; else if (_modernDragWindow == "mwiki") ModernWikiPosition = at; else if (_modernDragWindow == "mitems") ModernItemsPosition = at; else if (_modernDragWindow == "mterm") ModernTerminalPosition = at; else if (_modernDragWindow == "friends") ModernFriendsPosition = at; else if (_modernDragWindow == "guild") ModernGuildPosition = at; else if (_modernDragWindow == "finder") ModernFinderPosition = at; else if (_modernDragWindow == "talk") ModernTalkPosition = at; else if (_modernDragWindow == "trade") ModernTradePosition = at; else if (_modernDragWindow == "loot") ModernLootPosition = at; else if (_modernDragWindow == "vendor") ModernVendorPosition = at; else if (_modernDragWindow == "mlog") ModernLogPosition = at; else if (_modernDragWindow == "mopt") ModernOptionsPosition = at; else if (_modernDragWindow == "mvault") ModernVaultPosition = at; else ModernInventoryPosition = at;
    }
    private void BeginModernDrag(string id, EnbUiPoint p)
    {
        _modernDragWindow = ModernWindowOf(id) ?? "minv";
        RaiseModern(_modernDragWindow);
        var bounds = ModernWindowBounds(_modernDragWindow);
        _modernDragStart = p; _modernDragOrigin = new(bounds.X, bounds.Y); _modernDragging = false;
    }
    private bool EndModernDrag()
    {
        if (!_modernDragging) return false;
        _modernDragging = false;
        if (_modernDragWindow == "mequip") { if (ModernEquipmentPosition is { } placed) ModernEquipmentMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mskill") { if (ModernSkillsPosition is { } placed) ModernSkillsMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mmission") { if (ModernMissionsPosition is { } placed) ModernMissionsMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mfaction") { if (ModernFactionsPosition is { } placed) ModernFactionsMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mcommand") { if (ModernCommandsPosition is { } placed) ModernCommandsMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mlocal") { if (ModernLocalPosition is { } placed) ModernLocalMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mstation") { if (ModernStationMapPosition is { } placed) ModernStationMapMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mmusic") { if (ModernMusicPosition is { } placed) ModernMusicMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mwiki") { if (ModernWikiPosition is { } placed) ModernWikiMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mitems") { if (ModernItemsPosition is { } placed) ModernItemsMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mterm") { if (ModernTerminalPosition is { } placed) ModernTerminalMoved?.Invoke(placed); }
        else if (_modernDragWindow == "friends") { if (ModernFriendsPosition is { } placed) ModernFriendsMoved?.Invoke(placed); }
        else if (_modernDragWindow == "guild") { if (ModernGuildPosition is { } placed) ModernGuildMoved?.Invoke(placed); }
        else if (_modernDragWindow == "finder") { if (ModernFinderPosition is { } placed) ModernFinderMoved?.Invoke(placed); }
        else if (_modernDragWindow == "talk") { if (ModernTalkPosition is { } placed) ModernTalkMoved?.Invoke(placed); }
        else if (_modernDragWindow == "trade") { if (ModernTradePosition is { } placed) ModernTradeMoved?.Invoke(placed); }
        else if (_modernDragWindow == "loot") { if (ModernLootPosition is { } placed) ModernLootMoved?.Invoke(placed); }
        else if (_modernDragWindow == "vendor") { if (ModernVendorPosition is { } placed) ModernVendorMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mlog") { if (ModernLogPosition is { } placed) ModernLogMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mopt") { if (ModernOptionsPosition is { } placed) ModernOptionsMoved?.Invoke(placed); }
        else if (_modernDragWindow == "mvault") { if (ModernVaultPosition is { } placed) ModernVaultMoved?.Invoke(placed); }
        else if (ModernInventoryPosition is { } at) ModernInventoryMoved?.Invoke(at);
        return true;
    }

    /// <summary>The inventory's measured layout: the equipment window uses the same slot size.</summary>
    private (int Columns, int Rows, double Slot, double Content, double FilterWidth, double SortWidth) ModernInventoryMetrics()
    {
        int count = Math.Max(Data?.CargoSlotKeys.Length ?? 0, 1);
        // Columns for a roughly 1.6:1 grid: 28 slots -> 7 x 4, 40 -> 8 x 5.
        int columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count * 1.6)), 5, 10), rows = (count + columns - 1) / columns;
        double u = ModernUnit, text = 14 * u, gap = 4 * u, buttonPad = 12 * u;
        // Every width comes from the widest text it can ever show, so nothing moves or shrinks when a choice changes.
        double filterWidth = 0, sortWidth = 0;
        for (int i = 0; i < EnbItemInformation.Filters.Length; i++) filterWidth = Math.Max(filterWidth, ModernMeasure(FilterLabel(i), text));
        for (int mode = 0; mode < 3; mode++) sortWidth = Math.Max(sortWidth, ModernMeasure(SortLabel(mode), text));
        filterWidth += buttonPad * 2; sortWidth += buttonPad * 2;
        double footer = ModernMeasure("Credits: 9,999,999,999", text) + ModernMeasure("99 / 99 slots", text) + 24 * u;
        double content = Math.Max(Math.Max(columns * 46 * u + (columns - 1) * gap, 140 * u + filterWidth + sortWidth + gap * 2), footer);
        double slot = Math.Floor((content - (columns - 1) * gap) / columns);
        return (columns, rows, slot, columns * slot + (columns - 1) * gap, filterWidth, sortWidth);
    }

    private async Task<bool> ActivateModernInventory(string id, bool right)
    {
        if (!id.StartsWith("minv/", StringComparison.Ordinal)) return false;
        if (id != "minv/search" && _modernSearch is { IsFocused: true }) _modernSearch.IsVisible = false;
        if (right && id != "minv/sort") return true;
        switch (id)
        {
            case "minv/close": CloseModernInventory(); InteractionSoundRequested?.Invoke("TextClick01.wav"); break;
            case "minv/filter": _modernFilterOpen = !_modernFilterOpen; InteractionSoundRequested?.Invoke("TextClick03.wav"); break;
            case "minv/sort": await SortCargo(right); break;
            case "minv/search":
                _modernSearch ??= CreateModernSearch();
                _modernSearch.IsVisible = true; _modernSearch.Focus();
                break;
            default:
                if (id.StartsWith("minv/filter/", StringComparison.Ordinal))
                { SelectInventoryFilter(false, int.Parse(id.AsSpan(12), CultureInfo.InvariantCulture)); _modernFilterOpen = false; InteractionSoundRequested?.Invoke("TextClick03.wav"); }
                else if (id != "minv/filterFrame") _modernFilterOpen = false;
                break;
        }
        return true;
    }

    private EnbUiTextInput CreateModernSearch()
    {
        var input = CreateTextInput(new(32), key =>
        {
            if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return Task.FromResult(false);
            // Escape clears the search; Enter and Tab keep it. Either way the keyboard returns to the game.
            if (key == EnbInventoryKeys.Escape && _modernSearch is { } search) search.Text = "";
            if (_modernSearch is not null) _modernSearch.IsVisible = false;
            Publish(); return Task.FromResult(true);
        });
        input.Changed += (_, _) => Publish();
        input.LostFocus += (_, _) => Publish();
        return input;
    }

    private async Task SortCargo(bool right)
    {
        if (_busy || _owner is not uint owner || SortRequested is not { } sort) return;
        if (right) _reverse = !_reverse; else _sort = (_sort + 1) % 3;
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        int mode = _sort; bool reverse = _reverse;
        ReportInventorySort(owner, false, mode, reverse);
        await Send(() => sort(owner, _ship, mode, reverse));
        PersistInventorySort(owner, false, mode, reverse, right);
    }
}
