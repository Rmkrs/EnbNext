using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI group: in space the retail group panel gives way to the Modern UI group frame (EnbModernHud), which
/// shows the retained member rows. The retail panel's buttons become menus with the same group requests: the member
/// buttons (Assist, Ask for help, Boot) and the symbol chat the retail right-click opens are the member row's menu; the
/// group buttons (formation, ask the group for help, leave or disband) follow them in the same menu. The multibox
/// shortcuts stand beside the Modern UI rows as beside the retail ones. In a station the rows show names only, and a
/// row's menu holds what retail allows there: removing a member (the leader) and leaving or disbanding.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private ImmutableArray<EnbModernMember> _modernMembers = [];
    /// <summary>The member rows the Modern UI group frame shows (empty when there is no group, or in a station).</summary>
    /// Only while the retail group UI would show: after a disband the panels stop publishing, so the rows are dropped here.
    internal ImmutableArray<EnbModernMember> ModernGroupMembers => ModernGroupShown && HasGroupUi && Group is not null ? _modernMembers : [];
    /// <summary>The leader chose a formation the player has not formed up in (the retail formation button flashes).</summary>
    internal bool ModernFormationReady => ModernGroupMembers.Length > 0 && _spaceGroupVisual.FormationAttention.Forced;
    /// <summary>A Modern UI member row in screen space.</summary>
    internal Func<int, Rect?>? ModernGroupRow { get; set; }
    private bool ModernGroupShown => ModernUi;
    /// <summary>Multibox: the reactor (whole percent) of a member that is one of the player's own characters sharing it.</summary>
    internal Func<uint, int?>? MemberReactor { get; set; }

    /// <summary>The Modern UI rows from the retained members (names, help requests, received symbols), and the
    /// multibox shortcuts beside them.</summary>
    private void ModernGroupRows(long now)
    {
        var rows = ImmutableArray.CreateBuilder<EnbModernMember>(_groupMembers.Items.Count);
        long clock = Environment.TickCount64;
        foreach (var member in _groupMembers.Items)
        {
            bool attention = _spaceGroupVisual.MemberAttention.TryGetValue(member.PresentationLifetime, out var flash) && flash.Forced;
            var row = new EnbModernMember(_groupMemberLabels.GetValueOrDefault(member.PresentationLifetime, member.Name), member.GameId, member.PresentationLifetime, attention,
                Reactor: member.GameId != 0 ? MemberReactor?.Invoke(member.GameId) : null);
            if (_receivedGroupSymbols.TryGetValue(member.Name, out var received))
            {
                if (!_art.TryGetValue(LoadGroupSymbolArt(received.Asset), out var art)) _receivedGroupSymbols.Remove(member.Name);
                else
                {
                    _animatingText |= received.Lifetime.Remaining > 0;
                    row = row with { Symbol = art.Asset, SymbolDesign = art.Design, SymbolSince = clock - (now - received.Started), SymbolContext = received.Context };
                }
            }
            rows.Add(row);
        }
        if (!rows.SequenceEqual(_modernMembers)) _modernMembers = rows.ToImmutable();
        // The multibox shortcuts beside each row at the retail size, on the side facing the middle of the screen
        // (left of the rows where the retail panel stands, right of them when the frame is moved to the left).
        var layout = _layoutGroup; _layoutGroup = EnbUiGroup.None;
        double height = RetailGroupRow() is { } retail ? retail.Height : 0;
        for (int i = 0; i < _groupMembers.Items.Count; i++)
            if (ModernGroupRow?.Invoke(i) is { } screen)
            {
                var row = FromPixels(screen);
                if (height > row.Height) row = new(row.X, row.Center.Y - height / 2, row.Width, height); // never smaller than the retail ones
                DrawRemoteShortcuts(_groupMembers.Items[i].GameId, row, row.Center.X < 320);
            }
        Flush(); _layoutGroup = layout;
    }

    /// <summary>The retail group panel's first row (GROUP00, as GroupPanelAt places it), in design units.</summary>
    private Rect? RetailGroupRow()
    {
        if (!_groupAnchors.TryGetValue("group/GROUP00", out var origin) || !_art.ContainsKey("groupRow/GROUP") ||
            !_groupDepths.TryGetValue("group/GROUP00", out float depth) || !_groupDepths.TryGetValue("groupRow/GROUP", out float row)) return null;
        var r = Native("groupRow/GROUP").Translate(new(origin.X - 320, origin.Y - 240));
        double perspective = 320 / (320 - (depth + row));
        return new(320 + (r.X - 320) * perspective, 240 + (r.Y - 240) * perspective, r.Width * perspective, r.Height * perspective);
    }
    /// <summary>The retail group panel's first row in screen space (before its placement): where the Modern UI rows stand.</summary>
    internal Rect? ModernGroupAnchor() => RetailGroupRow() is { } r ? Screen(r) : null;

    /// <summary>A group request from a Modern UI menu or row, as the retail button with that id sends it.</summary>
    internal void ModernGroupCommand(string id) => _ = Activate(id);

    private bool ModernGroupAvailable => !_groupLeaveConfirm && Group?.Invitation is null;

    /// <summary>The group's own commands (the retail panel buttons): formation, ask the group for help, leave or disband.
    /// They close every member row's menu, in space and docked alike.</summary>
    internal List<EnbModernMenuItem> ModernGroupMenu()
    {
        if (!ModernGroupShown || Group is not { IsInGroup: true } group || _groupMembers.Items.Count == 0) return [];
        bool available = ModernGroupAvailable;
        EnbModernMenuItem Formation(string label, string action) => new(label, () => ModernGroupCommand("group/" + action), Enabled: available && CanGroupFormation(action));
        // One level: the formation choices under their label, then the group commands.
        List<EnbModernMenuItem> entries = [EnbModernMenuItem.Heading("Formation")];
        if (group.IsLeader) entries.AddRange([Formation("Slot formation", "Slot"), Formation("Pipe formation", "Pipe"), Formation("Block formation", "Block"), Formation("Break formation", "BreakFormation")]);
        else entries.AddRange([Formation("Form up", "Form"), Formation("Leave formation", "LeaveFormation")]);
        entries.Add(EnbModernMenuItem.Heading("Commands"));
        entries.Add(new("Ask group for help", () => ModernGroupCommand("group/AskGroup"), Enabled: available));
        entries.Add(new(group.IsLeader ? "Disband group" : "Leave group", () => ModernGroupCommand("group/LeavePrompt"), Enabled: available));
        return entries;
    }

    /// <summary>A member row's menu, one level: target, assist, ask for help, (for the leader) remove from the group,
    /// then the symbols under their section labels.</summary>
    internal (string Name, List<EnbModernMenuItem> Items)? ModernMemberMenu(int index)
    {
        if (!ModernGroupShown || Group is not { } group || (uint)index >= (uint)_groupMembers.Items.Count) return null;
        var member = _groupMembers.Items[index];
        bool available = ModernGroupAvailable, known = member.GameId != 0;
        string at = "/" + index.ToString(CultureInfo.InvariantCulture);
        string name = _groupMemberLabels.GetValueOrDefault(member.PresentationLifetime, member.Name);
        if (StationGroup)
        {
            // Docked, retail allows only Kick on a row and Leave/Disband on the panel (ActivateGroup drops the rest);
            // the group's own command follows, as in space.
            var docked = new List<EnbModernMenuItem>();
            if (group.IsLeader) docked.Add(new("Remove from group", () => ModernGroupCommand("group/Kick" + at), Enabled: available));
            docked.Add(EnbModernMenuItem.Heading("Group"));
            docked.Add(new(group.IsLeader ? "Disband group" : "Leave group", () => ModernGroupCommand("group/LeavePrompt"), Enabled: available));
            return (name, docked);
        }
        var items = new List<EnbModernMenuItem>
        {
            new("Target", () => ModernGroupCommand("group/TargetMember" + at), Enabled: known),
            new("Assist", () => ModernGroupCommand("group/Assist" + at), Enabled: available && known),
            new("Ask for help", () => ModernGroupCommand("group/AskMember" + at), Enabled: available && known),
        };
        if (group.IsLeader) items.Add(new("Remove from group", () => ModernGroupCommand("group/Kick" + at), Enabled: available));
        // The group's own commands follow the member's, as docked (one place for them).
        foreach (var entry in ModernGroupMenu())
            items.Add(entry.Section && entry.Label == "Commands" ? EnbModernMenuItem.Heading("Group") : entry);
        // The retail symbol chat menu, in one level: its three sections under their labels.
        foreach (var (section, title) in new[] { (0, "General symbols"), (1, "Team symbols"), (2, "Context symbols") })
        {
            bool labelled = false;
            for (int i = 0; i < _groupSymbols.Count; i++)
                if (_groupSymbols[i].Section == section && _groupSymbols[i].Text.Length > 0)
                {
                    if (!labelled) { items.Add(EnbModernMenuItem.Heading(title)); labelled = true; }
                    string id = "group/Symbol/" + i.ToString(CultureInfo.InvariantCulture);
                    items.Add(new(_groupSymbols[i].Text, () => ModernGroupCommand(id), Enabled: available));
                }
        }
        return (name, items);
    }
}
