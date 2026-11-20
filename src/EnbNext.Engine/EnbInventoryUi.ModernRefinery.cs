using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Refining terminal (Modern UI only): the retail refinery content in a
/// compact window, with the Modern inventory open beside it. The list of items you can refine (a click selects one),
/// the result slot, the required components, the cost, your credits, the result line and Refine / Refine Stack (as
/// retail: no Clear). The list upkeep, result lines and every request are the classic panel's (TerminalRows, TerminalStatus,
/// ActivateTerminal); only the drawing differs.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>
    /// A result line is shown as a failure (red) or not (green). The lab's message also carries success texts
    /// ("Analyze successful") with an attempt result, so it goes by the result: failed or damaged attempts, an unusable
    /// selection (any other validity) and the client's own messages are failures.
    /// </summary>
    private bool ModernStatusFailure(string value, EnbNext.Net7.Gameplay.Net7ManufacturingLabProjection lab)
    {
        if (_message is not null && value == _message) return true;
        if (value.StartsWith("Attempt Failed", StringComparison.Ordinal)) return true;
        if (value != lab.FailureMessage) return false;
        return lab.Validity is not (EnbNext.Net7.Gameplay.Net7ManufacturingValidity.AttemptNormalSuccess or EnbNext.Net7.Gameplay.Net7ManufacturingValidity.AttemptCriticalSuccess);
    }

    /// <summary>A terminal button in a Modern window: sends its retail action when enabled (as TerminalButton).</summary>
    private void ModernTerminalButton(string id, string label, Rect r, EnbTerminalCommand command, uint value, bool enabled, string tip)
    {
        enabled &= !JobsRequestPending && !_busy;
        ModernButton(id, label, r, 14 * ModernUnit, tip, enabled);
        if (enabled) _terminalActions[id] = (command, value);
    }

    /// <summary>A terminal item slot in a Modern window (the classic slot ids, so dragging and dropping work the same).</summary>
    private void ModernTerminalSlot(string name, byte inventory, int slot, bool writable, Rect r, string emptyTip)
    {
        var item = Terminal?.Items.FirstOrDefault(i => i.Inventory == inventory && i.Slot == slot);
        string id = "terminalSlot/" + name;
        var design = FromPixels(r);
        Slot(id, design, item, compact: true);
        ModernSlotHighlight(id, design);
        if (writable) _dropSlots[id] = (inventory, slot);
        if (item is null) _tips[id] = emptyTip;
    }

    private void ModernRefineryWindow()
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, head = 24 * u, gap = 8 * u, rowHeight = Math.Round(24 * u);
        var page = ModernTerminalFrame(720, 420);
        var state = Terminal; var lab = state?.Lab;
        if (lab is null)
        {
            ModernText("mterm/loading", _message ?? "Waiting for terminal data...", new(page.X, page.Y + page.Height / 2 - head / 2, page.Width, head),
                _message is null ? new(.75f, .8f, .92f) : new(1, .45f, .45f), text, 1);
            return;
        }
        // The items you can refine.
        double listWidth = Math.Round(page.Width * .45);
        var list = new Rect(page.X, page.Y, listWidth, page.Height);
        ModernTerminalList(list, "Choose resource to create", "Nothing in your cargo can be refined here.", state!, lab, null);

        // The result, its components, the costs, the result line and the actions.
        var box = new Rect(list.Right + gap, page.Y, page.Width - listWidth - gap, page.Height);
        ModernFrame("DIALOG_BOX_INFO", box);
        Flush();
        double dx = box.X + 12 * u, dw = box.Width - 24 * u, dy = box.Y + 4 * u;
        double big = Math.Round(64 * u), slot = Math.Round(48 * u), slotGap = Math.Round(6 * u);
        ModernText("mterm/refineResultHeading", "Result", new(dx, dy, dw, head), new(.7f, .75f, .85f), small);
        dy += head;
        ModernTerminalSlot("RESULT", 12, 0, true, new(Snap(dx), Snap(dy), big, big), "The refined item appears here");
        var target = state!.Items.FirstOrDefault(i => i.Inventory == 12 && i.Slot == 0);
        if (target is not null)
            foreach (var (line, n) in ModernWrap(target.Name, dw - big - 12 * u, text).Take(2).Select((l, n) => (l, n)))
                ModernText("mterm/refineTarget" + n, line, new(dx + big + 12 * u, dy + 8 * u + n * rowHeight, dw - big - 12 * u, rowHeight), new(1, .85f, .45f), text);
        dy += big + gap;
        ModernText("mterm/refineComponentsHeading", "Required", new(dx, dy, dw, head), new(.7f, .75f, .85f), small);
        dy += head;
        Flush();
        for (int i = 0; i < 6; i++)
            ModernTerminalSlot($"E{i:00}", 13, i, false, new(Snap(dx + i * (slot + slotGap)), Snap(dy), slot, slot), "");
        Flush();
        dy += slot + gap * 2;
        string cost = lab.NegotiatedCost.ToString("N0", CultureInfo.InvariantCulture), credits = Data?.Credits?.ToString("N0", CultureInfo.InvariantCulture) ?? "";
        ModernText("mterm/refineCostLabel", "Cost", new(dx, dy, dw / 2, rowHeight), new(.7f, .75f, .85f), text);
        ModernText("mterm/refineCost", cost, new(dx, dy, dw / 2, rowHeight), new(1, 1, 1), text, 2);
        ModernText("mterm/refineCreditsLabel", "Your credits", new(dx + dw / 2 + gap, dy, dw / 2 - gap, rowHeight), new(.7f, .75f, .85f), text);
        ModernText("mterm/refineCredits", credits, new(dx + dw / 2 + gap, dy, dw / 2 - gap, rowHeight), new(1, 1, 1), text, 2);
        dy += rowHeight + gap;
        // The result line: the failure, the outcome or the attempt in progress (the retail placeholder label is left out).
        var lines = TerminalStatus(state, lab, out _);
        double buttonHeight = Math.Round(30 * u), buttonsTop = box.Bottom - 10 * u - buttonHeight;
        foreach (var (label, value) in lines)
            foreach (string line in ModernWrap(value, dw, text))
            {
                if (dy + rowHeight > buttonsTop - gap) break;
                bool failure = ModernStatusFailure(value, lab);
                ModernText("mterm/refineStatus" + dy, line, new(dx, dy, dw, rowHeight), failure ? new(1, .45f, .45f) : new(.6f, 1, .6f), text);
                dy += rowHeight;
            }
        // Retail refining has Refine and Refine Stack only (no Clear).
        double buttonWidth = Math.Floor((dw - gap) / 2);
        ModernTerminalButton("terminal/go", "Refine", new(dx, buttonsTop, buttonWidth, buttonHeight), EnbTerminalCommand.Action, 2, state.CanAct, "Begin refining.");
        ModernTerminalButton("terminal/r_stack", "Refine Stack", new(dx + buttonWidth + gap, buttonsTop, buttonWidth, buttonHeight), EnbTerminalCommand.Action, 3, state.CanRefineStack, "Refine stack");
    }

    /// <summary>
    /// The terminal's list in a Modern window (TerminalRows: formulas, recent attempts or manufacturing categories):
    /// a click sends the row's retail request (row ids count from the list's top, as the retail rows), the chosen
    /// formula is highlighted, the wheel and the scroll bar move it. <paramref name="icon"/> gives a row's picture.
    /// </summary>
    private List<(string Name, EnbTerminalCommand Command, uint Value)> ModernTerminalList(Rect list, string heading, string emptyText,
        EnbStationTerminalSnapshot state, EnbNext.Net7.Gameplay.Net7ManufacturingLabProjection lab, Func<(string Name, EnbTerminalCommand Command, uint Value), string?>? icon, double top = 0)
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, head = 24 * u, rowHeight = Math.Round((icon is null ? 24 : 28) * u);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        ModernText("mterm/listHeading", heading, new(list.X + 8 * u, list.Y + 4 * u, list.Width - 16 * u, head), new(.7f, .75f, .85f), small);
        double rowsTop = list.Y + 4 * u + head + top;
        int visible = Math.Max(1, (int)((list.Bottom - 6 * u - rowsTop) / rowHeight));
        var rows = TerminalRows(state, lab, visible);
        double room = rows.Count > visible ? ModernScrollbarRoom : 0;
        if (rows.Count == 0)
            foreach (var (line, n) in ModernWrap(emptyText, list.Width - 16 * u, text).Select((l, n) => (l, n)))
                ModernText("mterm/listEmpty" + n, line, new(list.X + 8 * u, rowsTop + n * rowHeight, list.Width - 16 * u, rowHeight), new(.75f, .8f, .92f), text);
        bool enabled = !JobsRequestPending && !_busy;
        for (int n = 0; n < visible && _terminalPage + n < rows.Count; n++)
        {
            int index = _terminalPage + n;
            var entry = rows[index];
            var row = new Rect(list.X + 4 * u, rowsTop + n * rowHeight, list.Width - 8 * u - room, rowHeight);
            string id = $"terminal/desc{n:00}";
            if (enabled) { Hit(id, FromPixels(row)); _terminalActions[id] = (entry.Command, entry.Value); }
            bool selected = entry.Command == EnbTerminalCommand.Formula && index == _terminalSelectedFormula;
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 60), row));
            else if (n % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), row));
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            double tx = row.X + 6 * u;
            if (icon?.Invoke(entry) is { } picture && Texture(picture) is not null)
            {
                double side = rowHeight - 4 * u;
                Flush(); ImageTexture(picture, FromPixels(Snap(tx), Snap(row.Y + 2 * u), side, side)); Flush();
                tx += side + 6 * u;
            }
            ModernText(id + "/text", ModernEllipsis(entry.Name, row.Right - tx - 6 * u, text), new(tx, row.Y, row.Right - tx, row.Height),
                selected ? new(1, 1, .45f) : new(.92f, .94f, 1), text);
            _tips[id] = entry.Name + (entry.Command == EnbTerminalCommand.Formula ? "\nClick to choose it" : "\nClick to open it");
        }
        ModernScrollbar("terminal/mlist", new(list.X, rowsTop, list.Width, visible * rowHeight), rows.Count, visible, _terminalPage, v => SetTerminalScroll(v));
        return rows;
    }
}
