using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI guild window: the retail Guild Information window's content and actions (the same requests,
/// permissions, prompts and entry modes) in a Modern UI window: the members on the left, the rank abilities as a
/// column of buttons on the right, names and messages typed in the window itself, the guild's questions answered in
/// it, and the stats and contributions shown in a pane beside it.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private Rect _modernGuildBounds;
    internal EnbUiPoint? ModernGuildPosition { get; set; }
    internal Action<EnbUiPoint>? ModernGuildMoved { get; set; }

    /// <summary>A retail guild window label (fa_tsi35a.ini / fa_tsi36a.ini), trimmed.</summary>
    private string GuildString(string prefix, string id, string fallback)
        => _guildLayouts.TryGetValue(prefix, out var layout) && layout.TryResolve(id, out var definition) &&
           definition.Properties.GetValueOrDefault("String0", "").Trim() is { Length: > 0 } text ? text : fallback;

    private void ModernGuildPanel(EnbSceneGuildSnapshot guild)
    {
        bool editing = GuildEditing, asking = GuildConfirmationOpen, modal = editing || asking;
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u, section = 18 * u;
        double rowHeight = Math.Round(22 * u), listWidth = Math.Round(460 * u), columnGap = Math.Round(12 * u), abilityWidth = Math.Round(170 * u);
        double contentWidth = listWidth + columnGap + abilityWidth, nameWidth = Math.Round(150 * u), rankWidth = Math.Round(110 * u);
        int visible = Math.Clamp(guild.Members.Length, 12, 18);
        _guildPage = Math.Clamp(_guildPage, 0, Math.Max(0, guild.Members.Length - visible));
        double listHeight = visible * rowHeight;
        double abilitiesHeight = GuildButtons.Length * (head + gap) - gap;
        double body = Math.Max(section + head + listHeight + Math.Round(16 * u), section + abilitiesHeight);
        // The entry or the question, under both columns.
        string promptText = asking ? GuildPromptText(guild.Prompt!) : editing ? _guildEntry switch
        {
            EnbGuildAction.Recruit => "Recruit Member", EnbGuildAction.Tag => "Set Member Tag",
            EnbGuildAction.Message => "Enter Guild Message", EnbGuildAction.Stats => "Enter Guild Name", _ => "Create a new Guild"
        } : "";
        var promptLines = modal ? ModernWrap(promptText, contentWidth, text) : [];
        double footer = modal ? gap * 2 + promptLines.Count * Math.Round(18 * u) + gap + head : 0;
        double width = pad * 2 + contentWidth, height = pad * 2 + head + gap + body + footer + (_message is not null ? rowHeight : 0), x, y;
        if (ModernGuildPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernGuildBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernGuildBounds, "guild/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("guild/title", GuildString("guild", "ignore00_TSI35a", "Guild Information"), new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("guild/Close", new(x + width - pad - head, cy, head, head));
        _tips["guild/Close"] = _guildTips?.GetValueOrDefault("EXIT", "") is { Length: > 0 } closeTip ? closeTip : "Close";
        cy += head + gap;
        double top = cy;
        // Members: name and rank, online first (the roster's order).
        ModernText("guild/members", GuildString("guild", "MEMBER_TITLE_TSI35a", "All Members"), new(cx, cy, listWidth, section), new(.7f, .8f, 1), small);
        cy += section;
        double nameX = cx + 8 * u, rankX = nameX + nameWidth + 12 * u, placeX = rankX + rankWidth + 12 * u, placeWidth = listWidth - (placeX - cx) - 8 * u - (guild.Members.Length > visible ? ModernScrollbarRoom : 0);
        ModernText("guild/nameHeading", GuildString("guild", "NAME_TSI35a", "Name"), new(nameX, cy, nameWidth, head), new(.7f, .75f, .88f), text);
        ModernText("guild/rankHeading", GuildString("guild", "STATUS_TSI35a", "Rank"), new(rankX, cy, rankWidth, head), new(.7f, .75f, .88f), text);
        ModernText("guild/placeHeading", FriendString("STATUS_TSI28A", "Location"), new(placeX, cy, placeWidth, head), new(.7f, .75f, .88f), text);
        cy += head;
        var list = new Rect(cx, cy, listWidth, listHeight);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        Hit("guild/List", FromPixels(list));
        for (int n = 0; n < visible && _guildPage + n < guild.Members.Length; n++)
        {
            int index = _guildPage + n; var member = guild.Members[index];
            var row = new Rect(cx, Snap(cy + n * rowHeight), listWidth, rowHeight);
            string id = "guild/Member/" + index.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(row));
            bool selected = member.Identity == _guildMember;
            if (n % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if ((selected || _hover == id) && !modal) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            EnbRgb nameColor = member.IsOffline ? new(.55f, .58f, .66f) : new(1, 1, 1), rankColor = member.IsOffline ? new(.5f, .53f, .6f) : new(.88f, .91f, .97f);
            ModernText(id + "/name", ModernEllipsis(member.Identity, nameWidth, text), new(nameX, row.Y, nameWidth, row.Height), nameColor, text);
            // The roster line's status: the quoted rank, then where the member is (or that they are offline).
            var (rank, place) = GuildMemberStatus(member);
            ModernText(id + "/rank", ModernEllipsis(rank, rankWidth, text), new(rankX, row.Y, rankWidth, row.Height), rankColor, text);
            ModernText(id + "/place", ModernEllipsis(place, placeWidth, text), new(placeX, row.Y, placeWidth, row.Height), rankColor, text);
            _tips[id] = member.Identity + (member.Status.Length > 0 ? "\n" + member.Status : "") + (member.IsOffline ? "\nOffline" : "");
        }
        ModernScrollbar("guild/List", list, guild.Members.Length, visible, _guildPage, v => _guildPage = v);
        if (guild.Members.Length > 0)
            ModernText("guild/count", guild.Members.Length + (guild.Members.Length == 1 ? " member" : " members"), new(cx, cy + listHeight, listWidth, Math.Round(16 * u)), new(.6f, .65f, .8f), small, 2);
        // Rank abilities: what this rank may do, the others dimmed.
        double ax = cx + listWidth + columnGap, ay = top;
        ModernText("guild/abilities", GuildString("guild", "LEADER_TITLE_TSI35a", "Rank Abilities"), new(ax, ay, abilityWidth, section), new(.7f, .8f, 1), small);
        ay += section;
        foreach (var button in GuildButtons)
        {
            string id = "guild/" + button.Action;
            ModernButton(id, GuildString("guild", button.Mesh + "_TSI35a", button.Action.ToString()), new(ax, ay, abilityWidth, head), text,
                _guildTips?.GetValueOrDefault(button.Tip, "") ?? "", !modal && GuildAllowed(button.Action));
            ay += head + gap;
        }
        cy = top + body;
        if (modal)
        {
            cy += gap * 2;
            double line = Math.Round(18 * u);
            for (int i = 0; i < promptLines.Count; i++) ModernText("guild/prompt/" + i, promptLines[i], new(cx, cy + i * line, contentWidth, line), new(1, .85f, .3f), text);
            cy += promptLines.Count * line + gap;
            if (asking)
            {
                // The retail question's two answers (dialogbox.ini GUILD_*_CONFIRM).
                var definition = GuildPromptDefinition(guild.Prompt!);
                string yes = definition?.GetValueOrDefault("Option0", "") is { Length: > 0 } a ? a : "Accept", no = definition?.GetValueOrDefault("Option1", "") is { Length: > 0 } b ? b : "Reject";
                double yesWidth = Math.Ceiling(ModernMeasure(yes, text) + 24 * u), noWidth = Math.Ceiling(ModernMeasure(no, text) + 24 * u);
                ModernButton("guild/Accept", yes, new(cx, cy, yesWidth, head), text, "", !_busy);
                ModernButton("guild/Reject", no, new(cx + yesWidth + gap, cy, noWidth, head), text, "", !_busy);
            }
            else
            {
                string confirm = "Confirm", cancel = "Cancel";
                double confirmWidth = Math.Ceiling(ModernMeasure(confirm, text) + 24 * u), cancelWidth = Math.Ceiling(ModernMeasure(cancel, text) + 24 * u);
                var box = new Rect(cx, cy, contentWidth - confirmWidth - cancelWidth - gap * 2, head);
                ModernFrame("DIALOG_BOX_INFO", box, "guild/Input");
                string typed = _guildInput?.Text ?? "";
                bool focused = _guildInput is { IsFocused: true };
                string shown = focused ? typed.Insert(Math.Clamp(_guildInput!.CaretIndex, 0, typed.Length), "|") : typed;
                ModernText("guild/inputText", ModernEllipsis(shown, box.Width - 16 * u, text), new(box.X + 8 * u, cy, box.Width - 16 * u, head), new(1, 1, 1), text);
                if (_guildInput is not null) _guildInput.Bounds = box;
                _tips["guild/Input"] = _creditTips?.GetValueOrDefault("VALUE_BUTTON", "") ?? "";
                ModernButton("guild/Submit", confirm, new(box.Right + gap, cy, confirmWidth, head), text, _creditTips?.GetValueOrDefault("CONFIRM", "") ?? "", !_busy);
                ModernButton("guild/Cancel", cancel, new(box.Right + gap * 2 + confirmWidth, cy, cancelWidth, head), text, _creditTips?.GetValueOrDefault("CANCEL", "") ?? "", !_busy);
            }
            cy += head + gap;
        }
        if (_message is { } message) ModernText("guild/Error", message, new(cx, cy, contentWidth, rowHeight), new(1, .5f, .5f), small);
        Flush();
        if (_guildDisplay) ModernGuildDisplay(guild);
    }

    /// <summary>A roster entry's rank and the rest of its status (location; "Offline" for an offline member).</summary>
    private static (string Rank, string Place) GuildMemberStatus(EnbNext.Net7.Social.Net7GuildRosterEntry member)
    {
        string status = member.Status.Trim(), rank = member.RankName;
        if (rank.Length > 0 && status.StartsWith('"'))
        {
            int end = status.IndexOf('"', 1);
            status = end > 0 ? status[(end + 1)..].Trim() : "";
        }
        else if (rank.Length == 0) { rank = status; status = ""; }
        if (member.IsOffline) status = "Offline";
        return (rank, status);
    }

    /// <summary>A guild question while the guild window is closed (an invitation to join a guild): a small Modern UI window.</summary>
    private void ModernGuildQuestion(EnbSceneGuildSnapshot guild)
    {
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, line = Math.Round(18 * u);
        double width = Math.Round(420 * u), textWidth = width - pad * 2;
        var lines = ModernWrap(GuildPromptText(guild.Prompt!), textWidth, text);
        double height = pad * 2 + head + gap + lines.Count * line + gap * 2 + head;
        var bounds = new Rect(Snap((_size.Width - width) / 2), Snap((_size.Height - height) * .4), width, height);
        Hit("guild/ModalBlock", new(0, 0, 640, 480)); _hits["guild/ModalBlock"].LayoutGroup = EnbUiGroup.None;
        ModernFrame("DIALOG_BOX_WINDOW", bounds, "guild/Question");
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        ModernText("guild/questionTitle", GuildString("guild", "ignore00_TSI35a", "Guild Information"), new(cx, cy, textWidth, head), new(1, .85f, .3f), title);
        cy += head + gap;
        for (int i = 0; i < lines.Count; i++) ModernText("guild/question/" + i, lines[i], new(cx, cy + i * line, textWidth, line), new(1, 1, 1), text);
        cy += lines.Count * line + gap * 2;
        var definition = GuildPromptDefinition(guild.Prompt!);
        string yes = definition?.GetValueOrDefault("Option0", "") is { Length: > 0 } a ? a : "Accept", no = definition?.GetValueOrDefault("Option1", "") is { Length: > 0 } b ? b : "Reject";
        double yesWidth = Math.Ceiling(ModernMeasure(yes, text) + 24 * u), noWidth = Math.Ceiling(ModernMeasure(no, text) + 24 * u);
        ModernButton("guild/Accept", yes, new(cx, cy, yesWidth, head), text, "", !_busy);
        ModernButton("guild/Reject", no, new(cx + yesWidth + gap, cy, noWidth, head), text, "", !_busy);
        Flush();
    }

    /// <summary>The guild's stats or contributions (the retail Guild Display), in a pane beside the window.</summary>
    private void ModernGuildDisplay(EnbSceneGuildSnapshot guild)
    {
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, line = Math.Round(18 * u);
        var window = _modernGuildBounds;
        var lines = guild.DisplayLines;
        double width = Math.Round(420 * u), textWidth = width - pad * 2;
        int visible = Math.Clamp(lines.Length, 6, 24);
        _guildDisplayPage = Math.Clamp(_guildDisplayPage, 0, Math.Max(0, lines.Length - visible));
        double height = pad * 2 + head + gap + visible * line;
        double x = window.Right + gap <= _size.Width - width ? window.Right + gap : Math.Max(0, window.X - gap - width);
        double y = Math.Clamp(window.Y, 0, Math.Max(0, _size.Height - height));
        var bounds = new Rect(Snap(x), Snap(y), width, height);
        ModernFrame("DIALOG_BOX_WINDOW", bounds, "guild/Display");
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        ModernText("guild/displayTitle", GuildString("guildDisplay", "TITLE_TSI36a", "Guild Display"), new(cx, cy, textWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("guild/DisplayClose", new(bounds.Right - pad - head, cy, head, head));
        cy += head + gap;
        for (int i = 0; i < visible && _guildDisplayPage + i < lines.Length; i++)
            ModernText("guild/display/" + i, ModernEllipsis(lines[_guildDisplayPage + i], textWidth - (lines.Length > visible ? ModernScrollbarRoom : 0), text), new(cx, cy + i * line, textWidth, line), new(1, 1, 1), text);
        ModernScrollbar("guild/displayText", new(cx, cy, textWidth + pad / 2, visible * line), lines.Length, visible, _guildDisplayPage, v => _guildDisplayPage = v);
        Flush();
    }
}
