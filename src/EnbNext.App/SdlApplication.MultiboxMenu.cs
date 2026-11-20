using EnbNext.Client.Models;
using EnbNext.Host.Sdl;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private EnbMultiboxMenu? multiboxMenu;
    private SavedLoginGroup[] menuGroups=[];
    /// <summary>The group chosen from the multibox menu replaces the startup group for this
    /// process. Switching never launches or closes clients.</summary>
    private SavedLoginGroup? switchedGroup;
    private bool groupSwitched;
    private (EnbMultiboxOptions Options,Guid? Group,bool Focus,int NameSize,bool AllCharacters)? menuItemsKey;
    private SavedLoginGroup? CurrentGroup=>groupSwitched?switchedGroup:activeGroup;
    private void TickMultiboxMenu()
    {
        var options=worldScreen?.Owner.Options;
        bool enabled=multiboxOptions.Enabled&&multiboxCharacter is not null&&options is not null&&_client?.RetailAssets is not null;
        if(options is not null)options.MultiboxButton=enabled;
        if(!enabled){CloseMultiboxMenu(true);return;}
        multiboxMenu??=new(_client!.RetailAssets!){Activated=ActivateMultiboxMenu};
        var menu=multiboxMenu;var world=worldScreen!;
        menu.Layout=world.Owner.Ui.Layout;menu.Button=options!.MultiboxButtonBounds;menu.Modern=world.Owner.Ui.ModernUi;
        if(menu.Button.Width<=0)menu.SetOpen(false);
        world.Owner.Hud.MultiboxMenu=menu;
        // Rebuild the entries only when what they show changes.
        var key=(multiboxOptions,CurrentGroup?.Id,FocusModeOn,FocusNameSize,settingsScopes.Multibox);
        if(menuItemsKey!=key||menu.Items.IsDefaultOrEmpty){menuItemsKey=key;menu.Items=MultiboxMenuItems();menu.Invalidate();world.Owner.Hud.RequestPanelFrame();}
        options.SetMultiboxButtonState(menu.Open,menu.ButtonHovered);
    }
    private System.Collections.Immutable.ImmutableArray<EnbMultiboxMenuItem> MultiboxMenuItems()
    {
        var o=multiboxOptions;
        int group=CurrentGroup is {} current?Array.FindIndex(menuGroups,g=>g.Id==current.Id)+1:0;
        return
        [
            new("general","General",Heading:true),
            new("scope","Settings for: "+(settingsScopes.Multibox?"All characters":"Each character"),Value:settingsScopes.Multibox?1:0,Count:2),
            new("panel","Show My Characters panel",o.ShowCharacterPanel),
            new("group","Group: "+(CurrentGroup?.Name??"None"),Value:Math.Max(0,group),Count:menuGroups.Length+1),
            new("focus","Focus mode",FocusModeOn),
            new("focusname","Thumbnail name size: "+FocusNameSize,Value:FocusNameSize-FocusNameMinimum,Count:FocusNameMaximum-FocusNameMinimum+1),
            new("presence","Combine Discord presence",o.CombinePresence),
            new("invite","Invite all after group launch",o.InviteAfterLaunch),
            new("space","Space",Heading:true),
            new("target","Follow leader's target",o.FollowLeaderTarget),
            new("fire","Follow leader's Fire All",o.FollowLeaderFire),
            new("dock","Follow leader's dock and land",o.FollowLeaderDock),
            new("wormhole","Follow leader's wormholes",o.FollowLeaderWormhole),
            new("harvest","Shared harvesting",o.SharedHarvest),
            new("reactor","Reactor in group frame",o.SharedReactor),
            new("shortcuts","Alt shortcut buttons: "+o.ShortcutButtons,Value:o.ShortcutButtons,Count:13),
            new("formation","Formation after launch: "+EnbMultiboxOptions.FormationName(o.AutoFormation),Value:o.AutoFormation,Count:4),
            new("station","Station",Heading:true),
            new("launch","Follow leader's station launch",o.FollowLeaderLaunch),
            new("inside","Follow leader inside stations",o.FollowLeaderStation),
            new("spawn","Arrive at leader's spawn point",o.SharedStationSpawn),
            new("distance","Station follow distance: "+o.StationFollowDistance,Value:o.StationFollowDistance-1,Count:30),
        ];
    }
    private void ActivateMultiboxMenu(string key,int value)
    {
        var o=multiboxOptions;
        switch(key)
        {
            case "scope":if((value==1)!=ReadSettingsScopes().Multibox)SetSettingsScope("multibox",value==1);break;
            case "panel":SaveMultiboxOptions(o with {ShowCharacterPanel=!o.ShowCharacterPanel});ClaimMultiboxPanel();break;
            case "group":
                if((uint)value>(uint)menuGroups.Length)return;
                switchedGroup=value==0?null:menuGroups[value-1];groupSwitched=true;ClaimMultiboxPanel();break;
            case "target":SaveMultiboxOptions(o with {FollowLeaderTarget=!o.FollowLeaderTarget});break;
            case "fire":SaveMultiboxOptions(o with {FollowLeaderFire=!o.FollowLeaderFire});break;
            case "shortcuts":SaveMultiboxOptions(o with {ShortcutButtons=Math.Clamp(value,0,12)});break;
            case "dock":SaveMultiboxOptions(o with {FollowLeaderDock=!o.FollowLeaderDock});break;
            case "wormhole":SaveMultiboxOptions(o with {FollowLeaderWormhole=!o.FollowLeaderWormhole});break;
            case "harvest":SaveMultiboxOptions(o with {SharedHarvest=!o.SharedHarvest});break;
            case "reactor":SaveMultiboxOptions(o with {SharedReactor=!o.SharedReactor});break;
            case "formation":SaveMultiboxOptions(o with {AutoFormation=Math.Clamp(value,0,3)});break;
            case "launch":SaveMultiboxOptions(o with {FollowLeaderLaunch=!o.FollowLeaderLaunch});break;
            case "inside":SaveMultiboxOptions(o with {FollowLeaderStation=!o.FollowLeaderStation});break;
            case "spawn":SaveMultiboxOptions(o with {SharedStationSpawn=!o.SharedStationSpawn});break;
            case "distance":SaveMultiboxOptions(o with {StationFollowDistance=Math.Clamp(value+1,1,30)});break;
            case "focus":SetFocusMode(!FocusModeOn);break;
            case "focusname":SetFocusNameSize(FocusNameMinimum+value);break;
            case "presence":SaveMultiboxOptions(o with {CombinePresence=!o.CombinePresence});break;
            case "invite":SaveMultiboxOptions(o with {InviteAfterLaunch=!o.InviteAfterLaunch});break;
            default:return;
        }
        // A slider drag reports every step; only presses click.
        if(multiboxMenu?.Dragging!=true)worldScreen?.Owner.Hud.QueueUtilitySound("SR_scifielectronic40.wav");
        TickMultiboxMenu();
    }
    private void OpenMultiboxMenu()
    {
        if(multiboxMenu is not {} menu)return;
        try{_groups.Load();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){AppendDiagnostic("Saved groups: "+ex.Message);}
        menuGroups=_groups.Groups.Where(g=>SavedAccountStore.Same(g.Server,_savedAccountServer??"")).ToArray();
        // The saved groups were just reloaded: rebuild the entries that list them.
        menuItemsKey=null;menu.SetOpen(true);TickMultiboxMenu();
    }
    private void CloseMultiboxMenu(bool dispose)
    {
        if(multiboxMenu is not {} menu)return;
        menu.SetOpen(false);
        if(!dispose){TickMultiboxMenu();return;}
        if(worldScreen is {} world&&ReferenceEquals(world.Owner.Hud.MultiboxMenu,menu))world.Owner.Hud.MultiboxMenu=null;
        menu.Dispose();multiboxMenu=null;menuItemsKey=null;
    }
    private bool HandleMultiboxMenu(SDL_Event e)
    {
        if(multiboxMenu is not {} menu||worldScreen is not {} world||world.Owner.Ui.Layout.Editing)return false;
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED){menu.Release();world.Capture.Release(SdlPointerOwner.MultiboxMenu);if(menu.Open)CloseMultiboxMenu(false);return false;}
        if(type==SDL_EventType.SDL_EVENT_KEY_DOWN&&e.key.key==SDL_Keycode.SDLK_ESCAPE&&menu.Open){CloseMultiboxMenu(false);world.Owner.Hud.QueueUtilitySound("SR_scifielectronic42.wav");return true;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)
        {
            bool dragging=menu.Dragging;
            if(menu.Pointer(new(e.motion.x,e.motion.y))){TickMultiboxMenu();world.Owner.Hud.RequestPanelFrame();}
            return dragging||menu.Contains(new(e.motion.x,e.motion.y));
        }
        if(type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
        {
            var p=new EnbUiPoint(e.button.x,e.button.y);
            if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP&&menu.Dragging){menu.Release();world.Capture.Release(SdlPointerOwner.MultiboxMenu);TickMultiboxMenu();return true;}
            if(menu.ButtonContains(p))
            {
                if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN&&e.button.button==1)
                {
                    bool open=!menu.Open;if(open)OpenMultiboxMenu();else CloseMultiboxMenu(false);
                    world.Owner.Hud.QueueUtilitySound(open?"SR_scifielectronic40.wav":"SR_scifielectronic42.wav");
                }
                return true;
            }
            if(!menu.Open)return false;
            if(menu.Bounds.Contains(p))
            {
                if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN&&e.button.button==1){menu.Press(p);if(menu.Dragging)world.Capture.Acquire(SdlPointerOwner.MultiboxMenu);TickMultiboxMenu();}
                return true;
            }
            // A press elsewhere dismisses the menu and still reaches the game.
            if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN)CloseMultiboxMenu(false);
            return false;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)return menu.Open&&menu.Bounds.Contains(new(e.wheel.mouse_x,e.wheel.mouse_y));
        return false;
    }
}
