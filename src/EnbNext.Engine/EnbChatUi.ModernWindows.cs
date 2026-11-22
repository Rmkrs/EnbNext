using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI chat windows, as the other Modern UI windows: each is moved by its tab row or its history and sized by
/// the grip at its bottom right corner, and keeps its place. A tab dragged out of a window with several tabs opens a
/// window of its own where it is dropped; dropped on another window it joins that window's tabs (on its own tab row,
/// in its own order). A window moved by its tab row onto another window's tab row joins it whole. The first window
/// holds the input line, the Emote button and the server's offered message.
/// </summary>
internal sealed partial class EnbChatUi
{
    private sealed class ModernWindow
    {
        public ModernTab? Active;
        /// <summary>The top left corner as a fraction of the screen; null keeps the built-in place (the first window).</summary>
        public EnbUiPoint? Position;
        /// <summary>The size in Modern UI units; null keeps the built-in size.</summary>
        public EnbUiPoint? Size;
        public Rect Bounds, Bar;
        /// <summary>The first tab shown on a tab row longer than the window; the tab last brought into view.</summary>
        public int FirstTab;
        public ModernTab? Revealed;
        public readonly List<(ModernTab Tab, Rect Bounds)> TabRects = [];
    }
    private sealed record ModernWindowFile(int Active, double? X, double? Y, double? Width, double? Height);
    private const double ModernMinimumWidth = 180, ModernMinimumHeight = 80;
    private const int ModernMaximumWindows = 16;
    private readonly List<ModernWindow> _modernWindows = [];
    private readonly Dictionary<string, ModernTab> _modernTabHits = [];

    // The press being dragged: a tab, a tab row, a history or a grip.
    private string? _modernGrab;
    private EnbUiPoint _modernGrabStart, _modernGrabOffset, _modernTabGrab, _modernPointer, _modernResizeOrigin;
    private bool _modernDragging;
    private ModernTab? _modernDragTab;
    private int _modernDropWindow = -1;

    /// <summary>Probe view: the chat windows' tab names, window by window.</summary>
    internal IReadOnlyList<string[]> ModernWindowTabs
    {
        get { EnsureModernTabs(); NormalizeModernWindows(); return [.. Enumerable.Range(0, _modernWindows.Count).Select(w => TabsOf(w).Select(t => t.Name).ToArray())]; }
    }

    private IEnumerable<ModernTab> TabsOf(int window) => _modernTabs.Where(t => t.Window == window);
    private static bool IsModernHistory(string id) => id.StartsWith("mchat/", StringComparison.Ordinal) && id.EndsWith("/history", StringComparison.Ordinal);
    /// <summary>A chat window's hit: "mchat/{window}/{part}".</summary>
    private static bool ModernWindowId(string id, out int window, out string part)
    {
        window = -1; part = "";
        if (!id.StartsWith("mchat/", StringComparison.Ordinal)) return false;
        int slash = id.IndexOf('/', 6);
        if (slash < 0 || !int.TryParse(id.AsSpan(6, slash - 6), NumberStyles.None, CultureInfo.InvariantCulture, out window)) return false;
        part = id[(slash + 1)..]; return true;
    }
    /// <summary>Later windows lie above earlier ones; within a window the history is lowest and the grip highest.</summary>
    private static int ModernWindowZ(string id)
    {
        if (!ModernWindowId(id, out int window, out string part)) return 2;
        return Math.Min(window, ModernMaximumWindows) * 10 + (part == "history" ? 0 : part == "bar" ? 1 : part == "resize" ? 3 : 2);
    }

    /// <summary>Every window has tabs and an active one of its own; windows left without tabs go.</summary>
    private void NormalizeModernWindows()
    {
        if (_modernWindows.Count == 0) _modernWindows.Add(new());
        foreach (var tab in _modernTabs) if ((uint)tab.Window >= (uint)_modernWindows.Count) tab.Window = 0;
        var map = new int[_modernWindows.Count];
        var kept = new List<ModernWindow>(_modernWindows.Count);
        for (int w = 0; w < _modernWindows.Count; w++)
        {
            map[w] = -1;
            if (_modernTabs.Exists(t => t.Window == w)) { map[w] = kept.Count; kept.Add(_modernWindows[w]); }
        }
        foreach (var tab in _modernTabs) tab.Window = map[tab.Window];
        _modernWindows.Clear(); _modernWindows.AddRange(kept);
        if (_modernWindows.Count == 0) _modernWindows.Add(new());
        for (int w = 0; w < _modernWindows.Count; w++)
        {
            var window = _modernWindows[w];
            if (window.Active is not { } active || active.Window != w || !_modernTabs.Contains(active)) window.Active = _modernTabs.Find(t => t.Window == w);
        }
    }

    private void LoadModernWindows(ModernTabsFile file)
    {
        _modernWindows.Clear();
        int count = _modernTabs.Count == 0 ? 1 : _modernTabs.Max(t => t.Window) + 1;
        for (int i = 0; i < count; i++)
        {
            var window = new ModernWindow();
            if (file.Windows is { } list && i < list.Length && list[i] is { } saved)
            {
                if (saved is { X: { } x, Y: { } y } && double.IsFinite(x) && double.IsFinite(y)) window.Position = new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
                if (saved is { Width: { } width, Height: { } height } && double.IsFinite(width) && double.IsFinite(height))
                    window.Size = new(Math.Max(ModernMinimumWidth, width), Math.Max(ModernMinimumHeight, height));
                var tabs = TabsOf(i).ToList();
                if (tabs.Count > 0) window.Active = tabs[Math.Clamp(saved.Active, 0, tabs.Count - 1)];
            }
            _modernWindows.Add(window);
        }
        // A file from before the chat windows: one window, its active tab as saved.
        if (file.Windows is null && _modernTabs.Count > 0) _modernWindows[0].Active = _modernTabs[Math.Clamp(file.Active, 0, _modernTabs.Count - 1)];
        NormalizeModernWindows();
    }
    private ModernWindowFile[] SaveModernWindows() =>
        [.. _modernWindows.Select((w, i) => new ModernWindowFile(Math.Max(0, TabsOf(i).ToList().IndexOf(w.Active!)), w.Position?.X, w.Position?.Y, w.Size?.X, w.Size?.Y))];

    /// <summary>The first window's built-in place: <see cref="ModernChatArea"/>, where the layout editor placed the chat
    /// panel before the chat windows placed themselves (until the window is moved or sized).</summary>
    private Rect ModernDefaultWindowArea() => UiLayout?.ApplyStored(EnbUiGroup.ModernChat, ModernChatArea) ?? ModernChatArea;

    /// <summary>A window on screen: its kept place and size, within the screen.</summary>
    private Rect ModernWindowRect(int index)
    {
        var window = _modernWindows[index];
        double z = ModernZoom;
        var start = ModernDefaultWindowArea();
        double width = window.Size is { } size ? size.X * z : start.Width, height = window.Size is { } kept ? kept.Y * z : start.Height;
        width = Math.Clamp(width, Math.Min(ModernMinimumWidth * z, _size.Width), Math.Max(ModernMinimumWidth * z, _size.Width));
        height = Math.Clamp(height, Math.Min(ModernMinimumHeight * z, _size.Height), Math.Max(ModernMinimumHeight * z, _size.Height));
        double x = window.Position is { } at ? at.X * _size.Width : start.X, y = window.Position is { } top ? top.Y * _size.Height : start.Y;
        x = Math.Clamp(x, 0, Math.Max(0, _size.Width - width)); y = Math.Clamp(y, 0, Math.Max(0, _size.Height - height));
        return new(MdSnap(x), MdSnap(y), width, height);
    }
    /// <summary>The window's place and size as they are now become its own (before it is moved or sized).</summary>
    private void KeepModernWindowPlace(ModernWindow window)
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        double z = ModernZoom;
        window.Position ??= new(window.Bounds.X / _size.Width, window.Bounds.Y / _size.Height);
        window.Size ??= new(window.Bounds.Width / z, window.Bounds.Height / z);
    }

    private void PublishModernChat()
    {
        if (_menuLayer is null) return;
        EnsureModernTabs(); NormalizeModernWindows();
        _modernPublishedArea = ModernChatArea;
        _modernTabHits.Clear();
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        for (int w = 0; w < _modernWindows.Count; w++) PublishModernWindow(images, w);
        _bounds = _modernWindows[0].Bounds;
        PublishModernTabGhost(images);
        _draws.Add(new(_menuLayer, 0, new(0, 0, _size.Width, _size.Height), false) { ImagesOnly = true, LayoutGroup = EnbUiGroup.ModernChat, Images = images.ToImmutable() });
    }

    private void PublishModernWindow(ImmutableArray<EnbUiTextDraw>.Builder images, int index)
    {
        var window = _modernWindows[index];
        var panel = ModernWindowRect(index); window.Bounds = panel;
        bool primary = index == 0;
        string p = "mchat/" + index.ToString(CultureInfo.InvariantCulture) + "/";
        float fade = _modernFadeLevel / 10f;
        double z = ModernZoom, line = ModernLineHeight(), pad = 4 * z;
        double tabHeight = Math.Round(line + 6 * z), inputHeight = primary && Editing ? Math.Round(line + 8 * z) : 0;
        var tabs = new Rect(panel.X, panel.Y, panel.Width, tabHeight); window.Bar = tabs;
        var history = new Rect(panel.X, tabs.Bottom, panel.Width, Math.Max(line, panel.Height - tabHeight - inputHeight));
        var input = new Rect(panel.X, history.Bottom, panel.Width, inputHeight);
        var active = window.Active!;

        // Background: a translucent dark field that fades with the tabs; the text always stays.
        if (fade > 0) images.Add(new(ModernFill, new(history.X, history.Y, history.Width, history.Height + inputHeight)) { Opacity = .42f * fade });
        Hit(p + "history", history, () => { }, wheel: delta => ModernScroll(active, delta > 0 ? 3 : -3), group: EnbUiGroup.ModernChat);

        // Tabs: the active tab joined to the field, tabs with unread lines in yellow; right click for the tab menu.
        // A row longer than the window scrolls: arrows at its right end (and the wheel over the tabs) show the others.
        long newest = _modernLineId;
        active.Seen = Math.Max(active.Seen, newest);
        window.TabRects.Clear();
        var all = TabsOf(index).ToList();
        double gap = 2 * z, button = tabs.Height;
        double emoteWidth = primary ? Math.Ceiling(ModernMeasure("Emote") + 14 * z) : 0;
        double controls = button + (primary ? gap + emoteWidth : 0);
        var widths = all.ConvertAll(tab => Math.Ceiling(ModernMeasure(tab.Name) + 14 * z));
        double Span(int first, int last) { double sum = 0; for (int i = first; i <= last; i++) sum += widths[i] + gap; return sum; }
        bool overflow = all.Count > 0 && Span(0, all.Count - 1) + controls > tabs.Width;
        double room = tabs.Width - controls - (overflow ? 2 * (button + gap) + gap : 0);
        if (!overflow) window.FirstTab = 0;
        else
        {
            window.FirstTab = Math.Clamp(window.FirstTab, 0, all.Count - 1);
            // A tab that became active (a click, a drop, a new tab) is brought into view; the player's scrolling stays otherwise.
            int shown = all.IndexOf(active);
            if (!ReferenceEquals(window.Revealed, active) && shown >= 0)
            {
                if (shown < window.FirstTab) window.FirstTab = shown;
                while (window.FirstTab < shown && Span(window.FirstTab, shown) > room) window.FirstTab++;
            }
            // No empty space after the last tab while earlier tabs would fit.
            while (window.FirstTab > 0 && Span(window.FirstTab - 1, all.Count - 1) <= room) window.FirstTab--;
        }
        window.Revealed = active;
        void ScrollTabs(int by) { window.FirstTab = Math.Clamp(window.FirstTab + by, 0, Math.Max(0, all.Count - 1)); _modernActiveAt = Environment.TickCount64; Publish(); }
        Hit(p + "bar", tabs, () => { }, wheel: delta => { if (overflow) ScrollTabs(delta > 0 ? -1 : 1); else ModernScroll(active, delta > 0 ? 3 : -3); }, group: EnbUiGroup.ModernChat);
        double x = tabs.X;
        int last = window.FirstTab - 1;
        for (int i = window.FirstTab; i < all.Count; i++)
        {
            var tab = all[i];
            string id = p + "tab/" + i.ToString(CultureInfo.InvariantCulture);
            double width = widths[i];
            // Only whole tabs; the first one shown is shortened when even it is wider than the room.
            if (x + width > tabs.X + room + gap && i > window.FirstTab) break;
            width = Math.Min(width, Math.Max(button, tabs.X + room - x));
            string name = ModernFitted(tab.Name, width - 14 * z);
            var r = new Rect(x, tabs.Y, width, tabs.Height); x = r.Right + gap; last = i;
            window.TabRects.Add((tab, r)); _modernTabHits[id] = tab;
            bool selected = ReferenceEquals(tab, active), hovered = _hover == id;
            bool unread = !selected && ModernUnread(tab);
            // A tab being dragged out stays in its place, dimmed, until it is dropped.
            float tabOpacity = fade * (ReferenceEquals(tab, _modernDragTab) && _modernDragging ? .35f : 1);
            if (tabOpacity > 0)
            {
                images.Add(new(selected ? ModernFill : ModernTabFill, r) { Opacity = (selected ? .42f : .6f) * tabOpacity });
                if (selected) images.Add(new(ModernAccent, new(r.X, r.Y, r.Width, Math.Max(1, z))) { Opacity = tabOpacity });
                ModernText(images, name, r with { X = r.X + 7 * z, Width = r.Width - 14 * z }, unread ? new(1, .85f, .3f) : hovered || selected ? new(1, 1, 1) : new(.75f, .8f, .88f), tabOpacity);
            }
            Hit(id, r, () => { window.Active = tab; tab.Seen = newest; SaveModernTabs(); Publish(); },
                () => OpenModernTabMenu(tab, r), group: EnbUiGroup.ModernChat);
        }
        // The buttons follow the tabs; on a scrolling row they stand at its right end, after the arrows.
        if (overflow)
        {
            x = tabs.Right - controls - 2 * (button + gap);
            bool earlier = window.FirstTab > 0, later = last < all.Count - 1;
            // Unread lines on a tab out of view light its arrow.
            bool unreadBefore = false, unreadAfter = false;
            for (int i = 0; i < all.Count; i++)
                if ((i < window.FirstTab || i > last) && !ReferenceEquals(all[i], active) && ModernUnread(all[i])) { if (i < window.FirstTab) unreadBefore = true; else unreadAfter = true; }
            ModernTabArrow(images, p + "left", new(x, tabs.Y, button, button), "<", earlier, unreadBefore, fade, () => ScrollTabs(-1));
            ModernTabArrow(images, p + "right", new(x + button + gap, tabs.Y, button, button), ">", later, unreadAfter, fade, () => ScrollTabs(1));
            x += 2 * (button + gap);
        }
        var add = new Rect(x, tabs.Y, button, button);
        if (fade > 0)
        {
            images.Add(new(ModernTabFill, add) { Opacity = .6f * fade });
            ModernText(images, "+", add, _hover == p + "add" ? new(1, 1, 1) : new(.75f, .8f, .88f), fade, center: true);
        }
        Hit(p + "add", add, () => ModernNewTab(index), group: EnbUiGroup.ModernChat);
        if (primary)
        {
            var emote = new Rect(add.Right + gap, tabs.Y, emoteWidth, button);
            if (fade > 0)
            {
                images.Add(new(ModernTabFill, emote) { Opacity = .6f * fade });
                ModernText(images, "Emote", emote, _hover == p + "emote" ? new(1, 1, 1) : new(.75f, .8f, .88f), fade, center: true);
            }
            Hit(p + "emote", emote, () => OpenModernEmoteMenu(emote), group: EnbUiGroup.ModernChat);
            // The server's offered message stands just above the window (below it at the top of the screen).
            double above = tabs.Y - button - gap;
            ModernIncomingMessage(images, new(panel.X, above >= 0 ? above : panel.Bottom + gap, 0, button), z);
        }
        // Where a dragged tab or window would join this window.
        if (_modernDropWindow == index && _modernDragging)
            images.Add(new(ModernAccent, new(tabs.X, tabs.Bottom - Math.Max(1, 2 * z), tabs.Width, Math.Max(1, 2 * z))));

        // History: the newest lines of the active tab at the bottom, wrapped to the field.
        // With times on, each message starts after its time and its wrapped rows line up under its text.
        double timeWidth = _modernTimes ? Math.Ceiling(ModernMeasure("00:00") + 6 * z) : 0;
        // The text keeps clear of the scroll bar at the right.
        double textWidth = history.Width - 2 * pad - Math.Round(10 * z);
        int visibleRows = (int)Math.Max(1, (history.Height - pad) / line);
        var rows = ModernRows(active, textWidth, visibleRows, timeWidth);
        double y = history.Bottom - pad / 2 - rows.Count * line;
        if (primary) _modernShownRows = rows;
        int links = 0;
        foreach (var row in rows)
        {
            if (row.Separator) ModernText(images, row.Text, new(history.X + pad, y, history.Width - 2 * pad, line), new(.55f, .62f, .72f), 1, center: true);
            else
            {
                if (row.Time is { } time) ModernText(images, time, new(history.X + pad, y, timeWidth, line), new(.55f, .62f, .72f), 1);
                var at = new Rect(history.X + pad + row.Indent, y, history.Width - 2 * pad - row.Indent, line);
                if (row.Link is { } link)
                {
                    // A link line (the lag report offer): light blue and underlined, lighter while the pointer is on any of its rows.
                    string id = p + "link/" + (links++).ToString(CultureInfo.InvariantCulture);
                    _modernLinkHits[id] = link;
                    bool over = _modernLinkHits.TryGetValue(_hover, out var hovered) && hovered == link;
                    var underline = at with { Width = Math.Min(at.Width, Math.Ceiling(ModernMeasure(row.Text))) };
                    ModernText(images, row.Text, at, over ? new(.75f, .95f, 1) : new(.4f, .78f, 1), 1);
                    images.Add(new(ModernAccent, new(underline.X, y + line - Math.Max(1, z), underline.Width, Math.Max(1, z))) { Opacity = over ? 1 : .6f });
                    Hit(id, underline, () => RunModernLink(link), wheel: delta => ModernScroll(active, delta > 0 ? 3 : -3), group: EnbUiGroup.ModernChat);
                }
                else ModernText(images, row.Text, at, row.Color, 1);
            }
            y += line;
        }
        ModernChatScrollbar(images, p + "scroll", active, history, ModernRowCount(active, textWidth, timeWidth), visibleRows, fade, z);

        // The input line under the history: destination (click to cycle), then the text and caret.
        if (primary && Editing && !_ignoreEntry && !_privateJoinEntry && !_privatePasswordEntry)
        {
            images.Add(new(ModernAccent, new(input.X, input.Y, input.Width, Math.Max(1, z))) { Opacity = .8f });
            string caption = _modernRename ? "Tab name:" : (_destination == "Channel" ? ChatChannelLabel(_target)
                : _destination is "Secure" or "Reply" ? "To " + _target : _destination) + ":";
            double captionWidth = Math.Ceiling(ModernMeasure(caption) + 4 * z);
            var dest = new Rect(input.X + pad, input.Y, captionWidth, input.Height);
            ModernText(images, caption, dest, _modernRename ? new(1, .85f, .3f) : Color(DestinationType()), 1);
            if (!_modernRename) Hit("destination", dest, () => CycleInputDestination(false), () => CycleInputDestination(false), group: EnbUiGroup.ModernChat); // 0056F9F0: right press cycles forward too (CHAT-D29)
            var field = new Rect(dest.Right + 2 * z, input.Y, input.Right - dest.Right - 2 * z - pad, input.Height);
            Hit("editor", field, () => _editor.Focus(), group: EnbUiGroup.ModernChat);
            _editor.Bounds = field;
            ModernInput(images, field);
        }

        // The grip at the bottom right corner sizes the window; shown with the tabs.
        string grip = p + "resize";
        int gripPixels = (int)Math.Round(12 * z * _scale);
        var gripBox = new Rect(MdSnap(panel.Right - gripPixels / _scale - z), MdSnap(panel.Bottom - gripPixels / _scale - z), gripPixels / _scale, gripPixels / _scale);
        float gripOpacity = _modernGrab == grip ? 1 : fade;
        if (gripOpacity > 0) images.Add(new(EnbModernIcons.Grip(gripPixels, _hover == grip || _modernGrab == grip), gripBox) { Opacity = gripOpacity });
        Hit(grip, gripBox, () => { }, group: EnbUiGroup.ModernChat);
    }

    /// <summary>A scroll arrow of a long tab row: dimmed at the row's end, yellow while a tab out of view has unread lines.</summary>
    private void ModernTabArrow(ImmutableArray<EnbUiTextDraw>.Builder images, string id, Rect r, string glyph, bool enabled, bool unread, float fade, Action scroll)
    {
        if (fade > 0)
        {
            images.Add(new(ModernTabFill, r) { Opacity = .6f * fade });
            ModernText(images, glyph, r, !enabled ? new(.4f, .44f, .5f) : unread ? new(1, .85f, .3f) : _hover == id ? new(1, 1, 1) : new(.75f, .8f, .88f), fade, center: true);
        }
        Hit(id, r, () => { if (enabled) scroll(); }, group: EnbUiGroup.ModernChat);
    }
    /// <summary>A tab name within a width: shortened with an ellipsis when it does not fit.</summary>
    private string ModernFitted(string text, double width)
    {
        if (ModernMeasure(text) <= width) return text;
        for (int length = text.Length - 1; length > 0; length--)
        {
            string shortened = text[..length].TrimEnd() + "…";
            if (ModernMeasure(shortened) <= width) return shortened;
        }
        return "…";
    }

    /// <summary>The tab being dragged follows the pointer.</summary>
    private void PublishModernTabGhost(ImmutableArray<EnbUiTextDraw>.Builder images)
    {
        if (!_modernDragging || _modernDragTab is not { } tab) return;
        double z = ModernZoom, height = Math.Round(ModernLineHeight() + 6 * z), width = Math.Ceiling(ModernMeasure(tab.Name) + 14 * z);
        var r = new Rect(_modernPointer.X - _modernTabGrab.X, _modernPointer.Y - _modernTabGrab.Y, width, height);
        images.Add(new(ModernFill, r) { Opacity = .8f });
        images.Add(new(ModernAccent, new(r.X, r.Y, r.Width, Math.Max(1, z))));
        ModernText(images, tab.Name, r with { X = r.X + 7 * z, Width = r.Width - 14 * z }, new(1, 1, 1), 1);
    }

    /// <summary>A left press on a chat window: a tab (selected at once, as a click always did), the tab row, the history
    /// or the grip; it becomes a drag once the pointer moves.</summary>
    private bool ModernPress(string id, EnbUiPoint point)
    {
        if (!ModernWindowId(id, out int index, out string part) || (uint)index >= (uint)_modernWindows.Count) return false;
        bool tab = part.StartsWith("tab/", StringComparison.Ordinal);
        if (!tab && part is not ("bar" or "history" or "resize")) return false;
        if (part == "history" && _historyPassThrough) return false;
        if (tab && _hits.TryGetValue(id, out var hit)) hit.Left();
        var window = _modernWindows[index];
        _modernGrab = id; _modernGrabStart = _modernPointer = point; _modernDragging = false; _modernDragTab = null; _modernDropWindow = -1;
        _modernGrabOffset = new(point.X - window.Bounds.X, point.Y - window.Bounds.Y);
        _modernTabGrab = tab && _modernTabHits.TryGetValue(id, out var pressed) && window.TabRects.Find(t => ReferenceEquals(t.Tab, pressed)) is { Tab: not null } grabbed
            ? new(point.X - grabbed.Bounds.X, point.Y - grabbed.Bounds.Y) : new(7 * ModernZoom, 4 * ModernZoom);
        _modernResizeOrigin = new(window.Bounds.Width, window.Bounds.Height);
        _drag = id; CaptureRequested?.Invoke(id);
        return true;
    }

    private bool ModernDragMove(string id, EnbUiPoint point)
    {
        if (_modernGrab != id || !ModernWindowId(id, out int index, out string part) || (uint)index >= (uint)_modernWindows.Count) return false;
        _modernPointer = point; _modernActiveAt = Environment.TickCount64;
        var window = _modernWindows[index];
        if (!_modernDragging)
        {
            double dx = point.X - _modernGrabStart.X, dy = point.Y - _modernGrabStart.Y;
            if (dx * dx + dy * dy < 36 * ModernZoom * ModernZoom) return true;
            _modernDragging = true;
            // A tab of a window with several tabs comes out; the only tab of a window moves its window.
            if (part.StartsWith("tab/", StringComparison.Ordinal) && TabsOf(index).Skip(1).Any() && _modernTabHits.TryGetValue(id, out var tab)) _modernDragTab = tab;
            if (_modernDragTab is null) KeepModernWindowPlace(window);
        }
        double z = ModernZoom;
        if (part == "resize")
        {
            window.Size = new(Math.Max(ModernMinimumWidth, (_modernResizeOrigin.X + point.X - _modernGrabStart.X) / z),
                Math.Max(ModernMinimumHeight, (_modernResizeOrigin.Y + point.Y - _modernGrabStart.Y) / z));
            return true;
        }
        if (_modernDragTab is null && _size.Width > 0 && _size.Height > 0)
            window.Position = new((point.X - _modernGrabOffset.X) / _size.Width, (point.Y - _modernGrabOffset.Y) / _size.Height);
        _modernDropWindow = ModernDropTarget(point, index, _modernDragTab is not null);
        return true;
    }

    /// <summary>The window a drop lands on: for a tab the other window under the pointer, for a moved window the other
    /// window whose tab row is under the pointer (so windows can still be moved over each other).</summary>
    private int ModernDropTarget(EnbUiPoint point, int source, bool tab)
    {
        for (int w = _modernWindows.Count - 1; w >= 0; w--)
            if (w != source && (tab ? _modernWindows[w].Bounds : _modernWindows[w].Bar).Contains(point)) return w;
        return -1;
    }

    private bool ModernRelease(string id)
    {
        if (_modernGrab != id) return false;
        if (_modernDragging && ModernWindowId(id, out int index, out string part) && (uint)index < (uint)_modernWindows.Count)
        {
            var source = _modernWindows[index];
            var point = _modernPointer;
            if (_modernDragTab is { } tab)
            {
                if (_modernDropWindow >= 0) MoveModernTab(tab, _modernDropWindow, point);
                else if (source.Bar.Contains(point)) MoveModernTab(tab, index, point); // a new place among its own tabs
                else if (!source.Bounds.Contains(point)) NewModernWindow(tab, index, new(point.X - _modernTabGrab.X, point.Y - _modernTabGrab.Y));
            }
            // A window dropped on another window's tab row joins it, tabs and all.
            else if (part != "resize" && _modernDropWindow >= 0)
            {
                var target = _modernWindows[_modernDropWindow];
                var keep = target.Active;
                foreach (var moved in TabsOf(index).ToList()) MoveModernTab(moved, _modernDropWindow, null);
                target.Active = keep;
            }
            NormalizeModernWindows();
            SaveModernTabs();
        }
        EndModernDrag();
        Publish();
        return true;
    }
    private void EndModernDrag() { _modernGrab = null; _modernDragTab = null; _modernDragging = false; _modernDropWindow = -1; }

    /// <summary>Moves a tab into a window: before the first tab right of the point on its tab row, else after its last
    /// tab. A dropped tab becomes the window's active tab.</summary>
    private void MoveModernTab(ModernTab tab, int window, EnbUiPoint? point)
    {
        _modernTabs.Remove(tab); tab.Window = window;
        int insert = -1;
        if (point is { } at)
            foreach (var (other, r) in _modernWindows[window].TabRects)
                if (!ReferenceEquals(other, tab) && at.X < r.Center.X && _modernTabs.IndexOf(other) is >= 0 and var i) { insert = i; break; }
        if (insert < 0) insert = _modernTabs.FindLastIndex(t => t.Window == window) + 1;
        _modernTabs.Insert(insert, tab);
        if (point is not null) _modernWindows[window].Active = tab;
    }
    /// <summary>A tab in a window of its own (top left corner at the point, the size of the window it came from).</summary>
    private void NewModernWindow(ModernTab tab, int source, EnbUiPoint at)
    {
        if (_modernWindows.Count >= ModernMaximumWindows || _size.Width <= 0 || _size.Height <= 0) return;
        var from = _modernWindows[source];
        double z = ModernZoom;
        _modernWindows.Add(new() { Active = tab, Position = new(at.X / _size.Width, at.Y / _size.Height), Size = new(from.Bounds.Width / z, from.Bounds.Height / z) });
        _modernTabs.Remove(tab); tab.Window = _modernWindows.Count - 1; _modernTabs.Add(tab);
    }
    /// <summary>The tab menu's "Move to a new window": beside the window it came from.</summary>
    private void SplitModernTab(ModernTab tab)
    {
        if ((uint)tab.Window >= (uint)_modernWindows.Count) return;
        var from = _modernWindows[tab.Window].Bounds;
        double z = ModernZoom, offset = 28 * z;
        double x = from.Right + offset + from.Width <= _size.Width ? from.Right + offset : Math.Max(0, from.X - from.Width - offset);
        NewModernWindow(tab, tab.Window, new(x, from.Y));
        NormalizeModernWindows(); SaveModernTabs(); Publish();
    }
}
