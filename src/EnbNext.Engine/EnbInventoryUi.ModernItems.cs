using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI item catalogue (retail has none): every item the game knows, in a window
/// the player moves, resizes and closes. Along the top a search (name, type, specials and description), the inventory's
/// own kinds of item and the levels; below on the left the items found, on the right the chosen item as its tooltip
/// card shows it. Nothing is sent to the game server.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private static readonly EnbUiPoint ItemsDefaultSize = new(900, 640), ItemsMinimumSize = new(700, 420);
    private bool _modernItems, _itemsFilterOpen, _itemsByLevel, _itemsDescending;
    private Rect _modernItemsBounds;
    private EnbItemCatalogue? _itemCatalogue;
    private Task<EnbItemCatalogue>? _itemCatalogueLoading;
    private string? _itemCatalogueError;
    private EnbUiTextInput? _itemsSearch;
    /// <summary>The kind of item shown (EnbItemInformation.Filters; 0 = all) and the levels shown (bit n-1 = level n;
    /// none = all).</summary>
    private int _itemsKind, _itemsLevels;
    private int _itemsScroll, _itemsCardScroll, _itemsSelected = -1, _itemsPreview = -1, _itemsShown = -1;
    /// <summary>The items found (indices into the catalogue) and what they were found for.</summary>
    private readonly List<int> _itemsFound = [];
    private string? _itemsFoundFor;
    private readonly EnbModernTooltip _itemsCard = new();
    private string? _itemsWikiName;
    // Next: the equipped items a catalogue item compares with, stacked under its card.
    private readonly EnbModernTooltip[] _itemsCompareCards = [new(), new()];
    internal EnbUiPoint? ModernItemsPosition { get; set; }
    internal Action<EnbUiPoint>? ModernItemsMoved { get; set; }
    /// <summary>The window's size in Modern UI units; null: the default.</summary>
    internal EnbUiPoint? ModernItemsSize { get; set; }
    internal Action<EnbUiPoint>? ModernItemsResized { get; set; }
    public bool ModernItemsOpen => _modernItems;
    public bool ModernItemsSearchEditing => _itemsSearch is { IsVisible: true, IsFocused: true };
    internal EnbUiTextInput? ModernItemsSearchInput => _itemsSearch;
    /// <summary>The catalogue index of the item shown on the right (-1: none); the probe's view.</summary>
    internal int ModernItemsShownIndex => _itemsShown;
    /// <summary>The items listed now, in their order (the probe's view).</summary>
    internal IEnumerable<EnbSceneItemSnapshot> ModernItemsFound => _itemCatalogue is { } c ? _itemsFound.Select(i => c.Items[i]) : [];

    public bool ToggleModernItems()
    {
        if (!_interactive || !ModernUi) return false;
        _modernItems = !_modernItems;
        if (_modernItems) { _itemsFilterOpen = false; LoadItemCatalogue(); Opened?.Invoke(); }
        else CloseModernItems();
        InteractionSoundRequested?.Invoke(_modernItems ? "SR_scifielectronic40.wav" : "SR_scifielectronic42.wav");
        Publish(); return true;
    }

    private void CloseModernItems()
    {
        _modernItems = false; _itemsFilterOpen = false;
        if (_itemsSearch is not null) _itemsSearch.IsVisible = false;
    }

    private void LoadItemCatalogue()
    {
        if (_itemCatalogue is null) _itemCatalogueLoading ??= EnbItemCatalogue.Load();
        TickModernItems();
    }

    /// <summary>Each update: the catalogue is taken once it has been read (it is read in the background).</summary>
    private void TickModernItems()
    {
        if (_modernItems && !ModernUi) CloseModernItems();
        if (_itemCatalogueLoading is not { IsCompleted: true } done) return;
        _itemCatalogueLoading = null;
        if (done.IsCompletedSuccessfully) { _itemCatalogue = done.Result; _itemCatalogueError = null; }
        // Without the launcher's item data there is no catalogue: the window says so.
        else _itemCatalogueError = "The item catalogue could not be read: " + (done.Exception?.InnerException?.Message ?? "the Net-7 launcher data is missing.");
        _itemsFoundFor = null;
        if (_modernItems) Publish();
    }

    private static string ItemsLevelsKey(int levels) => levels.ToString(CultureInfo.InvariantCulture);

    /// <summary>The items for the search, the kind and the levels, in the chosen order; found again only when one changes.</summary>
    private void FindItems()
    {
        if (_itemCatalogue is not { } catalogue) { _itemsFound.Clear(); return; }
        string query = (_itemsSearch?.Text ?? "").Trim().ToLowerInvariant();
        string key = query + "\u0001" + _itemsKind.ToString(CultureInfo.InvariantCulture) + "\u0001" + ItemsLevelsKey(_itemsLevels) + "\u0001" + (_itemsByLevel ? "L" : "N") + (_itemsDescending ? "-" : "+");
        if (key == _itemsFoundFor) return;
        _itemsFoundFor = key; _itemsFound.Clear();
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < catalogue.Items.Length; i++)
        {
            var item = catalogue.Items[i];
            if (_itemsKind != 0 && !EnbItemInformation.Matches(item, _itemsKind)) continue;
            if (_itemsLevels != 0 && (item.Level is < 1 or > 9 || (_itemsLevels & (1 << (item.Level - 1))) == 0)) continue;
            bool all = true;
            foreach (string word in words) if (!catalogue.SearchText[i].Contains(word, StringComparison.Ordinal)) { all = false; break; }
            if (all) _itemsFound.Add(i);
        }
        if (_itemsByLevel) _itemsFound.Sort((a, b) => catalogue.Items[a].Level.CompareTo(catalogue.Items[b].Level) is var n and not 0 ? n : a.CompareTo(b));
        if (_itemsDescending) _itemsFound.Reverse();
    }

    private void ModernItemsWindow()
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 6 * u, pad = 10 * u, head = 24 * u;
        double rowHeight = Math.Round(26 * u);
        var size = ModernSized(ModernItemsSize, ItemsDefaultSize, ItemsMinimumSize);
        double width = Math.Round(size.X * u), height = Math.Round(size.Y * u), x, y;
        if (ModernItemsPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernItemsBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernItemsBounds, "mitems/frame");
        _tips.Remove("mitems/frame");
        Flush();
        double cx = x + pad, cy = y + pad, contentWidth = width - pad * 2;
        var dim = new EnbRgb(.65f, .7f, .85f);
        ModernText("mitems/title", "Item catalogue", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mitems/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;

        // The search and the kind of item.
        double kindWidth = Math.Ceiling(EnbItemInformation.Filters.Max(f => ModernMeasure(f, text)) + 40 * u);
        double searchWidth = contentWidth - kindWidth - gap;
        var searchRect = new Rect(cx, cy, searchWidth, head);
        ModernFrame("DIALOG_BOX_INFO", searchRect, "mitems/search");
        bool editing = ModernItemsSearchEditing;
        string search = _itemsSearch?.Text ?? "";
        var searchText = new Rect(cx + 8 * u, cy, searchWidth - 16 * u, head);
        if (search.Length == 0 && !editing) ModernText("mitems/searchText", "Search", searchText, new(.55f, .6f, .7f), text);
        else ModernText("mitems/searchText", editing ? search.Insert(Math.Clamp(_itemsSearch!.CaretIndex, 0, search.Length), "|") : search, searchText, new(1, 1, 1), text);
        if (_itemsSearch is not null) _itemsSearch.Bounds = searchRect;
        _tips["mitems/search"] = "Find items by name, type, special or description\n(every word must be found)";
        var kindRect = new Rect(cx + searchWidth + gap, cy, kindWidth, head);
        ModernButton("mitems/kind", EnbItemInformation.Filters[_itemsKind], kindRect, text, "Choose the kind of item to show");
        cy += head + gap;

        // The levels: none chosen shows every level.
        double chip = Math.Round(28 * u);
        ModernText("mitems/levelsLabel", "Level", new(cx, cy, 60 * u, head), dim, text);
        for (int level = 1; level <= 9; level++)
        {
            string id = "mitems/level/" + level.ToString(CultureInfo.InvariantCulture);
            bool on = (_itemsLevels & (1 << (level - 1))) != 0, hot = _hover == id;
            var r = new Rect(Snap(cx + 60 * u + (level - 1) * (chip + 4 * u)), cy, chip, head);
            Hit(id, FromPixels(r));
            if (on) _images.Add(new(EnbModernIcons.Pill((int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale), new(.35f, .55f, .95f), 200), r));
            else if (hot) _images.Add(new(EnbModernIcons.Pill((int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale), new(.35f, .45f, .7f), 110), r));
            ModernText(id + "/text", level.ToString(CultureInfo.InvariantCulture), r, on || hot ? new(1, 1, .6f) : new(.92f, .94f, 1), text, 1);
            _tips[id] = (on ? "Hide" : "Show") + " level " + level.ToString(CultureInfo.InvariantCulture) + " items\n(with no level chosen, every level shows)";
        }
        FindItems();
        var catalogue = _itemCatalogue;
        string count = catalogue is null ? "" : _itemsFound.Count.ToString("#,0", CultureInfo.InvariantCulture) + " of " + catalogue.Items.Length.ToString("#,0", CultureInfo.InvariantCulture) + " items";
        ModernText("mitems/count", count, new(cx, cy, contentWidth, head), dim, small, 2);
        cy += head + gap;

        // Below: the items found on the left, the chosen item on the right.
        double cardWidth = Math.Round(360 * u), detailsWidth = cardWidth + ModernScrollbarRoom + 4 * u;
        double listWidth = contentWidth - detailsWidth - gap;
        double bottom = y + height - pad - 14 * u;
        double listTop = cy;
        // The list's headings: a click sorts by it (again: the other way round).
        double levelWidth = Math.Round(54 * u), typeWidth = Math.Round(150 * u), icon = Math.Round(22 * u);
        double nameX = cx + 8 * u + icon + 6 * u, nameWidth = listWidth - ModernScrollbarRoom - (nameX - cx) - typeWidth - levelWidth - 8 * u;
        string order = _itemsDescending ? "\nSorted the other way round. Click to sort it back." : "\nClick again to sort the other way round.";
        ModernButtonText("mitems/sortName", "Name", new(nameX, cy, nameWidth, head), !_itemsByLevel, small, 0, _itemsByLevel ? "Sort by name" : "Sorted by name" + order);
        ModernButtonText("mitems/sortLevel", "Level", new(cx + listWidth - ModernScrollbarRoom - levelWidth - 4 * u, cy, levelWidth, head), _itemsByLevel, small, 2, _itemsByLevel ? "Sorted by level" + order : "Sort by level");
        ModernText("mitems/typeHead", "Type", new(nameX + nameWidth, cy, typeWidth, head), dim, small);
        cy += head;
        var list = new Rect(cx, cy, listWidth, Math.Max(rowHeight, bottom - cy));
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        Hit("mitems/list", FromPixels(list));
        int visible = Math.Max(1, (int)(list.Height / rowHeight));
        _itemsScroll = Math.Clamp(_itemsScroll, 0, Math.Max(0, _itemsFound.Count - visible));
        if (catalogue is null)
            ModernText("mitems/none", _itemCatalogueError ?? "Reading the item catalogue...", new(cx + 8 * u, cy, listWidth - 16 * u, rowHeight), _itemCatalogueError is null ? dim : new(1, .45f, .45f), text);
        else if (_itemsFound.Count == 0) ModernText("mitems/none", "No items found.", new(cx + 8 * u, cy, listWidth - 16 * u, rowHeight), dim, text);
        for (int n = 0; catalogue is not null && n < visible && _itemsScroll + n < _itemsFound.Count; n++)
        {
            var item = catalogue.Items[_itemsFound[_itemsScroll + n]];
            int index = _itemsFound[_itemsScroll + n];
            string id = "mitems/row/" + index.ToString(CultureInfo.InvariantCulture);
            var row = new Rect(cx, Snap(cy + n * rowHeight), listWidth - ModernScrollbarRoom, rowHeight);
            Hit(id, FromPixels(row));
            bool chosen = index == _itemsSelected;
            if (chosen) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            if (Icon(item.IconBaseAsset) is { } picture) _images.Add(new(picture, new(Snap(row.X + 8 * u), Snap(row.Center.Y - icon / 2), icon, icon)));
            ModernText(id + "/name", ModernEllipsis(item.Name, nameWidth - 4 * u, text), new(nameX, row.Y, nameWidth, row.Height), chosen ? new(1, 1, .6f) : new(.92f, .94f, 1), text);
            ModernText(id + "/type", ModernEllipsis(EnbItemInformation.TypeName(item.Subcategory), typeWidth - 4 * u, small), new(nameX + nameWidth, row.Y, typeWidth, row.Height), dim, small);
            ModernText(id + "/level", item.Level > 0 ? item.Level.ToString(CultureInfo.InvariantCulture) : "", new(row.Right - levelWidth - 4 * u, row.Y, levelWidth, row.Height), new(.92f, .94f, 1), text, 2);
            // No tooltip: hovering the row shows the item on the right instead.
            _tips.Remove(id);
        }
        ModernScrollbar("mitems/list", list, _itemsFound.Count, visible, _itemsScroll, v => _itemsScroll = v);

        // The item under the pointer (while the pointer stays in the catalogue), else the one clicked (it stays), as its
        // tooltip card shows it (scrolls with the wheel when taller than the pane).
        if (_hover is { } hovered && hovered.StartsWith("mitems/row/", StringComparison.Ordinal) &&
            int.TryParse(hovered.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int hoveredIndex)) _itemsPreview = hoveredIndex;
        else if (_hover is null || !_hover.StartsWith("mitems/", StringComparison.Ordinal)) _itemsPreview = -1;
        int shown = _itemsPreview >= 0 ? _itemsPreview : _itemsSelected;
        if (shown != _itemsShown) { _itemsShown = shown; _itemsCardScroll = 0; }
        var pane = new Rect(cx + listWidth + gap, listTop, detailsWidth, bottom - listTop);
        // The clicked item's Net-7 Wiki page (drops, build recipes), from a button under the card.
        _itemsWikiName = catalogue is not null && _itemsSelected >= 0 && _itemsSelected < catalogue.Items.Length ? catalogue.Items[_itemsSelected].Name?.Trim() : null;
        if (_itemsWikiName is { Length: > 0 } wikiName)
        {
            double wikiHeight = Math.Round(24 * u);
            ModernButton("mwiki/catalogue", "Look up on wiki", new(pane.X, pane.Bottom - wikiHeight, cardWidth, wikiHeight), text, "Open the Net-7 Wiki page of " + wikiName + ".");
            pane = pane with { Height = pane.Height - wikiHeight - gap };
        }
        Hit("mitems/card", FromPixels(pane));
        var shownCard = catalogue is not null && shown >= 0 && shown < catalogue.Items.Length ? ItemCard(catalogue.Items[shown]) : null;
        if (shownCard is not null && _itemsCard.CardLayout(shownCard, _size, _scale) is { } card)
        {
            double room = pane.Height;
            int extra = (int)Math.Ceiling(Math.Max(0, card.Height - room) / Math.Round(18 * u));
            _itemsCardScroll = Math.Clamp(_itemsCardScroll, 0, extra);
            double top = pane.Y - _itemsCardScroll * Math.Round(18 * u);
            var clip = new Rect(pane.X, pane.Y, cardWidth, room);
            if (card.Background is { } panel) _images.Add(new(panel, new(pane.X, Snap(top), card.Width, card.Height)) { Clip = clip });
            foreach (var line in card.Lines)
                _images.Add(line with { Bounds = new(line.Bounds.X + pane.X, line.Bounds.Y + Snap(top), line.Bounds.Width, line.Bounds.Height), Clip = clip });
            // Next: the equipped items it compares with stand beside the catalogue, as a hover's comparison does.
            var compared = new List<(ImmutableArray<EnbUiTextDraw> Lines, EnbUiRaster? Background, double Width, double Height)>();
            for (int i = 0; i < Math.Min(_itemsCompareCards.Length, shownCard.Compare.Length); i++)
                if (_itemsCompareCards[i].CardLayout(shownCard.Compare[i], _size, _scale) is { } other) compared.Add(other);
            if (compared.Count > 0)
            {
                double side = Math.Round(6 * u), besideWidth = compared.Sum(c => c.Width) + side * compared.Count;
                var window = _modernItemsBounds;
                double sx = window.Right + besideWidth <= _size.Width ? window.Right + side : Math.Max(0, window.X - besideWidth);
                foreach (var part in compared)
                {
                    double sy = Math.Clamp(pane.Y, 0, Math.Max(0, _size.Height - part.Height));
                    if (part.Background is { } other) _images.Add(new(other, new(Snap(sx), Snap(sy), part.Width, part.Height)));
                    foreach (var line in part.Lines)
                        _images.Add(line with { Bounds = new(line.Bounds.X + Snap(sx), line.Bounds.Y + Snap(sy), line.Bounds.Width, line.Bounds.Height) });
                    sx += part.Width + side;
                }
            }
            if (extra > 0)
                ModernScrollbar("mitems/card", new(pane.X, pane.Y, cardWidth + ModernScrollbarRoom, room), extra + 1, 1, _itemsCardScroll, v => _itemsCardScroll = v);
        }
        else
        {
            ModernFrame("DIALOG_BOX_INFO", pane);
            Flush();
            if (catalogue is not null) ModernText("mitems/pick", "Hover or click any item.", new(pane.X + 12 * u, pane.Y + 12 * u, pane.Width - 24 * u, head), dim, text);
        }
        ModernResizeGrip("mitems/resize", _modernItemsBounds);
        Flush();

        // The kinds of item, over the rest.
        if (_itemsFilterOpen)
        {
            double optionHeight = 22 * u;
            var names = EnbItemInformation.Filters;
            var drop = new Rect(kindRect.Right - kindWidth, kindRect.Bottom + 2 * u, kindWidth, names.Length * optionHeight + 4 * u);
            ModernFrame("DIALOG_BOX_WINDOW", drop, "mitems/kindFrame");
            for (int i = 0; i < names.Length; i++)
            {
                string id = "mitems/kind/" + i.ToString(CultureInfo.InvariantCulture);
                var r = new Rect(drop.X + 2 * u, drop.Y + 2 * u + i * optionHeight, drop.Width - 4 * u, optionHeight);
                Hit(id, FromPixels(r));
                if (_hover == id) ModernFrame("DIALOG_BOX_OPTION", r);
                ModernText(id + "/text", names[i], new(r.X + 10 * u, r.Y, r.Width - 12 * u, r.Height), _hover == id || i == _itemsKind ? new(1, 1, .3f) : new(1, 1, 1), text);
            }
            Flush();
        }
    }

    /// <summary>A column heading that sorts: bright when it is the order now.</summary>
    private void ModernButtonText(string id, string label, Rect area, bool active, double size, int align, string tip)
    {
        Hit(id, FromPixels(area));
        bool hot = _hover == id;
        ModernText(id + "/text", label, area, hot ? new(1, 1, .45f) : active ? new(.9f, .93f, 1) : new(.65f, .7f, .85f), size, align);
        _tips[id] = tip;
    }

    private bool ActivateModernItems(string id, bool right)
    {
        if (!id.StartsWith("mitems/", StringComparison.Ordinal)) return false;
        if (id != "mitems/search" && _itemsSearch is { IsFocused: true }) _itemsSearch.IsVisible = false;
        if (right) return true;
        if (id != "mitems/kind" && !id.StartsWith("mitems/kind/", StringComparison.Ordinal)) _itemsFilterOpen = false;
        switch (id)
        {
            case "mitems/close": CloseModernItems(); InteractionSoundRequested?.Invoke("TextClick01.wav"); return true;
            case "mitems/search":
                _itemsSearch ??= CreateItemsSearch();
                _itemsSearch.IsVisible = true; _itemsSearch.Focus(); return true;
            case "mitems/kind": _itemsFilterOpen = !_itemsFilterOpen; InteractionSoundRequested?.Invoke("TextClick03.wav"); return true;
            case "mitems/sortName" or "mitems/sortLevel":
                bool byLevel = id == "mitems/sortLevel";
                if (_itemsByLevel == byLevel) _itemsDescending = !_itemsDescending; else { _itemsByLevel = byLevel; _itemsDescending = false; }
                _itemsScroll = 0; InteractionSoundRequested?.Invoke("TextClick03.wav"); return true;
        }
        if (id.StartsWith("mitems/kind/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int kind))
        {
            _itemsKind = Math.Clamp(kind, 0, EnbItemInformation.Filters.Length - 1); _itemsFilterOpen = false; _itemsScroll = 0;
            InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
        else if (id.StartsWith("mitems/level/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(13), NumberStyles.None, CultureInfo.InvariantCulture, out int level) && level is >= 1 and <= 9)
        {
            _itemsLevels ^= 1 << (level - 1); _itemsScroll = 0;
            InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
        else if (id.StartsWith("mitems/row/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            _itemsSelected = index; _itemsCardScroll = 0;
            InteractionSoundRequested?.Invoke("fontclick.wav");
        }
        return true;
    }

    private EnbUiTextInput CreateItemsSearch()
    {
        var input = CreateTextInput(new(40), key =>
        {
            if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return Task.FromResult(false);
            // Escape clears the text; Enter and Tab keep it. Either way the keyboard returns to the game.
            if (key == EnbInventoryKeys.Escape && _itemsSearch is { } search) search.Text = "";
            if (_itemsSearch is not null) _itemsSearch.IsVisible = false;
            Publish(); return Task.FromResult(true);
        });
        input.Changed += (_, _) => { _itemsScroll = 0; Publish(); };
        input.LostFocus += (_, _) => Publish();
        return input;
    }

    /// <summary>The wheel over the list scrolls it; over the chosen item, its card.</summary>
    internal bool ScrollModernItems(string id, double delta)
    {
        if (!_modernItems || !id.StartsWith("mitems/", StringComparison.Ordinal) || id.StartsWith("mitems/kind", StringComparison.Ordinal) || delta == 0) return false;
        if (id.StartsWith("mitems/card", StringComparison.Ordinal)) _itemsCardScroll = Math.Max(0, _itemsCardScroll + (delta < 0 ? 3 : -3));
        else _itemsScroll = Math.Max(0, _itemsScroll + (delta < 0 ? 3 : -3));
        Publish(); return true;
    }
}
