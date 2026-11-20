namespace EnbNext.Host.Sdl;

using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

internal sealed unsafe class SdlWorldFlightPointer : IDisposable
{
    private readonly SdlWorldHost host;
    private readonly EnbRetailAssetBundle assets;
    private readonly Func<uint?> session;
    private readonly nint window;
    private readonly SdlWorldCursor cursor;
    private readonly Func<bool> stationCursor;
    private readonly Action updateFlight;
    private readonly EnbCameraInputState camera=new();
    private EnbUiPoint point;
    private bool captured,nativeCapture,disposed;
    internal Action<bool>? CaptureTransport {get;set;}
    internal bool Captured=>captured;
    internal bool CameraActive=>camera.Active;
    internal int Applied {get;private set;}
    internal int Recenters {get;private set;}
    internal int Wheels {get;private set;}
    internal SdlWorldFlightPointer(SdlWorldHost host,EnbRetailAssetBundle assets,Func<uint?> session,SDL_Window* window,
        SdlWorldCursor cursor,Func<bool> stationCursor,Action updateFlight)
    {
        this.host=host;this.assets=assets;this.session=session;this.window=(nint)window;this.cursor=cursor;
        this.stationCursor=stationCursor;this.updateFlight=updateFlight;
        host.Owner.SampleCameraInput=Sample;host.FlightCaptureReleased+=Release;
    }
    private static EnbKeyModifiers Modifiers()
    {
        uint value=(uint)SDL_GetModState();return ((value&SDL_KMOD_CTRL)!=0?EnbKeyModifiers.Control:0)|
            ((value&SDL_KMOD_SHIFT)!=0?EnbKeyModifiers.Shift:0)|((value&SDL_KMOD_ALT)!=0?EnbKeyModifiers.Alt:0);
    }
    internal EnbFlightPointer? Resolve()
    {
        if(!captured||host.Size.Width<=0||host.Size.Height<=0)return null;
        float x=(float)Math.Clamp(point.X/host.Size.Width,0,1),y=(float)Math.Clamp(point.Y/host.Size.Height,0,1);
        bool enabled=assets.ResolvePlayerOptions(session()).Options.CpitThrottleRightMouse;
        bool thrust=EnbMouseFlightPolicy.ShouldThrust(x,y,enabled,host.Owner.IsFlightSpeedLocked);
        cursor.Select(Math.Clamp((int)(x*7),0,6)+(thrust?7:0));
        return new(x,y,enabled,host.Owner.IsFlightSpeedLocked);
    }
    internal bool Handle(SDL_Event e)
    {
        if(disposed)return false;
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_MOUSE_MOTION or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)camera.ResetWheel();
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN&&e.button.button==SDL_BUTTON_RIGHT)
        {
            var at=new EnbUiPoint(e.button.x,e.button.y);if(!host.Owner.CanStartMouseFlight(at))return false;
            captured=true;point=at;
            if(CaptureTransport is {} capture)capture(true);
            else if(((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)!=0)
            {NativePins.CaptureMouse(true,"flight capture");nativeCapture=true;}
            updateFlight();return true;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP&&e.button.button==SDL_BUTTON_RIGHT&&captured)
        {Release();updateFlight();return true;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)
        {
            var at=new EnbUiPoint(e.motion.x,e.motion.y);
            if(captured){point=at;updateFlight();return true;}
            var mods=Modifiers();return host.Owner.CanStartMouseFlight(at)&&!mods.HasFlag(EnbKeyModifiers.Alt)&&(mods&(EnbKeyModifiers.Control|EnbKeyModifiers.Shift))!=0;
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)
        {
            double delta=e.wheel.direction==SDL_MouseWheelDirection.SDL_MOUSEWHEEL_FLIPPED?-e.wheel.y:e.wheel.y;
            if(!camera.ConsumeWheel(delta,out bool forward))return false;
            var at=new EnbUiPoint(e.wheel.mouse_x,e.wheel.mouse_y);
            if(captured||!host.Owner.CanStartMouseFlight(at))return false;
            var normalized=Normalize(at);
            if(!camera.AdmitWheel((float)normalized.X,(float)normalized.Y,Modifiers())||!host.Owner.ZoomCameraWheel(forward))return false;
            Wheels++;return true;
        }
        return false;
    }
    private EnbUiPoint Normalize(EnbUiPoint at)=>new((float)Math.Clamp(at.X*host.Scale/Math.Max(1,host.Size.Width*host.Scale-1),0,1),
        (float)Math.Clamp(at.Y*host.Scale/Math.Max(1,host.Size.Height*host.Scale-1),0,1));
    private void Sample(double elapsed)
    {
        if(((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)==0)return;
        float x,y;SDL_GetMouseState(&x,&y);SampleAt(SdlWindowCoordinates.ToUi((SDL_Window*)window,new EnbUiPoint(x,y),host.Scale),Modifiers(),elapsed,true);
    }
    internal void SampleAt(EnbUiPoint at,EnbKeyModifiers modifiers,double elapsed,bool warp=false)
    {
        if(disposed||!host.Owner.CanControlFlight||captured)return;
        if(!camera.Active&&!host.Owner.CanStartMouseFlight(at))return;
        var normalized=Normalize(at);var input=camera.Sample((float)normalized.X,(float)normalized.Y,modifiers);
        if(input.Action==EnbCameraPointerAction.Unchanged)return;
        if(input.Action==EnbCameraPointerAction.End){EndCamera();return;}
        if(!host.Owner.ApplyCameraPointer(normalized,elapsed,input.Initial,input.Distance,input.Control)){EndCamera();return;}
        camera.Accept();Applied++;cursor.Hide();
        if(warp&&((ulong)SDL_GetWindowFlags((SDL_Window*)window)&SDL_WINDOW_INPUT_FOCUS)!=0)
            {var center=SdlWindowCoordinates.ToWindow((SDL_Window*)window,new(Math.Truncate(host.Size.Width*host.Scale*.5)/host.Scale,Math.Truncate(host.Size.Height*host.Scale*.5)/host.Scale),host.Scale);SDL_WarpMouseInWindow((SDL_Window*)window,(float)center.X,(float)center.Y);}
        Recenters++;
    }
    internal void EndCamera(){camera.End();host.Owner.EndManualCameraInput();if(cursor.Hidden)cursor.Restore();}
    internal void Release()
    {
        captured=false;CaptureTransport?.Invoke(false);if(nativeCapture){NativePins.CaptureMouse(false,"release flight capture");nativeCapture=false;}
        if(!stationCursor())cursor.Restore();
    }
    public void Dispose()
    {if(disposed)return;disposed=true;host.Owner.SampleCameraInput=null;host.FlightCaptureReleased-=Release;EndCamera();Release();}
}
