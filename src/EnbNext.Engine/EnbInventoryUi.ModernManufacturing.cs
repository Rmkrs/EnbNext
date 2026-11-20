using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Manufacturing terminal (Modern UI only): the retail manufacturing content
/// in a compact window, with the Modern inventory open beside it. Browsing (the kind of item, its category and type,
/// each step back as a button above the list), Recent, the tech level filter, the formulas, the result, control interface
/// and required item slots, cost, credits, expected quality and its range, the result line and Manufacture. The list
/// upkeep, labels, result lines and every request are the classic panel's (TerminalRows, ManufacturingLabels,
/// TerminalStatus, ActivateTerminal); only the drawing differs.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private static readonly string[] TechLevelNames = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "All"];

    private void ModernManufacturingWindow()
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, head = 24 * u, gap = 8 * u, rowHeight = Math.Round(24 * u);
        var page = ModernTerminalFrame(860, 520);
        var state = Terminal; var lab = state?.Lab;
        if (state is null || lab is null)
        {
            ModernText("mterm/loading", _message ?? "Waiting for terminal data...", new(page.X, page.Y + page.Height / 2 - head / 2, page.Width, head),
                _message is null ? new(.75f, .8f, .92f) : new(1, .45f, .45f), text, 1);
            return;
        }
        double listWidth = Math.Round(page.Width * .48);
        // Browsing: Recent, then each step taken (a click goes back to choosing at that step), as the retail icons.
        double bx = page.X;
        Rect Step(string id, string label, string tip, bool on, bool enabled = true)
        {
            double w = Math.Min(Math.Round(ModernMeasure(label, text) + 24 * u), listWidth / 2);
            var r = new Rect(Snap(bx), page.Y, w, head);
            ModernButton(id, ModernEllipsis(label, w - 12 * u, text), r, text, tip, enabled && !JobsRequestPending && !_busy);
            if (on) _images.Add(new(EnbModernIcons.Fill(new(1, .85f, .3f), 200), new(r.X + 4 * u, r.Bottom - 2 * u, r.Width - 8 * u, 2 * u)));
            bx += w + 4 * u;
            return r;
        }
        string Tip(string key, string fallback) => _terminalTooltips?.TryGetSection("UI_MANUFACTURE", out var tips) == true && tips.TryGetValue(key, out var tip) ? tip : fallback;
        Step("terminalRecent", "Recent", Tip("ICON_RECENT", "Recall recent formula list."), _terminalRecent);
        var primaries = lab.PrimaryCategories;
        for (int depth = 0; depth < 3 && state.Depth >= depth; depth++)
        {
            string name = depth switch
            {
                0 => state.Primary >= 0 && state.Primary < primaries.Count ? primaries[state.Primary].Name : "Browse",
                1 => state.Primary >= 0 && state.Primary < primaries.Count && state.Category >= 0 && state.Category < primaries[state.Primary].Categories.Count
                    ? primaries[state.Primary].Categories[state.Category].Name : "Category",
                _ => state.Primary >= 0 && state.Primary < primaries.Count && state.Category >= 0 && state.Category < primaries[state.Primary].Categories.Count
                    && state.Subcategory >= 0 && state.Subcategory < primaries[state.Primary].Categories[state.Category].Subcategories.Count
                    ? primaries[state.Primary].Categories[state.Category].Subcategories[state.Subcategory].Name : "Type",
            };
            if (string.IsNullOrWhiteSpace(name)) name = depth == 0 ? "Browse" : depth == 1 ? "Category" : "Type";
            string id = "terminal/" + (depth == 0 ? "ITEM00" : depth == 1 ? "CAT00" : "SUB00");
            Step(id, (depth > 0 ? "› " : "") + name, depth == 0 ? "Choose the kind of item" : depth == 1 ? "Choose the category" : "Choose the type",
                !_terminalRecent && state.Depth == depth);
            if (!JobsRequestPending && !_busy) _terminalActions[id] = (EnbTerminalCommand.Browse, (uint)depth);
        }
        // The tech level filter (formulas only).
        double listTop = page.Y + head + gap;
        if (state.Depth == 3 && !_terminalRecent)
        {
            double label = Math.Round(ModernMeasure("Tech level", text) + 10 * u);
            ModernText("mterm/makeTechLevel", "Tech level", new(page.X, listTop, label, head), new(.7f, .75f, .85f), text);
            double fw = Math.Floor((listWidth - label - 9 * 3 * u) / 10);
            for (int i = 1; i <= 10; i++)
            {
                uint bits = i == 10 ? 0x1ffu : 1u << (i - 1);
                bool on = (state.Filter & bits) == bits;
                var r = new Rect(Snap(page.X + label + (i - 1) * (fw + 3 * u)), listTop, fw, head);
                ModernTerminalButton($"terminal/T_LEV_OFF_{i:00}", TechLevelNames[i - 1], r, on ? EnbTerminalCommand.FilterOff : EnbTerminalCommand.FilterOn, bits, true,
                    i == 10 ? (on ? "Hide every tech level" : "Show every tech level") : (on ? "Hide" : "Show") + " tech level " + TechLevelNames[i - 1] + " formulas");
                if (on) _images.Add(new(EnbModernIcons.Fill(new(1, .85f, .3f), 200), new(r.X + 3 * u, r.Bottom - 2 * u, r.Width - 6 * u, 2 * u)));
            }
            listTop += head + gap;
        }
        var list = new Rect(page.X, listTop, listWidth, page.Bottom - listTop);
        string heading = _terminalRecent ? "Recent formulas" : state.Depth switch { 0 => "Kinds of item", 1 => "Categories", 2 => "Types", _ => "Formulas" };
        string empty = _terminalRecent ? "No recent formulas." : state.Depth == 0 ? "Click Browse to choose the kind of item." : state.Depth == 3 ? "No formulas at the chosen tech levels." : "Nothing here.";
        string? Icon((string Name, EnbTerminalCommand Command, uint Value) row) => row.Command switch
        {
            EnbTerminalCommand.Primary => $"IM_{row.Value}.tga",
            EnbTerminalCommand.Category => $"IM_{state.Primary}{row.Value}.tga",
            EnbTerminalCommand.Subcategory => $"IM_{state.Primary}{state.Category}{row.Value}.tga",
            _ => null,
        };
        ModernTerminalList(list, heading, empty, state, lab, state.Depth < 3 && !_terminalRecent ? Icon : null);

        // The result, override and components, the costs and quality, the result line and the actions.
        var box = new Rect(list.Right + gap, page.Y, page.Width - listWidth - gap, page.Height);
        ModernFrame("DIALOG_BOX_INFO", box);
        Flush();
        double dx = box.X + 12 * u, dw = box.Width - 24 * u, dy = box.Y + 4 * u;
        double big = Math.Round(64 * u), slot = Math.Round(48 * u), slotGap = Math.Round(6 * u);
        ModernText("mterm/makeResultHeading", "Result", new(dx, dy, dw, head), new(.7f, .75f, .85f), small);
        ModernText("mterm/makeOverHeading", "Control interface", new(dx + dw - 2 * big, dy, 2 * big, head), new(.7f, .75f, .85f), small, 2);
        dy += head;
        ModernTerminalSlot("RESULT", 12, 0, true, new(Snap(dx), Snap(dy), big, big), "The manufactured item appears here");
        ModernTerminalSlot("OVER", 14, 0, true, new(Snap(dx + dw - big), Snap(dy), big, big), "");
        // The result slot's item by name (the retail "Unknown Item" stands for no target only in the chat report).
        if (state.Items.FirstOrDefault(i => i.Inventory == 12 && i.Slot == 0)?.Name is { Length: > 0 } target)
            foreach (var (line, n) in ModernWrap(target, dw - 2 * big - 24 * u, text).Take(2).Select((l, n) => (l, n)))
                ModernText("mterm/makeTarget" + n, line, new(dx + big + 12 * u, dy + 8 * u + n * rowHeight, dw - 2 * big - 24 * u, rowHeight), new(1, .85f, .45f), text);
        dy += big + gap;
        ModernText("mterm/makeComponentsHeading", "Required items", new(dx, dy, dw, head), new(.7f, .75f, .85f), small);
        dy += head;
        Flush();
        for (int i = 0; i < 6; i++)
            ModernTerminalSlot($"E{i:00}", 13, i, false, new(Snap(dx + i * (slot + slotGap)), Snap(dy), slot, slot), "");
        Flush();
        dy += slot + gap * 2;
        void Pair(string id, string label, string value, double x, double w)
        {
            ModernText(id + "Label", label, new(x, dy, w, rowHeight), new(.7f, .75f, .85f), text);
            ModernText(id, value, new(x, dy, w, rowHeight), new(1, 1, 1), text, 2);
        }
        double half = (dw - gap) / 2;
        Pair("mterm/makeCost", "Cost", lab.NegotiatedCost.ToString("N0", CultureInfo.InvariantCulture), dx, half);
        Pair("mterm/makeCredits", "Your credits", Data?.Credits?.ToString("N0", CultureInfo.InvariantCulture) ?? "", dx + half + gap, half);
        dy += rowHeight;
        Pair("mterm/makeExpected", "Expected quality", _expectedQualityText, dx, half);
        Pair("mterm/makeRange", "Quality range", _qualityRangeText, dx + half + gap, half);
        dy += rowHeight + gap;
        // The result line: the failure, the outcome or the attempt in progress (the retail placeholder label is left out).
        var lines = TerminalStatus(state, lab, out _);
        double buttonHeight = Math.Round(30 * u), buttonsTop = box.Bottom - 10 * u - buttonHeight;
        foreach (var (_, value) in lines)
            foreach (string line in ModernWrap(value, dw, text))
            {
                if (dy + rowHeight > buttonsTop - gap) break;
                bool failure = ModernStatusFailure(value, lab);
                ModernText("mterm/makeStatus" + dy, line, new(dx, dy, dw, rowHeight), failure ? new(1, .45f, .45f) : new(.6f, 1, .6f), text);
                dy += rowHeight;
            }
        // Retail manufacturing has Manufacture only (no Clear).
        double buttonWidth = Math.Round(160 * u);
        ModernTerminalButton("terminal/go", "Manufacture", new(box.Right - 12 * u - buttonWidth, buttonsTop, buttonWidth, buttonHeight), EnbTerminalCommand.Action, 2, _manufactureGoEnabled, Tip("ICON_GO", "Begin manufacturing."));
    }
}
