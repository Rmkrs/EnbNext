using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI menus: the help and social buttons' menus and the chat's tab and emote menus, which take over the
/// retail chat window's Chat, Group, Help and Emote drop-downs in the Modern UI, in space and docked alike.
/// </summary>
internal sealed partial class EnbWorldUi
{
    internal EnbModernMenu? ModernMenu { get; private set; }

    private void OpenHelpMenu(EnbUiRect button)
    {
        if (ModernMenu is not { } menu) return;
        if (menu.Open && menu.Owner == "next/Help") { menu.Close(); hudOwner.QueueUtilitySound("SR_scifielectronic42.wav"); return; }
        menu.Show("next/Help", "Help",
        [
            new("Commands", () => { if (!Inventory.ModernCommandsOpen) Inventory.ToggleModernCommands(); }),
            new("Net-7 Wiki", () => Inventory.OpenModernWikiHome()),
            // The retail Get Help and Self Help pages all lead to the Net-7 support site (tickets for a GM).
            new("Contact a GM", () => Options.OpenInformationBrowser(0, "Support")),
            // Next: a lag report for the developer.
            .. (Chat.DeveloperReportRequested is { } report ? new EnbModernMenuItem[] { new("Send a report to the developer", report) } : []),
        ], new(button.X, button.Y - 2), true, button);
        hudOwner.QueueUtilitySound("SR_scifielectronic40.wav");
    }

    /// <summary>A menu another Modern UI panel asks for (the My characters panel's).</summary>
    internal void ShowModernMenu(string owner, string? heading, IReadOnlyList<EnbModernMenuItem> items, EnbUiPoint at)
    {
        if (ModernMenu is not { } menu || items.Count == 0) return;
        menu.Show(owner, heading, items, at, false);
        hudOwner.QueueUtilitySound("SR_scifielectronic40.wav");
    }

    /// <summary>A Modern UI group member row's menu (the retail row buttons and symbol chat).</summary>
    private void OpenMemberMenu(int member, EnbUiPoint at)
    {
        if (ModernMenu is not { } menu || Inventory.ModernMemberMenu(member) is not { } entries) return;
        menu.Show("group/member", entries.Name, entries.Items, at, false);
        hudOwner.QueueUtilitySound("SR_scifielectronic40.wav");
    }

    /// <summary>The target frame's right-click menu: the target's faction in the Factions window, its Net-7 Wiki page, and
    /// clearing the target.</summary>
    private void OpenTargetMenu(EnbUiPoint at)
    {
        if (ModernMenu is not { } menu || modernScene?.Hud.CurrentTargetId is not uint id
            || modernScene.Objects.FirstOrDefault(o => o.GameId == id) is not { } target) return;
        var items = new List<EnbModernMenuItem>();
        // The faction the ship's data names (ShipAuxData FactionIdentifier, as the retail scanner's faction line), when it is in the character's list.
        if (target.PlayerPresentation?.FactionIdentifier is { Length: > 0 } faction && Inventory.ModernFactionName(faction) is { } name)
            items.Add(new("Show faction: " + name, () => Inventory.ShowModernFaction(faction)));
        // Mobs, named mobs, stations and the like have wiki pages (drops, missions); players (type 1) and remains (0x19) do not.
        if (target.ObjectType is not (1 or 0x19) && target.Name is { } wikiName && wikiName.Trim().Length > 0)
            items.Add(new("Look up on wiki", () => Inventory.OpenModernWiki(wikiName)));
        items.Add(new("Clear target", Scanner.ClearSelectedTarget));
        menu.Show("target", target.Name, items, at, false);
        hudOwner.QueueUtilitySound("SR_scifielectronic40.wav");
    }

    /// <summary>Next: the Social micro button's menu (friends list, guild, group finder), in space and docked alike.</summary>
    private void OpenSocialMenu(EnbUiRect button)
    {
        if (ModernMenu is not { } menu) return;
        if (menu.Open && menu.Owner == "next/Social") { menu.Close(); hudOwner.QueueUtilitySound("SR_scifielectronic42.wav"); return; }
        menu.Show("next/Social", "Social",
        [
            new("Friends list", () => _ = Inventory.OpenFriends()),
            new("Guild", () => _ = Inventory.OpenGuild()),
            new("Group finder", () => _ = Inventory.OpenGroupFinder()),
        ], new(button.X, button.Y - 2), true, button);
        hudOwner.QueueUtilitySound("SR_scifielectronic40.wav");
    }

    /// <summary>The Captain's Log button or key: opens the log, or closes (and saves) it when it is open, as the other windows.</summary>
    internal void ToggleCaptainLog()
    {
        if (Inventory.ModernLogOpen) { Inventory.CloseModernLog(); return; }
        if (Options.EndCaptainLog()) Inventory.OpenModernCaptainLog(Options.CaptainLogFactory);
    }

    /// <summary>A Modern UI chat menu (a tab's menu, the emotes): the shared menu, growing up from the chat.</summary>
    private void OpenChatMenu(string owner, string? heading, IReadOnlyList<EnbModernMenuItem> items, EnbUiPoint at, Func<IReadOnlyList<EnbModernMenuItem>>? refresh)
    {
        if (ModernMenu is not { } menu || items.Count == 0) return;
        if (menu.Open && menu.Owner == owner && owner == "mchat/emote") { menu.Close(); return; }
        menu.Show(owner, heading, items, at, true, refresh: refresh);
        hudOwner.QueueUtilitySound("SR_scifielectronic40.wav");
    }
}
