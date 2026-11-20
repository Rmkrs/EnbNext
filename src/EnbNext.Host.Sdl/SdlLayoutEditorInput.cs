using System.Runtime.InteropServices;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;
namespace EnbNext.Host.Sdl;
internal sealed unsafe class SdlLayoutEditorInput(EnbWorldUi ui,SdlPointerCapture capture,Action suspend,SDL_Window* window)
{
    private bool textInput,focused=true,consumeRelease,editing;
    internal void Sync()
    {
        if(editing!=ui.Layout.Editing){editing=ui.Layout.Editing;if(editing){capture.Cancel();suspend();}}
        bool next=focused&&ui.Layout.Editing&&ui.Editor.Naming!=0;
        if(next==textInput)return;
        NativePins.Check(next?SDL_StartTextInput(window):SDL_StopTextInput(window),"layout profile name");textInput=next;
    }
    internal bool Handle(SDL_Event e)
    {
        var type=(SDL_EventType)e.type;var edit=ui.Editor;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED)
        {focused=false;edit.CancelDrag();capture.Release(SdlPointerOwner.Layout);Sync();return false;}
        if(type==SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED){focused=true;Sync();return false;}
        if(!focused)return false;
        if(consumeRelease&&type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP){consumeRelease=false;return true;}
        if(!ui.Layout.Editing)
        {
            return false;
        }
        switch(type)
        {
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                if(e.button.button==SDL_BUTTON_LEFT){edit.BypassSnap=((uint)SDL_GetModState()&SDL_KMOD_SHIFT)!=0;edit.Press(new(e.button.x,e.button.y));if(!ui.Layout.Editing)consumeRelease=true;if(edit.Dragging)capture.Acquire(SdlPointerOwner.Layout);}Sync();return true;
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:edit.Move(new(e.motion.x,e.motion.y));return true;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                if(e.button.button==SDL_BUTTON_LEFT){edit.Release(new(e.button.x,e.button.y));capture.Release(SdlPointerOwner.Layout);}return true;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:edit.Wheel(e.wheel.y*(e.wheel.direction==SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED?-1:1));return true;
            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                edit.Type(e.text.text==null?"":Marshal.PtrToStringUTF8((nint)e.text.text)??"");return true;
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
                edit.BypassSnap=((uint)e.key.mod&SDL_KMOD_SHIFT)!=0||e.key.scancode is SDL_Scancode.SDL_SCANCODE_LSHIFT or SDL_Scancode.SDL_SCANCODE_RSHIFT;
                if(e.key.scancode==SDL_Scancode.SDL_SCANCODE_ESCAPE){edit.Escape();capture.Release(SdlPointerOwner.Layout);}
                else if(edit.ConfirmDelete)ui.Options.LayoutDeleteInput(edit.Pointer,SdlOptionsKeyboard.Code(e.key),SdlOptionsKeyboard.Modifiers(e.key.mod),false);
                else if(edit.Naming!=0)
                {
                    if(e.key.scancode==SDL_Scancode.SDL_SCANCODE_BACKSPACE)edit.Backspace();
                    if(e.key.scancode is SDL_Scancode.SDL_SCANCODE_RETURN or SDL_Scancode.SDL_SCANCODE_KP_ENTER)edit.AcceptName();
                    if(e.key.scancode==SDL_Scancode.SDL_SCANCODE_A&&((uint)e.key.mod&SDL_KMOD_CTRL)!=0)edit.ClearName();
                }
                Sync();return true;
            case SDL_EventType.SDL_EVENT_KEY_UP:edit.BypassSnap=((uint)e.key.mod&SDL_KMOD_SHIFT)!=0;return true;
        }
        return false;
    }
}
