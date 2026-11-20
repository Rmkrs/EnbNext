using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI commands window (the help menu's Commands): every chat command from the game's own list
/// (slash_commands.ini), one per line with its description, a box to find one, and the full text of the chosen
/// command beside the window with a button that puts it in the chat line.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernCommands;
    private Rect _modernCommandsBounds;
    private IReadOnlyList<EnbRetailSlashCommand>? _modernCommandList;
    private readonly List<int> _modernCommandRows = [];
    private int _modernCommandScroll, _modernCommandSelected = -1;
    private EnbUiTextInput? _modernCommandSearch;
    internal EnbUiPoint? ModernCommandsPosition { get; set; }
    internal Action<EnbUiPoint>? ModernCommandsMoved { get; set; }
    /// <summary>Puts a command in the chat line (the text typed so far is replaced).</summary>
    internal Action<string>? ModernCommandChatRequested { get; set; }
    public bool ModernCommandsOpen => _modernCommands;
    public bool ModernCommandSearchEditing => _modernCommandSearch is { IsVisible: true, IsFocused: true };
    internal EnbUiTextInput? ModernCommandSearchInput => _modernCommandSearch;
    internal int ModernCommandCount => _modernCommandRows.Count;

    public bool ToggleModernCommands()
    {
        if (!_interactive || !ModernUi) return false;
        _modernCommands = !_modernCommands;
        _modernCommandSelected = -1; _modernCommandScroll = 0;
        if (_modernCommandSearch is not null) { _modernCommandSearch.Text = ""; _modernCommandSearch.IsVisible = false; }
        if (_modernCommands) { RaiseModern("mcommand"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }

    private IReadOnlyList<EnbRetailSlashCommand> ModernCommandList()
    {
        if (_modernCommandList is not null || _assets is null) return _modernCommandList ?? [];
        try { _modernCommandList = EnbRetailSlashCommand.Parse(EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "slash_commands.ini"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _modernCommandList = []; }
        return _modernCommandList;
    }

    private static string CommandText(EnbRetailSlashCommand c) => c.Command.Trim();
    private static string CommandDescription(EnbRetailSlashCommand c) => c.Description.Trim().Replace("\\n", "\n");

    private void ModernCommandsWindow()
    {
        var all = ModernCommandList();
        string query = (_modernCommandSearch?.Text ?? "").Trim();
        _modernCommandRows.Clear();
        for (int i = 0; i < all.Count; i++)
            if (query.Length == 0 || all[i].Command.Contains(query, StringComparison.OrdinalIgnoreCase) || all[i].Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                _modernCommandRows.Add(i);
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u;
        double rowHeight = Math.Round(22 * u), commandWidth = Math.Round(230 * u), descriptionWidth = Math.Round(460 * u), column = Math.Round(12 * u);
        double contentWidth = 8 * u + commandWidth + column + descriptionWidth + 8 * u;
        double chrome = pad * 2 + head + gap + head + gap + Math.Round(18 * u) + gap;
        int visible = Math.Clamp((int)((_size.Height * .6 - chrome) / rowHeight), 6, Math.Max(6, all.Count));
        double width = pad * 2 + contentWidth, height = chrome + visible * rowHeight;
        double x, y;
        if (ModernCommandsPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernCommandsBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernCommandsBounds, "mcommand/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mcommand/title", "Commands", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mcommand/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;
        // Find: the commands whose name or description contain the text.
        var searchRect = new Rect(cx, cy, Math.Round(300 * u), head);
        ModernFrame("DIALOG_BOX_INFO", searchRect, "mcommand/search");
        bool editing = ModernCommandSearchEditing;
        string search = _modernCommandSearch?.Text ?? "";
        var searchText = new Rect(cx + 8 * u, cy, searchRect.Width - 16 * u, head);
        if (search.Length == 0 && !editing) ModernText("mcommand/searchText", "Find a command", searchText, new(.55f, .6f, .7f), text);
        else ModernText("mcommand/searchText", editing ? search.Insert(Math.Clamp(_modernCommandSearch!.CaretIndex, 0, search.Length), "|") : search, searchText, new(1, 1, 1), text);
        if (_modernCommandSearch is not null) _modernCommandSearch.Bounds = searchRect;
        _tips["mcommand/search"] = "Type part of a command or of what it does.";
        ModernText("mcommand/count", query.Length == 0 ? all.Count + " commands" : _modernCommandRows.Count + " of " + all.Count + " commands",
            new(searchRect.Right + gap * 2, cy, cx + contentWidth - searchRect.Right - gap * 2, head), new(.65f, .7f, .85f), small, 2);
        cy += head + gap;
        Flush();
        var list = new Rect(cx, cy, contentWidth, visible * rowHeight);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        _modernCommandScroll = Math.Clamp(_modernCommandScroll, 0, Math.Max(0, _modernCommandRows.Count - visible));
        Hit("mcommand/list", FromPixels(list));
        if (_modernCommandRows.Count == 0) ModernText("mcommand/none", all.Count == 0 ? "The game's command list is not available." : "No command matches.", new(cx + 8 * u, cy, contentWidth - 16 * u, rowHeight), new(.75f, .8f, .92f), text);
        double commandX = cx + 8 * u, descriptionX = commandX + commandWidth + column;
        for (int n = 0; n < visible && _modernCommandScroll + n < _modernCommandRows.Count; n++)
        {
            int index = _modernCommandRows[_modernCommandScroll + n];
            var command = all[index];
            var row = new Rect(cx, Snap(cy + n * rowHeight), contentWidth, rowHeight);
            string id = "mcommand/row/" + index.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(row));
            bool selected = index == _modernCommandSelected;
            if ((_modernCommandScroll + n) % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected || _hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            ModernText(id + "/command", ModernEllipsis(CommandText(command), commandWidth, text), new(commandX, row.Y, commandWidth, row.Height), new(1, .92f, .55f), text);
            ModernText(id + "/text", ModernEllipsis(CommandDescription(command).Replace('\n', ' '), descriptionWidth - ModernScrollbarRoom, text), new(descriptionX, row.Y, descriptionWidth, row.Height), new(.85f, .88f, .95f), text);
            _tips[id] = CommandText(command) + "\n\nClick for details";
        }
        cy += visible * rowHeight + gap;
        ModernScrollbar("mcommand/list", list, _modernCommandRows.Count, visible, _modernCommandScroll, v => _modernCommandScroll = v);
        Flush();
        if (_modernCommandSelected >= 0 && _modernCommandSelected < all.Count) ModernCommandDetails(all[_modernCommandSelected]);
        else _modernCommandSelected = -1;
    }

    /// <summary>The chosen command beside the window: the command, its whole description and a button to use it.</summary>
    private void ModernCommandDetails(EnbRetailSlashCommand command)
    {
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, line = Math.Round(18 * u);
        var window = _modernCommandsBounds;
        double width = Math.Round(380 * u), textWidth = width - pad * 2;
        var lines = ModernWrap(CommandDescription(command), textWidth, text);
        double height = pad * 2 + head + gap + Math.Max(1, lines.Count) * line + gap * 3 + head;
        double x = window.Right + gap <= _size.Width - width ? window.Right + gap : Math.Max(0, window.X - gap - width);
        double y = Math.Clamp(window.Y, 0, Math.Max(0, _size.Height - height));
        var bounds = new Rect(Snap(x), Snap(y), width, height);
        ModernFrame("DIALOG_BOX_WINDOW", bounds, "mcommand/detail");
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        ModernText("mcommand/detail/title", ModernEllipsis(CommandText(command), textWidth - head - gap, title), new(cx, cy, textWidth - head, head), new(1, .92f, .55f), title);
        ModernCloseButton("mcommand/detail/close", new(bounds.Right - pad - head, cy, head, head));
        cy += head + gap;
        for (int i = 0; i < lines.Count; i++) ModernText("mcommand/detail/t" + i, lines[i], new(cx, cy + i * line, textWidth, line), new(1, 1, 1), text);
        cy += Math.Max(1, lines.Count) * line + gap * 3;
        string label = "Type in chat";
        ModernButton("mcommand/detail/chat", label, new(cx, cy, Math.Ceiling(ModernMeasure(label, text) + 24 * u), head), text,
            "Put " + ChatCommand(command).TrimEnd() + " in the chat line, to finish and send.");
        Flush();
    }

    /// <summary>A command as it is typed: up to its first placeholder (&lt;...&gt;), with the space before it.</summary>
    private static string ChatCommand(EnbRetailSlashCommand command)
    {
        string text = CommandText(command);
        int placeholder = text.IndexOf('<');
        return placeholder < 0 ? text : text[..placeholder];
    }

    private bool ActivateModernCommands(string id, bool right)
    {
        if (!id.StartsWith("mcommand/", StringComparison.Ordinal)) return false;
        if (id != "mcommand/search" && _modernCommandSearch is { IsFocused: true }) _modernCommandSearch.IsVisible = false;
        if (right) return true;
        var all = ModernCommandList();
        if (id == "mcommand/close") { _modernCommands = false; _modernCommandSelected = -1; InteractionSoundRequested?.Invoke("TextClick03.wav"); }
        else if (id == "mcommand/detail/close") { _modernCommandSelected = -1; InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); }
        else if (id == "mcommand/search")
        {
            _modernCommandSearch ??= CreateModernCommandSearch();
            _modernCommandSearch.IsVisible = true; _modernCommandSearch.Focus();
        }
        else if (id == "mcommand/detail/chat" && _modernCommandSelected >= 0 && _modernCommandSelected < all.Count)
        {
            InteractionSoundRequested?.Invoke("TextClick03.wav");
            ModernCommandChatRequested?.Invoke(ChatCommand(all[_modernCommandSelected]));
        }
        else if (id.StartsWith("mcommand/row/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(13), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            _modernCommandSelected = _modernCommandSelected == index ? -1 : index;
            InteractionSoundRequested?.Invoke("fontclick.wav");
        }
        return true;
    }

    private EnbUiTextInput CreateModernCommandSearch()
    {
        var input = CreateTextInput(new(32), key =>
        {
            if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return Task.FromResult(false);
            // Escape clears the text; Enter and Tab keep it. Either way the keyboard returns to the game.
            if (key == EnbInventoryKeys.Escape && _modernCommandSearch is { } search) search.Text = "";
            if (_modernCommandSearch is not null) _modernCommandSearch.IsVisible = false;
            Publish(); return Task.FromResult(true);
        });
        input.Changed += (_, _) => { _modernCommandScroll = 0; Publish(); };
        input.LostFocus += (_, _) => Publish();
        return input;
    }

    /// <summary>The wheel over the list scrolls it.</summary>
    internal bool ScrollModernCommands(string id, double delta)
    {
        if (!_modernCommands || !id.StartsWith("mcommand/", StringComparison.Ordinal) || id.StartsWith("mcommand/detail", StringComparison.Ordinal) || delta == 0) return false;
        _modernCommandScroll = Math.Max(0, _modernCommandScroll + (delta < 0 ? 3 : -3));
        Publish(); return true;
    }
}
