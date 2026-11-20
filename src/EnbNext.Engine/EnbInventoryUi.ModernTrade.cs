using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI player trade: the retail TRADE00A content (your six offered items and credits, the partner's six
/// and credits, the trade status, Cancel and Confirm) in a Modern UI window, with the Modern inventory open beside it.
/// Slots, drop targets, the credit entry and every button send the same retail trade requests as PlayerTradePanel;
/// only the look differs. Retail mode keeps the trade window.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>Where the trade window was dragged (screen fraction); null keeps it centred in the upper half.</summary>
    internal EnbUiPoint? ModernTradePosition { get; set; }
    internal Action<EnbUiPoint>? ModernTradeMoved { get; set; }
    private Rect _modernTradeBounds;
    /// <summary>The trade opened the Modern inventory (so it closes it again when the trade ends).</summary>
    private bool _tradeOpenedModernInventory;

    private void OpenTradeCargo()
    {
        if (ModernUi)
        {
            _tradeOpenedCargo = false;
            _tradeOpenedModernInventory = !_modernInventory;
            _modernInventory = true; RaiseModern("minv");
        }
        else { _tradeOpenedModernInventory = false; _tradeOpenedCargo = !_cargo; _cargo = true; }
    }
    private void CloseTradeCargo()
    {
        if (_tradeOpenedCargo) _cargo = false;
        if (_tradeOpenedModernInventory) CloseModernInventory();
        _tradeOpenedCargo = _tradeOpenedModernInventory = false;
    }

    private void ModernPlayerTradePanel()
    {
        if (_playerTrade is not { } trade) return;
        Flush();
        double u = ModernUnit, text = 14 * u, small = 13 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, row = 24 * u, button = 28 * u;
        double slot = ModernInventoryMetrics().Slot, columnGap = 18 * u;
        double column = 3 * slot + 2 * gap;
        bool active = trade.PartnerId is not null && !_creditEntry;
        bool enabled = active && !_busy;
        string partner = _scene?.Objects.FirstOrDefault(o => o.GameId == _tradeVisualPartner)?.Name ?? "";
        string offered = "Credits: " + trade.OfferedCredits.ToString("N0", CultureInfo.InvariantCulture);
        string received = "Credits: " + trade.ReceivedCredits.ToString("N0", CultureInfo.InvariantCulture);
        // Wide enough for the widest credit line and both buttons.
        column = Math.Max(column, Math.Max(ModernMeasure("Credits: 4,294,967,295", text) + 28 * u, ModernMeasure("Credits: 9,999,999,999", text)));
        double content = column * 2 + columnGap;
        double buttonWidth = Math.Round(Math.Max(96 * u, ModernMeasure("Confirm", text) + 28 * u));
        double width = content + pad * 2;
        double height = pad + row + gap * 2 + row + 2 * slot + gap + gap * 2 + button + gap + row + gap + row + gap + button + (_message is null ? 0 : gap + row) + pad;
        double x = (_size.Width - width) / 2, y = Math.Max(0, _size.Height * .18);
        if (ModernTradePosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernTradeBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernTradeBounds, "playerTradeFrame/modern");
        _tips.Remove("playerTradeFrame/modern");

        double cx = x + pad, cy = y + pad;
        string heading = partner.Length > 0 ? "Trade with " + partner : "Trade";
        ModernText("playerTrade/title", ModernEllipsis(heading, content - row - gap, title), new(cx, cy, content - row - gap, row), new(1, .85f, .3f), title);
        if (enabled) ModernCloseButton("playerTradeClose", new(x + width - pad - row, cy, row, row));
        _tips["playerTradeClose"] = "Cancel the trade";
        cy += row + gap * 2;

        // Two columns: what you give (drop items here) and what the partner gives.
        double rightX = cx + column + columnGap;
        ModernText("playerTrade/give", "You give", new(cx, cy, column, row), new(.6f, .8f, 1), text);
        ModernText("playerTrade/receive", ModernEllipsis(partner.Length > 0 ? partner + " gives" : "They give", column, text), new(rightX, cy, column, row), new(.6f, .8f, 1), text);
        cy += row;
        Flush();
        for (int i = 0; i < 12; i++)
        {
            bool give = i < 6;
            int index = i % 6;
            string id = "playerTradeSlot/" + i.ToString(CultureInfo.InvariantCulture);
            double left = give ? cx : rightX;
            var slotRect = FromPixels(Snap(left + index % 3 * (slot + gap)), Snap(cy + index / 3 * (slot + gap)), slot, slot);
            var item = active ? (give ? trade.Give : trade.Receive).FirstOrDefault(s => s.Slot == index) : null;
            Slot(id, slotRect, item, enabled: active, compact: true);
            ModernSlotHighlight(id, slotRect);
            if (give && enabled) _dropSlots[id] = (16, index);
            if (item is null) _tips[id] = give ? "Drag an item from your inventory here to offer it" : "";
        }
        Flush();
        cy += 2 * slot + gap + gap * 2;

        // Credits: yours is a button that opens the credit entry; theirs is shown.
        ModernButton("playerTradeMoney", offered, new(cx, cy, column, button), text, "Change the credits you offer", enabled);
        var theirs = new Rect(rightX, cy, column, button);
        ModernFrame("DIALOG_BOX_INFO", theirs);
        ModernText("playerTrade/received", received, theirs, new(1, 1, 1), text, 1);
        cy += button + gap;
        ModernText("playerTrade/have", "You have " + (Data?.Credits ?? 0).ToString("N0", CultureInfo.InvariantCulture) + " credits",
            new(cx, cy, column, row), new(.7f, .75f, .85f), small);
        cy += row + gap;

        // The trade status (waiting, confirmed, completed).
        EnbRgb statusColor = trade.StatusMessage switch
        {
            EnbNext.Net7.Gameplay.Net7TradeStatusMessage.Completed or EnbNext.Net7.Gameplay.Net7TradeStatusMessage.Confirmed => new(.45f, 1, .45f),
            _ => new(1, .85f, .3f),
        };
        ModernText("playerTrade/status", trade.StatusText, new(cx, cy, content, row), statusColor, text, 1);
        cy += row + gap;

        double bx = x + width - pad;
        foreach (var (id, label, tip) in new[] { ("playerTradeConfirm", "Confirm", "Confirm the trade"), ("playerTradeCancel", "Cancel", "Cancel the trade") })
        {
            bx -= buttonWidth; ModernButton(id, label, new(bx, cy, buttonWidth, button), text, tip, enabled); bx -= gap * 2;
        }
        if (_message is not null)
            ModernText("playerTrade/error", ModernEllipsis(_message, content, small), new(cx, cy + button + gap, content, row), new(1, .5f, .5f), small);
        if (trade.Successful == true) _animatingText = true;
        Flush();
    }

    /// <summary>The Modern credit entry: the amount being typed (digits, as the retail entry) with Cancel and Offer.</summary>
    private void ModernTradeCreditPanel()
    {
        Flush(); Hit("tradeCreditModal", new(0, 0, 640, 480));
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, row = 24 * u, button = 28 * u;
        double buttonWidth = Math.Round(Math.Max(96 * u, ModernMeasure("Cancel", text) + 28 * u));
        double width = Math.Max(buttonWidth * 2 + gap * 2, ModernMeasure("999,999,999|", text) + 40 * u) + pad * 2;
        width = Math.Max(width, ModernMeasure("Offer credits", title) + row + gap + pad * 2);
        double height = pad + row + gap * 2 + button + gap * 3 + button + pad;
        var anchor = _modernTradeBounds.Width > 0 ? _modernTradeBounds : new Rect((_size.Width - width) / 2, (_size.Height - height) / 2, width, height);
        double x = Snap(Math.Clamp(anchor.X + (anchor.Width - width) / 2, 0, Math.Max(0, _size.Width - width)));
        double y = Snap(Math.Clamp(anchor.Y + (anchor.Height - height) / 2, 0, Math.Max(0, _size.Height - height)));
        ModernFrame("DIALOG_BOX_WINDOW", new(x, y, width, height));
        double cx = x + pad, cy = y + pad, content = width - pad * 2;
        ModernText("tradeCredits/title", "Offer credits", new(cx, cy, content - row - gap, row), new(1, .85f, .3f), title);
        ModernCloseButton("tradeCreditClose", new(x + width - pad - row, cy, row, row));
        cy += row + gap * 2;
        var field = new Rect(cx, cy, content, button);
        ModernFrame("DIALOG_BOX_INFO", field);
        string shown = _creditDigits.Insert(Math.Clamp(_creditCaret, 0, _creditDigits.Length), _creditOverwrite ? "_" : "|");
        ModernText("tradeCredits/value", shown, field, new(1, 1, 1), text, 1);
        cy += button + gap * 3;
        double bx = x + width - pad;
        foreach (var (id, label, tip) in new[] { ("tradeCreditConfirm", "Offer", "Offer this many credits"), ("tradeCreditCancel", "Cancel", "Keep the current offer") })
        {
            bx -= buttonWidth; ModernButton(id, label, new(bx, cy, buttonWidth, button), text, tip); bx -= gap * 2;
        }
        _tips["tradeCreditClose"] = "Keep the current offer";
        Flush();
    }
}
