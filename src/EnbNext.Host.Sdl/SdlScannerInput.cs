namespace EnbNext.Host.Sdl;

using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

internal sealed class SdlScannerInput : IDisposable
{
    private readonly EnbScannerUi owner;
    private bool timer;
    internal SdlScannerInput(EnbScannerUi owner){this.owner=owner;owner.TooltipTimerChanged+=Timer;}
    private void Timer(bool active)=>timer=active;
    internal void Tick(){if(timer)owner.TickTooltip();}
    internal EnbUiRect VerbHit(int index)=>owner.UiLayout?.Apply(EnbUiGroup.Scanner,owner.Verbs[index].Bounds)??owner.Verbs[index].Bounds;
    private EnbUiPoint Local(EnbUiPoint point){var p=owner.PanelBounds;return new((point.X-p.X)*128/p.Width,(point.Y-p.Y)*138/p.Height);}
    internal bool Hit(EnbUiPoint point)
    {
        if(!owner.IsVisible||owner.Replaced)return false;
        if(owner.PanelBounds.Contains(point))return true;
        if(owner.VerbsVisible)for(int i=0;i<owner.Verbs.Count;i++)if(VerbHit(i).Contains(point))return true;
        return false;
    }
    internal void Clear(){owner.ReleaseVerb();owner.ClearVerbHover();owner.PublishVerbs();owner.HideScannerTooltip();}
    internal async Task<bool> Handle(SDL_Event e)
    {
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE){Clear();return false;}
        if(!owner.IsVisible||owner.Replaced)return false;
        if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)
        {
            // 0060E300: wheel over the scanner cycles targets (101 next, 102 previous; Alt: any target).
            if(!owner.PanelBounds.Contains(new EnbUiPoint(e.wheel.mouse_x,e.wheel.mouse_y)))return false;
            float y=e.wheel.y*(e.wheel.direction==SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED?-1:1);
            if(y==0)return true;
            owner.WheelArrow(y>0,((uint)SDL_GetModState()&SDL_KMOD_ALT)!=0);return true;
        }
        bool move=type==SDL_EventType.SDL_EVENT_MOUSE_MOTION,press=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN,release=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP;
        if(!move&&!press&&!release)return false;
        var point=move?new EnbUiPoint(e.motion.x,e.motion.y):new EnbUiPoint(e.button.x,e.button.y);
        int button=press||release?(e.button.button==SDL_BUTTON_LEFT?1:e.button.button==SDL_BUTTON_RIGHT?2:0):0;
        if(release)
        {
            // A held verb button runs on release over it (0066B910); any other release is not the scanner's.
            if(button!=1||owner.ReleaseVerb() is not uint released)return false;
            if(owner.VerbsVisible)for(int i=0;i<owner.Verbs.Count;i++)
                if(owner.Verbs[i].Value==released&&owner.Verbs[i].Executable&&VerbHit(i).Contains(point)){var held=owner.Verbs[i];await owner.ExecuteVerbSafelyAsync(held.TargetId,held.Value);break;}
            return true;
        }
        if(owner.VerbsVisible)
            for(int i=0;i<owner.Verbs.Count;i++)if(VerbHit(i).Contains(point))
            {
                var verb=owner.Verbs[i];if(move){owner.HoverVerb(verb.Value,point);owner.TrackPressedVerb(verb.Value);}
                if(button==1&&verb.Executable){owner.PressVerb(verb.Value);return true;}
                return false;
            }
        if(move){owner.TrackPressedVerb(null);owner.ClearVerbHover();owner.PublishVerbs();}
        var panel=owner.PanelBounds;if(!panel.Contains(point)){if(move)owner.HideScannerTooltip();return false;}
        var local=Local(point);int kind=0;
        if(new EnbUiRect(95,120,17,18).Contains(local))kind=1;
        else if(new EnbUiRect(112,120,16,18).Contains(local))kind=2;
        else if(new EnbUiRect(28.43,3.2,26.23,3.9).Contains(local))kind=3;
        else if(new EnbUiRect(61.96,3.2,57.54,3.9).Contains(local))kind=4;
        else for(int i=0;i<2;i++)if(owner._interruptHit[i].IsVisible&&owner._interruptHit[i].Bounds.Contains(local)){kind=5+i;break;}
        if(move){if(kind!=0)owner.ShowRetailTooltip(kind,point);else owner.HideScannerTooltip();return false;}
        if(button==1&&kind is 1 or 2){owner.PressArrow(kind==1,RightHeld());return true;}
        return owner.PressPanel(local,button);
    }
    private static unsafe bool RightHeld()=>(SDL_GetMouseState(null,null)&SDL_MouseButtonFlags.SDL_BUTTON_RMASK)!=0;
    public void Dispose(){owner.TooltipTimerChanged-=Timer;Clear();}
}
