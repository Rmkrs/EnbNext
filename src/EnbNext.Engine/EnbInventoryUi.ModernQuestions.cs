using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI questions (Modern UI only): the group invitation and leave/disband
/// questions, the destroy confirmation and the quantity prompt in small Modern UI windows, and the station player menu
/// as a Modern UI menu. Each keeps the retail ids, keys, wording (dialogbox.ini) and requests; only the drawing differs.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>A modal question window in the screen's centre: a title, the message and its answers (retail ids).</summary>
    private void ModernQuestion(string modal, string title, string message, (string Id, string Label)[] answers, bool enabled = true)
    {
        double u = ModernUnit, text = 14 * u, titleSize = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, line = Math.Round(20 * u), button = Math.Round(28 * u);
        double width = Math.Round(420 * u), textWidth = width - pad * 2;
        var lines = ModernWrap(message, textWidth, text);
        double height = pad * 2 + (title.Length > 0 ? head + gap : 0) + lines.Count * line + gap * 3 + button;
        var bounds = new Rect(Snap((_size.Width - width) / 2), Snap((_size.Height - height) * .4), width, height);
        Flush();
        Hit(modal, new(0, 0, 640, 480)); _hits[modal].LayoutGroup = EnbUiGroup.None;
        ModernFrame("DIALOG_BOX_WINDOW", bounds);
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        if (title.Length > 0) { ModernText(modal + "/title", ModernEllipsis(title, textWidth, titleSize), new(cx, cy, textWidth, head), new(1, .85f, .3f), titleSize); cy += head + gap; }
        for (int i = 0; i < lines.Count; i++) ModernText(modal + "/line" + i, lines[i], new(cx, cy + i * line, textWidth, line), new(1, 1, 1), text);
        double bx = bounds.Right - pad, by = bounds.Bottom - pad - button;
        for (int i = answers.Length - 1; i >= 0; i--)
        {
            double w = Math.Max(Math.Round(90 * u), Math.Ceiling(ModernMeasure(answers[i].Label, text) + 32 * u));
            bx -= w;
            ModernButton(answers[i].Id, answers[i].Label, new(bx, by, w, button), text, "", enabled && !_busy);
            bx -= gap * 2;
        }
        Flush();
    }

    /// <summary>The group invitation or the leave/disband question (GroupDialog): the retail wording and answers.</summary>
    private void ModernGroupQuestion(bool invite, string message, IReadOnlyDictionary<string, string>? definition)
    {
        string Option(int i, string fallback) => definition?.GetValueOrDefault("Option" + i, "") is { Length: > 0 } label ? label : fallback;
        if (invite) ModernQuestion("group/Modal", "Group invitation", message, [("group/Accept", Option(0, "Accept")), ("group/Reject", Option(1, "Reject"))]);
        else ModernQuestion("group/Modal", Group?.IsLeader == true ? "Disband group" : "Leave group", message, [("group/Leave", Option(0, "Yes")), ("group/CancelLeave", Option(1, "No"))]);
    }

    /// <summary>The destroy confirmation (UI_TRANSACTION_DUMP_ITEM): its question, the item's name and No / Yes.</summary>
    private void ModernDestroyQuestion(IReadOnlyDictionary<string, string> dialog, EnbInventoryMoveRequest request)
    {
        string message = (dialog.GetValueOrDefault("Header0", "") + " " + dialog.GetValueOrDefault("Header1", "")).Trim();
        string name = Resolve(request.Source)?.Name ?? "";
        ModernQuestion("destroyModal", name, message.Length > 0 ? message : "Are you sure you want to destroy this item?",
            [("destroyNo", dialog.GetValueOrDefault("Option0", "No")), ("destroyYes", dialog.GetValueOrDefault("Option1", "Yes"))]);
    }

    /// <summary>
    /// The quantity prompt (ReloadQuantity) in a small window by the pointer: the item, a bar to pick the count (drag or
    /// click it; typed digits set it), the count, and the retail keys: a click or Enter moves that many, a right-click or
    /// Escape cancels.
    /// </summary>
    private void ModernQuantity(EnbInventoryMoveRequest request)
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, gap = 6 * u, pad = 12 * u, head = 22 * u, bar = Math.Round(16 * u);
        string name = Resolve(request.Source)?.Name ?? Terminal?.Items.FirstOrDefault(i => i.Inventory == request.Source.Inventory && i.Slot == request.Source.Slot)?.Name ?? "";
        string count = _reloadCount.ToString(CultureInfo.InvariantCulture) + " of " + request.SourceCount.ToString(CultureInfo.InvariantCulture);
        string hint = "Click or Enter to move, right-click or Escape to cancel";
        double countWidth = Math.Ceiling(ModernMeasure(request.SourceCount.ToString(CultureInfo.InvariantCulture) + " of " + request.SourceCount.ToString(CultureInfo.InvariantCulture), text) + 12 * u);
        double width = Math.Max(Math.Round(320 * u), Math.Ceiling(ModernMeasure(hint, small) + pad * 2)), inner = width - pad * 2;
        double height = pad * 2 + head + gap + head + gap + head;
        double x = Math.Clamp(_reloadOrigin.X * _size.Width / 640 - width / 2, 0, Math.Max(0, _size.Width - width));
        double y = Math.Clamp(_reloadOrigin.Y * _size.Height / 480 - height - 12 * u, 0, Math.Max(0, _size.Height - height));
        var bounds = new Rect(Snap(x), Snap(y), width, height);
        Flush();
        Hit("reloadModal", new(0, 0, 640, 480)); _hits["reloadModal"].LayoutGroup = EnbUiGroup.None;
        ModernFrame("DIALOG_BOX_WINDOW", bounds);
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        ModernText("reload/title", ModernEllipsis(name.Length > 0 ? "How many " + name + "?" : "How many?", inner, text), new(cx, cy, inner, head), new(1, .85f, .3f), text);
        cy += head + gap;
        var track = new Rect(Snap(cx), Snap(cy + (head - bar) / 2), Math.Round((inner - countWidth) * _scale) / _scale, bar);
        Hit("reloadTrack", FromPixels(track));
        _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 150), track));
        double fill = request.SourceCount > 0 ? track.Width * _reloadCount / request.SourceCount : 0;
        if (fill > 0) _images.Add(new(EnbModernIcons.Fill(new(.3f, .8f, .45f), 230), new(track.X, track.Y, Snap(fill), track.Height)));
        _images.Add(new(EnbModernIcons.Fill(new(.45f, .55f, .8f), 200), new(track.X, track.Bottom - 1, track.Width, 1)));
        ModernText("reload/count", count, new(cx, cy, inner, head), new(1, 1, 1), text, 2);
        cy += head + gap;
        ModernText("reload/hint", hint, new(cx, cy, inner, head), new(.6f, .65f, .78f), small);
        _tips["reloadTrack"] = "Drag or click to choose how many, or type the number";
        Flush();
    }
}
