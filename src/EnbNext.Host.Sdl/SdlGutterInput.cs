namespace EnbNext.Host.Sdl;

using EnbNext.Engine;
using SDL;
using static SDL.SDL3;

internal sealed class SdlGutterInput(EnbGutterUi owner)
{
    internal async Task<bool> Handle(SDL_Event e)
    {
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE or SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST){owner.ClearPointer();return false;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION){owner.TrackPointer(new(e.motion.x,e.motion.y));return false;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN&&e.button.button==SDL_BUTTON_LEFT&&owner.TargetAt(new(e.button.x,e.button.y)) is uint id)
        {await owner.RequestTargetAsync(id);return true;}
        return false;
    }
}
