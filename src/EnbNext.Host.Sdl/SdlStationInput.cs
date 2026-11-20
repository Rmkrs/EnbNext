namespace EnbNext.Host.Sdl;

using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

internal sealed unsafe class SdlStationInput : IDisposable
{
    private readonly SdlWorldHost host;
    private readonly nint window;
    private readonly Action<int> setCursor;
    private readonly Action restoreCursor;
    private readonly EnbWorldStationInput<SDL_Scancode> owner;
    private EnbUiPoint point;
    private EnbUiPoint? hover;
    private EnbKeyModifiers modifiers;
    private bool captured,nativeCapture,hoverCursor,disposed;
    // Next free movement (option): the mouse buttons held in the world, the hidden cursor's place to come back to.
    private bool freeLeft,freeRight,relative;
    private EnbUiPoint freePress;
    private long lastTick=long.MinValue;
    internal Action<bool>? CaptureTransport {get;set;}
    internal bool Captured=>captured;
    internal bool OwnsCursor=>captured||hoverCursor||host.Owner.StationShipHovered;
    internal SdlStationInput(SdlWorldHost host,EnbRetailAssetBundle assets,Func<uint?> session,SDL_Window* window,
        Action<int> setCursor,Action restoreCursor,Action<string> report)
    {
        this.host=host;this.window=(nint)window;this.setCursor=setCursor;this.restoreCursor=restoreCursor;
        owner=new(host.Owner,()=>assets,session,setCursor,report,ReleasePointer);
        host.Owner.Inventory.StationPlayerActionCompleted+=Release;
        host.Owner.Options.Opened+=OptionsOpened;
    }
    private void OptionsOpened(){if(host.Owner.Options.SuspendsGameplayMovement)Release();}
    internal void Modifiers(SDL_Keymod value)=>modifiers=((uint)value&SDL_KMOD_CTRL)!=0?EnbKeyModifiers.Control:EnbKeyModifiers.None;
    internal bool KeyDown(SDL_Scancode key,string? action)
    {if(!owner.KeyDown(key,action))return false;Update();return true;}
    internal bool KeyUp(SDL_Scancode key){bool used=owner.KeyUp(key);Update();return used;}
    private void Update(){owner.FreeButtons(freeLeft,freeRight);owner.Update(host.Size,captured?point:null,hover,modifiers);}
    internal void Tick(long now)
    {
        if(disposed || lastTick!=long.MinValue&&now-lastTick<16)return;lastTick=now;
        if(!host.Owner.CanControlStation){if(!host.Owner.StationRoomTransitioning)Release();}else Update();
        UpdateHover();
    }
    internal void RefreshCursor(){if(captured)Update();UpdateHover();}
    private void UpdateHover()
    {
        var world=host.Owner;world.UpdateStationShipHover(captured?null:hover);
        bool npc=!captured&&hover is {} at&&(world.StationNpcHovered(at)||world.StationPlayerHovered(at));
        bool terminal=!captured&&hover is {} terminalAt&&world.StationTerminalHovered(terminalAt);
        if(world.StationShipHovered||world.StationDoorHovered||npc||terminal)
        {setCursor(terminal?101:npc?100:99);hoverCursor=true;}
        else if(hoverCursor){hoverCursor=false;if(!captured)restoreCursor();}
    }
    internal bool Handle(SDL_Event e)
    {
        if(disposed)return false;
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED)
        {Release();return false;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION && relative)
        {
            Modifiers(SDL_GetModState());owner.FreeMouse(e.motion.xrel,e.motion.yrel);Update();return true;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN && host.Owner.StationFreeMovement &&
            e.button.button is (byte)SDL_BUTTON_LEFT or (byte)SDL_BUTTON_RIGHT)
        {
            // Next free movement: a left click on a person, terminal or the ship still uses it; otherwise the buttons
            // hide the cursor and the mouse turns the view (right: with the avatar; both: walk forward).
            var at=new EnbUiPoint(e.button.x,e.button.y);
            if(!relative && e.button.button==SDL_BUTTON_LEFT)
            {
                hover=at;UpdateHover();
                if(host.Owner.StationShipHovered){host.Owner.ActivateStationShip();return true;}
                if(host.Owner.ActivateStationNpc(at)||host.Owner.ActivateStationTerminal(at)||host.Owner.ActivateStationPlayer(at))return true;
            }
            if(!relative && !host.Owner.CanControlStationAt(at))return false;
            if(e.button.button==SDL_BUTTON_LEFT)freeLeft=true;else freeRight=true;
            if(!relative)
            {
                freePress=at;relative=true;
                if(((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)!=0)
                    NativePins.Check(SDL_SetWindowRelativeMouseMode((SDL_Window*)window,true),"station free look");
            }
            Update();return true;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP && relative && e.button.button is (byte)SDL_BUTTON_LEFT or (byte)SDL_BUTTON_RIGHT)
        {
            if(e.button.button==SDL_BUTTON_LEFT)freeLeft=false;else freeRight=false;
            if(!freeLeft && !freeRight)EndFree();
            Update();return true;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)
        {
            Modifiers(SDL_GetModState());hover=new(e.motion.x,e.motion.y);
            if(!captured){UpdateHover();return false;}
            point=hover.Value;Update();return true;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN)
        {
            var at=new EnbUiPoint(e.button.x,e.button.y);
            if(e.button.button==SDL_BUTTON_LEFT)
            {
                hover=at;UpdateHover();
                if(host.Owner.StationShipHovered){host.Owner.ActivateStationShip();return true;}
                return host.Owner.ActivateStationNpc(at)||host.Owner.ActivateStationTerminal(at)||host.Owner.ActivateStationPlayer(at);
            }
            if(e.button.button!=SDL_BUTTON_RIGHT||!host.Owner.CanControlStationAt(at))return false;
            captured=true;point=at;
            if(CaptureTransport is {} capture)capture(true);
            else if(((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)!=0)
            {NativePins.CaptureMouse(true,"station capture");nativeCapture=true;}
            Update();return true;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP&&e.button.button==SDL_BUTTON_RIGHT&&captured)
        {ReleasePointer();restoreCursor();Update();return true;}
        return false;
    }
    private void ReleasePointer()
    {
        captured=false;CaptureTransport?.Invoke(false);
        if(nativeCapture){NativePins.CaptureMouse(false,"station release");nativeCapture=false;}
    }
    /// <summary>Next free movement: the cursor comes back where the button went down.</summary>
    private void EndFree()
    {
        freeLeft=freeRight=false;
        if(!relative)return;
        relative=false;
        NativePins.Check(SDL_SetWindowRelativeMouseMode((SDL_Window*)window,false),"station free look end");
        if(((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)!=0)
        {
            var back=SdlWindowCoordinates.ToWindow((SDL_Window*)window,freePress,host.Scale);
            SDL_WarpMouseInWindow((SDL_Window*)window,(float)back.X,(float)back.Y);
        }
    }
    internal void Release(){bool held=captured;EndFree();owner.Release();if(held)restoreCursor();}
    public void Dispose()
    {
        if(disposed)return;disposed=true;host.Owner.Inventory.StationPlayerActionCompleted-=Release;
        host.Owner.Options.Opened-=OptionsOpened;Release();if(hoverCursor)restoreCursor();hoverCursor=false;
    }
}
