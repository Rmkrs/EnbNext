using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal interface IEnbWorldKeyEvent<TKey> where TKey:notnull
{
    TKey Key {get;}
    int ShortcutKey {get;}
    ushort Code {get;}
    ushort InventoryCode {get;}
    ushort Modifiers {get;}
    ushort QuitCode {get;}
    ushort QuitModifiers {get;}
    bool IsAlt {get;}
    string? Action {get;}
    EnbUiInputEvent ChatInput {get;}
    bool Handled {get;set;}
}

internal sealed class EnbWorldKeyboard<TKey>(EnbWorldController world,Func<uint?> session,
    Func<IEnbSceneFrame?> sceneAccessor,EnbWorldFlightCommands<TKey> flight,
    Action updateFlight,Action releasePointer,Action requestWarp,Func<Task> screenshot,Action fullscreen) where TKey:notnull
{
    internal readonly HashSet<TKey> Keys=[];
    internal readonly HashSet<TKey> CameraKeys=[];
    private readonly HashSet<TKey> altKeys=[];
    private bool altSuspended;
    internal Func<uint,ulong,Task<bool>>? FireAllRequested {get;set;}
    internal Func<uint,ulong,Task<bool>>? AutofollowRequested {get;set;}
    internal Func<uint,ulong,Task<bool>>? AutoLevelRequested {get;set;}
    private bool HandleFlightKeyDown<TEvent>(TEvent e,string? action) where TEvent:IEnbWorldKeyEvent<TKey>
    {
        if(action=="Stop" && world.CanControlFlight && world.Autopilot is {Active:true})
        {
            world.Autopilot?.Cancel("Manual engine stop");
            if(world.Cockpit.WarpEngaged)
            {if(Keys.Add(e.Key))requestWarp();e.Handled=true;return true;}
        }
        if(!flight.Input.KeyDown(e.Key,action,world.CanControlFlight,world.Cockpit.CanThrust,out bool changed,out bool release,world.Cockpit.CanHeldThrust))return false;
        e.Handled=true;if(release)releasePointer();if(changed)updateFlight();return true;
    }
    private bool HandleShortcutKeyDown<TEvent>(TEvent e,string? action) where TEvent:IEnbWorldKeyEvent<TKey>
    {
        //00610980 polls VK_MENU independently of configurable slot bindings.
        if(e.IsAlt)
        {if(altKeys.Add(e.Key)&&!altSuspended&&world.CanControlFlight&&!world.Ui.ModernUi)world.Cockpit.SetShortcutShift(true);e.Handled=true;return true;}
        int slot=EnbRetailKeyBindings.ShortcutSlot(action);if(slot<0)return false;
        e.Handled=true;
        if(world.CanControlFlight&&!altSuspended)world.Cockpit.PressShortcutKey(e.ShortcutKey,slot);
        else world.Cockpit.SuppressShortcutKey(e.ShortcutKey);
        return true;
    }
    internal bool ShortcutKeyUp(TKey key,int shortcutKey,bool isAlt)
    {
        bool handled=false;
        if(isAlt)
        {altKeys.Remove(key);if(altKeys.Count==0){altSuspended=false;world.Cockpit.SetShortcutShift(false);}handled=true;}
        if(!world.CanControlFlight)world.Cockpit.CancelShortcutKeys();
        return world.Cockpit.ReleaseShortcutKey(shortcutKey)||handled;
    }
    internal void CancelShortcuts()
    {altSuspended=altKeys.Count>0;world.Cockpit.CancelShortcutKeys();world.Cockpit.SetShortcutShift(false);}
    internal async Task KeyDownAsync<TEvent>(TEvent e) where TEvent:IEnbWorldKeyEvent<TKey>
    {
        if (world.Options.QuitOpen || world.Options.NativeOptionsOpen && !world.Options.NativeControlsOpen && e.Code == 0x1b)
        {
            e.Handled = true;
            if (!Keys.Add(e.Key)) return;
            world.Options.QueuePanelKey(e.QuitCode, e.QuitModifiers);
            return;
        }
        // Latch Escape before transient editors can dismiss themselves. A
        // held key must not then reach and dismiss the underlying screen.
        if (e.Code == 0x1b && !Keys.Add(e.Key)) { e.Handled = true; return; }
        if (await world.Inventory.GuildKey(e.InventoryCode)) { e.Handled = true; return; }
        if (await world.Inventory.FriendsKey(e.InventoryCode)) { e.Handled = true; return; }
        if (world.Inventory.FriendsEditing) return;
        if (world.Inventory.ModernSearchEditing || world.Inventory.ModernCommandSearchEditing || world.Inventory.ModernLocalSearchEditing || world.Inventory.ModernItemsSearchEditing || world.Inventory.ModernJobSearchEditing || world.Inventory.ModernVaultSearchEditing || world.Inventory.ModernLogEditing) return;
        if (world.NavigationMap.ModernFindEditing || world.GalaxyMap.ModernFindEditing) return;
        if (world.Inventory.GuildEditing) return;
        if (world.Inventory.GuildConfirmationOpen)
        { if (e.Code == 0x1b) world.Options.OpenQuit(); e.Handled = true; return; }
        if (world.Inventory.ConfirmedDialogOpen)
        {
            e.Handled = true;
            if (e.Code == 0x1b) world.Options.OpenQuit();
            else await world.Inventory.ConfirmedDialogKey(e.InventoryCode);
            return;
        }
        if (world.Inventory.GroupConfirmationOpen)
        {
            e.Handled = true;
            if(e.Code==0x1b)world.Options.OpenQuit();
            else await world.Inventory.GroupConfirmationKey(e.InventoryCode);
            return;
        }
        if (world.Inventory.CustomizationKey(e.InventoryCode)) { e.Handled = true; return; }
        if (world.Inventory.CustomizationNameEditing) return;
        if (await world.Inventory.ConfirmationKey(e.InventoryCode)) { e.Handled = true; return; }
        if (await world.Inventory.QuantityKey(e.InventoryCode)) { e.Handled = true; return; }
        if (await world.Inventory.TradeCreditKey(e.InventoryCode)) { e.Handled = true; return; }
        if (world.Options.InformationEditing) return;
        string? action = e.Action;
        bool history = action is "Prev Ephemeral History" or "Next Ephemeral History";
        if (!history && world.Chat.HandleKey(e.ChatInput, action)) { e.Handled = true; return; }
        // Leave text-producing keys unhandled so Win32 delivers WM_CHAR,
        // but do not fall through to gameplay bindings while chat is editing.
        if (world.Chat.Editing) return;
        // Next: the interface toggle; Escape first brings a hidden interface back.
        if (action == EnbNextControls.HideInterface)
        {
            e.Handled = true;
            if (Keys.Add(e.Key) && world.CanUseGeneralControls) world.ToggleInterface();
            return;
        }
        if (e.Code == 0x1b && world.InterfaceHidden) { world.ToggleInterface(); e.Handled = true; return; }
        if (e.Code == 0x1b && world.Options.ManualOpen)
        { world.Options.OpenQuit(); e.Handled = true; return; }
        if (e.Code == 0x1b && world.Options.Close()) { e.Handled = true; return; }
        // Skill selection activates through0062FFB0/0058A2C0 without an Escape binding.
        if (e.Code == 0x1b && world.Cockpit.SkillMenuOpen)
        { world.Options.OpenQuit(); e.Handled = true; return; }
        if (e.Code == 0x1b && world.Inventory.HandleEscape()) { e.Handled = true; return; }
        if (world.Options.IsOpen)
        {
            if (!world.Options.SuspendsGameplayMovement && HandleFlightKeyDown(e, action)) return;
            e.Handled = true; return;
        }
        if (action == "Help")
        {
            e.Handled = true;
            if (Keys.Add(e.Key) && world.CanUseGeneralControls) world.Options.OpenManual();
            return;
        }
        if (action == "Screenshot")
        {
            e.Handled = true;
            if (Keys.Add(e.Key)) await screenshot();
            return;
        }
        if (action == "Toggle Ping")
        {
            e.Handled = true;
            if (Keys.Add(e.Key) && world.CanUseGeneralControls) world.Chat.TogglePing();
            return;
        }
        // Next Modern UI: with a target, Escape first clears it (no window was left to close); the next Escape opens Quit.
        // Next Modern UI: Escape closes an open sector or galaxy map by itself (retail opens Quit, which closes the PDA).
        if (e.Code == 0x1b && world.Ui.ModernUi && (world.NavigationMap.IsOpen || world.GalaxyMap.IsOpen))
        { e.Handled = true; if (world.GalaxyMap.IsOpen) world.GalaxyMap.Close(); else world.NavigationMap.Close(); return; }
        if (e.Code == 0x1b && world.Ui.ModernUi && world.Ui.Scanner.SelectedTarget is not null && world.CanUseGeneralControls)
        { e.Handled = true; world.Ui.Chat.QuietTargetCanceled(); world.Ui.Scanner.ClearSelectedTarget(); return; }
        // 007C7B40 registers zoom keys only for the shared navigation/galaxy
        // frame. Opening Quit also closes the PDA owners through Options.Opened.
        if (e.Code == 0x1b)
        {
            e.Handled = true;
            if (world.CanUseGeneralControls) world.Options.OpenQuit();
            return;
        }
        if (history)
        {
            e.Handled = true;
            if (Keys.Add(e.Key)) world.Chat.HandleKey(e.ChatInput, action);
            return;
        }
        if (e.Modifiers == 0x400 && e.Code is 0x42 or 0x53 && world.Inventory.VendorOpen)
        {
            e.Handled = true;
            if (Keys.Add(e.Key)) await world.HandleVendorKey(e.Code == 0x42);
            return;
        }
        if (action is "Character" or "Missions" or "Cargo" or "Equipment" or "Vault" || action is not null && EnbNextControls.ModernWindows.Contains(action))
        {
            if (Keys.Add(e.Key)) world.HandleUtilityPanelAction(action);
            e.Handled = true; return;
        }
        if (action is "Restore Camera" or "Save Camera" or "First Person" or "Next Camera" or "Prev Camera")
        {
            if (CameraKeys.Add(e.Key)) world.HandleCameraAction(action, session());
            e.Handled = true; return;
        }
        if (action == "Warp") { if (Keys.Add(e.Key)) requestWarp(); e.Handled = true; return; }
        if (action == "Formation")
        {
            e.Handled = true;
            if (Keys.Add(e.Key) && world.CanUseCockpit && world.Inventory.GroupRequested is {} send &&
                session() is uint character && sceneAccessor() is {IsTransitioning:false} scene &&
                scene.Objects.FirstOrDefault(o=>o.IsLocalShip) is {} ship)
            {
                try
                {
                    if(await send(new(character,ship.RenderIdentity,EnbGroupAction.ToggleFormation)))
                        world.Inventory.FormationToggleCompleted();
                }
                catch(Exception ex) when(ex is InvalidOperationException or OperationCanceledException or System.IO.IOException)
                { /* Retired gameplay route; never replay the toggle. */ }
            }
            return;
        }
        if (action is "Fire All Weapons" or "Autofollow" or "Toggle Auto Level")
        {
            e.Handled = true;
            var request = action switch { "Autofollow" => AutofollowRequested,
                "Toggle Auto Level" => AutoLevelRequested, _ => FireAllRequested };
            bool ready = action == "Toggle Auto Level" ? world.CanUseGeneralControls : world.CanUseCockpit;
            if (Keys.Add(e.Key) && ready && request is { } fire &&
                sceneAccessor() is { IsTransitioning: false } scene &&
                scene.Objects.FirstOrDefault(o => o.IsLocalShip) is { } ship)
            {
                try { await fire(ship.GameId, ship.RenderIdentity); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.IO.IOException)
                { /* Retired route: do not replay or fabricate gameplay state. */ }
            }
            return;
        }
        if (action is "Nav Map" or "Galaxy Map" && world.CanUseGeneralControls)
        {
            if (Keys.Add(e.Key))
            {
                world.Cockpit.CloseSkillMenu();
                world.Cockpit.CancelPointerInput();
                // Next Modern UI: the sector and galaxy maps are separate windows (in a station the sector map key opens
                // the station map, as its button does).
                if (world.NavigationMap.Modern) world.Ui.ToggleModernMap(action=="Galaxy Map");
                else world.NavigationMap.ToggleMode(action=="Galaxy Map");
            }
            e.Handled = true; return;
        }
        if (world.TopMap.ZoomAction(action)) { e.Handled = true; return; }
        if (HandleShortcutKeyDown(e, action)) return;
        if (HandleFlightKeyDown(e, action)) return;
        if (e.Code == 0x7a && action is null)
        {
            fullscreen();
            world.Options.RefreshGraphicsDisplayChoice();
            e.Handled = true;
            return;
        }

        Task<bool>? targeting = action switch
        {
            "Target Self" => world.TargetSelfAsync(),
            "Target Group 1" => world.SelectGroupMemberAsync(0),
            "Target Group 2" => world.SelectGroupMemberAsync(1),
            "Target Group 3" => world.SelectGroupMemberAsync(2),
            "Target Group 4" => world.SelectGroupMemberAsync(3),
            "Target Group 5" => world.SelectGroupMemberAsync(4),
            "Target Near Enemy" => world.SelectNearestEnemyAsync(),
            "Target Near Player" => world.SelectNearestPlayerAsync(),
            "Target Near Attacker" => world.SelectNearestTargetAsync(EnbNext.Rendering.EnbRetailTargetCategory.Attacking),
            "Target Near Friendly" => world.SelectNearestTargetAsync(EnbNext.Rendering.EnbRetailTargetCategory.Friendly),
            "Target Near Navigation" => world.SelectNearestTargetAsync(EnbNext.Rendering.EnbRetailTargetCategory.Navigation),
            "Target Near Object" => world.SelectNearestTargetAsync(EnbNext.Rendering.EnbRetailTargetCategory.Object),
            "Next Context Target" => world.SelectAdjacentTargetAsync(previous: false, preserveCurrentCategory: true),
            "Prev Context Target" => world.SelectAdjacentTargetAsync(previous: true, preserveCurrentCategory: true),
            "Next Target" => world.SelectAdjacentTargetAsync(previous: false, preserveCurrentCategory: false),
            "Prev Target" => world.SelectAdjacentTargetAsync(previous: true, preserveCurrentCategory: false),
            _ => null,
        };
        if (targeting is not null)
        {
            e.Handled = true;
            try
            {
                await targeting;
            }
            catch (InvalidOperationException)
            {
                // Gameplay authority can retire during a sector handoff.
            }
        }
    }
}
