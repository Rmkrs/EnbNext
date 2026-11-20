using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Captain's Log: the retail log (the same numbered pages 1-999 of 1024 characters per character,
/// the same files, the current page and retained unsaved drafts) in a Modern UI window. The page is saved before
/// another page is read and when the window closes, like the retail log; Date Stamp inserts the retail stamp.
/// The text wraps to the window, the caret can be placed with the pointer and moved by line with Up and Down.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernLog;
    private Rect _modernLogBounds, _modernLogBody;
    private readonly EnbCaptainLogController _mlog = new();
    private readonly EnbRetailTextEditor _mlogText = new(1024, true), _mlogPageText = new(3) { CharacterClass = EnbRetailEditorCharacterClass.Digits };
    private EnbUiTextInput? _mlogInput, _mlogPageInput;
    private int _mlogScroll;
    private bool _mlogFollow, _mlogPageEditing;
    private string? _mlogMessage;
    private readonly List<(int Start, int End)> _mlogLines = [];
    private (string Text, double Width, double Size) _mlogLayout;
    private double _mlogLineHeight, _mlogSize;
    private int _mlogVisible = 1;
    internal EnbUiPoint? ModernLogPosition { get; set; }
    internal Action<EnbUiPoint>? ModernLogMoved { get; set; }
    public bool ModernLogOpen => _modernLog;
    public bool ModernLogEditing => _modernLog && (_mlogInput is { IsFocused: true } || _mlogPageInput is { IsFocused: true });
    internal EnbUiTextInput? ModernLogInput => _mlogInput;
    internal EnbUiTextInput? ModernLogPageInput => _mlogPageInput;
    internal int ModernLogPage => _mlog.Page;
    /// <summary>The retail stamp's date (005ABB10 uses the current local date); a probe sets a fixed one.</summary>
    internal Func<DateTime>? ModernLogClock { get; set; }

    private static bool ModernLogFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    /// <summary>Opens the log at the character's current page (or a draft whose save failed), as the retail log does.</summary>
    public bool OpenModernCaptainLog(Func<uint, EnbRetailCaptainLog>? factory = null)
    {
        if (!_interactive || !ModernUi || _assets is null || _owner is not uint owner || owner == 0) return false;
        if (!_modernLog)
        {
            try { _mlog.Open(factory?.Invoke(owner) ?? new(_assets.Paths.ClientOutputDirectory, owner), owner, _mlogText, _mlogPageText); }
            catch (Exception ex) when (ModernLogFailure(ex)) { _message = "Could not open Captain's Log: " + ex.Message; Publish(); return false; }
            _modernLog = true; _mlogScroll = 0; _mlogMessage = null; _mlogPageEditing = false;
            _mlogInput ??= CreateModernLogInput();
            _mlogPageInput ??= CreateModernLogPageInput();
            _mlogInput.IsVisible = _mlogPageInput.IsVisible = true;
            // Ready to write: the caret after the page's text.
            _mlogText.CaretIndex = _mlogText.Text.Length; _mlogFollow = true;
            _mlogInput.Focus();
            InteractionSoundRequested?.Invoke("TextClick03.wav");
            Opened?.Invoke();
        }
        RaiseModern("mlog"); Publish(); return true;
    }

    private bool SaveModernLog()
    {
        try { _mlog.Save(_mlogText.Text); return true; }
        catch (Exception ex) when (ModernLogFailure(ex)) { _mlogMessage = "Could not save Captain's Log: " + ex.Message; Publish(); return false; }
    }

    /// <summary>Saves and closes; a failed save keeps the window open with the reason (the retail CloseLog).</summary>
    internal bool CloseModernLog(bool force = false, bool publish = true)
    {
        if (!_modernLog) return true;
        bool saved;
        try { _mlog.Save(_mlogText.Text); saved = true; }
        catch (Exception ex) when (ModernLogFailure(ex)) { _mlogMessage = "Could not save Captain's Log: " + ex.Message; saved = false; }
        if (!saved && !force) { if (publish) Publish(); return false; }
        _modernLog = false; _mlogPageEditing = false; _mlog.Close();
        if (_mlogInput is not null) _mlogInput.IsVisible = false;
        if (_mlogPageInput is not null) _mlogPageInput.IsVisible = false;
        if (!publish) return true;
        InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        Publish(); return true;
    }

    private void ModernLogTurn(int page)
    {
        page = Math.Clamp(page, 1, 999);
        _mlogPageEditing = false;
        if (page != _mlog.Page && SaveModernLog())
        {
            try { _mlog.ReadPage(page, _mlogText, _mlogPageText); _mlogScroll = 0; _mlogMessage = null; _mlogText.CaretIndex = _mlogText.Text.Length; _mlogFollow = true; }
            catch (Exception ex) when (ModernLogFailure(ex)) { _mlogMessage = "Could not read Captain's Log: " + ex.Message; }
        }
        _mlogPageText.Text = _mlog.Page.ToString(CultureInfo.InvariantCulture);
        Publish();
    }

    private EnbUiTextInput CreateModernLogInput()
    {
        var input = new EnbUiTextInput(_mlogText) { Bounds = new(0, 0, 1, 1) };
        TextInputCreated?.Invoke(input);
        input.KeyDown += (_, e, handled) => { if (ModernLogKey(e.Code)) handled?.Invoke(); };
        input.Changed += (_, _) => { _mlogFollow = true; Publish(); };
        input.GotFocus += (_, _) => Publish();
        input.LostFocus += (_, _) => Publish();
        return input;
    }

    private EnbUiTextInput CreateModernLogPageInput()
    {
        var input = new EnbUiTextInput(_mlogPageText) { Bounds = new(0, 0, 1, 1) };
        TextInputCreated?.Invoke(input);
        input.KeyDown += (_, e, handled) =>
        {
            if (e.Code is 13 or 9) { handled?.Invoke(); CommitModernLogPage(); }
            // 005AB510: Escape only deactivates the entry; the parent commits it (OPT-D18).
            else if (e.Code == 27) { handled?.Invoke(); CommitModernLogPage(); }
        };
        input.Changed += (_, _) => Publish();
        // Leaving the page number turns to it, as the retail entry does when it loses focus.
        input.LostFocus += (_, _) => { if (_mlogPageEditing) CommitModernLogPage(); else Publish(); };
        return input;
    }

    private void CommitModernLogPage()
    {
        if (!_mlogPageEditing) return;
        _mlogPageEditing = false;
        _mlogPageInput?.SetFocused(false);
        // 006AA720: the entry's leading digits, blank or zero as 0, clamped to page 1.
        long page = 0;
        foreach (char c in _mlogPageText.Text ?? "") { if (c is < '0' or > '9') break; page = Math.Min(int.MaxValue, page * 10 + (c - '0')); }
        ModernLogTurn((int)Math.Max(1, page));
    }

    /// <summary>The keys the editor leaves: Up and Down move by line, Page Up and Page Down by a window, Escape leaves the text.</summary>
    private bool ModernLogKey(ushort code)
    {
        switch (code)
        {
            case 0x26: MoveModernLogCaret(-1); return true;
            case 0x28: MoveModernLogCaret(1); return true;
            case 0x21: MoveModernLogCaret(-Math.Max(1, _mlogVisible - 1)); return true;
            case 0x22: MoveModernLogCaret(Math.Max(1, _mlogVisible - 1)); return true;
            case 9: return true;
            case 27: _mlogInput?.SetFocused(false); Publish(); return true;
            default: return false;
        }
    }

    private int ModernLogLineOf(int caret)
    {
        int line = 0;
        for (int i = 0; i < _mlogLines.Count; i++) if (_mlogLines[i].Start <= caret) line = i;
        return line;
    }
    /// <summary>The character nearest a distance from the start of a visual line.</summary>
    private int ModernLogIndexAt(int line, double x)
    {
        var (start, end) = _mlogLines[line];
        string text = _mlogText.Text;
        // A wrapped line's last character (a space) belongs to the break: the caret stops before it.
        int last = line + 1 < _mlogLines.Count && _mlogLines[line + 1].Start == end && end > start ? end - 1 : end;
        int best = start; double bestDistance = double.MaxValue;
        for (int i = start; i <= last; i++)
        {
            double d = Math.Abs(ModernMeasure(text[start..i], _mlogSize) - x);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }
    private void MoveModernLogCaret(int lines)
    {
        if (_mlogLines.Count == 0) return;
        int caret = Math.Clamp(_mlogText.CaretIndex, 0, _mlogText.Text.Length), line = ModernLogLineOf(caret);
        double x = ModernMeasure(_mlogText.Text[_mlogLines[line].Start..caret], _mlogSize);
        int target = Math.Clamp(line + lines, 0, _mlogLines.Count - 1);
        _mlogText.CaretIndex = target == line ? (lines < 0 ? 0 : _mlogText.Text.Length) : ModernLogIndexAt(target, x);
        _mlogFollow = true; Publish();
    }

    /// <summary>Visual lines: the text's own lines, wrapped at spaces (or anywhere in a long word) to the width.</summary>
    private void LayoutModernLog(string s, double width, double size)
    {
        if (_mlogLayout == (s, width, size) && _mlogLines.Count > 0) return;
        _mlogLayout = (s, width, size); _mlogLines.Clear();
        int start = 0;
        while (true)
        {
            int newline = s.IndexOf('\n', start), end = newline < 0 ? s.Length : newline, from = start;
            while (true)
            {
                if (from >= end || ModernMeasure(s[from..end], size) <= width) { _mlogLines.Add((from, end)); break; }
                int low = from + 1, high = end;
                while (low < high) { int mid = (low + high + 1) / 2; if (ModernMeasure(s[from..mid], size) <= width) low = mid; else high = mid - 1; }
                int split = low, space = s.LastIndexOf(' ', split - 1, split - from);
                if (space > from) split = space + 1;
                _mlogLines.Add((from, split)); from = split;
            }
            if (newline < 0) break;
            start = newline + 1;
        }
    }

    private string LogString(string attach, string fallback)
    {
        if (_assets is null) return fallback;
        try
        {
            var layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fa_log00a.ini"));
            foreach (var definition in layout.Definitions)
                if (definition.Properties.GetValueOrDefault("Attach", "").Trim().Equals(attach, StringComparison.OrdinalIgnoreCase) &&
                    definition.Properties.GetValueOrDefault("String0", "").Trim() is { Length: > 0 } text)
                    return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return fallback;
    }
    private (string Title, string Next, string Date, string Page)? _mlogLabels;

    private void ModernCaptainLogWindow()
    {
        // The retail labels (fa_log00a.ini); its "Prev" is written out, as Modern UI uses no abbreviations.
        var labels = _mlogLabels ??= (LogString("title", "Captain's Log"), LogString("Next", "Next"), LogString("Date_stamp", "Date Stamp"), LogString("page", "Page"));
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u, line = Math.Round(20 * u);
        double contentWidth = Math.Round(560 * u), inner = 10 * u;
        _mlogVisible = 16; _mlogLineHeight = line; _mlogSize = text;
        double bodyHeight = _mlogVisible * line + inner;
        double height = pad * 2 + head + gap + head + gap * 2 + bodyHeight + gap + Math.Round(18 * u), width = pad * 2 + contentWidth, x, y;
        if (ModernLogPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernLogBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernLogBounds, "mlog/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mlog/title", labels.Title, new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mlog/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;

        // Pages: previous, the page number (click to type one), next; Date Stamp at the right.
        int page = _mlog.Page;
        string previous = "Previous", next = labels.Next, of = "of 999";
        double previousWidth = Math.Ceiling(ModernMeasure(previous, text) + 24 * u), nextWidth = Math.Ceiling(ModernMeasure(next, text) + 24 * u);
        ModernButton("mlog/previous", previous, new(cx, cy, previousWidth, head), text, "Save this page and turn to the previous one.", page > 1);
        double px = cx + previousWidth + gap * 3, labelWidth = Math.Ceiling(ModernMeasure(labels.Page, text));
        ModernText("mlog/pageLabel", labels.Page, new(px, cy, labelWidth, head), new(.7f, .75f, .88f), text);
        px += labelWidth + gap * 2;
        var pageBox = new Rect(px, cy, Math.Round(52 * u), head);
        ModernFrame("DIALOG_BOX_INFO", pageBox, "mlog/page");
        Flush();
        bool pageFocused = _mlogPageInput is { IsFocused: true };
        string pageText = _mlogPageText.Text;
        if (pageFocused) pageText = pageText.Insert(Math.Clamp(_mlogPageText.CaretIndex, 0, pageText.Length), "|");
        ModernText("mlog/pageText", pageText, pageBox, new(1, 1, 1), text, 1);
        if (_mlogPageInput is not null) _mlogPageInput.Bounds = pageBox;
        _tips["mlog/page"] = "Click to type the page to turn to (1 to 999).";
        px = pageBox.Right + gap * 2;
        double ofWidth = Math.Ceiling(ModernMeasure(of, text));
        ModernText("mlog/pageCount", of, new(px, cy, ofWidth, head), new(.7f, .75f, .88f), text);
        ModernButton("mlog/next", next, new(px + ofWidth + gap * 3, cy, nextWidth, head), text, "Save this page and turn to the next one.", page < 999);
        double dateWidth = Math.Ceiling(ModernMeasure(labels.Date, text) + 24 * u);
        ModernButton("mlog/date", labels.Date, new(cx + contentWidth - dateWidth, cy, dateWidth, head), text, "Write today's date at the caret.");
        cy += head + gap * 2;

        // The page's text, wrapped, with the caret; the wheel scrolls.
        _modernLogBody = new(cx, cy, contentWidth, bodyHeight);
        ModernFrame("DIALOG_BOX_INFO", _modernLogBody, "mlog/body");
        Flush();
        _tips["mlog/body"] = "";
        string body = _mlogText.Text;
        double textX = cx + inner, textWidth = contentWidth - inner * 2 - 8 * u, textY = cy + inner / 2;
        LayoutModernLog(body, textWidth, text);
        int caret = Math.Clamp(_mlogText.CaretIndex, 0, body.Length), caretLine = ModernLogLineOf(caret);
        int maxScroll = Math.Max(0, _mlogLines.Count - _mlogVisible);
        if (_mlogFollow) { _mlogScroll = Math.Clamp(_mlogScroll, caretLine - _mlogVisible + 1, caretLine); _mlogFollow = false; }
        _mlogScroll = Math.Clamp(_mlogScroll, 0, maxScroll);
        if (_mlogInput is not null) _mlogInput.Bounds = _modernLogBody;
        bool focused = _mlogInput is { IsFocused: true };
        if (body.Length == 0 && !focused)
            ModernText("mlog/empty", "Click here to write in your log.", new(textX, textY, textWidth, line), new(.55f, .6f, .7f), text);
        for (int n = 0; n < _mlogVisible && _mlogScroll + n < _mlogLines.Count; n++)
        {
            var (start, end) = _mlogLines[_mlogScroll + n];
            if (end > start) ModernText("mlog/line" + n.ToString(CultureInfo.InvariantCulture), body[start..end], new(textX, Snap(textY + n * line), textWidth, line), new(1, 1, 1), text);
        }
        if (focused && caretLine >= _mlogScroll && caretLine < _mlogScroll + _mlogVisible)
        {
            double caretX = textX + ModernMeasure(body[_mlogLines[caretLine].Start..caret], text), caretHeight = Math.Round(line * .8);
            double w = _mlogText.InsertMode ? Math.Max(1, Math.Round(u * _scale)) / _scale : Math.Max(4 * u, ModernMeasure(caret < body.Length && body[caret] != '\n' ? body[caret].ToString() : "n", text));
            _images.Add(new(EnbModernIcons.Fill(new(.55f, .8f, 1), (byte)(_mlogText.InsertMode ? 240 : 120)), new(Snap(caretX), Snap(textY + (caretLine - _mlogScroll) * line + (line - caretHeight) / 2), w, caretHeight)));
        }
        ModernScrollbar("mlog/body", _modernLogBody, _mlogLines.Count, _mlogVisible, _mlogScroll, v => { _mlogScroll = v; _mlogFollow = false; });
        cy += bodyHeight + gap;
        if (_mlogMessage is { } message) ModernText("mlog/error", ModernEllipsis(message, contentWidth * .7, small), new(cx, cy, contentWidth * .7, Math.Round(18 * u)), new(1, .5f, .5f), small);
        ModernText("mlog/count", body.Length.ToString(CultureInfo.InvariantCulture) + " of " + _mlogText.MaxLength.ToString(CultureInfo.InvariantCulture) + " characters",
            new(cx, cy, contentWidth, Math.Round(18 * u)), body.Length >= _mlogText.MaxLength ? new(1, .7f, .4f) : new(.6f, .65f, .8f), small, 2);
        Flush();
    }

    private bool ActivateModernLog(string id, bool right)
    {
        if (!id.StartsWith("mlog/", StringComparison.Ordinal)) return false;
        if (right || !_modernLog) return true;
        if (id != "mlog/page" && _mlogPageEditing) CommitModernLogPage();
        switch (id)
        {
            case "mlog/close": CloseModernLog(); break;
            case "mlog/previous": InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); ModernLogTurn(_mlog.Page - 1); break;
            case "mlog/next": InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); ModernLogTurn(_mlog.Page + 1); break;
            case "mlog/date":
                // 005ABB10: "MMMM dd, yyyy: " in the C locale, at the caret.
                InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
                _mlogText.Insert((ModernLogClock?.Invoke() ?? DateTime.Now).ToString("MMMM dd, yyyy: ", CultureInfo.InvariantCulture));
                _mlogFollow = true; _mlogInput?.Focus(); break;
            case "mlog/page":
                // The retail page entry starts empty for the new number.
                _mlogPageEditing = true; _mlogPageText.Text = ""; _mlogPageInput?.Focus(); break;
            case "mlog/body":
                if (_mlogLines.Count > 0)
                {
                    double textX = _modernLogBody.X + 10 * ModernUnit, textY = _modernLogBody.Y + 5 * ModernUnit;
                    int line = Math.Clamp(_mlogScroll + (int)Math.Floor((_point.Y - textY) / Math.Max(1, _mlogLineHeight)), 0, _mlogLines.Count - 1);
                    _mlogText.CaretIndex = ModernLogIndexAt(line, _point.X - textX);
                }
                _mlogInput?.Focus(); break;
            default: _mlogInput?.SetFocused(false); break;
        }
        Publish(); return true;
    }

    /// <summary>The wheel over the log scrolls its text.</summary>
    internal bool ScrollModernLog(string id, double delta)
    {
        if (!_modernLog || !id.StartsWith("mlog/", StringComparison.Ordinal) || delta == 0) return false;
        _mlogScroll = Math.Max(0, _mlogScroll + (delta < 0 ? 3 : -3)); _mlogFollow = false;
        Publish(); return true;
    }
}
