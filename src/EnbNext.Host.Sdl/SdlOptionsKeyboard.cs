namespace EnbNext.Host.Sdl;

using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Engine;
using SDL;
using static SDL.SDL3;

// SDL owns native event storage. Decode text during dispatch, before polling again.
internal sealed unsafe class SdlOptionsKeyboard(EnbOptionsGadgetCollection owner, SDL_Window* window) : IDisposable
{
    private bool editing;
    public void SetEditing(bool value)
    {
        if(editing==value)return;
        NativePins.Check(value ? SDL_StartTextInput(window) : SDL_StopTextInput(window), "editor text input");
        editing=value;
    }
    public void Dispose()=>SetEditing(false);
    public bool Handle(SDL_Event ev,bool panelActive,float x,float y)
    {
        if(!panelActive)return false;
        switch((SDL_EventType)ev.type)
        {
            case SDL_EventType.SDL_EVENT_TEXT_INPUT when editing:
                if(ev.text.text!=null)owner.QueueText(Marshal.PtrToStringUTF8((nint)ev.text.text),x,y);
                return true;
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
            case SDL_EventType.SDL_EVENT_KEY_UP:
                bool up=ev.type==(uint)SDL_EventType.SDL_EVENT_KEY_UP;
                ushort code=Code(ev.key),modifiers=Modifiers(ev.key.mod);
                if(!up && editing && owner.PrepareTextKey(code,modifiers))return false;
                if(code!=0)owner.Input.Push(new(code,(ushort)(modifiers|(up?0x1000:0)),x,y));
                return true;
            default:return false;
        }
    }
    internal static ushort Modifiers(SDL_Keymod mod)=>(ushort)(
        (((uint)mod&SDL_KMOD_SHIFT)!=0?0x100:0)|(((uint)mod&SDL_KMOD_CTRL)!=0?0x200:0)|(((uint)mod&SDL_KMOD_ALT)!=0?0x400:0));
    /// <summary>The character a key press types, or NUL. Retail's editor (0084AAD0) consumes a
    /// printable key on key-down, before any action lookup, so typing never fires a binding such as
    /// R (Reply). The typed text itself still arrives through SDL text input.</summary>
    internal static char Character(SDL_KeyboardEvent key)
    {
        uint value=(uint)key.key;
        if(value is >= 0x20 and < 0x7f or >= 0xa0 and < 0x40000000)return (char)value;
        if(((uint)key.mod&SDL_KMOD_NUM)==0)return '\0';
        return key.key switch
        {
            >=SDL_Keycode.SDLK_KP_1 and <=SDL_Keycode.SDLK_KP_9=>(char)('1'+(value-(uint)SDL_Keycode.SDLK_KP_1)),
            SDL_Keycode.SDLK_KP_0=>'0',SDL_Keycode.SDLK_KP_MULTIPLY=>'*',SDL_Keycode.SDLK_KP_PLUS=>'+',
            SDL_Keycode.SDLK_KP_MINUS=>'-',SDL_Keycode.SDLK_KP_PERIOD=>'.',SDL_Keycode.SDLK_KP_DIVIDE=>'/',_=>'\0'
        };
    }
    internal static ushort Code(SDL_KeyboardEvent key)
    {
        if(key.scancode==SDL_Scancode.SDL_SCANCODE_KP_ENTER)return 0x100;
        uint value=(uint)key.key;
        if(value is >= 'a' and <= 'z')return (ushort)(value-'a'+'A');
        if(value is >= 'A' and <= 'Z' or >= '0' and <= '9')return (ushort)value;
        // Key names are display labels and SDL's Windows backend localizes them.
        // Native key identities remain stable across display languages.
        return key.key switch
        {
            SDL_Keycode.SDLK_LSHIFT or SDL_Keycode.SDLK_RSHIFT=>0x10,
            SDL_Keycode.SDLK_LCTRL or SDL_Keycode.SDLK_RCTRL=>0x11,
            SDL_Keycode.SDLK_LALT or SDL_Keycode.SDLK_RALT=>0x12,
            SDL_Keycode.SDLK_LGUI=>0x5b,SDL_Keycode.SDLK_RGUI=>0x5c,SDL_Keycode.SDLK_APPLICATION=>0x5d,
            SDL_Keycode.SDLK_BACKSPACE=>8,SDL_Keycode.SDLK_TAB=>9,SDL_Keycode.SDLK_RETURN=>13,SDL_Keycode.SDLK_ESCAPE=>27,
            SDL_Keycode.SDLK_SPACE=>32,SDL_Keycode.SDLK_PAGEUP=>33,SDL_Keycode.SDLK_PAGEDOWN=>34,
            SDL_Keycode.SDLK_END=>35,SDL_Keycode.SDLK_HOME=>36,SDL_Keycode.SDLK_LEFT=>37,SDL_Keycode.SDLK_UP=>38,
            SDL_Keycode.SDLK_RIGHT=>39,SDL_Keycode.SDLK_DOWN=>40,SDL_Keycode.SDLK_PRINTSCREEN=>44,
            SDL_Keycode.SDLK_INSERT=>45,SDL_Keycode.SDLK_DELETE=>46,SDL_Keycode.SDLK_PAUSE=>0x13,SDL_Keycode.SDLK_CAPSLOCK=>0x14,
            SDL_Keycode.SDLK_NUMLOCKCLEAR=>0x90,SDL_Keycode.SDLK_SCROLLLOCK=>0x91,
            >=SDL_Keycode.SDLK_F1 and <=SDL_Keycode.SDLK_F12=>(ushort)(value-(uint)SDL_Keycode.SDLK_F1+0x70),
            >=SDL_Keycode.SDLK_F13 and <=SDL_Keycode.SDLK_F24=>(ushort)(value-(uint)SDL_Keycode.SDLK_F13+0x7c),
            >=SDL_Keycode.SDLK_KP_1 and <=SDL_Keycode.SDLK_KP_9=>(ushort)(value-(uint)SDL_Keycode.SDLK_KP_1+0x61),
            SDL_Keycode.SDLK_KP_0=>0x60,SDL_Keycode.SDLK_KP_MULTIPLY=>0x6a,SDL_Keycode.SDLK_KP_PLUS=>0x6b,
            SDL_Keycode.SDLK_KP_MINUS=>0x6d,SDL_Keycode.SDLK_KP_PERIOD=>0x6e,SDL_Keycode.SDLK_KP_DIVIDE=>0x6f,
            SDL_Keycode.SDLK_KP_ENTER=>0x100,
            _=>value<128 ? EnbRetailKeyIdentity.Code(value==';' ? "Semicolon" : ((char)value).ToString()) : (ushort)0
        };
    }
}
