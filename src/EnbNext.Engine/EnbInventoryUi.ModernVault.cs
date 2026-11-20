using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI vault (Modern UI only, in a station): every vault slot in one movable
/// window, with search, the retail vault filter and sort, and slot use. It opens the Modern inventory beside it (and
/// closes it again if it opened it). It is another layout of the retail vault: slots keep the retail item requests,
/// drop targets, tooltips, examine and drag, the filter is the retail vault filter and the sort sends the retail vault
/// sort, so only the presentation is new.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernVault, _modernVaultFilterOpen, _vaultOpenedModernInventory;
    private int _modernVaultScroll, _mvaultZ;
    private EnbUiTextInput? _modernVaultSearch;
    private Rect _modernVaultBounds;
    internal EnbUiPoint? ModernVaultPosition { get; set; }
    internal Action<EnbUiPoint>? ModernVaultMoved { get; set; }
    public bool ModernVaultOpen => _modernVault;
    public bool ModernVaultSearchEditing => _modernVaultSearch is { IsVisible: true, IsFocused: true };
    internal EnbUiTextInput? ModernVaultSearchInput => _modernVaultSearch;
    private const int VaultRowsShown = 6;

    /// <summary>The vault button (stations only): opens the vault with the inventory beside it, or closes it.</summary>
    public bool ToggleModernVault()
    {
        if (!_interactive || !ModernUi) return false;
        if (_modernVault) CloseModernVault();
        else if (_scene?.ActiveStarbaseId is not null)
        {
            _modernVault = true; _modernVaultScroll = 0; RaiseModern("mvault");
            _vaultOpenedModernInventory = !_modernInventory;
            if (!_modernInventory) { _modernInventory = true; RaiseModern("minv"); }
            Opened?.Invoke();
        }
        else return false;
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }
    private void CloseModernVault()
    {
        if (!_modernVault) return;
        _modernVault = _modernVaultFilterOpen = false;
        if (_modernVaultSearch is not null) _modernVaultSearch.IsVisible = false;
        if (_vaultOpenedModernInventory) CloseModernInventory();
        _vaultOpenedModernInventory = false;
    }

    private bool ModernVaultMatches(EnbSceneItemSnapshot item)
    {
        if (_vaultFilter > 0 && !EnbItemInformation.Matches(item, _vaultFilter)) return false;
        string search = (_modernVaultSearch?.Text ?? "").Trim();
        return search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void ModernVaultWindow()
    {
        // The vault belongs to the station.
        if (_scene?.ActiveStarbaseId is null) { CloseModernVault(); return; }
        var data = Data;
        var keys = data?.VaultSlotKeys ?? [];
        var (_, _, slot, content, filterWidth, sortWidth) = ModernInventoryMetrics();
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, row = 24 * u, buttonPad = 12 * u;
        int columns = Math.Max(1, (int)((content + gap) / (slot + gap)));
        int totalRows = Math.Max(1, (keys.Length + columns - 1) / columns), shownRows = Math.Min(VaultRowsShown, totalRows);
        _modernVaultScroll = Math.Clamp(_modernVaultScroll, 0, Math.Max(0, totalRows - shownRows));
        double room = totalRows > shownRows ? ModernScrollbarRoom : 0;
        double width = content + room + pad * 2, height = pad * 2 + row * 3 + gap * 3 + shownRows * slot + (shownRows - 1) * gap;
        double x, y;
        if (ModernVaultPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else if (_modernInventory) { x = _modernInventoryBounds.X - width - 10 * u; y = _modernInventoryBounds.Bottom - height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .5; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernVaultBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernVaultBounds, "mvault/frame");
        _tips.Remove("mvault/frame");
        Flush();
        double cx = x + pad, cy = y + pad, full = content + room;
        ModernText("mvault/title", "Vault", new(cx, cy, full - row, row), new(1, .85f, .3f), title);
        ModernCloseButton("mvault/close", new(x + width - pad - row, cy, row, row));
        cy += row + gap;
        // Search, filter, sort.
        double searchWidth = full - filterWidth - sortWidth - gap * 2;
        var searchRect = new Rect(cx, cy, searchWidth, row);
        ModernFrame("DIALOG_BOX_INFO", searchRect, "mvault/search");
        string search = _modernVaultSearch?.Text ?? ""; bool editing = ModernVaultSearchEditing;
        var searchText = new Rect(cx + 8 * u, cy, searchWidth - 16 * u, row);
        if (search.Length == 0 && !editing) ModernText("mvault/searchText", "Search", searchText, new(.55f, .6f, .7f), text);
        else ModernText("mvault/searchText", editing ? search.Insert(Math.Clamp(_modernVaultSearch!.CaretIndex, 0, search.Length), "|") : search, searchText, new(1, 1, 1), text);
        if (_modernVaultSearch is not null) _modernVaultSearch.Bounds = searchRect;
        _tips["mvault/search"] = "Search the vault by name";
        var filterRect = new Rect(cx + searchWidth + gap, cy, filterWidth, row);
        ModernButton("mvault/filter", FilterLabel(_vaultFilter), filterRect, text, "Show one kind of item");
        ModernButton("mvault/sort", SortLabel(_vaultSort), new(filterRect.Right + gap, cy, sortWidth, row), text,
            "Left-click to change the vault sort.\nRight-click to reverse the sort order.");
        cy += row + gap;
        Flush();
        // The slots, a few rows at a time (the wheel or the scroll bar shows the rest).
        var items = data?.Vault ?? [];
        int used = 0;
        foreach (var item in items) if (keys.IndexOf(item.Slot) >= 0) used++;
        var grid = new Rect(cx, cy, full, shownRows * slot + (shownRows - 1) * gap);
        Hit("mvault/grid", FromPixels(grid));
        int first = _modernVaultScroll * columns;
        for (int n = 0; n < shownRows * columns && first + n < keys.Length; n++)
        {
            int index = first + n, key = keys[index];
            EnbSceneItemSnapshot? item = null;
            foreach (var candidate in items) if (candidate.Slot == key) { item = candidate; break; }
            string id = "mvault/slot/" + index.ToString(CultureInfo.InvariantCulture);
            var slotRect = FromPixels(Snap(cx + n % columns * (slot + gap)), Snap(cy + n / columns * (slot + gap)), slot, slot);
            Slot(id, slotRect, item, enabled: true, dim: item is not null && !ModernVaultMatches(item), compact: true);
            ModernSlotHighlight(id, slotRect);
            _dropSlots[id] = (3, key);
            if (item is null) EmptyInventorySlotTooltip(id);
        }
        ModernScrollbar("mvault/rows", grid, totalRows, shownRows, _modernVaultScroll, v => _modernVaultScroll = v);
        Flush();
        cy += grid.Height + gap;
        string usage = used.ToString(CultureInfo.InvariantCulture) + " / " + keys.Length.ToString(CultureInfo.InvariantCulture) + " slots";
        ModernText("mvault/usage", usage, new(cx, cy, full, row), used >= keys.Length && keys.Length > 0 ? new(1, .55f, .4f) : new(.7f, .75f, .85f), text, 2);
        Flush();
        if (_modernVaultFilterOpen)
        {
            double rowHeight = 22 * u;
            var list = new Rect(filterRect.X, filterRect.Bottom + gap, filterRect.Width, EnbItemInformation.Filters.Length * rowHeight + gap * 2);
            ModernFrame("DIALOG_BOX_WINDOW", list, "mvault/filterFrame");
            for (int i = 0; i < EnbItemInformation.Filters.Length; i++)
            {
                string id = "mvault/filter/" + i.ToString(CultureInfo.InvariantCulture);
                var r = new Rect(list.X + 2 * u, list.Y + gap + i * rowHeight, list.Width - 4 * u, rowHeight);
                Hit(id, FromPixels(r));
                if (_hover == id) ModernFrame("DIALOG_BOX_OPTION", r);
                ModernText(id + "/text", EnbItemInformation.Filters[i], new(r.X + buttonPad - 2 * u, r.Y, r.Width, r.Height), _hover == id || _vaultFilter == i ? new(1, 1, .3f) : new(1, 1, 1), text);
            }
            Flush();
        }
    }

    private async Task<bool> ActivateModernVault(string id, bool right)
    {
        if (!id.StartsWith("mvault/", StringComparison.Ordinal)) return false;
        if (id != "mvault/search" && _modernVaultSearch is { IsFocused: true }) _modernVaultSearch.IsVisible = false;
        if (right && id != "mvault/sort") return true;
        switch (id)
        {
            case "mvault/close": CloseModernVault(); InteractionSoundRequested?.Invoke("TextClick01.wav"); break;
            case "mvault/filter": _modernVaultFilterOpen = !_modernVaultFilterOpen; InteractionSoundRequested?.Invoke("TextClick03.wav"); break;
            case "mvault/sort": await SortVault(right); break;
            case "mvault/search":
                _modernVaultSearch ??= CreateModernVaultSearch();
                _modernVaultSearch.IsVisible = true; _modernVaultSearch.Focus();
                break;
            default:
                if (id.StartsWith("mvault/filter/", StringComparison.Ordinal))
                { SelectInventoryFilter(true, int.Parse(id.AsSpan(14), CultureInfo.InvariantCulture)); _modernVaultFilterOpen = false; InteractionSoundRequested?.Invoke("TextClick03.wav"); }
                else if (id != "mvault/filterFrame") _modernVaultFilterOpen = false;
                break;
        }
        return true;
    }

    /// <summary>The retail vault sort (as the classic vault's sort button): left-click the next mode, right-click reverses.</summary>
    private async Task SortVault(bool right)
    {
        if (_busy || _scene?.ActiveStarbaseId is null || _owner is not uint owner || VaultSortRequested is not { } sort) return;
        if (right) _vaultReverse = !_vaultReverse; else _vaultSort = (_vaultSort + 1) % 3;
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        int mode = _vaultSort; bool reverse = _vaultReverse;
        ReportInventorySort(owner, true, mode, reverse);
        await Send(() => sort(owner, _ship, mode, reverse));
        PersistInventorySort(owner, true, mode, reverse, right);
    }

    /// <summary>The wheel over the vault scrolls its rows.</summary>
    internal bool ScrollModernVault(string id, double delta)
    {
        if (!_modernVault || !id.StartsWith("mvault/", StringComparison.Ordinal) || id.StartsWith("mvault/filter", StringComparison.Ordinal) || delta == 0) return false;
        _modernVaultScroll = Math.Max(0, _modernVaultScroll + (delta < 0 ? 1 : -1));
        Publish(); return true;
    }

    private EnbUiTextInput CreateModernVaultSearch()
    {
        var input = CreateTextInput(new(32), key =>
        {
            if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return Task.FromResult(false);
            // Escape clears the search; Enter and Tab keep it. Either way the keyboard returns to the game.
            if (key == EnbInventoryKeys.Escape && _modernVaultSearch is { } search) search.Text = "";
            if (_modernVaultSearch is not null) _modernVaultSearch.IsVisible = false;
            Publish(); return Task.FromResult(true);
        });
        input.Changed += (_, _) => Publish();
        input.LostFocus += (_, _) => Publish();
        return input;
    }
}
