namespace EnbNext.Host.Sdl;

using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

// Window tunnel -> parent observers -> captured/topmost actual engine owner.
internal sealed class SdlWorldUiInput : IDisposable
{
    private readonly SdlWorldHost host;
    private readonly SdlWorldKeyboardTransport keys;
    private readonly SdlWorldFlightPointer flight;
    private readonly SdlWorldCursor cursor;
    private readonly nint window;
    private readonly SdlInventoryPointer inventory;
    private readonly SdlScannerInput scanner;
    private readonly SdlGutterInput gutter;
    internal SdlPointerCapture Capture {get;}
    internal Action? DiagnosticCaptureLost {get;set;}
    private string? chatCapture,cockpitCapture;
    private int? menuCapture;
    private bool optionsKnown,inPointer,focused=true,left,right,hasPoint,disposed;
    private bool preview,inventoryHidden,cockpitHidden,ignoreMap;
    // Next Modern UI: two map windows (sector, galaxy); the one holding the pointer capture.
    private string? mapCapture;
    private EnbNavigationMapUi MapOf(string? id)=>id=="galaxy"?host.Owner.Ui.GalaxyMap:host.Owner.Ui.Map;
    private EnbUiPoint point;
    private Target hovered;
    private readonly record struct Target(SdlPointerOwner Kind,string? Id=null,int Row=-1);
    internal unsafe SdlWorldUiInput(SdlWorldHost host,SdlWorldKeyboardTransport keys,SdlWorldFlightPointer flight,SdlWorldCursor cursor,SDL_Window* window)
    {
        this.host=host;this.keys=keys;this.flight=flight;this.cursor=cursor;this.window=(nint)window;
        Capture=new(window,CancelOwner);
        inventory=new(host.Owner.Inventory,window,value=>SetCapture(SdlPointerOwner.Inventory,value));
        scanner=new(host.Owner.Ui.Scanner);gutter=new(host.Owner.Ui.Gutter);
        flight.CaptureTransport=FlightCapture;keys.Station.CaptureTransport=StationCapture;
        var ui=host.Owner.Ui;
        ui.Chat.CaptureRequested+=ChatCapture;ui.Cockpit.CaptureRequested+=CockpitCapture;ui.Cockpit.MenuCaptureRequested+=MenuCapture;
        ui.Map.CaptureRequested+=MapCapture;ui.GalaxyMap.CaptureRequested+=GalaxyCapture;ui.Options.CaptureRequested+=OptionsCapture;
        foreach(var map in new[]{ui.Map,ui.GalaxyMap})
        {map.CursorRequested=MapCursor;map.ClearCursorRequested+=ClearMapCursor;map.ClearCursorAssetsRequested+=ClearMapAssets;map.MapPointerWarpRequested=Warp;}
        ui.Inventory.PreviewCursorRequested+=Preview;ui.Inventory.HideCursorRequested+=HideInventory;ui.Inventory.RestoreCursorRequested+=RestoreInventory;
        ui.Inventory.AssetsReleased+=ReleaseInventoryAssets;ui.Inventory.EquipmentPointerWarpRequested=at=>Warp(ui.Inventory.LayoutOutput("shipPreview",at));
        ui.Cockpit.DragCursorChanged+=CockpitDragCursor;cursor.RestoreOverlay=RestoreOverlay;
    }
    private void SetCapture(SdlPointerOwner owner,bool value){if(value)Capture.Acquire(owner);else Capture.Release(owner);}
    private void FlightCapture(bool value)=>SetCapture(SdlPointerOwner.Flight,value);
    private void StationCapture(bool value)=>SetCapture(SdlPointerOwner.Station,value);
    private void ChatCapture(string? id){chatCapture=id;SetCapture(SdlPointerOwner.Chat,id is not null);}
    private void CockpitCapture(string? id){cockpitCapture=id;SetCapture(SdlPointerOwner.Cockpit,id is not null);}
    private void MenuCapture(int? row){menuCapture=row;SetCapture(SdlPointerOwner.SkillMenu,row is not null);}
    private void MapCapture(bool value){if(value)mapCapture="sector";SetCapture(SdlPointerOwner.Map,value);}
    private void GalaxyCapture(bool value){if(value)mapCapture="galaxy";SetCapture(SdlPointerOwner.Map,value);}
    private void OptionsCapture(bool value,bool forget)
    {
        if(value){if(inPointer||optionsKnown){optionsKnown=true;Capture.Acquire(SdlPointerOwner.Options);}}
        else {Capture.Release(SdlPointerOwner.Options);if(forget)optionsKnown=false;}
    }
    private void CancelOwner(SdlPointerOwner owner)
    {
        var ui=host.Owner.Ui;
        switch(owner)
        {
            case SdlPointerOwner.GroupPanel:host.Owner.Hud.LaunchPanel?.EndDrag();break;
            case SdlPointerOwner.DamagePanel:host.Owner.Hud.DamagePanel?.EndDrag();break;
            case SdlPointerOwner.MultiboxMenu:host.Owner.Hud.MultiboxMenu?.Release();break;
            case SdlPointerOwner.Layout:ui.Editor.CancelDrag();break;
            case SdlPointerOwner.Diagnostics:DiagnosticCaptureLost?.Invoke();break;
            case SdlPointerOwner.Flight:flight.Release();keys.UpdateFlight();break;
            case SdlPointerOwner.Station:keys.Station.Release();break;
            case SdlPointerOwner.Inventory:inventory.Cancel();break;
            case SdlPointerOwner.Chat:var id=chatCapture;chatCapture=null;if(id is not null)ui.Chat.CaptureLost(id);break;
            case SdlPointerOwner.Cockpit:cockpitCapture=null;ui.Cockpit.ReleasePointer();break;
            case SdlPointerOwner.SkillMenu:menuCapture=null;ui.Cockpit.CancelMenuCapture();break;
            case SdlPointerOwner.Map:MapOf(mapCapture).CancelPointer();break;
            case SdlPointerOwner.Options:optionsKnown=false;ui.Options.CaptureLost();break;
        }
    }
    private unsafe void Warp(EnbUiPoint at)
    {at=SdlWindowCoordinates.ToWindow((SDL_Window*)window,at,host.Scale);if(((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)!=0)SDL_WarpMouseInWindow((SDL_Window*)window,(float)at.X,(float)at.Y);}
    private bool MapPointer=>Capture.Owner==SdlPointerOwner.Map||Capture.Owner==SdlPointerOwner.None&&hovered.Kind==SdlPointerOwner.Map;
    private bool MapCursor(string resource)=>cursor.Map(resource,MapPointer);
    private void ClearMapCursor(){ignoreMap=true;try{cursor.Restore();}finally{ignoreMap=false;}}
    private void ClearMapAssets(){ignoreMap=true;try{cursor.ReleaseMapCursors();cursor.Restore();}finally{ignoreMap=false;}}
    private void Preview(EnbUiRaster raster,EnbUiPoint hotspot){preview=true;inventoryHidden=false;cursor.Preview(raster,hotspot);}
    private void HideInventory(){inventoryHidden=true;preview=false;cursor.Hide();}
    private void RestoreInventory(){inventoryHidden=preview=false;cursor.Restore();}
    private void ReleaseInventoryAssets(){inventoryHidden=preview=false;cursor.ReleasePreview();cursor.Restore();}
    private void CockpitDragCursor(bool hidden){cockpitHidden=hidden;if(hidden)cursor.Hide();else cursor.Restore();}
    private bool RestoreOverlay()
    {
        if(inventoryHidden||cockpitHidden){cursor.Hide();return true;}
        if(preview&&cursor.ShowPreview())return true;
        if(!ignoreMap&&MapPointer&&MapOf(Capture.Owner==SdlPointerOwner.Map?mapCapture:hovered.Id).CursorResource is {} resource)return cursor.Map(resource,true);
        if(keys.Station.OwnsCursor){keys.Station.RefreshCursor();return true;}
        return false;
    }
    /// <summary>The retail chat's hits (game UI), or the Modern UI chat panel's: a Next panel under every game window.</summary>
    private string? ChatHit(bool modern=false)
    {
        string? found=null;int z=int.MinValue;
        foreach(var pair in host.Owner.Chat.Hits)
            if(pair.Value.IsVisible&&(pair.Value.Group==EnbUiGroup.ModernChat)==modern&&pair.Value.IsHitTestVisible&&pair.Value.Bounds.Contains(host.Owner.Ui.Layout.Inverse(pair.Value.Group,point))&&pair.Value.ZIndex>=z)
            {found=pair.Key;z=pair.Value.ZIndex;}
        return found;
    }
    /// <summary>A chat hit's layout group: the retail chat's, or the Modern UI chat windows' (which the layout does not move).</summary>
    private EnbUiGroup ChatGroup(string id)=>host.Owner.Chat.Hits.TryGetValue(id,out var hit)?hit.Group:EnbUiGroup.Chat;
    private Target Hit(bool honorCapture=true)
    {
        if(honorCapture&&Capture.Owner!=SdlPointerOwner.None)return Capture.Owner switch
        {
            SdlPointerOwner.Inventory=>new(Capture.Owner,inventory.Captured),SdlPointerOwner.Chat=>new(Capture.Owner,chatCapture),
            SdlPointerOwner.Cockpit=>new(Capture.Owner,cockpitCapture),SdlPointerOwner.SkillMenu=>new(Capture.Owner,Row:menuCapture??-1),
            SdlPointerOwner.Map=>new(Capture.Owner,mapCapture),_=>new(Capture.Owner)
        };
        if(host.Owner.LoadingVisible||host.Owner.InterfaceHidden)return default;
        var ui=host.Owner.Ui;
        string? inv=ui.Inventory.IsVisible?inventory.Hit(point):null,chat=ChatHit();
        // The Modern UI Local window is a Next panel under the maps: where a map covers it, the map takes the pointer.
        if(inv?.StartsWith("mlocal/",StringComparison.Ordinal)==true&&ui.MapAt(point) is {} over)return new(SdlPointerOwner.Map,over==ui.GalaxyMap?"galaxy":"sector");
        bool inventoryAbove=ui.Inventory.GroupFinderOpen||ui.Inventory.StationPlayerMenuOpen;
        if(inv is not null&&chat is not null&&!ui.Inventory.CustomizationOpen &&
            (!ui.Layout.Placement(ui.Inventory.Hits[inv].LayoutGroup).IsIdentity||!ui.Layout.Placement(EnbUiGroup.Chat).IsIdentity))return new(SdlPointerOwner.Inventory,inv);
        if(inventoryAbove&&inv is not null)return new(SdlPointerOwner.Inventory,inv);
        if(chat is not null)return new(SdlPointerOwner.Chat,chat);
        if(inv is not null)return new(SdlPointerOwner.Inventory,inv);
        if(ui.MapAt(point) is {} map)return new(SdlPointerOwner.Map,map==ui.GalaxyMap?"galaxy":"sector");
        if(ui.Cockpit.SkillMenuOpen)return new(SdlPointerOwner.SkillMenu,Row:ui.Cockpit.HitMenuRow(point));
        for(int i=EnbCockpitUi.HitNames.Length-1;i>=0;i--)if(ui.Cockpit.Contains(EnbCockpitUi.HitNames[i],point))return new(SdlPointerOwner.Cockpit,EnbCockpitUi.HitNames[i]);
        // Last: the Modern UI chat panel lies under every game window (inventory, map, options).
        if(ChatHit(modern:true) is {} modernChat)return new(SdlPointerOwner.Chat,modernChat);
        return default;
    }
    internal bool AutopilotHit(EnbUiPoint at)
    {
        var previous=point;point=at;
        try{return !host.IssueContains(at)&&!host.Owner.Options.BlocksFlight(at)&&Hit() is {Kind:SdlPointerOwner.Cockpit,Id:"AUTOPILOT"};}
        finally{point=previous;}
    }
    internal bool WorldDropAllowed(EnbUiPoint at)
    {
        var previous=point;point=at;
        try{return !host.Owner.LoadingVisible&&!host.IssueContains(at)&&!host.Owner.Options.BlocksFlight(at)&&Hit(false).Kind==SdlPointerOwner.None&&!scanner.Hit(at);}
        finally{point=previous;}
    }
    private void Hover(Target target)
    {
        if(target==hovered)return;
        var ui=host.Owner.Ui;
        switch(hovered.Kind)
        {
            case SdlPointerOwner.Inventory:inventory.Exit();break;
            case SdlPointerOwner.Chat:ui.Chat.ClearHover();break;
            case SdlPointerOwner.Cockpit:case SdlPointerOwner.SkillMenu:ui.Cockpit.ClearHover();break;
            case SdlPointerOwner.Map:MapOf(hovered.Id).ExitPointer();break;
        }
        hovered=target;
        switch(target.Kind)
        {
            case SdlPointerOwner.Chat:if(target.Id is {} chat)ui.Chat.Hover(chat,host.Owner.Ui.Layout.Inverse(ChatGroup(chat),point));break;
            case SdlPointerOwner.Cockpit:if(target.Id is {} cockpit)ui.Cockpit.Hover(cockpit,point);break;
            case SdlPointerOwner.SkillMenu:if(target.Row>=0)ui.Cockpit.HoverMenu(target.Row,point);break;
        }
        if(!flight.Captured&&!flight.CameraActive)cursor.Restore();
    }
    private unsafe void TrackExit()
    {
        float x,y;SDL_GetMouseState(&x,&y);
        host.Owner.Options.TrackPanelPointer(SdlWindowCoordinates.ToUi((SDL_Window*)window,new EnbUiPoint(x,y),host.Scale),0,SdlOptionsKeyboard.Modifiers(SDL_GetModState()),true,
            Capture.Owner is not (SdlPointerOwner.None or SdlPointerOwner.Options),Capture.Owner==SdlPointerOwner.Options);
    }
    internal void Sync()
    {if(disposed)return;keys.Sync();scanner.Tick();if(host.Owner.Ui.Layout.Editing){Hover(default);return;}if(focused&&hasPoint)Hover(Hit());}
    internal async Task<bool> HandleAsync(SDL_Event e)
    {
        if(disposed)return false;
        var type=(SDL_EventType)e.type;
        if(type==SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED)focused=true;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED)
        {focused=false;await keys.HandleAsync(e);Capture.Cancel();left=right=false;Hover(default);scanner.Clear();host.Owner.Ui.Gutter.ClearPointer();return false;}
        if(type==SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE)
        {TrackExit();hasPoint=false;Hover(default);scanner.Clear();host.Owner.Ui.Gutter.ClearPointer();host.Owner.NavigationMap.ExitPointer();host.Owner.GalaxyMap.ExitPointer();return false;}
        bool move=type==SDL_EventType.SDL_EVENT_MOUSE_MOTION,down=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN,up=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP,wheel=type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL;
        if(!move&&!down&&!up&&!wheel)return await keys.HandleAsync(e);
        if(!focused)return false;
        point=move?new(e.motion.x,e.motion.y):wheel?new(e.wheel.mouse_x,e.wheel.mouse_y):new(e.button.x,e.button.y);hasPoint=true;
        byte button=down||up?e.button.button:(byte)0;
        if(button==SDL_BUTTON_LEFT)left=down;if(button==SDL_BUTTON_RIGHT)right=down;
        inventory.ObserveButtons(left,right);
        var target=Hit();Hover(target);if(target.Kind!=SdlPointerOwner.None||host.Owner.InterfaceHidden||!scanner.Hit(point))scanner.Clear();inPointer=true;
        try
        {
            bool handled=await keys.HandleAsync(e);var ui=host.Owner.Ui;
            if(move)
            {if(host.Owner.InterfaceHidden||ui.Cockpit.BlocksFlight(point)||ui.MapAt(point) is not null)ui.Gutter.ClearPointer();else if(!host.Owner.AwaitingWorld)ui.Gutter.TrackPointer(point);}
            ushort code=button==SDL_BUTTON_LEFT?(ushort)1:button==SDL_BUTTON_RIGHT?(ushort)2:(ushort)0;
            ushort modifiers=SdlOptionsKeyboard.Modifiers(SDL_GetModState());
            bool external=Capture.Owner is not (SdlPointerOwner.None or SdlPointerOwner.Options);
            handled|=ui.Options.TrackPanelPointer(point,code,(ushort)(modifiers|(up?0x1000:0)),move,external,Capture.Owner==SdlPointerOwner.Options);
            double delta=wheel?e.wheel.y*(e.wheel.direction==SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED?-1:1):0;
            if(wheel)ui.Options.TrackPanelWheel(delta,modifiers,external);
            if(down&&!handled)ui.Chat.DismissMenuOutside(point);
            if(handled)return true;
            if(Capture.Owner!=SdlPointerOwner.None)target=Hit();
            int logicalButton=button==SDL_BUTTON_LEFT?1:button==SDL_BUTTON_RIGHT?2:0;
            switch(target.Kind)
            {
                case SdlPointerOwner.Inventory:await inventory.Handle(e);return true;
                case SdlPointerOwner.Chat:
                    if(target.Id is not {} chat)return false;
                    if(move)ui.Chat.Move(chat,host.Owner.Ui.Layout.Inverse(ChatGroup(chat),point));else if(down)ui.Chat.Press(chat,host.Owner.Ui.Layout.Inverse(ChatGroup(chat),point),left,right);else if(up)ui.Chat.Release(chat);else if(wheel)ui.Chat.Wheel(chat,delta);return true;
                case SdlPointerOwner.Cockpit:
                    if(target.Id is not {} cockpit)return false;
                    if(move)ui.Cockpit.MoveShortcutPointer(cockpit,point);else if(down)ui.Cockpit.PressPointer(cockpit,point,logicalButton);else if(up)ui.Cockpit.ReleasePointer(point,logicalButton);return true;
                case SdlPointerOwner.SkillMenu:
                    if(move){if(target.Row>=0)ui.Cockpit.HoverMenu(target.Row,point);ui.Cockpit.MoveMenuPointer(point);}
                    else if(down)ui.Cockpit.PressMenuPointer(target.Row,button==SDL_BUTTON_LEFT);
                    else if(up)ui.Cockpit.ReleaseMenuPointer(target.Row,point,button==SDL_BUTTON_LEFT);return true;
                case SdlPointerOwner.Map:
                    var map=MapOf(target.Id);
                    if(move)map.RouteMapPointerMove(point,0,left,right,((uint)SDL_GetModState()&SDL_KMOD_CTRL)!=0);
                    else if(down&&logicalButton!=0)
                    {if(Capture.Owner==SdlPointerOwner.Map)map.RouteMapPointerMove(point,logicalButton,left,right,((uint)SDL_GetModState()&SDL_KMOD_CTRL)!=0);else map.PressPointer(point,logicalButton==2);}
                    else if(up&&Capture.Owner==SdlPointerOwner.Map)map.ReleasePointer(point,logicalButton==2,left||right);
                    else if(wheel)map.WheelPointer(delta);return true;
            }
            if(host.Owner.LoadingVisible)return true;
            // A hidden interface (Next) takes no clicks: the scanner and gutter stay out of the way.
            if(host.Owner.InterfaceHidden){scanner.Clear();return false;}
            if(scanner.Hit(point)){await scanner.Handle(e);return true;}
            if(move)scanner.Clear();
            return host.Owner.GutterVisible&&await gutter.Handle(e);
        }
        finally{inPointer=false;}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;Capture.Cancel();Hover(default);cursor.RestoreOverlay=null;
        flight.CaptureTransport=null;keys.Station.CaptureTransport=null;
        var ui=host.Owner.Ui;
        ui.Chat.CaptureRequested-=ChatCapture;ui.Cockpit.CaptureRequested-=CockpitCapture;ui.Cockpit.MenuCaptureRequested-=MenuCapture;
        ui.Map.CaptureRequested-=MapCapture;ui.GalaxyMap.CaptureRequested-=GalaxyCapture;ui.Options.CaptureRequested-=OptionsCapture;
        foreach(var map in new[]{ui.Map,ui.GalaxyMap})
        {map.CursorRequested=null;map.ClearCursorRequested-=ClearMapCursor;map.ClearCursorAssetsRequested-=ClearMapAssets;map.MapPointerWarpRequested=null;}
        ui.Inventory.PreviewCursorRequested-=Preview;ui.Inventory.HideCursorRequested-=HideInventory;ui.Inventory.RestoreCursorRequested-=RestoreInventory;
        ui.Inventory.AssetsReleased-=ReleaseInventoryAssets;ui.Inventory.EquipmentPointerWarpRequested=null;ui.Cockpit.DragCursorChanged-=CockpitDragCursor;
        inventory.Dispose();scanner.Dispose();Capture.Dispose();cursor.ReleasePreview();cursor.ReleaseMapCursors();cursor.Restore();
    }
}
