using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>The actual world UI owners and their shared presentation/interaction wiring.</summary>
internal sealed partial class EnbWorldUi
{
    internal EnbGroupLaunchPanel? MultiboxPanel=>hudOwner.LaunchPanel;
    internal EnbDamageMeterPanel? DamagePanel=>hudOwner.DamagePanel;
    // Game UI has precedence over the Next-only panels: they draw underneath
    // it and take the pointer only where no game window shows.
    internal bool GameUiCovers(EnbUiPoint point) => Inventory.CoversScreen || Inventory.BlocksFlight(point) ||
        Options.BlocksFlight(point) || MapAt(point) is not null || Chat.ContainsGameUi(point);
    /// <summary>The map window under a point: the sector map, or (Next Modern UI) the galaxy map window, the one used last on top.</summary>
    internal EnbNavigationMapUi? MapAt(EnbUiPoint point)
    {
        bool sector = Map.BlocksFlight(point), galaxy = GalaxyMap.IsOpen && GalaxyMap.BlocksFlight(point);
        return sector && galaxy ? (GalaxyMap.Raised > Map.Raised ? GalaxyMap : Map) : sector ? Map : galaxy ? GalaxyMap : null;
    }
    /// <summary>The open map window used last (the keyboard's zoom keys act on it).</summary>
    internal EnbNavigationMapUi TopMap => GalaxyMap.IsOpen && (!Map.IsOpen || GalaxyMap.Raised > Map.Raised) ? GalaxyMap : Map;
    private readonly EnbWorldRenderer hudOwner;
    internal EnbUiLayoutEditor Editor {get;}
    internal EnbUiLayoutProfiles Layout {get;}=new(null);
    internal EnbGutterUi Gutter {get;}
    internal EnbScannerUi Scanner {get;}
    internal EnbCockpitUi Cockpit {get;}
    internal EnbNavigationMapUi Map {get;}
    /// <summary>Next Modern UI: the galaxy map's own window, open beside the sector map; it shares the sector map's route.</summary>
    internal EnbNavigationMapUi GalaxyMap {get;}
    internal EnbOptionsUi Options {get;}
    internal EnbInventoryUi Inventory {get;}
    internal EnbChatUi Chat {get;}
    internal EnbFixedTooltip FixedTooltip {get;}=new();
    internal EnbWorldUi(Func<uint?> session,Func<uint,CancellationToken,Task<bool>> target,
        Func<uint,uint,CancellationToken,Task<bool>> verb,
        Func<Uri,string,string,IEnbOptionsBrowser> browser,EnbWorldRenderer world,EnbWorldRenderer hud,Func<int,bool> bridgePanel)
    {
        hudOwner=hud;worldOwner=world;targetRequest=target;
        Gutter=new(target,session);Scanner=new(target,verb){FixedSessionIdAccessor=session};
        Cockpit=new(){Session=session};Map=new(target,session);
        GalaxyMap=new(target,session){Guidance=Map.Guidance,SharesGuidance=true,GalaxyWindow=true,GalaxyMapAccessor=()=>Map.GalaxyMapAccessor?.Invoke(),ToggleGalaxyMapRequested=()=>Map.ToggleGalaxyMapRequested?.Invoke()??Task.FromResult(false)};
        Options=new(session,()=>Map.ToggleMode(false)?Map.IsOpen:null,browser);
        Inventory=new(session);Chat=new(session){UsePerformanceOverlay=true};
        Inventory.ModernOptions=Options;Options.ModernOptionsChanged+=Inventory.RefreshModernOptions;
        Cockpit.ModernShortcutCard=Inventory.ShortcutCard;
        // Next Modern UI: an item activated from the action bar may become one of the player's buffs (EnbModernHud.BuffCards).
        Cockpit.EquipmentRequested+=request=>{if(ModernHud is {} modern&&Inventory.ShortcutItem(request.Assignment) is {} item)modern.ItemActivated(item.Name,item.Effects);};
        Cockpit.SkillRequested+=request=>ModernHud?.SkillActivated(request.AbilityId);
        Editor=new(this);Layout.Listed=Editor.Lists;
        world.UiLayout=hud.UiLayout=Layout;
        FixedTooltip.Layout=Layout;hud.FixedTooltip=FixedTooltip;
        world.LayoutBuffPreview=Cockpit.LayoutBuffPreview;
        Map.UiLayout=Layout;GalaxyMap.UiLayout=Layout;Options.UiLayout=Layout;Options.LayoutEditor=Editor;
        Cockpit.UiLayout=Layout;Scanner.UiLayout=Layout;Chat.UiLayout=Layout;Inventory.UiLayout=Layout;
        Chat.OptionsRequested = category =>
        {
            Options.OpenCategory(category switch { "Control" => "main/BTTN01", "Graphics" => "main/BTTN03", "Interface" => "main/BTTN02", "Sound" => "main/BTTN04", "Chat Font" => "main/BTTN06", "Credits" => "main/BTTN05",
                "Advanced Graphics" => "main/NEXT00", "Social" => "main/NEXT01", "Navigation" => "main/NEXT02", "Quality of Life" => "main/NEXT03", "Modern UI" => "main/NEXT04", "Labels" => "main/NEXT05", _ => "main/BTTN00" });
        };
        Chat.Changed += hud.SetChatPresentation;
        Chat.ArtChanged += hud.SetChatArt;
        Chat.PrivateMessageReceived += () => hud.QueueInteractionSound("private_message_beep");
        Chat.GuildSoundRequested += hud.QueueInteractionSound;
        Chat.InteractionSoundRequested += world.QueueInteractionSound;
        Map.TooltipChanged += hud.SetTooltipPresentation;
        Map.Changed += () => hud.SetMapPresentation(Map.Presentation);
        GalaxyMap.TooltipChanged += hud.SetTooltipPresentation;
        GalaxyMap.Changed += () => hud.SetGalaxyMapPresentation(GalaxyMap.Presentation);
        Map.ModernGalaxyRequested = () => { if (!GalaxyMap.IsOpen) GalaxyMap.ToggleModern(true); else GalaxyMap.RaiseModern(); };
        Map.WikiRequested = name => Inventory.OpenModernWiki(name);
        GalaxyMap.WikiRequested = name => Inventory.OpenModernWiki(name);
        Cockpit.TooltipChanged += hud.SetTooltipPresentation;
        Cockpit.SkillMenuChanged += hud.SetSkillMenuPresentation;
        Options.SplitChatRequested = () => Chat.HasSplitChat;
        Chat.InformationRequested = name => Options.OpenInformation(name);
        Chat.GuildInfoRequested = Inventory.OpenGuild;
        Chat.FriendsListRequested = Inventory.OpenFriends;
        Chat.GroupFinderRequested = Inventory.OpenGroupFinder;
        Inventory.FriendMessageRequested += Chat.OpenSecureRecipient;
        Inventory.StationPlayerChatRequested += Chat.OpenSecureRecipient;
        Inventory.StationPlayerQuery = world.StationPlayer;
        Inventory.LocalChatMessageRequested = (owner,type,text) => Chat.LocalChatMessageRequested?.Invoke(owner,type,text);
        Inventory.RadarLabelsInvalidated += Map.NotifyGroupLabelChange;
        Options.OtherBrowserOpen = () => Inventory.BrowserOpen;
        Inventory.OtherBrowserOpen = () => Options.ManualOpen;
        Inventory.BridgePanelRequested=bridgePanel;Map.BridgePanelRequested=bridgePanel;GalaxyMap.BridgePanelRequested=bridgePanel;
        Options.ManualOpened+=()=>{Inventory.CloseForPopup();Map.CloseForPopup();GalaxyMap.CloseForPopup();};
        Options.Opened += () =>
        {
            if (!Options.QuitOpen) return;
            // 00565F80 activates the modal, then 00695F40 closes the five PDA
            // owners. This is separate from each active interface's Escape map.
            bool hadPda = Inventory.InventoryOpen || Inventory.CharacterOpen || Map.IsOpen || GalaxyMap.IsOpen;
            Map.CloseForPopup(); GalaxyMap.CloseForPopup();
            Inventory.ClosePdaForModal();
            if (hadPda) bridgePanel(0);
        };
        Inventory.Changed += hud.SetInventoryPresentation;
        Chat.PanesResized += Inventory.PutCustomizationBehindChat;
        Inventory.TooltipChanged += hud.SetTooltipPresentation;
        Inventory.TargetPortraitChatRequested += Chat.OpenSecureRecipient;
        Inventory.InteractionSoundRequested = hud.QueueInteractionSound;
        Inventory.CharacterTextSoundRequested += hud.QueueFontSound;
        Inventory.AnimationSoundRequested = hud.QueueClientAnimationSound;
        Options.AnimationSoundRequested = hud.QueueClientAnimationSound;
        hud.StationLocalPortraitAccessor = world.StationLocalPortrait;
        hud.StationRemotePortraitAccessor = world.StationRemotePortrait;
        world.StationPortraitsAccessor = () => Inventory.Presentation;
        Inventory.StationGroupAvatarAcquisitionRequested = world.AcquireStationGroupAvatar;
        Cockpit.CombatSoundRequested += hud.QueueInteractionSound;
        Cockpit.ShortcutSoundRequested += hud.QueueInteractionSound;
        Cockpit.BuffSoundRequested += hud.QueueInteractionSound;
        Scanner.HarvestSoundRequested += hud.QueueFontSound;
        Scanner.InteractionSoundRequested += hud.QueueUtilitySound;
        Scanner.PlayerMessageRequested += name => { if (!Chat.Editing) Chat.OpenSecureRecipient(name); };
        Scanner.AssessmentSoundRequested += hud.QueueInteractionSound;
        Cockpit.WeaponRangeSoundChanged += hud.SetWeaponRangeSound;
        Options.InteractionSoundRequested += hud.QueueUtilitySound;
        Map.InteractionSoundRequested += hud.QueueUtilitySound;
        GalaxyMap.InteractionSoundRequested += hud.QueueUtilitySound;
        Options.InventoryRequested += () => {if(ModernUi){Inventory.ToggleModernInventory();return;}Inventory.ToggleInventory();hud.QueueUtilitySound(Inventory.InventoryOpen?"SR_scifielectronic40.wav":"SR_scifielectronic42.wav");};
        Options.CharacterRequested += () => {Inventory.ToggleCharacter();hud.QueueUtilitySound(Inventory.CharacterOpen?"SR_scifielectronic40.wav":"SR_scifielectronic42.wav");};
        Cockpit.ItemExamined += Inventory.ExamineAt;
        Options.Changed += draws => hud.SetOptionsPresentation(Cockpit.RoundButtons(draws));
        Options.TooltipChanged += hud.SetTooltipPresentation;
        Chat.PingTooltipChanged += hud.SetTooltipPresentation;
        Scanner.TooltipPresentationChanged += hud.SetTooltipPresentation;
        Scanner.VerbPresentationChanged += hud.SetVerbPresentation;
        Inventory.Changed += _ => Chat.SetHistoryPassThrough(
            Inventory.CustomizationOpen || Inventory.TerminalOwner is not null);
    }
}
