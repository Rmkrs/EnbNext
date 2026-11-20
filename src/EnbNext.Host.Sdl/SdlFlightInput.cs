namespace EnbNext.Host.Sdl;

using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using SDL;
using static SDL.SDL3;

// Public SDL event translation only. The same owner also drives LiveWorldWindow.
internal sealed class SdlFlightInput
{
    private readonly EnbRetailKeyBindings bindings;
    private readonly Dictionary<string,SDL_Keycode> keys=new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<bool> capture;
    internal EnbFlightInputState<SDL_Scancode> State { get; }
    private bool pointerHeld;
    private float x,y;
    internal bool PointerHeld=>pointerHeld;
    /// <summary>Modern UI is on: its own controls resolve and the action bar slots have their own keys.</summary>
    internal Func<bool>? Modern {get;set;}
    internal SdlFlightInput(EnbRetailKeyBindings bindings,Action<bool> capture,EnbFlightInputState<SDL_Scancode>? state=null)
    {
        this.bindings=bindings;this.capture=capture;State=state??new();
        foreach(var binding in bindings.Bindings)
        {
            string name=binding.Key.ToUpperInvariant() switch
            {
                "ESC"=>"Escape","PGUP"=>"PageUp","PGDN"=>"PageDown","DEL"=>"Delete","INS"=>"Insert",
                "CAPS"=>"CapsLock","NUM LOCK"=>"Numlock","SCROLL LOCK"=>"ScrollLock","SEMICOLON"=>";",
                _=>binding.Key
            };
            if(name.Equals("M Mouse",StringComparison.OrdinalIgnoreCase))continue;
            keys[binding.Key]=SDL_GetKeyFromName(name);
        }
    }
    internal string? ActionFor(SDL_KeyboardEvent key)
    {
        EnbKeyModifiers modifiers=0;uint mod=(uint)key.mod;
        if((mod&SDL_KMOD_ALT)!=0)modifiers|=EnbKeyModifiers.Alt;
        if((mod&SDL_KMOD_CTRL)!=0)modifiers|=EnbKeyModifiers.Control;
        if((mod&SDL_KMOD_SHIFT)!=0)modifiers|=EnbKeyModifiers.Shift;
        return bindings.ResolveAction(name=>name.Equals("M Mouse",StringComparison.OrdinalIgnoreCase)?key.scancode==SDL_Scancode.SDL_SCANCODE_UNKNOWN&&(uint)key.key==0:keys.TryGetValue(name,out var code) && (uint)code!=0 && code==key.key,modifiers,Modern?.Invoke()==true);
    }
    internal void Suspend()
    {
        State.Suspend();ReleasePointer();
    }
    private void ReleasePointer()
    {
        if(!pointerHeld)return;
        pointerHeld=false;capture(false);
    }
    internal bool Handle(SDL_Event ev,bool canControl,bool canThrust)
    {
        var type=(SDL_EventType)ev.type;
        switch(type)
        {
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
            case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
            case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                Suspend();return true;
            case SDL_EventType.SDL_EVENT_KEY_UP:return State.KeyUp(ev.key.scancode);
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
                // SDL may synthesize key-up on focus loss; an OS repeat after
                // reactivation is not a new physical press.
                if(ev.key.repeat)return false;
                State.KeyDown(ev.key.scancode,ActionFor(ev.key),canControl,canThrust,out bool changed,out bool release);
                if(release)ReleasePointer();return changed;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN when ev.button.button==SDL_BUTTON_RIGHT && canControl:
                x=ev.button.x;y=ev.button.y;capture(true);pointerHeld=true;return true;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP when ev.button.button==SDL_BUTTON_RIGHT:
                ReleasePointer();return true;
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION when pointerHeld:
                x=ev.motion.x;y=ev.motion.y;return true;
            default:return false;
        }
    }
    internal Net7FlightInput Resolve(int windowWidth,int windowHeight,bool canControl,bool canThrust,bool mouseThrottle,bool speedLocked,long now)
    {
        // SDL pointer coordinates and SDL window size use the same units;
        // the ratio is independent of display scale and physical pixel density.
        EnbFlightPointer? pointer=pointerHeld && windowWidth>0 && windowHeight>0
            ?new(Math.Clamp(x/windowWidth,0,1),Math.Clamp(y/windowHeight,0,1),mouseThrottle,speedLocked):null;
        return State.Resolve(canControl,canThrust,now,false,pointer);
    }
}
