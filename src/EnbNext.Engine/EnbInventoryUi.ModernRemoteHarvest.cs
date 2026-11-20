using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next multibox "Shared harvesting": a follower's open harvest window, shown in the leader's client where its own loot
/// window would be (the leader cannot harvest, so it has none). A click on a resource asks the follower to take it; the
/// close button closes the follower's window. Modern UI only.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>The follower's harvest window to show (null: none).</summary>
    internal Func<EnbRemoteHarvest?>? RemoteHarvestWindow { get; set; }
    internal Action<string, uint, int, string>? RemoteHarvestCommand { get; set; }
    private EnbRemoteHarvest? _remoteHarvestShown;
    /// <summary>The follower's harvest window changed (the host compares what it publishes).</summary>
    internal void RemoteHarvestChanged() { if (ModernUi) Publish(); }

    private bool ModernRemoteHarvestPanel()
    {
        _remoteHarvestShown = null;
        if (!ModernUi || RemoteHarvestWindow?.Invoke() is not { Open: true } remote) return false;
        _remoteHarvestShown = remote;
        Flush();
        var items = remote.Items.OrderBy(i => i.Slot).ToArray();
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, row = 24 * u;
        double slot = ModernInventoryMetrics().Slot;
        const int columns = 4;
        int rows = Math.Max(1, (items.Length + columns - 1) / columns);
        string name = _scene?.Objects.FirstOrDefault(o => o.GameId == remote.Target)?.Name ?? "";
        string heading = "Harvest" + (name.Length > 0 ? ": " + name : "");
        double grid = columns * slot + (columns - 1) * gap;
        double content = Math.Round(Math.Clamp(ModernMeasure(heading, title) + row + gap, grid, grid * 1.9));
        double width = content + pad * 2, height = pad + row + gap * 2 + rows * slot + (rows - 1) * gap + pad;
        double x = _size.Width * .5 + 160 * u, y = _size.Height * .32;
        if (ModernLootPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernLootBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernLootBounds, "lootFrame/modern");
        _tips.Remove("lootFrame/modern");
        double cx = x + pad, cy = y + pad;
        ModernText("rloot/title", ModernEllipsis(heading, content - row - gap, title), new(cx, cy, content - row - gap, row), new(1, .85f, .3f), title);
        ModernCloseButton("rlootClose", new(x + width - pad - row, cy, row, row));
        _tips["rlootClose"] = "Stop harvesting (" + remote.Pilot + ")";
        cy += row + gap * 2;
        Flush();
        for (int i = 0; i < rows * columns; i++)
        {
            string id = "rloot/" + i.ToString(CultureInfo.InvariantCulture);
            var slotRect = FromPixels(Snap(cx + (content - grid) / 2 + i % columns * (slot + gap)), Snap(cy + i / columns * (slot + gap)), slot, slot);
            var item = i < items.Length ? items[i] : null;
            Slot(id, slotRect, item, enabled: item is not null, compact: true);
            // The follower's item: a click asks it to take the item; it is not this client's to drag.
            _items.Remove(id);
            if (item is null) continue;
            ModernSlotHighlight(id, slotRect);
            _tips[id] = item.Name + (item.Count > 1 ? " (" + item.Count.ToString(CultureInfo.InvariantCulture) + ")" : "") + "\n" + remote.Pilot;
        }
        Flush();
        if (items.Length == 0)
            ModernText("rloot/empty", "Nothing left to harvest", new(cx, cy, content, slot), new(.7f, .75f, .85f), text, 1);
        Flush();
        return true;
    }

    private bool ActivateRemoteHarvest(string id)
    {
        if (_remoteHarvestShown is not { } remote || !id.StartsWith("rloot", StringComparison.Ordinal)) return false;
        if (id == "rlootClose") { RemoteHarvestCommand?.Invoke("harvestClose", remote.Target, 0, remote.Pilot); InteractionSoundRequested?.Invoke("TextClick01.wav"); }
        else if (id.StartsWith("rloot/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            var items = remote.Items.OrderBy(i => i.Slot).ToArray();
            if (index < items.Length) { RemoteHarvestCommand?.Invoke("harvestItem", remote.Target, items[index].Slot, remote.Pilot); InteractionSoundRequested?.Invoke("fontclick.wav"); }
        }
        return true;
    }
}
