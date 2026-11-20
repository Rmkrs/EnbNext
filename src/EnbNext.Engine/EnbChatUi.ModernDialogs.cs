using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect = EnbNext.Presentation.EnbUiRect;
using Vector = EnbNext.Presentation.EnbUiPoint;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI chat settings: the retail chat dialogs (Set Channels, Set Messages, Ignore Players, the reply list
/// and the private channel dialogs) as Modern UI windows. The values, requests and their order stay the retail
/// owner's (the same dialog values, commits, requests and editors); only the presentation is replaced. Without the
/// installed dialog frames the retail dialogs are shown.
/// </summary>
internal sealed partial class EnbChatUi
{
    private readonly List<(int Frame, double Width, double Height, EnbAuthoredUiAsset Asset, Rect Design)> _modernDialogArt = [];
    /// <summary>Where the chat settings windows are centred, as fractions of the screen (null: the default place).</summary>
    private Vector? _modernDialogCentre;
    private bool _modernDialogShown;
    private ImmutableArray<EnbUiTextDraw>.Builder _md = ImmutableArray.CreateBuilder<EnbUiTextDraw>();

    private double MdUnit => Math.Max(.75, _size.Height / 1080);
    private double MdSnap(double v) => Math.Round(v * _scale) / _scale;
    private EnbConfirmationResources? MdFrames()
    {
        if (_assets is null) return null;
        try { return EnbRenderSession.Of(_assets).Confirmations; }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; } // The retail dialogs instead.
    }

    /// <summary>A retail label of a chat dialog (by its Attach name), trimmed.</summary>
    private string MdLabel(string prefix, string attach, string fallback)
    {
        if (!_dialogLayouts.TryGetValue(prefix, out var layout)) return fallback;
        foreach (var definition in layout.Definitions)
            if (definition.Properties.GetValueOrDefault("Attach", "").Trim().Equals(attach, StringComparison.OrdinalIgnoreCase) &&
                definition.Properties.GetValueOrDefault("String0", "").Trim() is { Length: > 0 } text)
                return text.Equals("Ok", StringComparison.Ordinal) ? "OK" : text;
        return fallback;
    }

    private double MdMeasure(string text, double size)
    {
        if (EnbModernText.Renderer is { } m) return m.Measure(text, (float)(size * _scale)) / _scale;
        return Font("FONT_SMALL") is { } font ? font.MeasureWidth(text) * font.Scale : text.Length * size * .6;
    }
    /// <summary>One line at its natural size, centred vertically on its capitals. Align 0 left, 1 centre, 2 right.</summary>
    private void MdText(string text, Rect area, EnbRgb color, double size, int align = 0)
    {
        if (text.Length == 0) return;
        if (EnbModernText.Renderer is not { } m)
        {
            int first = _images.Count;
            Text(text, area, color, "FONT_SMALL", centerVertically: true);
            for (int i = first; i < _images.Count; i++) _md.Add(_images[i]);
            while (_images.Count > first) _images.RemoveAt(_images.Count - 1);
            return;
        }
        float px = (float)(size * _scale);
        if (ModernRaster(m, text, px, color) is not { } image) return;
        double pad = m.Padding(px) / _scale, iw = image.Width / _scale, ih = image.Height / _scale, width = iw - 2 * pad;
        double x = align == 0 ? area.X : align == 1 ? area.X + (area.Width - width) / 2 : area.Right - width;
        double y = area.Center.Y - ih / 2 - m.InkCenterOffset("H", px) / _scale;
        _md.Add(new(image, new(MdSnap(x - pad), MdSnap(y), iw, ih)));
    }
    private List<string> MdWrap(string text, double width, double size)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && MdMeasure(next, size) > width) { lines.Add(line); line = word; }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }
    /// <summary>A dynawin dialog frame (0 window, 1 inset) at a screen rectangle, sized like the other Modern UI windows.</summary>
    private void MdFrame(EnbConfirmationResources frames, int frame, Rect r, string? owner = null)
    {
        double u = MdUnit;
        var entry = _modernDialogArt.FirstOrDefault(a => a.Frame == frame && a.Width == r.Width && a.Height == r.Height);
        if (entry.Asset is null)
        {
            var source = frames.Frames[frame];
            var art = source.Asset with { Geometry = source.Resize.Geometry((float)(r.Width / u), (float)(r.Height / u)) };
            var min = art.Geometry!.Positions.Aggregate(System.Numerics.Vector3.Min);
            var max = art.Geometry.Positions.Aggregate(System.Numerics.Vector3.Max);
            entry = (frame, r.Width, r.Height, art, new(min.X, -max.Y, max.X - min.X, max.Y - min.Y));
            if (_modernDialogArt.Count >= 12) _modernDialogArt.RemoveAt(0);
            _modernDialogArt.Add(entry);
        }
        _draws.Add(new(entry.Asset, 0, r, false) { LayoutGroup = EnbUiGroup.None, GadgetOwner = owner is null ? null : _hits.GetValueOrDefault(owner), DesignBounds = entry.Design });
    }
    private void MdButton(string id, string label, Rect r, double size, Action action, bool enabled = true)
    {
        var face = new Rect(MdSnap(r.X), MdSnap(r.Y), Math.Round(r.Width * _scale) / _scale, Math.Round(r.Height * _scale) / _scale);
        Hit(id, face, enabled ? () => { PlayRollSelect(); action(); } : () => { }, group: EnbUiGroup.None);
        bool hot = enabled && _hover == id;
        _md.Add(new(EnbModernIcons.Button((int)Math.Round(face.Width * _scale), (int)Math.Round(face.Height * _scale), hot), face) { Opacity = enabled ? 1 : .55f });
        MdText(label, r, hot ? new(1, 1, .3f) : enabled ? new(1, 1, 1) : new(.5f, .53f, .6f), size, 1);
    }
    private double MdButtonWidth(string label, double size) => Math.Ceiling(MdMeasure(label, size) + 24 * MdUnit);
    private void MdCheck(string id, string label, Rect row, bool on, Action toggle, double size)
    {
        Hit(id, row, toggle, group: EnbUiGroup.None);
        bool hot = _hover == id;
        double u = MdUnit; int box = (int)Math.Round(14 * u * _scale);
        _md.Add(new(EnbModernIcons.Check(box, on, hot), new(MdSnap(row.X + 4 * u), MdSnap(row.Center.Y - box / _scale / 2), box / _scale, box / _scale)));
        MdText(label, new(row.X + 4 * u + box / _scale + 8 * u, row.Y, row.Width, row.Height), hot ? new(1, 1, .45f) : new(.9f, .93f, 1), size);
    }
    /// <summary>A text field: the editor's text (masked for passwords) kept in view round the caret, or a hint when empty.</summary>
    private void MdField(EnbConfirmationResources frames, string id, Rect box, EnbUiTextInput editor, string hint, double size)
    {
        double u = MdUnit;
        MdFrame(frames, 1, box);
        var inner = new Rect(box.X + 8 * u, box.Y, box.Width - 16 * u, box.Height);
        editor.Bounds = inner;
        Hit(id, box, () => FocusDialogEditor(editor), group: EnbUiGroup.None);
        string raw = editor.Text ?? "";
        string text = editor.PasswordChar == '\0' ? raw : new string(editor.PasswordChar, raw.Length);
        int caret = Math.Clamp(editor.CaretIndex, 0, text.Length), start = 0, end = text.Length;
        while (start < caret && MdMeasure(text[start..caret], size) > inner.Width - 4 * u) start++;
        while (end > caret && MdMeasure(text[start..end], size) > inner.Width) end--;
        if (text.Length == 0 && !editor.IsFocused) MdText(hint, inner, new(.55f, .6f, .7f), size);
        else MdText(text[start..end], inner, new(1, 1, 1), size);
        if (!editor.IsFocused) return;
        double at = inner.X + MdMeasure(text[start..caret], size), height = Math.Round(size * 1.15);
        _md.Add(new(ModernAccent, new(MdSnap(at), MdSnap(inner.Center.Y - height / 2), Math.Max(1, Math.Round(u * _scale)) / _scale, height)));
    }
    /// <summary>A list of names in an inset: alternate rows shaded, the selected row marked, a click selects, the wheel scrolls.</summary>
    private void MdList(EnbConfirmationResources frames, string prefix, Rect list, IReadOnlyList<string> rows, int first, int visible, int selected,
        Action<int> select, Action<double> wheel, Action<int> scrollTo, string empty, double size)
    {
        double u = MdUnit, rowHeight = list.Height / visible, room = rows.Count > visible ? Math.Round(14 * u) : 0;
        MdFrame(frames, 1, list);
        if (rows.Count == 0) MdText(empty, new(list.X + 8 * u, list.Y, list.Width - 16 * u, rowHeight), new(.7f, .75f, .88f), size);
        for (int n = 0; n < visible && first + n < rows.Count; n++)
        {
            int index = first + n;
            var row = new Rect(list.X, MdSnap(list.Y + n * rowHeight), list.Width, rowHeight);
            string id = "chatdialog/" + prefix + "/row/" + index.ToString(CultureInfo.InvariantCulture);
            Hit(id, row, () => { PlayFontClick(); select(index); Publish(); }, wheel: wheel, group: EnbUiGroup.None);
            var band = new Rect(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height);
            if (n % 2 == 1) _md.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), band));
            if (index == selected) _md.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), band));
            if (index == selected || _hover == id) _md.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            MdText(rows[index], new(list.X + 8 * u, row.Y, list.Width - 16 * u - room, row.Height), new(1, 1, 1), size);
        }
        MdScrollbar("chatdialog/" + prefix, list, rows.Count, visible, first, scrollTo);
    }

    private readonly Dictionary<string, (Rect Track, int Total, int Visible, int First, Action<int> Set)> _mdScrollbars = [];
    private double _mdScrollGrab;
    private double MdThumbHeight((Rect Track, int Total, int Visible, int First, Action<int> Set) bar) => Math.Round(Math.Max(20 * MdUnit, bar.Track.Height * bar.Visible / bar.Total) * _scale) / _scale;
    private double MdThumbTop((Rect Track, int Total, int Visible, int First, Action<int> Set) bar) => bar.Track.Y + (bar.Track.Height - MdThumbHeight(bar)) * bar.First / Math.Max(1, bar.Total - bar.Visible);
    /// <summary>The Modern UI scroll bar (as in the other Modern UI windows): drag the thumb, click the track for a page.</summary>
    private void MdScrollbar(string id, Rect area, int total, int visible, int first, Action<int> set)
    {
        if (total <= visible || visible <= 0) { _mdScrollbars.Remove(id); return; }
        double u = MdUnit, width = Math.Max(4, Math.Round(8 * u * _scale)) / _scale, inset = Math.Round(3 * u);
        var track = new Rect(MdSnap(area.Right - inset - width), MdSnap(area.Y + inset), width, Math.Round((area.Height - inset * 2) * _scale) / _scale);
        first = Math.Clamp(first, 0, total - visible);
        var bar = (track, total, visible, first, set);
        _mdScrollbars[id] = bar;
        var thumb = new Rect(track.X, MdSnap(MdThumbTop(bar)), width, MdThumbHeight(bar));
        Hit(id + "/scrolltrack", new(track.X - inset, track.Y, width + inset * 2, track.Height), () =>
        {
            if (!_mdScrollbars.TryGetValue(id, out var now)) return;
            double top = MdThumbTop(now); int page = Math.Max(1, now.Visible - 1);
            int next = _last.Y < top ? now.First - page : _last.Y > top + MdThumbHeight(now) ? now.First + page : now.First;
            now.Set(Math.Clamp(next, 0, now.Total - now.Visible)); InteractionSoundRequested?.Invoke("slider_bar_1.wav"); Publish();
        }, group: EnbUiGroup.None);
        Hit(id + "/scrollthumb", new(thumb.X - inset, thumb.Y, width + inset * 2, thumb.Height), () => { }, group: EnbUiGroup.None);
        bool hot = _hover == id + "/scrollthumb" || _drag == id + "/scrollthumb";
        int pixels = (int)Math.Round(width * _scale);
        _md.Add(new(EnbModernIcons.Pill(pixels, (int)Math.Round(track.Height * _scale), new(0, 0, 0), 120), track));
        _md.Add(new(EnbModernIcons.Pill(pixels, (int)Math.Round(thumb.Height * _scale), hot ? new(.72f, .84f, 1) : new(.48f, .6f, .9f), 235), thumb));
    }
    private void BeginMdScroll(string id, Vector point)
    {
        if (_mdScrollbars.TryGetValue(id[..^"/scrollthumb".Length], out var bar)) _mdScrollGrab = point.Y - MdThumbTop(bar);
    }
    private void DragMdScroll(string id, Vector point)
    {
        if (!_mdScrollbars.TryGetValue(id[..^"/scrollthumb".Length], out var bar)) return;
        double room = bar.Track.Height - MdThumbHeight(bar);
        int next = room <= 0 ? 0 : (int)Math.Round(Math.Clamp((point.Y - _mdScrollGrab - bar.Track.Y) / room, 0, 1) * (bar.Total - bar.Visible));
        if (next != bar.First) bar.Set(next);
    }

    /// <summary>The window: centred where it was last placed, kept on screen; title and close button. Returns the content area.</summary>
    private Rect MdWindow(EnbConfirmationResources frames, double contentWidth, double contentHeight, string title, Action close, Action<double>? wheel = null)
    {
        double u = MdUnit, pad = 10 * u, head = 24 * u, gap = 4 * u;
        double width = Math.Round(pad * 2 + contentWidth), height = Math.Round(pad * 2 + head + gap + contentHeight);
        var centre = _modernDialogCentre ?? new Vector(.5, .42);
        double x = MdSnap(Math.Clamp(centre.X * _size.Width - width / 2, 0, Math.Max(0, _size.Width - width)));
        double y = MdSnap(Math.Clamp(centre.Y * _size.Height - height / 2, 0, Math.Max(0, _size.Height - height)));
        // While dragged, the place is the window's clamped centre: dragging past an edge does not stick.
        if (_drag == "chatdialog/block") _modernDialogCentre = new((x + width / 2) / _size.Width, (y + height / 2) / _size.Height);
        var window = new Rect(x, y, width, height);
        Hit("chatdialog/block", window, () => { }, wheel: wheel, group: EnbUiGroup.None);
        MdFrame(frames, 0, window, "chatdialog/block");
        MdText(title, new(x + pad, y + pad, contentWidth - head, head), new(1, .85f, .3f), 16 * u);
        int closePixels = (int)Math.Round(head * _scale);
        var closeBox = new Rect(MdSnap(x + width - pad - head), MdSnap(y + pad), closePixels / _scale, closePixels / _scale);
        Hit("chatdialog/mclose", closeBox, () => { PlayRollSelect(); close(); }, group: EnbUiGroup.None);
        _md.Add(new(EnbModernIcons.CloseButton(closePixels, _hover == "chatdialog/mclose"), closeBox));
        return new(x + pad, y + pad + head + gap, contentWidth, contentHeight);
    }
    private void DragModernDialog(Vector delta)
    {
        var centre = _modernDialogCentre ?? new Vector(.5, .42);
        _modernDialogCentre = new(centre.X + delta.X / _size.Width, centre.Y + delta.Y / _size.Height);
    }

    /// <summary>The Modern UI version of the open chat dialog; false leaves it to the retail dialog.</summary>
    private bool PublishModernChatDialog()
    {
        if (_chatDialog is ChatDialog.None or ChatDialog.Commands || _menuLayer is null || MdFrames() is not { } frames) return false;
        _md = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        switch (_chatDialog)
        {
            case ChatDialog.Channels: ModernChannels(frames); break;
            case ChatDialog.Messages: ModernMessages(frames); break;
            case ChatDialog.Ignore: ModernIgnore(frames); break;
            case ChatDialog.Private: ModernPrivate(frames); break;
            case ChatDialog.PrivateMembers: ModernPrivateMembers(frames); break;
            case ChatDialog.PrivateJoin: ModernPrivateJoin(frames); break;
            case ChatDialog.PrivatePassword: ModernPrivatePassword(frames); break;
            default: return false;
        }
        _draws.Add(new(_menuLayer, 0, new(0, 0, _size.Width, _size.Height), false) { ImagesOnly = true, LayoutGroup = EnbUiGroup.None, Images = _md.ToImmutable() });
        _modernDialogShown = true;
        return true;
    }

    /// <summary>OK and Cancel at the bottom right, and the last error at the left.</summary>
    private void MdFooter(Rect content, string prefix, Action ok)
    {
        double u = MdUnit, text = 14 * u, head = 24 * u, gap = 4 * u;
        string okLabel = MdLabel(prefix, "ok", "OK"), cancelLabel = MdLabel(prefix, "cancel", "Cancel");
        double w = Math.Max(MdButtonWidth(okLabel, text), MdButtonWidth(cancelLabel, text)), y = content.Bottom - head;
        MdButton("chatdialog/m" + prefix + "/OK", okLabel, new(content.Right - w * 2 - gap, y, w, head), text, ok);
        MdButton("chatdialog/m" + prefix + "/CANCEL", cancelLabel, new(content.Right - w, y, w, head), text, CloseChatDialog);
        if (Error is { } error) MdText(error, new(content.X, y, content.Width - w * 2 - gap * 2, head), new(1, .5f, .5f), 12 * u);
    }

    private void ModernChannels(EnbConfirmationResources frames)
    {
        double u = MdUnit, text = 14 * u, head = 24 * u, gap = 4 * u, rowHeight = Math.Round(22 * u), column = Math.Round(170 * u);
        // The retail window's first column holds the chat channels (by message type), the others the global channels.
        var chat = Enumerable.Range(0, ChannelOptions.Length).Where(i => i % 3 == 0 && ChannelOptions[i].Option is not null).ToArray();
        var global = Enumerable.Range(0, ChannelOptions.Length).Where(i => i % 3 != 0 && ChannelOptions[i].Option is not null).ToArray();
        int rows = Math.Max(chat.Length, (global.Length + 1) / 2);
        double contentWidth = column * 3 + gap * 4, contentHeight = head + rows * rowHeight + gap * 3 + head;
        var c = MdWindow(frames, contentWidth, contentHeight, MdLabel("channels", "Title", "Set Channels To Monitor"), CloseChatDialog);
        MdText(MdLabel("channels", "SubTitle", "Chat Channels"), new(c.X + 4 * u, c.Y, column, head), new(.7f, .75f, .88f), text);
        MdText(MdLabel("channels", "SubTitle2", "Global Channels"), new(c.X + column + gap * 4 + 4 * u, c.Y, column * 2, head), new(.7f, .75f, .88f), text);
        for (int n = 0; n < chat.Length; n++) Check(chat[n], c.X, c.Y + head + n * rowHeight);
        for (int n = 0; n < global.Length; n++) Check(global[n], c.X + column + gap * 4 + n / rows * (column + gap), c.Y + head + n % rows * rowHeight);
        MdFooter(c, "channels", () => _ = CommitChannelsAsync());

        void Check(int index, double x, double y)
        {
            var (option, label, _) = ChannelOptions[index];
            MdCheck("chatdialog/mchannels/CHECK" + index.ToString("00", CultureInfo.InvariantCulture), MdLabel("channels", "text" + index.ToString("00", CultureInfo.InvariantCulture), label),
                new(x, y, column, rowHeight), _dialogValues.GetValueOrDefault(option!), () => ToggleDialogCheck("channels", index), text);
        }
    }

    private void ModernMessages(EnbConfirmationResources frames)
    {
        double u = MdUnit, text = 14 * u, head = 24 * u, gap = 4 * u, rowHeight = Math.Round(22 * u), column = Math.Round(190 * u);
        int rows = (MessageOptions.Length + 1) / 2;
        double contentWidth = column * 2 + gap, contentHeight = rows * rowHeight + gap * 3 + head;
        var c = MdWindow(frames, contentWidth, contentHeight, MdLabel("messages", "help text", "Set Message Filters"), CloseChatDialog);
        for (int index = 0; index < MessageOptions.Length; index++)
        {
            int i = index; var (option, label, _) = MessageOptions[i];
            MdCheck("chatdialog/mmessages/CHECK" + i.ToString("00", CultureInfo.InvariantCulture), MdLabel("messages", "text" + i.ToString("00", CultureInfo.InvariantCulture), label),
                new(c.X + i / rows * (column + gap), c.Y + i % rows * rowHeight, column, rowHeight), _dialogValues.GetValueOrDefault(option), () => ToggleDialogCheck("messages", i), text);
        }
        MdFooter(c, "messages", CommitMessages);
    }

    private void ModernIgnore(EnbConfirmationResources frames)
    {
        double u = MdUnit, text = 14 * u, head = 24 * u, gap = 4 * u, rowHeight = Math.Round(22 * u);
        var rows = IgnoreRows;
        const int Visible = 10;
        _ignoreScroll = Math.Clamp(Math.Round(_ignoreScroll), 0, Math.Max(0, rows.Length - Visible));
        string add = MdLabel("ignore", "button01", "Add Player"), remove = MdLabel("ignore", "button02", "Remove Player"), target = MdLabel("ignore", "button03", "Ignore Target");
        double contentWidth = Math.Max(Math.Round(320 * u), MdButtonWidth(add, text) + MdButtonWidth(remove, text) + MdButtonWidth(target, text) + gap * 2);
        double contentHeight = Visible * rowHeight + gap * 2 + head + (_ignoreEntry ? gap * 2 + head : 0) + (Error is not null ? rowHeight : 0);
        void Scroll(double delta) { _ignoreScroll = Math.Clamp(_ignoreScroll + (delta > 0 ? -1 : 1), 0, Math.Max(0, IgnoreRows.Length - Visible)); Publish(); }
        var c = MdWindow(frames, contentWidth, contentHeight, MdLabel("ignore", "title", "Ignore Players"), CloseChatDialog, Scroll);
        MdList(frames, "mignore", new(c.X, c.Y, c.Width, Visible * rowHeight), rows, (int)_ignoreScroll, Visible, _ignoreSelectionIndex,
            index => { _ignoreSelectionIndex = index; _ignoreSelection = IgnoreRows[index]; }, Scroll, v => _ignoreScroll = v, _ignoreReady ? "No players are ignored." : "Requesting the list of ignored players.", text);
        double y = c.Y + Visible * rowHeight + gap * 2, x = c.X;
        Button("BUTTON01", add, OpenIgnoreEntry); Button("BUTTON02", remove, () => _ = RemoveIgnoredPlayerAsync()); Button("BUTTON03", target, () => _ = IgnoreSelectedTargetAsync());
        y += head;
        if (_ignoreEntry)
        {
            // The name to ignore, typed here; Enter confirms it (the retail entry's keys).
            y += gap * 2;
            string confirm = MdLabel("ignoreentry", "confirm", "Confirm"), cancel = MdLabel("ignoreentry", "cancel", "Cancel");
            double confirmWidth = MdButtonWidth(confirm, text), cancelWidth = MdButtonWidth(cancel, text);
            MdField(frames, "chatdialog/mignore/input", new(c.X, y, c.Width - confirmWidth - cancelWidth - gap * 2, head), _editor, "Player name", text);
            MdButton("chatdialog/mignore/confirm", confirm, new(c.Right - confirmWidth - cancelWidth - gap, y, confirmWidth, head), text, () => _ = ConfirmIgnoreEntryAsync());
            MdButton("chatdialog/mignore/cancel", cancel, new(c.Right - cancelWidth, y, cancelWidth, head), text, CloseIgnoreEntry);
            y += head;
        }
        if (Error is { } error) MdText(error, new(c.X, y, c.Width, rowHeight), new(1, .5f, .5f), 12 * u);

        void Button(string mesh, string label, Action action)
        {
            double w = MdButtonWidth(label, text);
            MdButton("chatdialog/mignore/" + mesh, label, new(x, y, w, head), text, action, DialogPieceEnabled("ignore", mesh));
            x += w + gap;
        }
    }

    /// <summary>A message window: wrapped text and up to three buttons (the private channel dialogs' shape).</summary>
    private Rect MdMessage(EnbConfirmationResources frames, string title, string message, double extraHeight, Action close, params (string Id, string Label, Action Action, bool Enabled)[] buttons)
    {
        double u = MdUnit, text = 14 * u, head = 24 * u, gap = 4 * u, rowHeight = Math.Round(22 * u), contentWidth = Math.Round(380 * u);
        var lines = MdWrap(message, contentWidth, text);
        double contentHeight = lines.Count * rowHeight + gap * 2 + extraHeight + head + (Error is not null ? rowHeight : 0);
        var c = MdWindow(frames, contentWidth, contentHeight, title, close);
        for (int i = 0; i < lines.Count; i++) MdText(lines[i], new(c.X, c.Y + i * rowHeight, c.Width, rowHeight), new(.9f, .93f, 1), text);
        double right = c.Right, y = c.Bottom - head;
        foreach (var (id, label, action, enabled) in Enumerable.Reverse(buttons))
        {
            double w = Math.Max(Math.Round(90 * u), MdButtonWidth(label, text));
            MdButton(id, label, new(right - w, y, w, head), text, action, enabled);
            right -= w + gap;
        }
        if (Error is { } error) MdText(error, new(c.X, y - rowHeight, c.Width, rowHeight), new(1, .5f, .5f), 12 * u);
        return new(c.X, c.Y + lines.Count * rowHeight + gap, c.Width, extraHeight);
    }

    private void ModernPrivate(EnbConfirmationResources frames)
    {
        var buttons = new List<(string, string, Action, bool)>();
        for (int n = 1; n <= 3; n++)
        {
            int button = n; string mesh = "BUTTON0" + n.ToString(CultureInfo.InvariantCulture);
            if (PrivatePanelLabel(mesh) is not { Length: > 0 } label || !DialogPieceVisible("private", mesh)) continue;
            buttons.Add(("chatdialog/mprivate/" + mesh, label, () => PrivatePanelButton(button), DialogPieceEnabled("private", mesh)));
        }
        MdMessage(frames, PrivatePanelLabel("HELP TEXT"), PrivatePanelLabel("MESSAGE"), 0, CloseChatDialog, [.. buttons]);
    }

    private void ModernPrivateMembers(EnbConfirmationResources frames)
    {
        double u = MdUnit, text = 14 * u, head = 24 * u, gap = 4 * u, rowHeight = Math.Round(22 * u);
        var members = PrivateMembers;
        const int Visible = 10;
        _privateMembersPage = Math.Clamp(_privateMembersPage, 0, Math.Max(0, members.Length - Visible)); _privateMembersScroll = _privateMembersPage;
        var labels = Enumerable.Range(1, 5).Select(n => PrivateMembersLabel("BUTTON0" + n.ToString(CultureInfo.InvariantCulture))).ToArray();
        double contentWidth = Math.Max(Math.Round(320 * u), labels.Sum(l => MdButtonWidth(l, text)) + gap * 4);
        double contentHeight = Visible * rowHeight + gap * 2 + head + (Error is not null ? rowHeight : 0);
        void Scroll(double delta) { _privateMembersPage = Math.Clamp(_privateMembersPage + (delta > 0 ? -1 : 1), 0, Math.Max(0, PrivateMembersCount() - Visible)); Publish(); }
        var c = MdWindow(frames, contentWidth, contentHeight, MdLabel("privatemembers", "title", "Channel Members"), CloseChatDialog, Scroll);
        MdList(frames, "mprivatemembers", new(c.X, c.Y, c.Width, Visible * rowHeight), members, _privateMembersPage, Visible, _privateMemberSelectionIndex,
            index => { _privateMemberSelectionIndex = index; _privateMemberSelection = PrivateMembers[index]; }, Scroll, v => _privateMembersPage = v,
            _privateMembersReady ? "No members." : "Requesting the channel members.", text);
        double x = c.X, y = c.Y + Visible * rowHeight + gap * 2;
        for (int n = 1; n <= 5; n++)
        {
            int button = n; string mesh = "BUTTON0" + n.ToString(CultureInfo.InvariantCulture);
            double w = MdButtonWidth(labels[n - 1], text);
            MdButton("chatdialog/mprivatemembers/" + mesh, labels[n - 1], new(x, y, w, head), text, () => _ = PrivateMemberButtonAsync(button), DialogPieceEnabled("privatemembers", mesh));
            x += w + gap;
        }
        if (Error is { } error) MdText(error, new(c.X, y + head, c.Width, rowHeight), new(1, .5f, .5f), 12 * u);
    }

    private void ModernPrivateJoin(EnbConfirmationResources frames)
    {
        double u = MdUnit, head = 24 * u, gap = 4 * u;
        var field = MdMessage(frames, PrivateJoinLabel("TITLE", ""), PrivateJoinLabel("MAIN_TEXT", ""), head + gap * 2, CloseChatDialog,
            ("chatdialog/mprivatejoin/BUTTON01", MdLabel("privatejoin", "button01", "Cancel"), ReturnToPrivateChoice, true),
            ("chatdialog/mprivatejoin/BUTTON03", MdLabel("privatejoin", "button03", "Confirm"), () => _ = ConfirmPrivateJoinAsync(), true));
        MdField(frames, "chatdialog/mprivatejoin/TEXT_FIELD", new(field.X, field.Y, Math.Round(160 * u), head), _editor, "Channel number", 14 * u);
    }

    private void ModernPrivatePassword(EnbConfirmationResources frames)
    {
        double u = MdUnit, text = 14 * u, head = 24 * u, gap = 4 * u, labelWidth = Math.Round(140 * u);
        bool verify = _privatePasswordMode == 2;
        double extra = (verify ? 2 : 1) * (head + gap) + gap;
        var area = MdMessage(frames, PrivatePasswordLabel("TITLE", ""), PrivatePasswordLabel("MAIN_TEXT", ""), extra, CancelPrivatePassword,
            ("chatdialog/mprivatepassword/BUTTON02", MdLabel("privatepassword", "button02", "Cancel"), CancelPrivatePassword, true),
            ("chatdialog/mprivatepassword/BUTTON03", MdLabel("privatepassword", "button03", "Confirm"), () => _ = ConfirmPrivatePasswordAsync(), true));
        Field("TEXT_FIELD00", PrivatePasswordLabel("PASSWORD_TEXT", "Password"), _editor, area.Y);
        if (verify) Field("TEXT_FIELD01", PrivatePasswordLabel("VARIFY_TEXT", "Verify Password"), _passwordVerifyEditor, area.Y + head + gap);

        void Field(string mesh, string label, EnbUiTextInput editor, double y)
        {
            MdText(label, new(area.X, y, labelWidth, head), new(.7f, .75f, .88f), text);
            MdField(frames, "chatdialog/mprivatepassword/" + mesh, new(area.X + labelWidth, y, area.Width - labelWidth, head), editor, "", text);
        }
    }

    /// <summary>The reply list: the recent private correspondents above the chat panel; a click replies to one.</summary>
    private void PublishModernReplyPopup()
    {
        if (!_replyPopup || _data is not { RecentPeers.Length: > 0 } data || _menuLayer is null || MdFrames() is not { } frames) return;
        _md = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double u = MdUnit, text = 14 * u, pad = 10 * u, head = 28 * u, rowHeight = Math.Round(24 * u);
        string title = "Reply to";
        double width = Math.Ceiling(Math.Max(Math.Round(160 * u), Math.Max(MdMeasure(title, text), data.RecentPeers.Max(p => MdMeasure(p, text))) + pad * 2 + 24 * u));
        double height = Math.Round(pad * 2 + head + data.RecentPeers.Length * rowHeight);
        var panel = ModernChatArea;
        var window = new Rect(MdSnap(panel.X), MdSnap(Math.Max(0, panel.Y - height - 4 * u)), width, height);
        Hit("replypopup/frame", window, () => { }, group: EnbUiGroup.None);
        MdFrame(frames, 0, window, "replypopup/frame");
        MdText(title, new(window.X + pad, window.Y + pad, width - pad * 2, head - 6 * u), new(1, .85f, .3f), text);
        for (int i = 0; i < data.RecentPeers.Length; i++)
        {
            string peer = data.RecentPeers[i], id = "replypopup/" + i.ToString(CultureInfo.InvariantCulture);
            var row = new Rect(window.X + pad / 2, MdSnap(window.Y + pad + head + i * rowHeight), width - pad, rowHeight);
            Hit(id, row, () => { PlayFontClick(); SelectReply(peer); }, group: EnbUiGroup.None);
            if (_hover == id) _md.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            MdText(peer, new(row.X + pad, row.Y, row.Width - pad * 2, row.Height), _hover == id ? new(1, 1, .45f) : new(1, 1, 1), text);
        }
        _draws.Add(new(_menuLayer, 0, new(0, 0, _size.Width, _size.Height), false) { ImagesOnly = true, LayoutGroup = EnbUiGroup.None, Images = _md.ToImmutable() });
    }
}
