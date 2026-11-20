using System.Collections.Immutable;
using System.Text.Json;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI chat (opt-in, with Modern UI): the retail chat history shown as a WoW-style panel at the bottom left,
/// with the player's own tabs (each a set of retail message types), the Modern UI's clear font at a chosen size, a
/// background and tab row that fade when idle, and the input line under it. Sending, destinations, commands, menus and
/// dialogs stay the retail chat owner's; only the history panes and their frame are replaced.
/// </summary>
internal sealed partial class EnbChatUi
{
    private sealed class ModernTab
    {
        public string Name = "";
        public readonly bool[] Kinds = new bool[(int)ModernKind.Count];
        /// <summary>Named channels shown (public and private); AllChannels shows every channel.</summary>
        public readonly List<string> Channels = [];
        public bool AllChannels;
        public long Seen;
        public int Scroll;
    }
    private sealed record ModernTabFile(string Name, string[]? Kinds, string[]? Channels, bool AllChannels);
    private sealed record ModernTabsFile(ModernTabFile[] Tabs, int Active, double? DialogX = null, double? DialogY = null);
    /// <summary>
    /// What a chat line is, for tab filters. The server uses the retail message type mostly as a colour (its notices
    /// arrive as the green NPC type, loot as the group type, rewards as the private type), so player speech is
    /// sorted by type, channel lines by their channel, and server notices (lines without a speaker) by their text:
    /// combat and loot/reward notices first, the rest by type.
    /// </summary>
    private enum ModernKind { Say, OutOfCharacter, Local, Broadcast, Private, Group, Guild, Combat, Loot, Notices, Tips, Errors, Warnings, Echo, Count }
    private static readonly (string Label, ModernKind Kind)[] ModernCategories =
    [
        ("Say (in character)", ModernKind.Say), ("Out of character", ModernKind.OutOfCharacter), ("Local", ModernKind.Local), ("Broadcast", ModernKind.Broadcast),
        ("Private messages", ModernKind.Private), ("Group", ModernKind.Group), ("Guild", ModernKind.Guild), ("Combat", ModernKind.Combat),
        ("Loot and rewards", ModernKind.Loot), ("Game notices", ModernKind.Notices), ("Tips", ModernKind.Tips), ("Errors", ModernKind.Errors),
        ("Warnings and alerts", ModernKind.Warnings), ("Echo and debug", ModernKind.Echo),
    ];
    private static readonly string[] ModernLootWords = ["looted", "loot", "credits", "experience", "picked up", "you have gained", "you receive", "awarded", "reward"];
    private static readonly string[] ModernCombatWords = ["activated", "resist", "attempt to", "failed using", "ability", "damage", " hit", "missed", "destroyed", "killed", "interrupt", "out of range", "hacking", "hacked", "attacking", "attacks"];
    private static bool ModernHasWord(string text, string[] words)
    {
        foreach (string word in words) if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    /// <summary>Test view: the number of tabs after loading the character's tab file.</summary>
    internal int ModernTabCount { get { EnsureModernTabs(); return _modernTabs.Count; } }
    /// <summary>Probe view: the texts a tab shows, newest last.</summary>
    internal IEnumerable<string> ModernTabTexts(int tab)
    {
        EnsureModernTabs();
        foreach (var line in _modernHistory) if (ModernShows(_modernTabs[tab], line)) yield return line.Line.Text;
    }
    private static ModernKind ModernClassify(EnbChatLine line)
    {
        bool notice = line.Peer is null && line.Channel is null;
        if (notice && line.Type is 0 or 3 or 4 or 5 or 6 or 20)
        {
            if (ModernHasWord(line.Text, ModernLootWords)) return ModernKind.Loot;
            if (line.Type == 20 || ModernHasWord(line.Text, ModernCombatWords)) return ModernKind.Combat;
            if (line.Type is 0 or 5) return ModernKind.Notices;
        }
        return line.Type switch
        {
            0 => ModernKind.Say, 1 => ModernKind.OutOfCharacter, 2 => ModernKind.Broadcast, 3 => ModernKind.Private, 4 => ModernKind.Group,
            5 => ModernKind.Notices, 6 => ModernKind.Tips, 7 => ModernKind.Local, 8 => ModernKind.Guild, 20 => ModernKind.Combat,
            17 or 18 or 19 => ModernKind.Errors, 21 or 22 => ModernKind.Warnings, 16 or 23 => ModernKind.Echo, _ => ModernKind.Notices,
        };
    }
    private static readonly EnbUiRaster ModernFill = Pixel(0, 0, 0, 255), ModernTabFill = Pixel(18, 30, 52, 255), ModernAccent = Pixel(70, 160, 210, 255),
        ModernCheck = Pixel(90, 220, 120, 255);
    private const long ModernIdleMilliseconds = 6000, ModernFadeMilliseconds = 700;
    private readonly List<ModernTab> _modernTabs = [];
    private int _modernTab;
    private bool _modernChat;
    private long _modernActiveAt = Environment.TickCount64;
    private int _modernFadeLevel = 10;
    private int _modernMenuTab;
    private bool _modernRename;
    private readonly EnbRetailTextEditor _tabNameInput = new(16);
    private uint? _modernTabsOwner;
    private Guid _modernStream;
    private readonly List<(string Text, float Size, EnbRgb Color, EnbUiRaster? Raster)> _modernRasters = [];

    /// <summary>Modern UI owns the chat panel (Modern UI on); off restores the retail chat window unchanged.</summary>
    internal bool ModernChat
    {
        get => _modernChat;
        set { if (_modernChat == value) return; _modernChat = value; if (_modernRename) CloseInput(); _modernActiveAt = Environment.TickCount64; Publish(); }
    }
    /// <summary>Chat text size in Modern UI units (Modern UI options).</summary>
    internal double ModernTextSize { get => _modernTextSize; set { if (_modernTextSize == value) return; _modernTextSize = value; _modernRasters.Clear(); Publish(); } }
    private double _modernTextSize = 12;
    /// <summary>Where a character's tabs are kept (Next preferences); null keeps them for the session only.</summary>
    internal Func<uint, string?>? ModernTabsPath { get; set; }

    private double ModernZoom => Math.Max(1, _size.Height / 760);
    /// <summary>What the panel keeps clear of: the Modern HUD's player frame (with its buffs) and the left edge of the
    /// action bar's engine hub, before placement; null before the HUD is drawn.</summary>
    internal Func<(Rect PlayerFrame, double ClusterLeft)?>? ModernHudClearance { get; set; }
    /// <summary>The panel before its placement: the bottom left, inside the gutter, clear of the unit frames. Where the
    /// screen is too narrow for that (narrower than 16:9), it reaches to the action bar's hub instead and keeps under the
    /// player frame: wider and lower.</summary>
    internal Rect ModernChatArea
    {
        get
        {
            double z = ModernZoom, band = EnbModernHud.EdgeRailHidden ? 0 : EnbGutterUi.Band, gx = band * _size.Width / 640 + 6, gy = band * _size.Height / 480 + 6;
            double width = Math.Clamp(_size.Width / 2 - 400 * z - gx - 8, 240 * z, 520 * z), height = Math.Min(_size.Height * .32, 230 * z);
            if (ModernHudClearance?.Invoke() is { } clear && gx + width > clear.PlayerFrame.X - 4 * z)
            {
                double bottom = _size.Height - gy, top = clear.PlayerFrame.Bottom + 4 * z;
                width = Math.Min(520 * z, clear.ClusterLeft - 8 * z - gx);
                // Too little room under the frame (or none beside the hub): beside the frame at full height instead.
                if (bottom - top < 3 * ModernLineHeight() || width < 160 * z) width = Math.Max(120 * z, clear.PlayerFrame.X - 4 * z - gx);
                else height = bottom - top;
            }
            return new(gx, _size.Height - gy - height, width, height);
        }
    }

    private Rect _modernPublishedArea;
    /// <summary>The panel's built-in place follows the HUD it keeps clear of (its arrangement, or the HUD's first draw).</summary>
    private bool AdvanceModernPlace() => ModernChat && ModernChatArea != _modernPublishedArea;

    private void EnsureModernTabs()
    {
        uint? owner = _session();
        // A new chat stream (login, sector) starts with its history read: only later lines mark tabs unread.
        if (_absorbStream != _modernStream)
        {
            _modernStream = _absorbStream;
            foreach (var tab in _modernTabs) tab.Seen = _modernLineId;
        }
        if (_modernTabs.Count > 0 && owner == _modernTabsOwner) return;
        _modernTabsOwner = owner; _modernTabs.Clear(); _modernTab = 0;
        try
        {
            if (owner is uint character && ModernTabsPath?.Invoke(character) is { } path && EnbNext.Installation.EnbPathCase.FileExists(path) &&
                JsonSerializer.Deserialize<ModernTabsFile>(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path))) is { Tabs.Length: > 0 } file)
            {
                // Tabs saved before categories and channels (no Kinds) are replaced by the defaults.
                foreach (var saved in file.Tabs.Where(t => t.Kinds is not null))
                {
                    var tab = new ModernTab { Name = saved.Name.Length > 0 ? saved.Name[..Math.Min(16, saved.Name.Length)] : "Tab", AllChannels = saved.AllChannels };
                    foreach (string kind in saved.Kinds!) if (Enum.TryParse<ModernKind>(kind, out var k) && k < ModernKind.Count) tab.Kinds[(int)k] = true;
                    if (saved.Channels is { } channels) tab.Channels.AddRange(channels);
                    _modernTabs.Add(tab);
                }
                if (_modernTabs.Count > 0) _modernTab = Math.Clamp(file.Active, 0, _modernTabs.Count - 1);
                if (file is { DialogX: { } dx, DialogY: { } dy }) _modernDialogCentre = new(Math.Clamp(dx, 0, 1), Math.Clamp(dy, 0, 1));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _modernTabs.Clear(); } // The default tabs.
        if (_modernTabs.Count == 0) DefaultModernTabs();
        foreach (var tab in _modernTabs) tab.Seen = _modernLineId;
    }
    /// <summary>The tabs file was replaced (the tabs are one set for all characters): read it again, keeping the tab in view.</summary>
    internal void ReloadModernTabs()
    {
        if (_modernTabs.Count == 0) return;
        int keep = _modernTab;
        _modernTabs.Clear(); EnsureModernTabs();
        _modernTab = Math.Clamp(keep, 0, Math.Max(0, _modernTabs.Count - 1));
        Publish();
    }
    private void DefaultModernTabs()
    {
        _modernTabs.Clear(); _modernTab = 0;
        ModernTab Tab(string name, bool channels, params ModernKind[] kinds) { var tab = new ModernTab { Name = name, AllChannels = channels }; foreach (var kind in kinds) tab.Kinds[(int)kind] = true; _modernTabs.Add(tab); return tab; }
        var general = Tab("General", true); for (int kind = 0; kind < (int)ModernKind.Count; kind++) general.Kinds[kind] = kind != (int)ModernKind.Combat;
        Tab("Social", true, ModernKind.Say, ModernKind.OutOfCharacter, ModernKind.Local, ModernKind.Broadcast, ModernKind.Private, ModernKind.Group, ModernKind.Guild);
        // Loot and rewards come from the fight, so they follow it; the category stays separate to move elsewhere.
        Tab("Combat", false, ModernKind.Combat, ModernKind.Loot);
        Tab("System", false, ModernKind.Notices, ModernKind.Tips, ModernKind.Errors, ModernKind.Warnings, ModernKind.Echo);
    }
    private void SaveModernTabs()
    {
        if (_modernTabsOwner is not uint character || ModernTabsPath?.Invoke(character) is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new ModernTabsFile([.. _modernTabs.Select(t => new ModernTabFile(t.Name,
                [.. Enumerable.Range(0, (int)ModernKind.Count).Where(i => t.Kinds[i]).Select(i => ((ModernKind)i).ToString())], [.. t.Channels], t.AllChannels))], _modernTab, _modernDialogCentre?.X, _modernDialogCentre?.Y);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(file)); File.Move(path + ".tmp", EnbNext.Installation.EnbPathCase.Resolve(path), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "Could not save chat tabs: " + ex.Message; }
    }

    /// <summary>Background and tab visibility in tenths (10 shown, 0 faded out).</summary>
    internal int ModernFadeLevel => _modernFadeLevel;
    private bool ModernActive => _hover.StartsWith("mchat", StringComparison.Ordinal) || Editing;
    /// <summary>The background and tabs fade in tenths; true when the visible level changed and the panel must be drawn again.</summary>
    private bool AdvanceModernFade()
    {
        if (!_modernChat) return false;
        long now = Environment.TickCount64;
        if (ModernActive) _modernActiveAt = now;
        long idle = now - _modernActiveAt - ModernIdleMilliseconds;
        int level = idle <= 0 ? 10 : (int)Math.Round(10 * Math.Max(0, 1 - idle / (double)ModernFadeMilliseconds));
        if (level == _modernFadeLevel) return false;
        _modernFadeLevel = level; return true;
    }

    private void PublishModernChat()
    {
        if (_menuLayer is null) return;
        EnsureModernTabs();
        var renderer = EnbModernText.Renderer;
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var panel = ModernChatArea; _modernPublishedArea = panel;
        float fade = _modernFadeLevel / 10f;
        double z = ModernZoom, line = ModernLineHeight(), pad = 4 * z;
        double tabHeight = Math.Round(line + 6 * z), inputHeight = Editing ? Math.Round(line + 8 * z) : 0;
        var tabs = new Rect(panel.X, panel.Y, panel.Width, tabHeight);
        var history = new Rect(panel.X, tabs.Bottom, panel.Width, panel.Height - tabHeight - inputHeight);
        var input = new Rect(panel.X, history.Bottom, panel.Width, inputHeight);
        _modernTab = Math.Clamp(_modernTab, 0, _modernTabs.Count - 1);
        var active = _modernTabs[_modernTab];

        // Background: a translucent dark field that fades with the tabs; the text always stays.
        if (fade > 0) images.Add(new(ModernFill, new(history.X, history.Y, history.Width, history.Height + inputHeight)) { Opacity = .42f * fade });
        Hit("mchat/history", history, () => { }, wheel: delta => ModernScroll(delta > 0 ? 3 : -3), group: EnbUiGroup.ModernChat);

        // Tabs: the active tab joined to the field, tabs with unread lines in yellow; right click for the tab menu.
        long newest = _modernLineId;
        active.Seen = Math.Max(active.Seen, newest);
        double x = tabs.X;
        for (int i = 0; i < _modernTabs.Count; i++)
        {
            var tab = _modernTabs[i]; int index = i;
            double width = Math.Ceiling(ModernMeasure(tab.Name) + 14 * z);
            var r = new Rect(x, tabs.Y, width, tabs.Height); x = r.Right + 2 * z;
            bool selected = i == _modernTab, hovered = _hover == "mchat/tab/" + i;
            bool unread = !selected && ModernUnread(tab);
            float tabOpacity = fade;
            if (tabOpacity > 0)
            {
                images.Add(new(selected ? ModernFill : ModernTabFill, r) { Opacity = (selected ? .42f : .6f) * tabOpacity });
                if (selected) images.Add(new(ModernAccent, new(r.X, r.Y, r.Width, Math.Max(1, z))) { Opacity = tabOpacity });
                ModernText(images, tab.Name, r with { X = r.X + 7 * z, Width = r.Width - 14 * z }, unread ? new(1, .85f, .3f) : hovered || selected ? new(1, 1, 1) : new(.75f, .8f, .88f), tabOpacity);
            }
            Hit("mchat/tab/" + i, r, () => { _modernTab = index; _modernTabs[index].Seen = newest; SaveModernTabs(); Publish(); },
                () => OpenModernTabMenu(index, r), group: EnbUiGroup.ModernChat);
        }
        var add = new Rect(x, tabs.Y, tabs.Height, tabs.Height);
        if (fade > 0)
        {
            images.Add(new(ModernTabFill, add) { Opacity = .6f * fade });
            ModernText(images, "+", add, _hover == "mchat/add" ? new(1, 1, 1) : new(.75f, .8f, .88f), fade, center: true);
        }
        Hit("mchat/add", add, ModernNewTab, group: EnbUiGroup.ModernChat);
        var emote = new Rect(add.Right + 2 * z, tabs.Y, Math.Ceiling(ModernMeasure("Emote") + 14 * z), tabs.Height);
        if (fade > 0)
        {
            images.Add(new(ModernTabFill, emote) { Opacity = .6f * fade });
            ModernText(images, "Emote", emote, _hover == "mchat/emote" ? new(1, 1, 1) : new(.75f, .8f, .88f), fade, center: true);
        }
        Hit("mchat/emote", emote, () => OpenModernEmoteMenu(emote), group: EnbUiGroup.ModernChat);
        ModernIncomingMessage(images, new(emote.Right + 6 * z, tabs.Y, 0, tabs.Height), z);

        // History: the newest lines of the active tab at the bottom, wrapped to the field.
        // With times on, each message starts after its time and its wrapped rows line up under its text.
        double timeWidth = _modernTimes ? Math.Ceiling(ModernMeasure("00:00") + 6 * z) : 0;
        // The text keeps clear of the scroll bar at the right.
        double textWidth = history.Width - 2 * pad - Math.Round(10 * z);
        int visibleRows = (int)Math.Max(1, (history.Height - pad) / line);
        var rows = ModernRows(active, textWidth, visibleRows, timeWidth);
        double y = history.Bottom - pad / 2 - rows.Count * line;
        _modernShownRows = rows;
        foreach (var row in rows)
        {
            if (row.Separator) ModernText(images, row.Text, new(history.X + pad, y, history.Width - 2 * pad, line), new(.55f, .62f, .72f), 1, center: true);
            else
            {
                if (row.Time is { } time) ModernText(images, time, new(history.X + pad, y, timeWidth, line), new(.55f, .62f, .72f), 1);
                ModernText(images, row.Text, new(history.X + pad + row.Indent, y, history.Width - 2 * pad - row.Indent, line), row.Color, 1);
            }
            y += line;
        }
        ModernChatScrollbar(images, active, history, ModernRowCount(active, textWidth, timeWidth), visibleRows, fade, z);

        // The input line under the history: destination (click to cycle), then the text and caret.
        if (Editing && !_ignoreEntry && !_privateJoinEntry && !_privatePasswordEntry)
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
        _draws.Add(new(_menuLayer, 0, panel, false) { ImagesOnly = true, LayoutGroup = EnbUiGroup.ModernChat, Images = images.ToImmutable() });
    }

    private byte DestinationType() => _destination switch { "Local" => 7, "Guild" => 8, "Group" => 4, "Secure" or "Reply" => 3, "Channel" => 13, _ => 2 };
    private bool ModernUnread(ModernTab tab)
    {
        for (int i = _modernHistory.Count - 1; i >= 0 && _modernHistory[i].Id > tab.Seen; i--)
            if (ModernShows(tab, _modernHistory[i])) return true;
        return false;
    }
    // Next Modern UI: the server's "Current target canceled." answering a target the player just cleared with Escape is not shown.
    private long _quietAfter = long.MaxValue, _quietUntil, _quietSequence = long.MinValue;
    internal void QuietTargetCanceled() { _quietAfter = _modernLineId; _quietUntil = Environment.TickCount64 + 5000; }
    private bool Quieted(ModernLine line)
    {
        if (line.Id == _quietSequence) return true;
        if (line.Id <= _quietAfter || Environment.TickCount64 > _quietUntil || line.Line.Text != "Current target canceled.") return false;
        _quietSequence = line.Id; _quietAfter = long.MaxValue; return true;
    }

    private bool ModernShows(ModernTab tab, ModernLine entry)
    {
        if (Quieted(entry)) return false;
        var line = entry.Line;
        if (line.Type != 13 && _hiddenTypes.Contains(line.Type)) return false;
        if (line.Channel is { } channel) return tab.AllChannels || tab.Channels.Contains(channel, StringComparer.OrdinalIgnoreCase);
        return tab.Kinds[(int)ModernClassify(line)];
    }
    /// <summary>The channels a tab can choose from: the public channels, joined ones and the private channel.</summary>
    private List<string> ModernChannelChoices()
    {
        var list = new List<string>(PublicChannels);
        foreach (string channel in _data?.Channels ?? []) if (!list.Contains(channel, StringComparer.OrdinalIgnoreCase)) list.Add(channel);
        if (_data?.PrivateChannel is { Length: > 0 } own && !list.Contains(own, StringComparer.OrdinalIgnoreCase)) list.Add(own);
        return list;
    }

    private readonly record struct ModernRow(string Text, EnbRgb Color, double Indent, string? Time = null, bool Separator = false);
    private List<ModernRow> _modernShownRows = [];
    /// <summary>Probe view: the rows the panel last drew, oldest first (a time before its text, a separator between dashes).</summary>
    internal IEnumerable<string> ModernShownRows => _modernShownRows.Select(r => r.Separator ? "— " + r.Text + " —" : r.Time is { } t ? t + " " + r.Text : r.Text);
    /// <summary>
    /// The rows to show, oldest first: wrapped lines of the tab from its scroll position upwards. A line between two
    /// sessions or two days names the newer one's start (a kept history reaches back over both).
    /// </summary>
    private List<ModernRow> ModernRows(ModernTab tab, double width, int visible, double timeWidth)
    {
        var rows = new List<ModernRow>(visible + 8);
        int skip = tab.Scroll;
        double continuation = timeWidth > 0 ? timeWidth : 10 * ModernZoom;
        ModernLine? newer = null;
        for (int i = _modernHistory.Count - 1; i >= 0 && rows.Count < visible; i--)
        {
            var source = _modernHistory[i];
            if (!ModernShows(tab, source)) continue;
            if (newer is not null && (newer.Session != source.Session || newer.Time.Date != source.Time.Date))
            {
                if (skip > 0) skip--;
                else rows.Insert(0, new(newer.Session != source.Session
                    ? newer.Session.ToString("dddd d MMMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                    : newer.Time.ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture), default, 0, Separator: true));
                if (rows.Count >= visible) break;
            }
            newer = source;
            var wrapped = ModernWrapped(source, width - timeWidth, timeWidth > 0);
            for (int w = wrapped.Count - 1; w >= 0 && rows.Count < visible; w--)
            {
                if (skip > 0) { skip--; continue; }
                rows.Insert(0, new(wrapped[w], Color(source.Line.Type), w > 0 ? continuation : timeWidth,
                    w == 0 && timeWidth > 0 ? source.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null));
            }
        }
        // Scrolled past the oldest line: keep the window full.
        if (skip > 0) { tab.Scroll = Math.Max(0, tab.Scroll - skip); if (rows.Count < visible && tab.Scroll > 0) return ModernRows(tab, width, visible, timeWidth); }
        return rows;
    }
    /// <summary>All rows of the tab at this width, as <see cref="ModernRows"/> counts them (wrapped lines and separators).</summary>
    private int ModernRowCount(ModernTab tab, double width, double timeWidth)
    {
        int count = 0;
        ModernLine? newer = null;
        for (int i = _modernHistory.Count - 1; i >= 0; i--)
        {
            var source = _modernHistory[i];
            if (!ModernShows(tab, source)) continue;
            if (newer is not null && (newer.Session != source.Session || newer.Time.Date != source.Time.Date)) count++;
            newer = source;
            count += ModernWrapped(source, width - timeWidth, timeWidth > 0).Count;
        }
        return count;
    }

    /// <summary>
    /// The history's scroll bar at its right edge, as the other Modern UI windows have: the thumb shows where the view is
    /// in the tab's history; drag it, or click the track for a page. Shown with the tabs, and always while scrolled back.
    /// </summary>
    private void ModernChatScrollbar(ImmutableArray<EnbUiTextDraw>.Builder images, ModernTab tab, Rect history, int total, int visible, float fade, double z)
    {
        const string Id = "mchat/scroll";
        float opacity = tab.Scroll > 0 || _drag == Id + "/scrollthumb" ? 1 : fade;
        if (total <= visible || opacity <= 0) { _mdScrollbars.Remove(Id); return; }
        double width = Math.Max(4, Math.Round(6 * z * _scale)) / _scale, inset = Math.Round(3 * z);
        var track = new Rect(MdSnap(history.Right - inset - width), MdSnap(history.Y + inset), width, Math.Round((history.Height - inset * 2) * _scale) / _scale);
        int first = Math.Clamp(total - visible - tab.Scroll, 0, total - visible);
        var bar = (track, total, visible, first, (Action<int>)(v => { tab.Scroll = Math.Max(0, total - visible - v); _modernActiveAt = Environment.TickCount64; Publish(); }));
        _mdScrollbars[Id] = bar;
        var thumb = new Rect(track.X, MdSnap(MdThumbTop(bar)), width, MdThumbHeight(bar));
        Hit(Id + "/scrolltrack", new(track.X - inset, track.Y, width + inset * 2, track.Height), () =>
        {
            if (!_mdScrollbars.TryGetValue(Id, out var now)) return;
            double top = MdThumbTop(now); int page = Math.Max(1, now.Visible - 1);
            int next = _last.Y < top ? now.First - page : _last.Y > top + MdThumbHeight(now) ? now.First + page : now.First;
            now.Set(Math.Clamp(next, 0, now.Total - now.Visible));
        }, wheel: delta => ModernScroll(delta > 0 ? 3 : -3), group: EnbUiGroup.ModernChat);
        Hit(Id + "/scrollthumb", new(thumb.X - inset, thumb.Y, width + inset * 2, thumb.Height), () => { },
            wheel: delta => ModernScroll(delta > 0 ? 3 : -3), group: EnbUiGroup.ModernChat);
        bool hot = _hover == Id + "/scrollthumb" || _drag == Id + "/scrollthumb";
        int pixels = (int)Math.Round(width * _scale);
        images.Add(new(EnbModernIcons.Pill(pixels, (int)Math.Round(track.Height * _scale), new(0, 0, 0), 120), track) { Opacity = opacity });
        images.Add(new(EnbModernIcons.Pill(pixels, (int)Math.Round(thumb.Height * _scale), hot ? new(.72f, .84f, 1) : new(.48f, .6f, .9f), 235), thumb) { Opacity = opacity });
    }

    /// <summary>A line's rows at this width and text size, kept with the line (the panel republishes on hover and fade).</summary>
    private List<string> ModernWrapped(ModernLine line, double width, bool aligned)
    {
        var key = (width, ModernPixels, aligned);
        if (line.Wrapped is { } kept && line.WrapKey == key) return kept;
        line.WrapKey = key;
        return line.Wrapped = ModernWrap(line.Plain ??= ModernPlain(line.Line.Text), width, aligned ? 0 : 10 * ModernZoom);
    }
    /// <summary>
    /// A chat line as the Modern UI font shows it: control characters (the CR LF some server messages end with)
    /// have no glyph in that font and would draw as boxes, so line ends become spaces and the rest is dropped.
    /// </summary>
    private static string ModernPlain(string text)
    {
        bool control = false;
        foreach (char ch in text) if (char.IsControl(ch)) { control = true; break; }
        if (!control) return text.TrimStart();
        var plain = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
            if (ch is '\n' or '\t') plain.Append(' ');
            else if (!char.IsControl(ch)) plain.Append(ch);
        return plain.ToString().Trim();
    }
    private List<string> ModernWrap(string text, double width, double indent)
    {
        var result = new List<string>(2);
        int start = 0;
        while (start < text.Length)
        {
            double room = result.Count == 0 ? width : width - indent;
            int end = text.Length;
            if (ModernMeasure(text[start..end]) > room)
            {
                // The longest prefix that fits, broken at a space when there is one.
                // (Found by halving: a long line is measured a few times, not once per character.)
                int fit = start + 1, over = text.Length;
                while (over - fit > 1) { int mid = (fit + over) / 2; if (ModernMeasure(text[start..mid]) <= room) fit = mid; else over = mid; }
                int space = text.LastIndexOf(' ', fit - 1, fit - start);
                end = space > start ? space : fit;
            }
            result.Add(text[start..end]);
            start = end;
            while (start < text.Length && text[start] == ' ') start++;
        }
        if (result.Count == 0) result.Add("");
        return result;
    }
    private void ModernScroll(int lines)
    {
        if (_modernTabs.Count == 0) return;
        var tab = _modernTabs[_modernTab];
        tab.Scroll = Math.Max(0, tab.Scroll + lines);
        _modernActiveAt = Environment.TickCount64; Publish();
    }

    private void ModernNewTab()
    {
        var tab = new ModernTab { Name = "Tab " + (_modernTabs.Count + 1), AllChannels = true };
        for (int kind = 0; kind < (int)ModernKind.Count; kind++) tab.Kinds[kind] = true;
        _modernTabs.Add(tab); _modernTab = _modernTabs.Count - 1;
        tab.Seen = _modernLineId;
        SaveModernTabs(); Publish();
    }

    /// <summary>Next: a Modern UI chat menu for the shared pop-up menu (it lies above every window):
    /// owner, heading, entries, screen point (the menu grows up from it) and how to rebuild the entries after a tick.</summary>
    internal event Action<string, string?, IReadOnlyList<EnbModernMenuItem>, EnbUiPoint, Func<IReadOnlyList<EnbModernMenuItem>>?>? ModernMenuRequested;
    private EnbUiPoint ModernMenuAnchor(Rect r) { var at = UiLayout?.Apply(EnbUiGroup.ModernChat, r) ?? r; return new(at.X, at.Y - 2); }

    // The tab menu (right click on a tab): rename, its message types and channels (tick boxes), chat settings, tabs.
    private void OpenModernTabMenu(int index, Rect at)
    {
        if ((uint)index >= (uint)_modernTabs.Count) return;
        var tab = _modernTabs[index];
        ModernMenuRequested?.Invoke("mchat/tab", tab.Name, ModernTabMenu(tab), ModernMenuAnchor(at), () => ModernTabMenu(tab));
    }
    private List<EnbModernMenuItem> ModernTabMenu(ModernTab tab)
    {
        var types = new List<EnbModernMenuItem>();
        foreach (var (label, kind) in ModernCategories)
            types.Add(new(label, () => { tab.Kinds[(int)kind] = !tab.Kinds[(int)kind]; tab.Scroll = 0; SaveModernTabs(); Publish(); }) { Checked = tab.Kinds[(int)kind] });
        // Channels: all of them, or the ones ticked; unticking one of "all" keeps the others.
        var choices = ModernChannelChoices();
        var channels = new List<EnbModernMenuItem>
        { new("All channels", () => { tab.AllChannels = !tab.AllChannels; tab.Channels.Clear(); tab.Scroll = 0; SaveModernTabs(); Publish(); }) { Checked = tab.AllChannels } };
        foreach (string channel in choices)
        {
            bool on = tab.AllChannels || tab.Channels.Contains(channel, StringComparer.OrdinalIgnoreCase);
            channels.Add(new(channel, () =>
            {
                bool shown = tab.AllChannels || tab.Channels.Contains(channel, StringComparer.OrdinalIgnoreCase);
                if (tab.AllChannels) { tab.AllChannels = false; tab.Channels.Clear(); tab.Channels.AddRange(choices); }
                if (shown) tab.Channels.RemoveAll(c => c.Equals(channel, StringComparison.OrdinalIgnoreCase)); else tab.Channels.Add(channel);
                tab.Scroll = 0; SaveModernTabs(); Publish();
            }) { Checked = on });
        }
        var items = new List<EnbModernMenuItem>
        {
            new("Rename", () => { int i = _modernTabs.IndexOf(tab); if (i >= 0) OpenTabRename(i); }),
            new("Message types", Children: types),
            new("Channels", Children: channels),
            new("Chat settings", Children: ChatSettingEntries().ConvertAll(e => new EnbModernMenuItem(e.Label, e.Run))),
            new("New tab", ModernNewTab),
        };
        if (_modernTabs.Count > 1)
            items.Add(new("Delete tab", () =>
            {
                int i = _modernTabs.IndexOf(tab); if (i < 0) return;
                _modernTabs.RemoveAt(i); _modernTab = Math.Clamp(_modernTab >= i && _modernTab > 0 ? _modernTab - 1 : _modernTab, 0, _modernTabs.Count - 1);
                SaveModernTabs(); Publish();
            }));
        items.Add(new("Restore default tabs", () => { DefaultModernTabs(); SaveModernTabs(); Publish(); }));
        return items;
    }
    // Next: the emotes (ship manoeuvres in space, avatar emotes docked) from the button beside the tabs.
    private void OpenModernEmoteMenu(Rect at) =>
        ModernMenuRequested?.Invoke("mchat/emote", "Emote", EmoteEntries().ConvertAll(e => new EnbModernMenuItem(e.Label, e.Run)), ModernMenuAnchor(at), null);

    private void OpenTabRename(int index)
    {
        _modernRename = true; _modernMenuTab = index;
        _hidden = false; _menu = ""; _replyPopup = false; _ignoreEntry = false; Error = null; _inputStart = 0;
        _editor.Use(_tabNameInput); _editor.PasswordChar = '\0'; _editor.IsVisible = true;
        _editor.Text = _modernTabs[index].Name; _editor.Focus(); _editor.CaretIndex = _editor.Text.Length;
        InputOpened?.Invoke(); Publish();
    }
    /// <summary>Enter keeps the typed tab name, Escape the old one.</summary>
    private void ModernRenameKey(int code)
    {
        if (code == 0x0d && (_editor.Text ?? "").Trim() is { Length: > 0 } name && _modernMenuTab < _modernTabs.Count)
        { _modernTabs[_modernMenuTab].Name = name[..Math.Min(16, name.Length)]; SaveModernTabs(); }
        if (code is 0x0d or 0x1b) { _modernRename = false; CloseInput(); }
    }

    private void ModernInput(ImmutableArray<EnbUiTextDraw>.Builder images, Rect field)
    {
        string text = _editor.Text ?? "";
        int caret = Math.Clamp(_editor.CaretIndex, 0, text.Length);
        // Keep the caret in view: the visible text starts where the caret still fits.
        int start = Math.Min(_inputStart, caret);
        while (start < caret && ModernMeasure(text[start..caret]) > field.Width - 6) start++;
        _inputStart = start;
        int end = text.Length;
        while (end > caret && ModernMeasure(text[start..end]) > field.Width - 2) end--;
        ModernText(images, text[start..end], field, new(1, 1, 1), 1);
        if (_editor.IsFocused)
        {
            double at = field.X + ModernMeasure(text[start..caret]);
            double height = ModernLineHeight() * .8;
            // Insert mode: a thin bar; overwrite mode: a block.
            double width = _editor.Editor.InsertMode ? Math.Max(1, ModernZoom) : Math.Max(4, ModernMeasure(caret < text.Length ? text[caret].ToString() : "n"));
            images.Add(new(ModernAccent, new(at, field.Center.Y - height / 2, width, height)) { Opacity = _editor.Editor.InsertMode ? .95f : .5f });
        }
    }

    private static readonly EnbUiRaster ModernNotice = Pixel(230, 180, 60, 255);
    /// <summary>
    /// The server's offered message (the retail chat window's blinking B05 button, e.g. the welcome message) as a
    /// Modern UI notice beside the tabs: it does not fade with the panel, blinks with the retail font effect, and a
    /// click accepts it exactly as the retail button does (the server then opens the dialogue).
    /// </summary>
    private void ModernIncomingMessage(ImmutableArray<EnbUiTextDraw>.Builder images, Rect at, double z)
    {
        if (_incomingMessage is not { } message || ReferenceEquals(message, _dismissedIncomingMessage)) return;
        string caption = string.IsNullOrWhiteSpace(message.Message) ? "Incoming message" : message.Message.Trim();
        var r = at with { Width = Math.Ceiling(ModernMeasure(caption) + 16 * z) };
        bool hot = _hover == "incomingMessage";
        float blink = hot ? 1 : Math.Clamp(_incomingEffects?.Opacity ?? 1, .35f, 1);
        images.Add(new(ModernTabFill, r) { Opacity = .85f });
        images.Add(new(ModernNotice, new(r.X, r.Bottom - Math.Max(1, 2 * z), r.Width, Math.Max(1, 2 * z))) { Opacity = blink });
        ModernText(images, caption, r with { X = r.X + 8 * z, Width = r.Width - 16 * z }, hot ? new(1, 1, .6f) : new(1, .85f, .3f), blink);
        Hit("incomingMessage", r, () => _ = AcceptIncomingMessage(), group: EnbUiGroup.ModernChat);
    }

    // Text in the Modern UI font (logical units); without the host's font, the retail chat font.
    private float ModernPixels => (float)(ModernTextSize * ModernZoom * _scale);
    private double ModernLineHeight() => Math.Round(ModernTextSize * ModernZoom * 1.35);
    private double ModernMeasure(string text)
    {
        if (EnbModernText.Renderer is { } m) return m.Measure(text, ModernPixels) / _scale;
        return Font("FONT_CHAT_LINE_00") is { } font ? font.MeasureWidth(text) * font.Scale : text.Length * 7;
    }
    private void ModernText(ImmutableArray<EnbUiTextDraw>.Builder images, string text, Rect r, EnbRgb color, float opacity, bool center = false, bool right = false)
    {
        if (text.Length == 0 || opacity <= 0) return;
        if (EnbModernText.Renderer is not { } m)
        {
            int first = _images.Count;
            Text(text, r, color, "FONT_CHAT_LINE_00", centerVertically: true, opacity: opacity);
            for (int i = first; i < _images.Count; i++) images.Add(_images[i]);
            while (_images.Count > first) _images.RemoveAt(_images.Count - 1);
            return;
        }
        float px = ModernPixels;
        if (ModernRaster(m, text, px, color) is not { } raster) return;
        double pad = m.Padding(px) / _scale, w = raster.Width / _scale, h = raster.Height / _scale, textWidth = w - 2 * pad;
        double x = center ? r.Center.X - textWidth / 2 : right ? r.Right - textWidth : r.X;
        images.Add(new(raster, new(x - pad, r.Center.Y - h / 2, w, h)) { Opacity = opacity });
    }
    /// <summary>Rendered lines kept between publishes (the panel republishes on hover and fade).</summary>
    private EnbUiRaster? ModernRaster(IEnbModernTextRenderer m, string text, float px, EnbRgb color)
    {
        for (int i = _modernRasters.Count - 1; i >= 0; i--)
        {
            var entry = _modernRasters[i];
            if (entry.Size == px && entry.Color == color && entry.Text == text)
            {
                if (i != _modernRasters.Count - 1) { _modernRasters.RemoveAt(i); _modernRasters.Add(entry); }
                return entry.Raster;
            }
        }
        var raster = m.Render(text, px, color);
        if (_modernRasters.Count == 160) _modernRasters.RemoveAt(0);
        _modernRasters.Add((text, px, color, raster));
        return raster;
    }
}
