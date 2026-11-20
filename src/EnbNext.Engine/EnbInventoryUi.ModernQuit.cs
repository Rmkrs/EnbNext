using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Quit menu: the retail Quit menu (fa_opt02a labels, tooltip.ini tooltips, the same quit state and
/// logoff requests, Escape and Alt-Q / Alt-C / Alt-L) in a Modern UI window over a dimmed screen, which waits for it.
/// The note about powering down shows in space only, as in retail. Next adds Options (the game options, as other games
/// offer from their Escape menu) and Edit mode where the retail menu offers it.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool ModernQuitShown => ModernOptions is { ModernQuitOpen: true };

    private void ModernQuitWindow()
    {
        var options = ModernOptions!;
        double u = ModernUnit, text = 14 * u, small = 13 * u, title = 17 * u, pad = Math.Round(16 * u), gap = Math.Round(8 * u);
        double button = Math.Round(30 * u), line = Math.Round(18 * u), width = Math.Round(380 * u);
        bool busy = options.ModernQuitBusy, edit = options.ModernQuitCanEdit;
        // The screen waits for the menu.
        Flush();
        var screen = new Rect(0, 0, _size.Width, _size.Height);
        Hit("mquit/block", FromPixels(screen));
        Draw("dragLayer", FromPixels(screen));
        _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 110), screen));
        Flush();

        string note = options.ModernQuitInStation ? "" : options.ModernQuitLabel("note", "Please Note: Your ship will power down before the game is exited.");
        var noteLines = note.Length > 0 ? ModernWrap(note, width - pad * 2, small) : [];
        string? error = options.ModernQuitError;
        double height = pad + Math.Round(26 * u) + gap + 3 * (button + gap) + (noteLines.Count > 0 ? noteLines.Count * line + gap : 0)
            + button + gap + button + (edit ? gap + button : 0) + (error is null ? 0 : gap + line) + pad;
        double x = Snap((_size.Width - width) / 2), y = Snap((_size.Height - height) * .42);
        ModernFrame("DIALOG_BOX_WINDOW", new(x, y, width, height), "mquit/frame");
        Flush();
        double cy = y + pad;
        ModernText("mquit/title", options.ModernQuitLabel("help text", "Quit Menu"), new(x + pad, cy, width - pad * 2, Math.Round(26 * u)), new(1, .85f, .3f), title, 1);
        cy += Math.Round(26 * u) + gap;
        foreach (var (key, fallback) in new[] { ("BTTN00", "Quit to Desktop"), ("BTTN01", "Quit to Character Selection"), ("BTTN02", "Quit to Login") })
        {
            ModernButton("mquit/" + key, options.ModernQuitLabel(key.ToLowerInvariant(), fallback), new(x + pad, cy, width - pad * 2, button), text, options.ModernQuitTip(key), !busy);
            cy += button + gap;
        }
        for (int i = 0; i < noteLines.Count; i++)
            ModernText("mquit/note/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), noteLines[i], new(x + pad, cy + i * line, width - pad * 2, line), new(.8f, .84f, .92f), small, 1);
        if (noteLines.Count > 0) cy += noteLines.Count * line + gap;
        ModernButton("mquit/OPTIONS", "Options", new(x + pad, cy, width - pad * 2, button), text, "Open the game options.", !busy);
        cy += button + gap;
        if (edit)
        {
            ModernButton("mquit/EDIT", "Edit mode", new(x + pad, cy, width - pad * 2, button), text, "Move and resize interface panels. Create or choose a layout profile.", !busy);
            cy += button + gap;
        }
        // Cancel stays last, under Next's additions.
        ModernButton("mquit/BTTN03", options.ModernQuitLabel("bttn03", "Cancel"), new(x + pad, cy, width - pad * 2, button), text, options.ModernQuitTip("BTTN03"), !busy);
        cy += button;
        if (error is not null)
            ModernText("mquit/error", ModernEllipsis(error, width - pad * 2, small), new(x + pad, cy + gap, width - pad * 2, line), new(1, .5f, .5f), small, 1);
        Flush();
    }

    /// <summary>A click in the Modern UI Quit menu: its buttons are the retail menu's commands; elsewhere it waits.</summary>
    private bool ActivateModernQuit(string id)
    {
        if (!id.StartsWith("mquit/", StringComparison.Ordinal)) return false;
        if (ModernOptions is not { ModernQuitOpen: true } options || options.ModernQuitBusy) return true;
        if (id == "mquit/EDIT") options.ModernQuitEdit();
        else if (id == "mquit/OPTIONS") options.ModernQuitOptions();
        else if (id is "mquit/BTTN00" or "mquit/BTTN01" or "mquit/BTTN02" or "mquit/BTTN03") options.Activate("quit/" + id[6..]);
        return true;
    }
}
