namespace EnbNext.Host.Sdl;

using System.Runtime.InteropServices;
using EnbNext.Engine;
using SDL;
using static SDL.SDL3;

// SDL transports events; the engine owns all frontend interaction state.
internal sealed unsafe class SdlFrontendInput : IDisposable
{
    private readonly EnbFrontendController owner;
    private readonly Func<string?> readClipboard;
    private readonly SDL_Window* window;
    private readonly EnbUiTextInput[] inputs;
    private EnbUiTextInput? resume;
    private bool editing, nativeCapture, disposed;
    internal bool Captured {get;private set;}
    internal EnbUiTextInput? Active
    {
        get
        {
            if(!owner.IsEnabled || disposed)return null;
            foreach(var input in inputs)if(input.IsVisible&&input.IsEnabled&&input.IsFocused)return input;
            return null;
        }
    }
    internal SdlFrontendInput(EnbFrontendController owner,SDL_Window* window,Func<string?>? readClipboard=null)
    {
        this.owner=owner;this.window=window;this.readClipboard=readClipboard??SDL_GetClipboardText;
        inputs=[owner.Owner.Username,owner.Owner.Password,owner.Owner.CharacterName,owner.Owner.ShipName,owner.Owner.SiteUsername,owner.Owner.SitePassword,owner.Owner.GroupName];
        foreach(var input in inputs)
        {
            input.FocusRequested=()=>Focus(input);
            input.TransportChanged+=Sync;input.GotFocus+=FocusChanged;input.LostFocus+=FocusChanged;
        }
        owner.EnableChanged+=EnableChanged;
    }
    private void EnableChanged(bool enabled){if(!enabled){resume=null;Cancel();}Sync();}
    private void FocusChanged(object? sender,EventArgs e)=>Sync();
    private bool Focus(EnbUiTextInput input)
    {
        if(disposed||!owner.IsEnabled||!input.IsVisible||!input.IsEnabled)return false;
        foreach(var other in inputs)if(!ReferenceEquals(input,other))other.SetFocused(false);
        input.SetFocused(true);Sync();return true;
    }
    internal void Sync()
    {
        bool next=Active is not null;if(next==editing)return;
        NativePins.Check(next?SDL_StartTextInput(window):SDL_StopTextInput(window),"frontend text input");editing=next;
    }
    private void Capture(bool value)
    {
        Captured=value;
        bool next=value&&((ulong)SDL_GetWindowFlags(window)&SDL_WINDOW_INPUT_FOCUS)!=0;
        if(next==nativeCapture)return;
        NativePins.CaptureMouse(next,"frontend capture");nativeCapture=next;
    }
    internal void Cancel(){owner.ReleasePointer();owner.PointerExit();Capture(false);foreach(var input in inputs)input.SetFocused(false);}
    internal bool Handle(SDL_Event e)
    {
        if(disposed)return false;
        var type=(SDL_EventType)e.type;
        if(type==SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST){resume=Active;Cancel();Sync();return false;}
        if(type==SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED){if(resume is {} input)Focus(input);resume=null;return false;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP && e.button.button==SDL_BUTTON_LEFT)
        {bool held=Captured;owner.ReleasePointer();Capture(false);return held;}
        if(!owner.IsEnabled)return false;
        switch(type)
        {
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                owner.PointerMove(new(e.motion.x,e.motion.y));return true;
            case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE:
                owner.PointerExit();return false;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN when e.button.button==SDL_BUTTON_LEFT:
                var result=owner.PointerPress(new(e.button.x,e.button.y));if(result.Capture)Capture(true);return result.Handled;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                return owner.Wheel(e.wheel.y*(e.wheel.direction==SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED?-1:1));
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
                var key=new EnbNext.Presentation.EnbUiInputEvent(SdlOptionsKeyboard.Code(e.key),SdlOptionsKeyboard.Modifiers(e.key.mod),0,0);
                if(owner.Key(key,e.key.repeat))return true;
                if((key.Code==0x56&&key.Modifiers==0x200||key.Code==0x2d&&key.Modifiers==0x100)&&Active is {} credential&&
                    (ReferenceEquals(credential,owner.Owner.Username)||ReferenceEquals(credential,owner.Owner.Password)||
                     ReferenceEquals(credential,owner.Owner.SiteUsername)||ReferenceEquals(credential,owner.Owner.SitePassword)||ReferenceEquals(credential,owner.Owner.GroupName)))
                {
                    if(!e.key.repeat)credential.InsertBulk(readClipboard()??"");
                    return true;
                }
                bool handled=false;Active?.Key(key,()=>handled=true);
                if(!handled&&key.Code==9&&(key.Modifiers&~0x100)==0)
                {
                    int current=Array.IndexOf(inputs,Active),step=(key.Modifiers&0x100)!=0?-1:1;
                    if(current<0&&step<0)current=0;
                    for(int n=1;n<=inputs.Length;n++)if(Focus(inputs[(current+step*n+inputs.Length*2)%inputs.Length]))return true;
                }
                Sync();return handled;
            case SDL_EventType.SDL_EVENT_TEXT_INPUT when Active is {} current && e.text.text!=null:
                current.TextInput(Marshal.PtrToStringUTF8((nint)e.text.text)??"");return true;
            default:return false;
        }
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;resume=null;Cancel();
        owner.EnableChanged-=EnableChanged;
        foreach(var input in inputs)
        {input.FocusRequested=null;input.TransportChanged-=Sync;input.GotFocus-=FocusChanged;input.LostFocus-=FocusChanged;}
        Sync();
    }
}
