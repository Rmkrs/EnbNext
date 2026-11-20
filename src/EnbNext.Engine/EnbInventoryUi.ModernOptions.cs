using EnbNext.Assets;
using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Options window: the categories in a sidebar at the left, the chosen category's settings at the right
/// (checks, sliders and choices from a list, dependents indented and dimmed while their switch is off), OK and Cancel at the bottom. The
/// settings, their preview and their saving are the Options owner's (EnbOptionsUi.Modern); this draws and routes input.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    internal EnbOptionsUi? ModernOptions { get; set; }
    private Rect _modernOptionsBounds;
    internal EnbUiPoint? ModernOptionsPosition { get; set; }
    internal Action<EnbUiPoint>? ModernOptionsMoved { get; set; }
    private int _moptScroll;
    // The choice row whose list is open, and the first choice the list shows.
    private string? _moptDropdown;
    private int _moptDropScroll;
    private string _moptScrollCategory = "";
    private bool ModernOptionsShown => ModernOptions is { ModernOptionsOpen: true };
    /// <summary>The Options owner opened, closed or changed the window.</summary>
    internal void RefreshModernOptions()
    {
        // Newly opened: in front of the other Modern UI windows.
        if (ModernOptionsShown && !_moptShown) RaiseModern("mopt");
        _moptShown = ModernOptionsShown;
        Publish();
    }
    private bool _moptShown;

    private void ModernOptionsWindow()
    {
        var options = ModernOptions!;
        var rows = options.ModernOptionRows();
        var categories = options.ModernOptionCategories;
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u;
        double rowHeight = Math.Round(28 * u), categoryHeight = Math.Round(26 * u), sideWidth = Math.Round(190 * u), paneWidth = Math.Round(540 * u);
        const int Visible = 14;
        double paneHeight = Visible * rowHeight, contentWidth = sideWidth + gap * 3 + paneWidth;
        double height = pad * 2 + head + gap + head + gap + paneHeight + gap * 3 + head, width = pad * 2 + contentWidth, x, y;
        if (ModernOptionsPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernOptionsBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernOptionsBounds, "mopt/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mopt/title", options.ModernOptionsTitle, new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mopt/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;

        // The categories: the chosen one marked; a click keeps the one left and opens the other.
        var side = new Rect(cx, cy, sideWidth, head + gap + paneHeight);
        ModernFrame("DIALOG_BOX_INFO", side);
        Flush();
        for (int i = 0; i < categories.Count; i++)
        {
            var (key, label) = categories[i];
            string id = "mopt/cat/" + key;
            var r = new Rect(side.X + 3 * u, Snap(side.Y + 4 * u + i * categoryHeight), side.Width - 6 * u, categoryHeight);
            Hit(id, FromPixels(r));
            bool selected = key == options.ModernOptionsCategory;
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 52), r));
            if (selected || _hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale)), r));
            ModernText(id + "/text", label, new(r.X + 10 * u, r.Y, r.Width - 20 * u, r.Height), selected ? new(1, .85f, .3f) : _hover == id ? new(1, 1, .45f) : new(.88f, .91f, .97f), text);
        }

        // The chosen category's settings.
        double px = cx + sideWidth + gap * 3, py = cy;
        string categoryLabel = categories.FirstOrDefault(c => c.Key == options.ModernOptionsCategory).Label ?? "";
        ModernText("mopt/heading", categoryLabel, new(px, py, paneWidth, head), new(.7f, .8f, 1), title);
        py += head + gap;
        var pane = new Rect(px, py, paneWidth, paneHeight);
        ModernFrame("DIALOG_BOX_INFO", pane, "mopt/pane");
        Flush();
        if (_moptScrollCategory != options.ModernOptionsCategory) { _moptScrollCategory = options.ModernOptionsCategory; _moptScroll = 0; }
        _moptScroll = Math.Clamp(_moptScroll, 0, Math.Max(0, rows.Count - Visible));
        double room = rows.Count > Visible ? ModernScrollbarRoom : 0;
        (EnbModernOptionRow Row, Rect Box)? dropdown = null;
        for (int n = 0; n < Visible && _moptScroll + n < rows.Count; n++)
        {
            var row = rows[_moptScroll + n];
            var r = new Rect(pane.X + 4 * u, Snap(pane.Y + n * rowHeight), pane.Width - 8 * u - room, rowHeight);
            double indent = row.Indent * Math.Round(24 * u), left = r.X + 8 * u + indent;
            var labelColor = !row.Enabled ? new EnbRgb(.5f, .53f, .6f) : new EnbRgb(.92f, .94f, 1);
            switch (row.Kind)
            {
                case EnbModernOptionKind.Heading when row.Choices is { Count: 2 } columns:
                {
                    // The key columns' names over the keys below.
                    var (first, second) = ModernKeyColumns(r, left, indent);
                    ModernText("mopt/h/" + n + "/0", columns[0], first, new(.7f, .8f, 1), small, 1);
                    ModernText("mopt/h/" + n + "/1", columns[1], second, new(.7f, .8f, 1), small, 1);
                    break;
                }
                case EnbModernOptionKind.Heading:
                    ModernText("mopt/h/" + n, row.Label, new(left, r.Y, r.Right - left, r.Height), new(.7f, .8f, 1), small);
                    break;
                case EnbModernOptionKind.Keys:
                {
                    // The control and its primary and alternate key; a key waiting for its new key is lit.
                    double labelWidth = Math.Round(220 * u) - indent;
                    ModernText("mopt/k/" + n, ModernEllipsis(row.Label, labelWidth - 8 * u, text), new(left, r.Y, labelWidth, r.Height), labelColor, text);
                    var (first, second) = ModernKeyColumns(r, left, indent);
                    for (int slot = 1; slot <= 2; slot++)
                    {
                        string id = "mopt/key/" + slot.ToString(CultureInfo.InvariantCulture) + "/" + row.Id;
                        var column = slot == 1 ? first : second;
                        var box = new Rect(Snap(column.X), Snap(r.Y + 3 * u), Math.Round(column.Width * _scale) / _scale, Math.Round((r.Height - 6 * u) * _scale) / _scale);
                        bool waiting = row.Value == slot, hot = row.Enabled && (_hover == id || waiting);
                        if (row.Enabled) Hit(id, FromPixels(box));
                        _images.Add(new(EnbModernIcons.Button((int)Math.Round(box.Width * _scale), (int)Math.Round(box.Height * _scale), hot), box) { Opacity = row.Enabled ? 1 : .55f });
                        string key = row.Choices is { } keys && keys.Count >= slot ? keys[slot - 1] : "";
                        ModernText(id + "/text", ModernEllipsis(key, box.Width - 12 * u, text), box, waiting ? new(1, 1, .45f) : hot ? new(1, 1, .6f) : labelColor, text, 1);
                    }
                    break;
                }
                case EnbModernOptionKind.Check:
                {
                    string id = "mopt/row/" + row.Id;
                    bool hot = row.Enabled && _hover == id;
                    int box = (int)Math.Round(16 * u * _scale);
                    // A second check at the right of the line takes its own part of the row.
                    double pairWidth = row.SideId is null ? 0 : Math.Round(ModernMeasure(row.SideLabel, text) + box / _scale + 24 * u);
                    var main = new Rect(r.X, r.Y, r.Width - pairWidth, r.Height);
                    if (row.Enabled) Hit(id, FromPixels(main));
                    _images.Add(new(EnbModernIcons.Check(box, row.On, hot), new(Snap(left), Snap(r.Center.Y - box / _scale / 2), box / _scale, box / _scale)) { Opacity = row.Enabled ? 1 : .5f });
                    ModernText(id + "/text", ModernEllipsis(row.Label, main.Right - left - box / _scale - 10 * u, text), new(left + box / _scale + 10 * u, r.Y, main.Right - left, r.Height), hot ? new(1, 1, .45f) : labelColor, text);
                    if (row.SideId is { } sideKey)
                    {
                        string pairId = "mopt/row/" + sideKey;
                        bool sideHot = row.SideEnabled && _hover == pairId;
                        var sideRect = new Rect(main.Right, r.Y, pairWidth, r.Height);
                        if (row.SideEnabled) Hit(pairId, FromPixels(sideRect));
                        double sideLeft = sideRect.X + 8 * u;
                        _images.Add(new(EnbModernIcons.Check(box, row.SideOn, sideHot), new(Snap(sideLeft), Snap(r.Center.Y - box / _scale / 2), box / _scale, box / _scale)) { Opacity = row.SideEnabled ? 1 : .5f });
                        ModernText(pairId + "/text", row.SideLabel, new(sideLeft + box / _scale + 10 * u, r.Y, sideRect.Right - sideLeft, r.Height),
                            sideHot ? new(1, 1, .45f) : row.SideEnabled ? new EnbRgb(.92f, .94f, 1) : new EnbRgb(.5f, .53f, .6f), text);
                    }
                    break;
                }
                case EnbModernOptionKind.Slider:
                {
                    // Label, track with its filled part and thumb, and the value.
                    string id = "mopt/slider/" + row.Id;
                    double labelWidth = Math.Round(220 * u) - indent, valueWidth = Math.Round(64 * u);
                    ModernText(id + "/text", ModernEllipsis(row.Label, labelWidth - 8 * u, text), new(left, r.Y, labelWidth, r.Height), labelColor, text);
                    var track = new Rect(Snap(left + labelWidth), Snap(r.Center.Y - 3 * u), Math.Round(r.Right - left - labelWidth - valueWidth - 12 * u), Math.Round(6 * u));
                    int steps = Math.Max(2, row.Steps);
                    double fraction = Math.Clamp(row.Value / (double)(steps - 1), 0, 1), thumbWidth = Math.Round(12 * u), thumbHeight = Math.Round(18 * u);
                    var thumb = new Rect(Snap(track.X + (track.Width - thumbWidth) * fraction), Snap(r.Center.Y - thumbHeight / 2), thumbWidth, thumbHeight);
                    bool hot = row.Enabled && (_hover == id || _pressed == id);
                    int trackHeight = (int)Math.Round(track.Height * _scale);
                    _images.Add(new(EnbModernIcons.Pill((int)Math.Round(track.Width * _scale), trackHeight, new(0, 0, 0), 150), track));
                    double filled = Math.Max(track.Height, thumb.Center.X - track.X);
                    if (row.Enabled) _images.Add(new(EnbModernIcons.Pill((int)Math.Round(filled * _scale), trackHeight, new(.35f, .55f, .95f), 220), new(track.X, track.Y, filled, track.Height)));
                    _images.Add(new(EnbModernIcons.Pill((int)Math.Round(thumbWidth * _scale), (int)Math.Round(thumbHeight * _scale), hot ? new(1, 1, .6f) : row.Enabled ? new(.85f, .9f, 1) : new(.45f, .48f, .55f), 255), thumb));
                    ModernText(id + "/value", row.ValueText, new(r.Right - valueWidth, r.Y, valueWidth, r.Height), labelColor, text, 2);
                    if (row.Enabled)
                    {
                        Hit(id, FromPixels(new(track.X, r.Y, track.Width, r.Height)));
                    }
                    break;
                }
                case EnbModernOptionKind.Choice:
                {
                    // Label and a button with the chosen value and an arrow; a click opens the list below (or above) it.
                    string id = "mopt/choice/" + row.Id;
                    double labelWidth = Math.Round(220 * u) - indent;
                    ModernText(id + "/text", ModernEllipsis(row.Label, labelWidth - 8 * u, text), new(left, r.Y, labelWidth, r.Height), labelColor, text);
                    var box = new Rect(Snap(left + labelWidth), Snap(r.Y + 3 * u), Math.Round((r.Right - left - labelWidth) * _scale) / _scale, Math.Round((r.Height - 6 * u) * _scale) / _scale);
                    bool open = _moptDropdown == row.Id, hot = row.Enabled && (_hover == id || open);
                    if (row.Enabled) Hit(id, FromPixels(box));
                    _images.Add(new(EnbModernIcons.Button((int)Math.Round(box.Width * _scale), (int)Math.Round(box.Height * _scale), hot), box) { Opacity = row.Enabled ? 1 : .55f });
                    int arrow = (int)Math.Round(10 * u * _scale);
                    ModernText(id + "/value", ModernEllipsis(row.ValueText, box.Width - arrow / _scale - 26 * u, text), new(box.X + 10 * u, box.Y, box.Width - arrow / _scale - 20 * u, box.Height), hot ? new(1, 1, .45f) : labelColor, text);
                    _images.Add(new(EnbModernIcons.Chevron(arrow, hot, open), new(Snap(box.Right - 10 * u - arrow / _scale), Snap(box.Center.Y - arrow / _scale / 2), arrow / _scale, arrow / _scale)));
                    if (open && row.Enabled) dropdown = (row, box);
                    break;
                }
                case EnbModernOptionKind.Button:
                {
                    // One button, or a row of them (their labels in Choices).
                    var labels = row.Choices ?? [row.Label];
                    double buttonX = left;
                    for (int i = 0; i < labels.Count; i++)
                    {
                        double rowButton = Math.Ceiling(ModernMeasure(labels[i], text) + 48 * u);
                        string id = "mopt/button/" + row.Id + (row.Choices is null ? "" : "/" + i.ToString(CultureInfo.InvariantCulture));
                        ModernButton(id, labels[i], new(Snap(buttonX), Snap(r.Y + 3 * u), rowButton, Math.Round((r.Height - 6 * u) * _scale) / _scale), text, row.ValueText, row.Enabled);
                        buttonX += rowButton + 8 * u;
                    }
                    break;
                }
            }
        }
        ModernScrollbar("mopt/pane", pane, rows.Count, Visible, _moptScroll, v => _moptScroll = v);

        // OK and Cancel; a reason a setting could not be kept at the left.
        double by = y + height - pad - head, buttonWidth = Math.Ceiling(Math.Max(ModernMeasure("OK", text), ModernMeasure("Cancel", text)) + 48 * u);
        ModernButton("mopt/ok", "OK", new(x + width - pad - buttonWidth * 2 - gap * 2, by, buttonWidth, head), text, "Keep the changes and close Options.");
        ModernButton("mopt/cancel", "Cancel", new(x + width - pad - buttonWidth, by, buttonWidth, head), text, "Undo the changes in this category and close Options.");
        if (options.ModernOptionsError is { } error)
            ModernText("mopt/error", ModernEllipsis(error, px - cx + paneWidth - buttonWidth * 2 - gap * 4, small), new(cx, by, contentWidth - buttonWidth * 2 - gap * 4, head), new(1, .5f, .5f), small);
        Flush();
        // An open list is drawn last, over the rest of the window; a list whose row is no longer shown closes.
        if (dropdown is { } drop) ModernOptionsDropdown(drop.Row, drop.Box);
        else _moptDropdown = null;
        if (options.ModernOptionsQuestion is { } question) ModernOptionsQuestionBox(question.Text, question.Notice, pane);
    }

    /// <summary>The two key columns of a controls row, right of the control's name.</summary>
    private (Rect First, Rect Second) ModernKeyColumns(Rect r, double left, double indent)
    {
        double u = ModernUnit, labelWidth = Math.Round(220 * u) - indent, gap = Math.Round(6 * u), width = Math.Floor((r.Right - left - labelWidth - gap) / 2);
        return (new(left + labelWidth, r.Y, width, r.Height), new(left + labelWidth + width + gap, r.Y, width, r.Height));
    }

    /// <summary>A key change's question (Yes and No) or notice (OK) over the window, which waits for the answer.</summary>
    private void ModernOptionsQuestionBox(string text, bool notice, Rect pane)
    {
        double u = ModernUnit, size = 14 * u, pad = Math.Round(16 * u), width = Math.Round(400 * u), lineHeight = Math.Round(20 * u), head = 24 * u;
        Hit("mopt/ask", FromPixels(_modernOptionsBounds));
        // The window beneath is dimmed while it waits.
        Flush();
        _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 120), _modernOptionsBounds));
        Flush();
        var lines = text.Split('\n').SelectMany(line => ModernWrap(line.Trim(), width - pad * 2, size)).ToList();
        double height = Math.Round(pad * 2 + lines.Count * lineHeight + 12 * u + head);
        var box = new Rect(Snap(pane.Center.X - width / 2), Snap(pane.Center.Y - height / 2), width, height);
        ModernFrame("DIALOG_BOX_WINDOW", box, "mopt/ask/frame");
        Flush();
        for (int i = 0; i < lines.Count; i++)
            ModernText("mopt/ask/" + i.ToString(CultureInfo.InvariantCulture), lines[i], new(box.X + pad, box.Y + pad + i * lineHeight, width - pad * 2, lineHeight), new(.92f, .94f, 1), size, 1);
        double by = box.Bottom - pad - head, buttonWidth = Math.Ceiling(Math.Max(ModernMeasure("Yes", size), ModernMeasure("No", size)) + 48 * u);
        if (notice) ModernButton("mopt/ask/ok", "OK", new(box.Center.X - buttonWidth / 2, by, buttonWidth, head), size, "Close this message.");
        else
        {
            ModernButton("mopt/ask/yes", "Yes", new(box.Center.X - buttonWidth - 4 * u, by, buttonWidth, head), size, "Yes, go ahead.");
            ModernButton("mopt/ask/no", "No", new(box.Center.X + 4 * u, by, buttonWidth, head), size, "No, leave it as it is.");
        }
        Flush();
    }

    private void ModernOptionsDropdown(EnbModernOptionRow row, Rect box)
    {
        var choices = row.Choices ?? [];
        int visible = Math.Min(choices.Count, 8);
        if (visible == 0) return;
        double u = ModernUnit, text = 14 * u, itemHeight = Math.Round(26 * u), listHeight = visible * itemHeight + 8 * u, bottom = _modernOptionsBounds.Bottom - 6 * u;
        // Below the button while it fits in the window, else above it.
        double top = box.Bottom + 2 * u + listHeight <= bottom ? box.Bottom + 2 * u : Math.Max(_modernOptionsBounds.Y + 6 * u, box.Y - 2 * u - listHeight);
        var list = new Rect(box.X, Snap(top), box.Width, listHeight);
        ModernFrame("DIALOG_BOX_INFO", list, "mopt/drop");
        Flush();
        _moptDropScroll = Math.Clamp(_moptDropScroll, 0, Math.Max(0, choices.Count - visible));
        double room = choices.Count > visible ? ModernScrollbarRoom : 0;
        for (int n = 0; n < visible; n++)
        {
            int index = _moptDropScroll + n;
            string id = "mopt/pick/" + index.ToString(CultureInfo.InvariantCulture);
            var r = new Rect(list.X + 4 * u, Snap(list.Y + 4 * u + n * itemHeight), list.Width - 8 * u - room, itemHeight);
            Hit(id, FromPixels(r));
            bool selected = index == row.Value, hot = _hover == id;
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 52), r));
            if (hot) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale)), r));
            ModernText(id + "/text", ModernEllipsis(choices[index], r.Width - 20 * u, text), new(r.X + 10 * u, r.Y, r.Width - 20 * u, r.Height),
                selected ? new(1, .85f, .3f) : hot ? new(1, 1, .45f) : new(.92f, .94f, 1), text);
        }
        ModernScrollbar("mopt/drop", list, choices.Count, visible, _moptDropScroll, v => _moptDropScroll = v);
        Flush();
    }

    private bool ActivateModernOptions(string id, bool right)
    {
        if (!id.StartsWith("mopt/", StringComparison.Ordinal)) return false;
        if (right || ModernOptions is not { ModernOptionsOpen: true } options) return true;
        if (id.StartsWith("mopt/ask", StringComparison.Ordinal))
        {
            // A key question waits for its answer; the rest of the window waits with it.
            if (id is "mopt/ask/yes" or "mopt/ask/ok" or "mopt/ask/no") options.ModernOptionAnswer(id != "mopt/ask/no");
            return true;
        }
        if (id.StartsWith("mopt/key/", StringComparison.Ordinal) && id.Length > 11 && id[10] == '/')
        {
            const string prefix = "controls/key/";
            string row = id[11..];
            if (row.StartsWith(prefix, StringComparison.Ordinal)) options.ModernOptionKey(row[prefix.Length..], id[9] - '0');
            _moptDropdown = null; return true;
        }
        if (id.StartsWith("mopt/pick/", StringComparison.Ordinal))
        {
            if (_moptDropdown is { } list && int.TryParse(id.AsSpan(10), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
            {
                options.ModernOptionChoose(list, index);
                if (list == "controls/group") _moptScroll = 0;
            }
            _moptDropdown = null; return true;
        }
        if (id == "mopt/drop") return true;
        if (id.StartsWith("mopt/choice/", StringComparison.Ordinal))
        {
            // The button opens its list (at the chosen value) or closes it again.
            string key = id[12..];
            _moptDropdown = _moptDropdown == key ? null : key;
            if (_moptDropdown is not null) _moptDropScroll = Math.Max(0, (options.ModernOptionRows().FirstOrDefault(r => r.Id == key)?.Value ?? 0) - 3);
            InteractionSoundRequested?.Invoke("fontclick.wav");
            return true;
        }
        // Any other click closes an open list.
        _moptDropdown = null;
        if (id is "mopt/close" or "mopt/cancel") options.ModernOptionsCancel();
        else if (id == "mopt/ok") options.ModernOptionsOk();
        else if (id.StartsWith("mopt/cat/", StringComparison.Ordinal)) options.SelectModernCategory(id[9..]);
        else if (id.StartsWith("mopt/row/", StringComparison.Ordinal)) options.ModernOptionToggle(id[9..]);
        else if (id.StartsWith("mopt/button/", StringComparison.Ordinal)) options.ModernOptionPress(id[12..]);
        return true;
    }

    /// <summary>A press on a slider moves it to the pointer; dragging keeps moving it.</summary>
    private void BeginModernOptionSlider(string id, EnbUiPoint point)
    {
        if (id.StartsWith("mopt/slider/", StringComparison.Ordinal)) SlideModernOption(id[12..], point);
    }
    private bool DragModernOptionSlider(EnbUiPoint point)
    {
        if (_pressed?.StartsWith("mopt/slider/", StringComparison.Ordinal) != true) return false;
        SlideModernOption(_pressed[12..], point);
        return true;
    }
    private void SlideModernOption(string option, EnbUiPoint point)
    {
        // The thumb always lies on the track, so the hit area is the track and the pointer's place on it is the value.
        if (ModernOptions is not { ModernOptionsOpen: true } options || !_hits.TryGetValue("mopt/slider/" + option, out var hit) ||
            options.ModernOptionRows().FirstOrDefault(r => r.Id == option) is not { Enabled: true } row) return;
        var track = hit.Bounds;
        int value = (int)Math.Round(Math.Clamp((point.X - track.X) / Math.Max(1, track.Width), 0, 1) * (Math.Max(2, row.Steps) - 1));
        options.ModernOptionSlide(option, value);
        Publish();
    }

    /// <summary>The wheel over a slider steps it; over the settings it scrolls them.</summary>
    internal bool ScrollModernOptions(string id, double delta)
    {
        if (!ModernOptionsShown || !id.StartsWith("mopt/", StringComparison.Ordinal) || delta == 0) return false;
        if (id.StartsWith("mopt/pick/", StringComparison.Ordinal) || id.StartsWith("mopt/drop", StringComparison.Ordinal))
        { _moptDropScroll = Math.Max(0, _moptDropScroll + (delta < 0 ? 1 : -1)); Publish(); return true; }
        if (id.StartsWith("mopt/slider/", StringComparison.Ordinal) && !id.EndsWith("/scrollThumb", StringComparison.Ordinal) &&
            ModernOptions!.ModernOptionRows().FirstOrDefault(r => r.Id == id[12..]) is { Enabled: true } row)
            ModernOptions.ModernOptionSlide(row.Id, Math.Clamp(row.Value + (delta > 0 ? 1 : -1), 0, Math.Max(0, row.Steps - 1)));
        else if (!id.StartsWith("mopt/cat/", StringComparison.Ordinal)) _moptScroll = Math.Max(0, _moptScroll + (delta < 0 ? 1 : -1));
        Publish(); return true;
    }
}
