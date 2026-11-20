using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI vendor window: the retail CE02A trade content (the vendor's wares by category or Compare, sorted by
/// name or price, the owned count, your credits, Buy / Buy stack / Sell / Sell stack and their confirmations) in a
/// Modern UI window, with the Modern inventory open beside it to sell from. Every control sends the same retail
/// requests as VendorPanel (ActivateConversation); the list shows every ware in one scrolling list instead of five per
/// page. Retail mode keeps the vendor window.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>Where the vendor window was dragged (screen fraction); null keeps it left of the screen centre.</summary>
    internal EnbUiPoint? ModernVendorPosition { get; set; }
    internal Action<EnbUiPoint>? ModernVendorMoved { get; set; }
    private Rect _modernVendorBounds;
    private int _modernVendorScroll;
    private const int VendorRows = 8;

    /// <summary>Trade opens: the Modern inventory comes up beside the vendor (and closes with it if the trade opened it).</summary>
    private void OpenVendorCargo()
    {
        if (ModernUi) { OpenTradeCargo(); _modernVendorScroll = 0; }
        else _cargo = true;
    }
    private void CloseVendorCargo()
    {
        if (ModernUi) CloseTradeCargo(); else _cargo = false;
    }

    private void ModernVendorPanel()
    {
        Flush();
        uint mask = EnbVendorList.CategoryMask(Data?.Vendor ?? []);
        if (mask != _vendorCategoryMask)
        {
            _vendorCategoryMask = mask; _vendorCategories = EnbVendorList.Categories(mask);
            _vendorCategory = Math.Clamp(_vendorCategory, 0, Math.Max(0, _vendorCategories.Length - 1));
            _tradeSelection = null;
        }
        var wares = Data is { } inventory ? EnbVendorList.Rows(inventory, _vendorCategories.IsEmpty ? null : _vendorCategories[_vendorCategory], _vendorCompare, _vendorSort) : [];
        bool enabled = _tradeConfirmation is null && !_busy;

        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, button = 28 * u;
        double rowHeight = Math.Round(46 * u), icon = rowHeight - Math.Round(6 * u);
        double content = Math.Round(560 * u);
        double priceWidth = Math.Round(Math.Max(ModernMeasure("999,999,999 each", text), ModernMeasure("Price", text)) + 16 * u);
        double height = pad + head + gap * 2 + head + gap + head + gap + VendorRows * rowHeight + gap * 2 + head + gap + button + (_message is null ? 0 : gap + head) + pad;
        double width = content + pad * 2;
        double x = _size.Width * .5 - width - 40 * u, y = _size.Height * .14;
        if (ModernVendorPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernVendorBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernVendorBounds, "vendorFrame/modern");
        _tips.Remove("vendorFrame/modern");

        double cx = x + pad, cy = y + pad;
        string heading = _conversationName.Length > 0 ? "Trade: " + _conversationName : "Trade";
        ModernText("vendor/title", ModernEllipsis(heading, content - head - gap, title), new(cx, cy, content - head - gap, head), new(1, .85f, .3f), title);
        ModernCloseButton("vendorClose", new(x + width - pad - head, cy, head, head));
        _tips["vendorClose"] = "Close the trade";
        cy += head + gap * 2;

        // The kinds of ware the vendor stocks (retail: up to six), then Compare (wares like those you own).
        double tx = cx;
        var tabs = new List<(string Id, string Label, bool On, string Tip)>();
        for (int i = 0; i < _vendorCategories.Length; i++)
            tabs.Add(("vendorCategory/" + i.ToString(CultureInfo.InvariantCulture), EnbVendorList.CategoryNames[_vendorCategories[i]], !_vendorCompare && _vendorCategory == i, "Show this kind of ware"));
        tabs.Add(("vendorCompare", "Compare", _vendorCompare, _vendorTooltips?.GetValueOrDefault("COMPARE") is { Length: > 0 } compareTip ? compareTip : "Show wares like the ones you own"));
        foreach (var (id, label, on, tip) in tabs)
        {
            double w = Math.Round(ModernMeasure(label, text) + 20 * u);
            var tab = new Rect(Snap(tx), cy, w, head);
            Hit(id, FromPixels(tab));
            if (on) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 60), tab));
            else if (_hover == id && enabled) _images.Add(new(EnbModernIcons.Fill(new(.6f, .75f, 1), 30), tab));
            if (on) _images.Add(new(EnbModernIcons.Fill(new(1, .85f, .3f), 220), new(tab.X, tab.Bottom - Math.Max(1, Math.Round(2 * u)), tab.Width, Math.Max(1, Math.Round(2 * u)))));
            ModernText(id + "/text", label, tab, on || _hover == id ? new(1, .85f, .3f) : new(.8f, .84f, .92f), text, 1);
            _tips[id] = tip;
            tx += w + gap;
        }
        cy += head + gap;

        // Column headings: Name and Price sort the list (the retail sort, kept per character).
        double nameX = cx + icon + 10 * u, listWidth = content - ModernScrollbarRoom;
        double nameWidth = listWidth - (nameX - cx) - priceWidth;
        foreach (var (id, label, area, byName) in new[]
        {
            ("vendorSortName", "Name", new Rect(nameX, cy, nameWidth, head), true),
            ("vendorSortPrice", "Price", new Rect(cx + listWidth - priceWidth, cy, priceWidth, head), false),
        })
        {
            Hit(id, FromPixels(area));
            bool sorted = _vendorSort == byName, hot = _hover == id;
            ModernText(id + "/text", label, new(area.X, area.Y, area.Width - 8 * u, area.Height), hot ? new(1, 1, .45f) : sorted ? new(1, .85f, .3f) : new(.7f, .75f, .85f), text, byName ? 0 : 2);
            _tips[id] = sorted ? (byName ? "Sorted by name" : "Sorted by price, lowest first") : "Sort by " + label.ToLowerInvariant();
        }
        cy += head + gap;
        Flush();

        var list = new Rect(cx, cy, content, VendorRows * rowHeight);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        Hit("vendor/list", FromPixels(list));
        _modernVendorScroll = Math.Clamp(_modernVendorScroll, 0, Math.Max(0, wares.Length - VendorRows));
        if (wares.Length == 0)
            ModernText("vendor/none", _vendorCompare ? "Nothing here is like what you own." : "Nothing for sale here.", new(cx + 10 * u, cy, content - 20 * u, rowHeight), new(.75f, .8f, .92f), text);
        for (int n = 0; n < VendorRows && _modernVendorScroll + n < wares.Length; n++)
        {
            var item = wares[_modernVendorScroll + n];
            string id = "vendorSelect/" + item.Slot.ToString(CultureInfo.InvariantCulture);
            var row = new Rect(cx, Snap(cy + n * rowHeight), listWidth, rowHeight);
            Hit(id, FromPixels(row));
            bool selected = _tradeSelection is { Inventory: 4 } chosen && chosen.Slot == item.Slot;
            if ((_modernVendorScroll + n) % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 60), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (_hover?.StartsWith(id, StringComparison.Ordinal) == true && (_hover.Length == id.Length || _hover[id.Length] == '/'))
                _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            Flush();
            Slot(id + "/icon", FromPixels(Snap(cx + 4 * u), Snap(row.Y + (rowHeight - icon) / 2), icon, icon), item with { Count = 1 }, compact: true);
            Flush();
            string owned = Data!.OwnedText(item.TemplateId), stack = EnbVendorList.StackText(item.MaxStack);
            string detail = owned.Length > 0 && stack.Length > 0 ? owned + ", " + stack : owned + stack;
            double nameY = detail.Length > 0 ? row.Y + 2 * u : row.Y;
            ModernText(id + "/name", ModernEllipsis(item.Name, nameWidth - 8 * u, text), new(nameX, nameY, nameWidth - 8 * u, detail.Length > 0 ? rowHeight / 2 : rowHeight), selected ? new(1, .85f, .3f) : new(1, 1, 1), text);
            if (detail.Length > 0)
                ModernText(id + "/detail", ModernEllipsis(detail, nameWidth - 8 * u, small), new(nameX, row.Y + rowHeight / 2, nameWidth - 8 * u, rowHeight / 2 - 2 * u), new(.65f, .7f, .82f), small);
            ModernText(id + "/price", item.Price.GetValueOrDefault().ToString("N0", CultureInfo.InvariantCulture) + " each",
                new(cx + listWidth - priceWidth, row.Y, priceWidth - 8 * u, rowHeight), new(.85f, .88f, .95f), text, 2);
            _tips[id] = item.Name + "\nClick to choose it, then Buy\nControl-click buys one, Shift-click a stack";
        }
        ModernScrollbar("vendor/list", list, wares.Length, VendorRows, _modernVendorScroll, v => _modernVendorScroll = v);
        cy += VendorRows * rowHeight + gap * 2;

        // What is chosen and what it costs or fetches; your credits.
        string? choice = null;
        if (_tradeSelection is { } pick && Resolve(pick) is { } picked)
            choice = (pick.Inventory == 4 ? "Buy " : "Sell ") + picked.Name + (picked.Price is { } p ? ": " + p.ToString("N0", CultureInfo.InvariantCulture) + " each" : "");
        ModernText("vendor/choice", ModernEllipsis(choice ?? "Choose a ware to buy, or an item in your inventory to sell.", content, text),
            new(cx, cy, content, head), choice is null ? new(.65f, .7f, .82f) : new(1, 1, 1), text);
        cy += head + gap;
        ModernText("vendorCredits", "Credits: " + (Data?.Credits ?? 0).ToString("N0", CultureInfo.InvariantCulture), new(cx, cy, content / 2, button), new(1, .85f, .3f), text);

        double bx = x + width - pad;
        var buttons = new List<(string Id, string Label, string Tip)>();
        if (_tradeSelection?.Inventory == 4) { buttons.Add(("vendorBuyStack", "Buy stack", "Buy a full stack")); buttons.Add(("vendorBuy", "Buy", "Buy one")); }
        if (_tradeSelection is { Inventory: 1 } sell && Resolve(sell) is { } selling)
        {
            if (selling.MaxStack > 1 && selling.Count > 1) buttons.Add(("vendorSellStack", "Sell stack", "Sell the whole stack"));
            buttons.Add(("vendorSell", "Sell", "Sell one"));
        }
        foreach (var (id, label, tip) in buttons)
        {
            double bw = Math.Round(Math.Max(96 * u, ModernMeasure(label, text) + 28 * u));
            bx -= bw; ModernButton(id, label, new(bx, cy, bw, button), text, tip, enabled); bx -= gap * 2;
        }
        cy += button;
        if (_message is not null)
            ModernText("vendorError", ModernEllipsis(_message, content, small), new(cx, cy + gap, content, head), new(1, .5f, .5f), small);
        Flush();
        if (_tradeConfirmation is { } confirm) ModernVendorConfirmation(confirm);
    }

    /// <summary>The retail confirmations (purchase, sale, loss warnings) as a Modern UI dialog over the vendor window.</summary>
    private void ModernVendorConfirmation(EnbStationPurchaseRequest confirm)
    {
        var item = Resolve(confirm.Item);
        string prompt = _tradeConfirmationStage is EnbVendorConfirmationStage.SellOneLoss or EnbVendorConfirmationStage.SellStackLoss
            ? (item is not null && EnbVendorConfirmation.Loss(item.ItemType, confirm.Price, item.AverageCost) == EnbVendorLoss.Significant ? "You will take significant losses on this sale. Do you wish to proceed?" : "You will take a loss on this sale. Do you wish to proceed?")
            : confirm.Item.Inventory == 4 && item is not null
                ? EnbVendorConfirmation.PurchasePrompt(confirm.Price, item.MaxStack ?? 1, item.Name)
                : EnbVendorConfirmation.SalePrompt(confirm.Price, confirm.Count);
        double u = ModernUnit, text = 14 * u, gap = 4 * u, pad = 14 * u, line = Math.Round(20 * u), button = 28 * u;
        double width = Math.Round(420 * u), inner = width - pad * 2;
        var lines = ModernWrap(prompt, inner, text);
        double height = pad + lines.Count * line + gap * 4 + button + pad;
        var w = _modernVendorBounds;
        double x = Snap(w.X + (w.Width - width) / 2), y = Snap(w.Y + (w.Height - height) / 2);
        Hit("tradeConfirmModal", FromPixels(w));
        ModernFrame("DIALOG_BOX_WINDOW", new(x, y, width, height), "tradeConfirm/frame");
        _tips.Remove("tradeConfirm/frame");
        for (int i = 0; i < lines.Count; i++)
            ModernText("tradeConfirm/line" + i.ToString(CultureInfo.InvariantCulture), lines[i], new(x + pad, y + pad + i * line, inner, line), new(1, 1, 1), text, 1);
        double by = y + pad + lines.Count * line + gap * 4, bw = Math.Round(Math.Max(96 * u, ModernMeasure("Yes", text) + 28 * u));
        ModernButton("tradeYes", "Yes", new(x + width / 2 - gap - bw, by, bw, button), text, "Go ahead");
        ModernButton("tradeNo", "No", new(x + width / 2 + gap, by, bw, button), text, "Cancel");
        Flush();
    }

    /// <summary>The Modern vendor's own controls: sorting by column, and the wheel over the list.</summary>
    private bool ActivateModernVendor(string id)
    {
        if (!ModernUi || !_trading || _tradeConfirmation is not null || _busy) return false;
        if (id is not ("vendorSortName" or "vendorSortPrice")) return false;
        // The retail sort is a name/price switch: a heading that is not the sorted one switches it.
        if (_vendorSort != (id == "vendorSortName")) { _ = ActivateConversation("vendorSort", null); }
        _modernVendorScroll = 0;
        return true;
    }
    internal bool ScrollModernVendor(string id, double delta)
    {
        if (!ModernUi || !_trading || delta == 0 || !(id.StartsWith("vendorSelect/", StringComparison.Ordinal) || id == "vendor/list")) return false;
        _modernVendorScroll = Math.Max(0, _modernVendorScroll + (delta < 0 ? 3 : -3));
        Publish(); return true;
    }
}
