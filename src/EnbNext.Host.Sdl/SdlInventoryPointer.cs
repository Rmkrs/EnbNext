namespace EnbNext.Host.Sdl;

using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

// Native event transport; all gadget state and hit geometry belong to the engine.
internal sealed class SdlInventoryPointer : IDisposable
{
    private readonly EnbInventoryUi owner;
    private readonly nint window;
    private readonly Action<bool>? captureTransport;
    private string? captured,hovered;
    private bool left,right,nativeCapture;
    private EnbUiPoint point;
    internal string? Captured=>captured;
    internal unsafe SdlInventoryPointer(EnbInventoryUi owner,SDL_Window* window,Action<bool>? captureTransport=null)
    {
        this.owner=owner;this.window=(nint)window;this.captureTransport=captureTransport;owner.CaptureRequested+=Capture;
    }
    private unsafe void Capture(string? id)
    {
        captured=id;
        if(captureTransport is not null){captureTransport(id is not null);return;}
        // Injected hidden-window fixtures have no OS focus. Real focus loss cancels
        // the retained engine interaction through the same event path below.
        bool next=id is not null && ((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)!=0;
        if(next!=nativeCapture){NativePins.CaptureMouse(next,"inventory capture");nativeCapture=next;}
    }
    internal string? Hit(EnbUiPoint p)
    {
        string? found=null;int highest=int.MinValue;
        foreach(var pair in owner.Hits)
            if(pair.Value.IsVisible&&owner.LayoutContains(pair.Value,p)&&pair.Value.ZIndex>=highest)
            {found=pair.Key;highest=pair.Value.ZIndex;}
        return found;
    }
    private EnbInventoryPointer Frame(EnbInventoryPointerChange change=EnbInventoryPointerChange.Other,byte initial=0,EnbUiPoint delta=default)
    {
        uint mod=(uint)SDL_GetModState();
        var modifiers=((mod&SDL_KMOD_CTRL)!=0?EnbKeyModifiers.Control:0)|((mod&SDL_KMOD_SHIFT)!=0?EnbKeyModifiers.Shift:0)|((mod&SDL_KMOD_ALT)!=0?EnbKeyModifiers.Alt:0);
        var id=captured??Hit(point);
        return new(id is null?point:owner.LayoutInput(id,point),left,right,change,modifiers,initial,delta);
    }
    internal void ObserveButtons(bool left,bool right){this.left=left;this.right=right;}
    internal void Exit(){if(hovered is {} old)owner.PointerExit(old);hovered=null;}
    internal async Task<bool> Handle(SDL_Event e)
    {
        var type=(SDL_EventType)e.type;
        if(type==SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST)
        {Cancel();return false;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)
        {
            point=new(e.motion.x,e.motion.y);string? id=captured??Hit(point);
            if(id!=hovered){if(hovered is {} old)owner.PointerExit(old);hovered=id;if(id is not null)owner.PointerEnter(id,owner.LayoutInput(id,point));}
            if(id is not null)owner.PointerMove(id,Frame());return id is not null;
        }
        if(type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
        {
            point=new(e.button.x,e.button.y);byte button=e.button.button==SDL_BUTTON_LEFT?(byte)1:e.button.button==SDL_BUTTON_RIGHT?(byte)2:(byte)0;
            if(button==0)return false;
            bool down=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN;
            if(button==1)left=down;else right=down;
            string? id=captured??Hit(point);if(id is null)return false;
            var change=button==1?(down?EnbInventoryPointerChange.LeftButtonPressed:EnbInventoryPointerChange.LeftButtonReleased):(down?EnbInventoryPointerChange.RightButtonPressed:EnbInventoryPointerChange.RightButtonReleased);
            if(down)owner.PointerPress(id,Frame(change));else await owner.PointerRelease(id,Frame(change,button));
            return true;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)
        {
            point=new(e.wheel.mouse_x,e.wheel.mouse_y);
            double direction=e.wheel.direction==SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED?-1:1;
            string? id=captured??Hit(point);return id is not null&&owner.PointerWheel(id,Frame(delta:new(e.wheel.x*direction,e.wheel.y*direction)));
        }
        return false;
    }
    internal void Cancel()
    {
        bool held=captured is not null;Capture(null);left=right=false;
        if(hovered is {} old)owner.PointerExit(old);hovered=null;
        if(held)owner.PointerCaptureLost();
    }
    public void Dispose(){Cancel();owner.CaptureRequested-=Capture;}
}
