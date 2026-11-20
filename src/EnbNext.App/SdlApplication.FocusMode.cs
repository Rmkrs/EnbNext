using EnbNext.Host.Sdl;
using EnbNext.Presentation;
using SDL;

namespace EnbNext.App;

// Next multibox Focus mode. The client where it is turned on fills its monitor's work area
// above a band of thumbnails of every other local client. A click on a thumbnail only swaps
// that client with the main one; it is never a game click. Turning it off returns every
// client to its saved window placement. The layout travels with the multibox state; the
// highest revision wins.
internal sealed partial class SdlApplication
{
    private MultiboxFocusLayout? focusLayout;
    private EnbFocusModePlacement.Area? focusApplied;
    private bool focusActive,focusThumbnail;
    private DisplayState focusRestore;
    private bool FocusModeOn=>focusLayout is {On:true}&&multibox is not null;
    internal const int FocusNameMinimum=12,FocusNameMaximum=40;
    /// <summary>The thumbnails' name size: the layout's while Focus mode runs (every client draws the same), otherwise this client's preference.</summary>
    private int FocusNameSize=>Math.Clamp(focusLayout is {On:true,NameSize:>0} layout?layout.NameSize:_preferences.FocusNameSize,FocusNameMinimum,FocusNameMaximum);
    private void SetFocusNameSize(int size)
    {
        size=Math.Clamp(size,FocusNameMinimum,FocusNameMaximum);
        _preferences=_preferences with {FocusNameSize=size};SavePreferences();
        if(multibox is {} bus&&focusLayout is {On:true} layout){focusLayout=layout with {Revision=DateTime.UtcNow.Ticks,NameSize=size};AnnounceFocus(bus);}
    }

    private void SetFocusMode(bool on)
    {
        if(multibox is not {} bus)return;
        var area=SdlDisplays.Current(window).WorkingArea;
        focusLayout=on?new(DateTime.UtcNow.Ticks,true,multiboxInstance,area.X,area.Y,area.Width,area.Height,FocusNameSize)
            :(focusLayout??new(0,false,multiboxInstance,0,0,0,0)) with {Revision=DateTime.UtcNow.Ticks,On=false};
        AnnounceFocus(bus);
    }
    private void SwapIntoFocus()
    {
        if(multibox is not {} bus||focusLayout is not {On:true} layout)return;
        focusLayout=layout with {Revision=DateTime.UtcNow.Ticks,Main=multiboxInstance};
        AnnounceFocus(bus);TickFocusMode();
    }
    private void AnnounceFocus(MultiboxPeer bus)
    {
        bus.PublishFocus(focusLayout);
        dispatch.Observe(bus.AnnounceAsync());
    }

    private void TickFocusMode()
    {
        if(hidden)return;
        EnbFocusModePlacement.Area? target=null;
        if(multibox is {} bus)
        {
            var peers=bus.Peers;
            foreach(var peer in peers)if(peer.Focus is {} other&&(focusLayout is null||other.Revision>focusLayout.Revision))focusLayout=other;
            if(focusLayout is {On:true} layout&&_selectedCharacter is {} self)
            {
                var monitor=new EnbFocusModePlacement.Area(layout.X,layout.Y,layout.Width,layout.Height);
                var others=peers.Select(p=>(p.Instance,p.Name)).Append((Instance:multiboxInstance,Name:self.FullName))
                    .Where(c=>c.Instance!=layout.Main).OrderBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ThenBy(c=>c.Instance).ToArray();
                bool mainPresent=layout.Main==multiboxInstance||peers.Any(p=>p.Instance==layout.Main);
                int index=Array.FindIndex(others,c=>c.Instance==multiboxInstance);
                target=layout.Main==multiboxInstance?EnbFocusModePlacement.Main(monitor,others.Length)
                    :mainPresent&&index>=0?EnbFocusModePlacement.Thumbnail(monitor,index,others.Length):null;
            }
        }
        if(target==focusApplied)return;
        focusApplied=target;
        if(target is not {} area){ExitFocusMode();return;}
        if(!focusActive)
        {
            focusRestore=CurrentWindowState==DisplayState.Minimized?DisplayState.Normal:CurrentWindowState;focusActive=true;placementDue=null;
            window.SetFullscreen(false);window.Restore();window.Bordered(false);
        }
        focusThumbnail=focusLayout!.Main!=multiboxInstance;
        // A thumbnail keeps the 640x480 logical UI by rendering it below scale 1.
        double density=window.PixelDensity;
        window.ThumbnailScale=focusThumbnail?Math.Min(1,Math.Min(area.Width*density/640,area.Height*density/480)):null;
        window.RefreshSize();window.Place(area.X,area.Y,area.Width,area.Height);
        window.RefreshSize();cursor?.SetScale(window.Scale);frontendScreen?.Resize();worldScreen?.Resize();
    }
    private void ExitFocusMode()
    {
        if(!focusActive)return;
        focusActive=false;focusThumbnail=false;focusApplied=null;
        window.ThumbnailScale=null;window.Bordered(true);
        RestoreDisplay(ActiveDisplayPreferences,focusRestore);
    }
    // Thumbnails take no game input: a press swaps, everything else from the mouse is dropped.
    // Gaining focus (the click that activates the window, or Alt+Tab) already swaps, so one
    // click is enough.
    private long focusClickGuard;
    private bool HandleFocusThumbnail(SDL_Event e)
    {
        // Should the activating click still arrive after the swap, it is not a game click either.
        if((SDL_EventType)e.type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP&&Environment.TickCount64<focusClickGuard)return true;
        if(!focusActive||!focusThumbnail)return false;
        switch((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:SwapIntoFocus();focusClickGuard=Environment.TickCount64+400;return false;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:SwapIntoFocus();window.Raise();return true;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP or SDL_EventType.SDL_EVENT_MOUSE_MOTION or SDL_EventType.SDL_EVENT_MOUSE_WHEEL:return true;
            default:return false;
        }
    }
}
