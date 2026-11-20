using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldUi
{
    private readonly EnbWorldRenderer worldOwner;
    private readonly Func<uint, CancellationToken, Task<bool>> targetRequest;
    private bool modernUi;
    private IEnbSceneFrame? modernScene;
    internal EnbModernHud? ModernHud { get; private set; }

    /// <summary>
    /// Next Modern UI (opt-in). While on, the retail target panel and target portrait give way to the
    /// Modern UI player and target frames; the scanner keeps owning the target, its model and its verbs.
    /// </summary>
    internal bool ModernUi
    {
        get => modernUi;
        set
        {
            if (modernUi == value) return;
            modernUi = value;
            Scanner.Replaced = value; Inventory.ModernUi = value; worldOwner.ScannerChromeHidden = value;
            hudOwner.ModernTooltips = value; Cockpit.Modern = value; worldOwner.CockpitReplaced = value; Chat.ModernMenus = value; Chat.ModernChat = value; Options.ModernUtility = value; Map.Modern = value; GalaxyMap.Modern = value;
            if (value) return;
            Options.ModernMultiboxButton = null; ModernMenu?.Close(); hudOwner.ModernMenu = null; ModernMenu = null; Inventory.ModernCommandChatRequested = null; Inventory.ModernMenuRequested = null;
            Cockpit.ModernSlotBounds = null; Cockpit.ModernEngineBounds = null; Cockpit.ModernRoundButtons = null;
            hudOwner.ModernHud = null; hudOwner.ModernTargetSource = null; ModernHud?.Dispose(); ModernHud = null;
            Scanner.ReplacementModelBounds = null; Inventory.ModernPortraits = []; Inventory.ModernInventoryAnchor = null; Inventory.ModernShortcutBounds = null; Inventory.ModernBarStops = null; Inventory.ModernGroupRow = null;
        }
    }

    /// <summary>The Modern map button or key: the galaxy map, or the sector map (in a station the station map, as the
    /// sector map has nothing to show there).</summary>
    internal void ToggleModernMap(bool galaxy)
    {
        if (!galaxy && ModernHud?.Station == true) Inventory.ToggleModernStationMap();
        else (galaxy ? GalaxyMap : Map).ToggleModern(galaxy);
    }
    internal void UpdateModern(IEnbSceneFrame scene, EnbRetailAssetBundle? assets) => UpdateModern(scene, assets, false);
    /// <summary>
    /// In a station the Modern HUD shows what the retail utility HUD shows there (the micro menu, experience and group);
    /// the ship's frames, bars and target controls belong to space.
    /// </summary>
    internal void UpdateModernStation(IEnbSceneFrame scene, EnbRetailAssetBundle? assets) => UpdateModern(scene, assets, true);

    private void UpdateModern(IEnbSceneFrame scene, EnbRetailAssetBundle? assets, bool station)
    {
        if (!modernUi || assets is null) return;
        modernScene = scene;
        if (!station) UpdateLocalPending();
        if (ModernHud is null)
        {
            ModernHud = new(assets) { Layout = Layout };
            ModernHud.ScannerHovered += ModernScannerHover;
            ModernHud.VerbPressed += verb =>
            {
                // Next multibox: the Harvest button a follower lends to this target goes to that follower.
                if (verb.Pilot is { } pilot) RemoteHarvestCommand?.Invoke("harvest", verb.TargetId, 0, pilot);
                else _ = Scanner.ExecuteVerbSafelyAsync(verb.TargetId, verb.Value);
            };
            ModernHud.CycleRequested += previous => Scanner.PressArrow(previous);
            ModernHud.ClearTargetRequested += Scanner.ClearSelectedTarget;
            ModernHud.TargetSelfRequested += () => { if (modernScene?.Objects.FirstOrDefault(o => o.IsLocalShip) is { } self) _ = RequestTarget(self.GameId); };
            ModernHud.Shortcut = Cockpit.ModernShortcut; ModernHud.ShortcutHovered = Cockpit.ModernHovered; ModernHud.Powerup = () => Cockpit.ModernPowerup;
            ModernHud.SlotArt = () => Cockpit.ModernSlotArt; ModernHud.DragImages = () => Cockpit.ModernDragImages;
            Cockpit.ModernSlotBounds = ModernHud.SlotBounds; Cockpit.ModernEngineBounds = name => ModernHud?.EngineBounds(name); ModernHud.Engine = () => Cockpit.ModernEngine; Cockpit.ModernRoundButtons = () => ModernHud?.RoundButtonBounds;
            ModernMenu = new(EnbRenderSession.Of(assets).OptionsPanels.Main.Asset);
            ModernHud.MenuOwner = () => ModernMenu is { Open: true } open ? open.Owner : null;
            ModernMenu.Changed += hudOwner.RequestPanelFrame; ModernMenu.SoundRequested += hudOwner.QueueUtilitySound;
            hudOwner.ModernMenu = ModernMenu;
            ModernHud.HelpRequested += OpenHelpMenu;
            ModernHud.OwnSkillLevel = Inventory.SkillLevel;
            ModernHud.SocialRequested += OpenSocialMenu; ModernHud.CaptainLogRequested += ToggleCaptainLog; Chat.ModernMenuRequested += OpenChatMenu;
            ModernHud.TargetMenuRequested += OpenTargetMenu;
            ModernHud.MemberMenuRequested += OpenMemberMenu;
            ModernHud.MemberPressed += member => Inventory.ModernGroupCommand("group/TargetMember/" + member.ToString(CultureInfo.InvariantCulture));
            ModernHud.MemberSymbolPressed += member => Inventory.ModernGroupCommand("group/ContextSymbol/" + member.ToString(CultureInfo.InvariantCulture));
            Inventory.ModernGroupRow = member => ModernHud?.MemberRowBounds(member);
            ModernHud.RetailGroupAnchor = Inventory.ModernGroupAnchor;
            Inventory.ModernCommandChatRequested = text => Chat.OpenInput(text: text);
            Inventory.ModernMenuRequested = ShowModernMenu;
            ModernHud.UtilityRequested += Options.Activate;
            ModernHud.InventoryRequested += () => Inventory.ToggleModernInventory();
            ModernHud.VaultRequested += () => Inventory.ToggleModernVault();
            ModernHud.EquipmentRequested += () => Inventory.ToggleModernEquipment();
            ModernHud.SkillsRequested += () => Inventory.ToggleModernSkills();
            ModernHud.MissionsRequested += () => Inventory.ToggleModernMissions();
            ModernHud.FactionsRequested += () => Inventory.ToggleModernFactions();
            // In a station the map button opens the station map (the sector map has nothing to show there).
            ModernHud.MapRequested += ToggleModernMap;
            ModernHud.LocalRequested += () => Inventory.ToggleModernLocal();
            ModernHud.MusicRequested += () => Inventory.ToggleModernMusic();
            ModernHud.ItemsRequested += () => Inventory.ToggleModernItems();
            Inventory.ModernLocalColor = Gutter.ContactColor;
            Inventory.ModernLocalTargetRequested = RequestTarget;
            Inventory.ModernLocalAction = LocalAction;
            Inventory.ModernLocalStatus = LocalStatus;
            Inventory.ModernLocalOpened = CorpseOpened;
            Inventory.WikiPlaces = WikiPlaces;
            Inventory.WikiRouteRequested = WikiRoute;
            Inventory.NamedSectors = NamedSectors;
            Map.ModernTextInputFactory = (length, command) => Inventory.CreateHostedTextInput(length, command);
            GalaxyMap.ModernTextInputFactory = Map.ModernTextInputFactory;
            Inventory.ModernBarStops = () => ModernHud?.BarStops;
            Inventory.ModernShortcutBounds = () => ModernHud?.SlotBounds(0);
            Inventory.ModernInventoryAnchor = () => ModernHud?.MicroScreenBounds;
            Chat.ModernHudClearance = () => ModernHud?.ChatClearance;
            ModernHud.MultiboxShown = () => Options.MultiboxButton;
            ModernHud.PerformanceSource = () => worldOwner.Performance;
            Options.ModernMultiboxButton = () => ModernHud?.MultiboxButton ?? default;
            hudOwner.ModernHud = ModernHud; hudOwner.ModernTargetSource = worldOwner;
        }
        ModernHud.Station = station;
        var local = station ? null : scene.Objects.FirstOrDefault(o => o.IsLocalShip);
        var stats = scene.Hud.Inventory?.CharacterStats;
        int Stat(string key) => int.TryParse(stats?.GetValueOrDefault(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        EnbModernUnit? self = null;
        if (local is not null && scene.Hud.Cockpit is { } cockpit)
        {
            string name = stats?.GetValueOrDefault("Name") is { Length: > 0 } owner ? owner : local.PlayerPresentation?.Owner ?? "";
            int level = Stat("CombatLevel") + Stat("ExploreLevel") + Stat("TradeLevel");
            self = new(name, stats is null ? null : level, new(1, .82f, .35f), cockpit.Vitals, cockpit.EnergyPercent, cockpit.MaximumEnergyPower,
                local.Avatar, Inventory.PortraitPoseFor(local.RenderIdentity), local.RenderIdentity);
        }
        // Next multibox "Shared harvesting": followers that can harvest the targeted asteroid lend its level, its contents
        // and each its own Harvest button (its name in the tooltip) to this client's target frame.
        var remotes = !station && Scanner.SelectedTarget is { ObjectType: 0x26 } asteroid && RemoteHarvest?.Invoke(asteroid.GameId) is { Count: > 0 } found ? found : [];
        EnbModernUnit? selected = null;
        if (!station && Scanner.SelectedTarget is { } target)
        {
            // A player's face, otherwise the scanner's live model.
            var avatar = target.ObjectType == 1 ? target.Avatar : null;
            int? level = target.PlayerPresentation?.CombatLevel is int combat && combat > 0 ? combat : target.HarvestableTechLevel is int tech && tech > 0 ? tech
                : remotes.FirstOrDefault(r => r.TechLevel > 0)?.TechLevel;
            selected = new(Scanner.TargetDisplayName ?? "", level, Gutter.SelectedTargetColor ?? Scanner.TargetNameColor, target.TargetVitals, null, null,
                avatar, avatar is null ? null : Inventory.PortraitPoseFor(target.RenderIdentity), target.RenderIdentity, Scanner.TargetAssessment);
        }
        var verbs = !station && Scanner.TargetVerbsVisible
            ? Scanner.Verbs.Select(v => new EnbModernVerb(v.Value, v.TargetId, v.Executable, v.Asset)).ToImmutableArray() : [];
        var harvest = station ? [] : Scanner.HarvestContents;
        if (harvest.IsDefaultOrEmpty && remotes.FirstOrDefault(r => !r.Contents.IsDefaultOrEmpty) is { } known) harvest = known.Contents;
        if (!verbs.Any(v => (v.Value & 0xFFFF0000u) == HarvestVerb) && Scanner.VerbAsset(HarvestVerb) is { } harvestAsset)
            foreach (var remote in remotes)
                if (remote.Verb != 0)
                    verbs = verbs.Add(new EnbModernVerb(remote.Verb, remote.Target, remote.Executable, harvestAsset, remote.Pilot));
        string? distance = station ? null : Scanner.TargetReadout(3), eta = station ? null : Scanner.TargetReadout(4);
        // "distance\neta": the frame shows the distance, its tooltip the ETA.
        string? readout = distance is null && eta is null ? null : distance + "\n" + eta;
        var experience = scene.Hud.Experience;
        var warp = scene.Hud.Cockpit?.Warp?.IsEngaged == true;
        var status = new EnbModernStatus(local is null ? null : Cockpit.SpeedText(local.Spatial?.Transform), warp,
            experience.Combat, experience.Explore, experience.Trade, Stat("CombatLevel"), Stat("ExploreLevel"), Stat("TradeLevel"), harvest);
        ModernHud.Update(self, selected, verbs, readout, scene.Environment, status, scene.Hud.Buffs, scene.Hud.TargetBuffs, scene.ClientTime, station ? null : Scanner.ModernCast);
        ModernHud.UpdateGroup(Inventory.ModernGroupMembers, scene.Objects, Inventory.ModernFormationReady);
        ModernHud.UpdateConnection();
        Scanner.ReplacementModelBounds = ModernHud.TargetModelBounds;
        Inventory.ModernPortraits = selected is { Avatar: not null } t && t.Identity != self?.Identity
            ? [self?.Identity ?? 0, t.Identity] : self is { } s ? [s.Identity] : [];
    }

    private async Task RequestTarget(uint id)
    {
        try { await targetRequest(id, CancellationToken.None); }
        catch (InvalidOperationException) { } // A sector transition can retire gameplay authority between frame and click.
    }

    private void ModernScannerHover(string? kind, uint verb, EnbUiPoint pointer)
    {
        if (kind == "verb") { Scanner.HoverVerb(verb, pointer); return; }
        Scanner.ClearVerbHover();
        if (kind is "prev" or "next") Scanner.ShowScannerTooltip(kind == "prev", pointer);
        else Scanner.HideScannerTooltip();
    }
}
