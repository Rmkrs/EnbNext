using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI group finder: the retail Find / Leave window's actions and options (the same requests and
/// permissions) in a Modern UI window, and its search results (sortable by name, race/class and level; a click targets
/// the player) in a pane beside it.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private Rect _modernFinderBounds;
    internal EnbUiPoint? ModernFinderPosition { get; set; }
    internal Action<EnbUiPoint>? ModernFinderMoved { get; set; }

    /// <summary>A retail group finder label (fa_tsi24a.ini / fa_tsi25a.ini), trimmed; all-capital labels in title case.</summary>
    private string FinderString(string prefix, string id, string fallback)
    {
        string text = _finderLayouts.TryGetValue(prefix, out var layout) && layout.TryResolve(id, out var definition) &&
            definition.Properties.GetValueOrDefault("String0", "").Trim() is { Length: > 0 } value ? value : fallback;
        return text.Any(char.IsLower) ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    /// <summary>The retail label attached to a group finder piece (by its Attach name), in title case.</summary>
    private string FinderAttached(string prefix, string attach, string fallback)
    {
        string text = _finderLayouts.TryGetValue(prefix, out var layout) ? layout.Definitions.Where(d => d.Properties.GetValueOrDefault("Attach", "").Trim().Equals(attach, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Properties.GetValueOrDefault("String0", "").Trim()).FirstOrDefault(t => t.Length > 0) ?? fallback : fallback;
        return text.Any(char.IsLower) ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    private void ModernGroupFinderPanel(EnbSceneGroupSnapshot group)
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u, rowHeight = Math.Round(22 * u);
        double contentWidth = Math.Round(340 * u);
        var checks = FinderChecks.Where(c => c.Option is not (3 or 4) || group.IsInGroup).ToArray();
        double height = pad * 2 + head + gap + 4 * (head + gap) + gap + checks.Length * rowHeight + (_message is not null ? rowHeight : 0);
        double width = pad * 2 + contentWidth, x, y;
        if (ModernFinderPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernFinderBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernFinderBounds, "finder/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("finder/title", "Group Finder", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("finder/Close", new(x + width - pad - head, cy, head, head));
        _tips["finder/Close"] = _finderTips?.GetValueOrDefault("exit", "") is { Length: > 0 } closeTip ? closeTip : "Close";
        cy += head + gap;
        Button("Looking", _finderSettings.LookingForGroup ? "Stop Looking" : FinderString("finder", "LOOKING_opt00", "Looking for new Group"), !group.IsInGroup,
            _finderSettings.LookingForGroup ? "STOP_LOOKING" : "LOOK_FOR_GROUP", _finderSettings.LookingForGroup);
        Button("Find", _finderList ? "Close List" : FinderString("finder", "FIND_opt00", "Available Recruits"), true, _finderList ? "CLOSE_LIST" : "AVAILABLE_RECRUITS", _finderList);
        Button("Leave", FinderString("finder", "LEAVE_opt00", "Leave Group"), group.IsInGroup && !group.IsLeader, "LEAVE_GROUP");
        Button("Disband", FinderString("finder", "DISBAND_opt00", "Disband Group"), group.IsInGroup && group.IsLeader, "DISBAND_GROUP");
        cy += gap;
        foreach (var check in checks)
        {
            // An option this member may not change is shown, dimmed.
            string id = "finder/Option/" + check.Option.ToString(CultureInfo.InvariantCulture);
            bool enabled = FinderOptionAllowed(check.Option) && !_busy, on = FinderValue(check.Option), hot = enabled && _hover == id;
            var r = new Rect(cx, cy, contentWidth, rowHeight);
            if (enabled) Hit(id, FromPixels(r));
            int box = (int)Math.Round(14 * u * _scale);
            _images.Add(new(EnbModernIcons.Check(box, on, hot), new(Snap(r.X + 4 * u), Snap(r.Center.Y - box / _scale / 2), box / _scale, box / _scale)) { Opacity = enabled ? 1 : .5f });
            ModernText(id + "/text", FinderString("finder", check.Label, ""), new(r.X + 4 * u + box / _scale + 8 * u, r.Y, r.Width, r.Height),
                hot ? new(1, 1, .45f) : enabled ? new(.9f, .93f, 1) : new(.5f, .53f, .6f), text);
            cy += rowHeight;
        }
        if (_message is { } message) ModernText("finder/Error", message, new(cx, cy, contentWidth, rowHeight), new(1, .5f, .5f), small);
        Flush();
        if (_finderList) ModernFinderResults();

        void Button(string action, string label, bool enabled, string tip, bool active = false)
        {
            ModernButton("finder/" + action, label, new(cx, cy, contentWidth, head), text, _finderTips?.GetValueOrDefault(tip, "") ?? "", enabled && !_busy);
            if (active) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 40), new(cx, cy, contentWidth, head)));
            cy += head + gap;
        }
    }

    /// <summary>The search results beside the window: name, race/class and level; a click targets the player.</summary>
    private void ModernFinderResults()
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, rowHeight = Math.Round(22 * u);
        var window = _modernFinderBounds;
        double width = Math.Round(440 * u), contentWidth = width - pad * 2;
        int visible = Math.Clamp(_finderRows.Length, 8, 16);
        _finderScroll = Math.Clamp(Math.Round(_finderScroll), 0, Math.Max(0, _finderRows.Length - visible));
        double height = pad * 2 + head + gap + head + visible * rowHeight + gap;
        double x = window.Right + gap <= _size.Width - width ? window.Right + gap : Math.Max(0, window.X - gap - width);
        double y = Math.Clamp(window.Y, 0, Math.Max(0, _size.Height - height));
        var bounds = new Rect(Snap(x), Snap(y), width, height);
        ModernFrame("DIALOG_BOX_WINDOW", bounds, "finderList/frame");
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        ModernText("finderList/title", FinderAttached("finderList", "search", "Search Results"), new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("finderList/Close", new(bounds.Right - pad - head, cy, head, head));
        cy += head + gap;
        double nameX = cx + 8 * u, nameWidth = Math.Round(200 * u), classX = nameX + nameWidth + 8 * u, classWidth = Math.Round(130 * u), levelX = classX + classWidth + 8 * u, levelWidth = cx + contentWidth - 8 * u - levelX;
        Heading(0, "name", "Name", nameX, nameWidth); Heading(1, "rc", "R/C", classX, classWidth); Heading(2, "level", "Level", levelX, levelWidth);
        cy += head;
        var list = new Rect(cx, cy, contentWidth, visible * rowHeight);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        Hit("finderList/List", FromPixels(list));
        if (_finderRows.Length == 0) ModernText("finderList/none", "No players found yet.", new(nameX, cy, contentWidth - 16 * u, rowHeight), new(.7f, .75f, .88f), text);
        int first = (int)_finderScroll;
        for (int n = 0; n < visible && first + n < _finderRows.Length; n++)
        {
            int index = first + n; var candidate = _finderRows[index];
            var row = new Rect(cx, Snap(cy + n * rowHeight), contentWidth, rowHeight);
            string id = "finderList/Row/" + index.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(row));
            if (n % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            ModernText(id + "/name", ModernEllipsis(candidate.Name, nameWidth, text), new(nameX, row.Y, nameWidth, row.Height), new(1, 1, 1), text);
            ModernText(id + "/rc", ModernEllipsis(candidate.CurrentClassLabel, classWidth, text), new(classX, row.Y, classWidth, row.Height), new(.88f, .91f, .97f), text);
            ModernText(id + "/level", unchecked((int)candidate.NumericField).ToString(CultureInfo.InvariantCulture), new(levelX, row.Y, levelWidth, row.Height), new(.88f, .91f, .97f), text);
            _tips[id] = _finderTips?.GetValueOrDefault("CLICK_TARGET", "") ?? "";
        }
        ModernScrollbar("finderList/List", list, _finderRows.Length, visible, first, v => _finderScroll = v);
        Flush();

        void Heading(int sort, string label, string fallback, double hx, double hw)
        {
            string id = "finderList/Sort/" + sort.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(new(hx, cy, hw, head)));
            ModernText(id + "/text", FinderAttached("finderList", label, fallback), new(hx, cy, hw, head),
                _finderSort == sort ? new(1, .85f, .3f) : _hover == id ? new(1, 1, .45f) : new(.7f, .75f, .88f), text);
            _tips[id] = "Sort by " + FinderAttached("finderList", label, fallback).ToLowerInvariant();
        }
    }
}
