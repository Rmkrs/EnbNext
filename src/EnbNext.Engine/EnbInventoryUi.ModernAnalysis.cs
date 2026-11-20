using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Analyze terminal (Modern UI only): the retail analysis content in a compact
/// window, with the Modern inventory open beside it: the item slot, the control interface slot, the six result slots,
/// the cost, the success and critical chances (recover one / recover all when dismantling), the result lines and the retail
/// Analyze or Dismantle and Inv actions. The labels, result lines and every request are the classic panel's
/// (ManufacturingLabels, TerminalStatus, ActivateTerminal); only the drawing differs.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private void ModernAnalysisWindow()
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, head = 24 * u, gap = 8 * u, rowHeight = Math.Round(24 * u);
        var page = ModernTerminalFrame(560, 380);
        var state = Terminal; var lab = state?.Lab;
        if (state is null || lab is null)
        {
            ModernText("mterm/loading", _message ?? "Waiting for terminal data...", new(page.X, page.Y + page.Height / 2 - head / 2, page.Width, head),
                _message is null ? new(.75f, .8f, .92f) : new(1, .45f, .45f), text, 1);
            return;
        }
        string Tip(string key, string fallback) => _terminalTooltips?.TryGetSection("UI_ANALYZE", out var tips) == true && tips.TryGetValue(key, out var tip) ? tip : fallback;
        var box = page;
        ModernFrame("DIALOG_BOX_INFO", box);
        Flush();
        double dx = box.X + 12 * u, dw = box.Width - 24 * u, dy = box.Y + 4 * u;
        double big = Math.Round(64 * u), slot = Math.Round(52 * u), slotGap = Math.Round(8 * u);
        // The item and the control interface.
        ModernText("mterm/analyzeItemHeading", "Item", new(dx, dy, dw, head), new(.7f, .75f, .85f), small);
        ModernText("mterm/analyzeOverHeading", "Control interface", new(dx + dw - 2 * big, dy, 2 * big, head), new(.7f, .75f, .85f), small, 2);
        dy += head;
        ModernTerminalSlot("RESULT", 12, 0, true, new(Snap(dx), Snap(dy), big, big), "Drag an item here to analyze or dismantle it");
        ModernTerminalSlot("OVER", 14, 0, true, new(Snap(dx + dw - big), Snap(dy), big, big), "");
        // The item's name, or the retail prompt (result_text "Place Item Here") while the slot is empty.
        var target = state.Items.FirstOrDefault(i => i.Inventory == 12 && i.Slot == 0);
        foreach (var (line, n) in ModernWrap(target?.Name ?? "Place item here", dw - 2 * big - 24 * u, text).Take(2).Select((l, n) => (l, n)))
            ModernText("mterm/analyzeTarget" + n, line, new(dx + big + 12 * u, dy + 8 * u + n * rowHeight, dw - 2 * big - 24 * u, rowHeight),
                target is null ? new(.75f, .8f, .92f) : new(1, .85f, .45f), text);
        dy += big + gap;
        // The six result slots.
        ModernText("mterm/analyzeComponentsHeading", "Components", new(dx, dy, dw, head), new(.7f, .75f, .85f), small);
        dy += head;
        Flush();
        for (int i = 0; i < 6; i++)
            ModernTerminalSlot($"E{i:00}", 13, i, false, new(Snap(dx + i * (slot + slotGap)), Snap(dy), slot, slot), "");
        Flush();
        dy += slot + gap * 2;
        // The cost and the chances (005443F0 labels), as the classic panel.
        bool dismantle = lab.Mode == 3;
        double half = (dw - gap) / 2;
        ModernText("mterm/analyzeCostLabel", "Cost", new(dx, dy, half, rowHeight), new(.7f, .75f, .85f), text);
        ModernText("mterm/analyzeCost", lab.NegotiatedCost.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), new(dx, dy, half, rowHeight), new(1, 1, 1), text, 2);
        ModernText("mterm/analyzeCreditsLabel", "Your credits", new(dx + half + gap, dy, half, rowHeight), new(.7f, .75f, .85f), text);
        ModernText("mterm/analyzeCredits", Data?.Credits?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "", new(dx + half + gap, dy, half, rowHeight), new(1, 1, 1), text, 2);
        dy += rowHeight;
        ModernText("mterm/analyzeChanceLabel", dismantle ? "Recover one" : "Success", new(dx, dy, half, rowHeight), new(.7f, .75f, .85f), text);
        ModernText("mterm/analyzeChance", _analysisChanceText, new(dx, dy, half, rowHeight), new(1, 1, 1), text, 2);
        ModernText("mterm/analyzeCriticalLabel", dismantle ? "Recover all" : "Critical", new(dx + half + gap, dy, half, rowHeight), new(.7f, .75f, .85f), text);
        ModernText("mterm/analyzeCritical", _analysisCriticalText, new(dx + half + gap, dy, half, rowHeight), new(1, 1, 1), text, 2);
        dy += rowHeight + gap;
        // The result lines: the failure, the outcome and what it gave.
        var lines = TerminalStatus(state, lab, out _);
        double buttonHeight = Math.Round(30 * u), buttonsTop = box.Bottom - 10 * u - buttonHeight;
        foreach (var (_, value) in lines)
            foreach (string line in ModernWrap(value, dw, text))
            {
                if (dy + rowHeight > buttonsTop - gap) break;
                bool failure = ModernStatusFailure(value, lab);
                ModernText("mterm/analyzeStatus" + dy, line, new(dx, dy, dw, rowHeight), failure ? new(1, .45f, .45f) : new(.6f, 1, .6f), text);
                dy += rowHeight;
            }
        // Retail: Go (Analyze or Dismantle, by the lab's mode) and Inv (action 0).
        double buttonWidth = Math.Floor((dw - gap) / 2);
        ModernTerminalButton("terminal/go", dismantle ? "Dismantle" : "Analyze", new(dx, buttonsTop, buttonWidth, buttonHeight), EnbTerminalCommand.Action, 2, state.CanAct, Tip("ICON_GO", "Begin Analyze/Dismantle"));
        ModernTerminalButton("terminal/INV", "Inventory", new(dx + buttonWidth + gap, buttonsTop, buttonWidth, buttonHeight), EnbTerminalCommand.Action, 0, true, Tip("ICON_INV", "Inventory"));
    }
}
