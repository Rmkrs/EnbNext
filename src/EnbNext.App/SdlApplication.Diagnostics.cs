using EnbNext.Engine;
using EnbNext.Host.Sdl;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private EnbDiagnosticUi diagnostics=null!;
    private SdlPointerCapture diagnosticCapture=null!;
    private int diagnosticForeground=360;
    private int? diagnosticBackground;
    private void InitializeDiagnostics()
    {
        diagnostics=new(()=>_runtimeIssues.Describe(),window.Clipboard,()=> (diagnosticForeground,diagnosticBackground??_preferences.BackgroundFpsLimit),
            (foreground,background)=>{diagnosticForeground=foreground;diagnosticBackground=background;});
        unsafe{diagnosticCapture=new(window.Window,_=>diagnostics.Cancel());}
        diagnostics.CaptureRequested+=value=>
        {
            var capture=worldScreen?.Capture??diagnosticCapture;
            if(value){frontendScreen?.CancelPointer();capture.Acquire(SdlPointerOwner.Diagnostics);}
            else capture.Release(SdlPointerOwner.Diagnostics);
        };
        diagnostics.GameplaySuspended+=()=>worldScreen?.SuspendGameplay();
    }
    private bool HandleDiagnostics(SDL_Event e)
    {
        try
        {
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED){diagnostics.Cancel();return false;}
        if(type==SDL_EventType.SDL_EVENT_KEY_DOWN&&e.key.scancode==SDL_Scancode.SDL_SCANCODE_F12&&!e.key.repeat&&
            (e.key.mod&SDL_Keymod.SDL_KMOD_CTRL)!=0&&(e.key.mod&SDL_Keymod.SDL_KMOD_SHIFT)!=0)
        {bool opened=diagnostics.OpenDeveloper();if(opened)window.Title(WindowTitle);return opened;}
        if(type is SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP or SDL_EventType.SDL_EVENT_TEXT_INPUT)
            return diagnostics.DeveloperOpen;
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)return diagnostics.Move(new(e.motion.x,e.motion.y));
        if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)return diagnostics.Wheel(new(e.wheel.mouse_x,e.wheel.mouse_y),e.wheel.y);
        if(type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
        {
            var point=new EnbUiPoint(e.button.x,e.button.y);
            if(e.button.button==SDL_BUTTON_LEFT)return type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN?diagnostics.Press(point):diagnostics.Release(point);
            return diagnostics.Blocks(point);
        }
        return false;
        }
        finally{if(worldScreen is {} world)world.Host.IssuePanelOpen=diagnostics.Captured||diagnostics.IssuesOpen||diagnostics.DeveloperOpen;}
    }
}
