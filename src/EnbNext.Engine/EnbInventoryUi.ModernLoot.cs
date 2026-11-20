using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI loot and harvest window: the retail PSI09A content (the opened hulk's cargo or the harvested
/// resource's contents, 006889F0 / 00689100) in a Modern UI window that shows every item at once. A click on an item
/// is the retail tractor request, the close button is the retail X (trigger 12 / action 11 through CloseLoot), and
/// Loot all keeps its option. Only the look differs; retail mode keeps PSI09A.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>Where the loot window was dragged (screen fraction); null keeps it right of the screen centre.</summary>
    internal EnbUiPoint? ModernLootPosition { get; set; }
    internal Action<EnbUiPoint>? ModernLootMoved { get; set; }
    private Rect _modernLootBounds;
    /// <summary>The window keeps the rows it opened with while items are taken (no shrinking under the pointer).</summary>
    private (uint Target, int Rows) _modernLootRows;

    private void ModernLootPanel()
    {
        if (Data is not { } data || (data.HarvestableId ?? data.HulkId) is not uint target) return;
        Flush();
        bool harvest = data.HarvestableId is not null;
        var items = (harvest ? data.Harvest : data.Loot.Where(i => i.Inventory == 6)).OrderBy(i => i.Slot).ToArray();
        double u = ModernUnit, text = 14 * u, small = 13 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, row = 24 * u, button = 28 * u;
        double slot = ModernInventoryMetrics().Slot;
        // Rows of four, as retail PSI09A's grid; more items add rows rather than a scrollbar (owner: one row for four or fewer).
        const int columns = 4;
        int rows = Math.Max(1, (items.Length + columns - 1) / columns);
        if (_modernLootRows.Target != target || rows > _modernLootRows.Rows) _modernLootRows = (target, rows);
        rows = _modernLootRows.Rows;
        string name = !harvest && data.HulkName.Length > 0 ? data.HulkName : _scene?.Objects.FirstOrDefault(o => o.GameId == target)?.Name ?? "";
        string heading = (harvest ? "Harvest" : "Loot") + (name.Length > 0 ? ": " + name : "");
        // The grid is centred in a window wide enough for the corpse's name (up to a limit; longer names end in an ellipsis).
        double grid = columns * slot + (columns - 1) * gap;
        double content = Math.Round(Math.Clamp(ModernMeasure(heading, title) + row + gap, grid, grid * 1.7));
        bool lootAll = _lootAllEnabled;
        double width = content + pad * 2;
        double height = pad + row + gap * 2 + rows * slot + (rows - 1) * gap + (lootAll ? gap * 2 + button : 0) + (_message is null ? 0 : gap + row) + pad;
        double x = _size.Width * .5 + 160 * u, y = _size.Height * .32;
        if (ModernLootPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernLootBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernLootBounds, "lootFrame/modern");
        _tips.Remove("lootFrame/modern");

        double cx = x + pad, cy = y + pad;
        ModernText("loot/title", ModernEllipsis(heading, content - row - gap, title), new(cx, cy, content - row - gap, row), new(1, .85f, .3f), title);
        ModernCloseButton("lootClose", new(x + width - pad - row, cy, row, row));
        _tips["lootClose"] = harvest ? "Stop harvesting" : "Close";
        cy += row + gap * 2;
        Flush();
        for (int i = 0; i < rows * columns; i++)
        {
            string id = "lootItem/" + i.ToString(CultureInfo.InvariantCulture);
            var slotRect = FromPixels(Snap(cx + (content - grid) / 2 + i % columns * (slot + gap)), Snap(cy + i / columns * (slot + gap)), slot, slot);
            var item = i < items.Length ? items[i] : null;
            Slot(id, slotRect, item, enabled: item is not null, compact: true);
            if (item is not null) ModernSlotHighlight(id, slotRect);
        }
        Flush();
        if (items.Length == 0)
            ModernText("loot/empty", harvest ? "Nothing left to harvest" : "Nothing left to loot", new(cx, cy, content, slot), new(.7f, .75f, .85f), text, 1);
        cy += rows * slot + (rows - 1) * gap;
        if (lootAll)
        {
            cy += gap * 2;
            double bw = Math.Round(Math.Max(96 * u, ModernMeasure("Loot all", text) + 28 * u));
            ModernButton("lootAll", "Loot all", new(x + width - pad - bw, cy, bw, button), text, harvest ? "Harvest everything" : "Take every item", items.Length > 0 && !_lootingAll && !_busy);
            cy += button;
        }
        if (_message is not null)
            ModernText("loot/error", ModernEllipsis(_message, content, small), new(cx, cy + gap, content, row), new(1, .5f, .5f), small);
        Flush();
    }
}
