using System.Diagnostics;
using System.Runtime.InteropServices;
using EnbNext.Presentation;
using EnbNext.Rendering.OpenGL;
using SDL;
using static SDL.SDL3;

namespace EnbNext.Host.Sdl;

// Owns the public SDL window/context boundary. Scene and UI owners never own a window.
internal sealed unsafe class SdlGameWindow : IDisposable
{
    private SDL_Window* window;
    private SDL_GLContextState* context;
    private WindowsAngleDevice? device;
    private bool initialized,disposed;
    private int minimumWidth=640,minimumHeight=480;
    internal SDL_Window* Window=>window;
    internal nint NativeHandle=>OperatingSystem.IsWindows()?SDL_GetPointerProperty(SDL_GetWindowProperties(window),"SDL.window.win32.hwnd",0):0;
    internal EnbGlApi Gl {get;private set;}=null!;
    internal int PixelWidth {get;private set;}
    internal int PixelHeight {get;private set;}
    internal double Scale {get;private set;}=1;
    internal double SystemScale {get;private set;}=1;
    internal double? UiScaleOverride {get;set;}
    /// <summary>Focus mode thumbnails: a UI scale below 1 keeps the 640x480 logical UI inside a small window.</summary>
    internal double? ThumbnailScale {get;set;}

    internal EnbUiSize Size=>new(PixelWidth/Scale,PixelHeight/Scale);
    internal string ContextDescription {get;private set;}="";
    internal bool Focused
    {
        get
        {
            if(!OperatingSystem.IsWindows())return ((ulong)SDL_GetWindowFlags(window)&SDL_WINDOW_INPUT_FOCUS)!=0;
            GetWindowThreadProcessId(GetForegroundWindow(),out uint process);
            return process==(uint)Environment.ProcessId;
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    internal bool Minimized=>((ulong)SDL_GetWindowFlags(window)&SDL_WINDOW_MINIMIZED)!=0;
    internal bool Maximized=>((ulong)SDL_GetWindowFlags(window)&SDL_WINDOW_MAXIMIZED)!=0;
    internal bool Fullscreen=>((ulong)SDL_GetWindowFlags(window)&SDL_WINDOW_FULLSCREEN)!=0;
    private readonly bool placeable;
    /// <summary>True on Wayland: the window appears with its first presented frame.</summary>
    internal bool MapsOnPresent=>!placeable;
    internal SdlGameWindow(string title,int width,int height,bool hidden,long? adapter=null)
    {
        try
        {
            // Windows presents through EnbNext's own ANGLE device on SDL's window; Linux through SDL's EGL.
            NativePins.Load();var platform=OperatingSystem.IsWindows()?null:new LinuxEglPlatform();
            platform?.ConfigureBeforeInit();NativePins.Check(SDL_Init((SDL_InitFlags)SDL_INIT_VIDEO),"initialize video");initialized=true;
            // Wayland compositors own window placement; SDL refuses positions there.
            placeable=SDL_GetCurrentVideoDriver()!="wayland";
            if(platform is not null)
            {
            SDL_GL_ResetAttributes();
            Attribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK,SDL_GL_CONTEXT_PROFILE_ES);
            Attribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION,3);Attribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION,0);
            Attribute(SDL_GLAttr.SDL_GL_RED_SIZE,8);Attribute(SDL_GLAttr.SDL_GL_GREEN_SIZE,8);Attribute(SDL_GLAttr.SDL_GL_BLUE_SIZE,8);Attribute(SDL_GLAttr.SDL_GL_ALPHA_SIZE,8);
            Attribute(SDL_GLAttr.SDL_GL_DEPTH_SIZE,24);Attribute(SDL_GLAttr.SDL_GL_STENCIL_SIZE,8);Attribute(SDL_GLAttr.SDL_GL_DOUBLEBUFFER,1);
            platform.ConfigureContext();
            }
            window=SDL_CreateWindow(title,width,height,(SDL_WindowFlags)((platform is null?0UL:SDL_WINDOW_OPENGL)|SDL_WINDOW_RESIZABLE|SDL_WINDOW_HIGH_PIXEL_DENSITY|SDL_WINDOW_HIDDEN));
            if(window==null)throw new InvalidOperationException(SDL_GetError());
            NativePins.Check(SDL_SetWindowMinimumSize(window,640,480),"minimum window size");
            if(hidden&&placeable)
            {
                SDL_Rect primary;NativePins.Check(SDL_GetDisplayBounds(SDL_GetPrimaryDisplay(),&primary),"primary bounds");
                // Keep the hidden probe on the primary adapter/DPI domain. A far-left
                // position selects the neighbouring 150% monitor on mixed-DPI desktops.
                NativePins.Check(SDL_SetWindowPosition(window,primary.x+(primary.w-width)/2,primary.y+(primary.h-height)/2),"hidden primary window");
            }
            SetPixelSize(width,height);RefreshSize();
            if(platform is null){device=new(NativeHandle,adapter);Attach();}
            else
            {
                context=SDL_GL_CreateContext(window);if(context==null)throw new InvalidOperationException(SDL_GetError());
                NativePins.Check(SDL_GL_MakeCurrent(window,context),"activate context");NativePins.Check(SDL_GL_SetSwapInterval(0),"swap interval");
                ContextDescription=platform.ValidateContext(new GlApi(name=>(nint)SDL_GL_GetProcAddress(name)),Environment.GetEnvironmentVariable("ENB_EXPECT_ADAPTER"));
                Gl=new(new(EnbGlProfileType.OpenGLES,3,0),name=>(nint)SDL_GL_GetProcAddress(name));
            }
            if(!hidden)NativePins.Check(SDL_ShowWindow(window),"show window");
        }
        catch{Dispose();throw;}
    }
    private void Attach()
    {
        var device=this.device!;
        ContextDescription=device.Describe(new GlApi(device.ProcAddress).String,Environment.GetEnvironmentVariable("ENB_EXPECT_ADAPTER"));
        Gl=new(new(EnbGlProfileType.OpenGLES,3,0),device.ProcAddress);
    }
    /// <summary>Windows: the graphics card presentation runs on (its DXGI LUID). Null where the system picks the card at start.</summary>
    internal long? AdapterLuid=>device?.AdapterLuid;
    /// <summary>Windows: the card asked for, null for the one Windows picks for this program.</summary>
    internal long? RequestedAdapterLuid=>device?.RequestedLuid;
    internal bool CanSwitchAdapter=>device is not null;
    /// <summary>Moves presentation to another graphics card (null: the one Windows picks), keeping the window. Every GL
    /// object must be released before and created again after, also when this throws: the previous card is then back.
    /// <see cref="Gl"/> is a new instance afterwards.</summary>
    internal void SwitchAdapter(long? luid)
    {
        var device=this.device??throw new InvalidOperationException("This system chooses the graphics card when EnbNext starts.");
        try{device.Switch(luid);}finally{Attach();}
    }
    /// <summary>A GL or EGL entry point of the presenting context (the browser's texture import resolves through this).</summary>
    internal nint ProcAddress(string name)=>device is not null?device.ProcAddress(name):
        name.StartsWith("egl",StringComparison.Ordinal)?(nint)SDL_EGL_GetProcAddress(name):(nint)SDL_GL_GetProcAddress(name);
    private static void Attribute(SDL_GLAttr name,int value)=>NativePins.Check(SDL_GL_SetAttribute(name,value),name.ToString());
    internal void RefreshSize()
    {
        int w,h;NativePins.Check(SDL_GetWindowSizeInPixels(window,&w,&h),"pixel size");PixelWidth=w;PixelHeight=h;
        SystemScale=SDL_GetWindowDisplayScale(window);if(!double.IsFinite(SystemScale)||SystemScale<=0)throw new InvalidOperationException("Invalid SDL display scale");
        Scale=ThumbnailScale is >0 and <=2 ? ThumbnailScale.Value : UiScaleOverride is >=1 and <=2 ? UiScaleOverride.Value : SystemScale;
        float density=SDL_GetWindowPixelDensity(window);if(!float.IsFinite(density)||density<=0)throw new InvalidOperationException("Invalid SDL pixel density");
        // SDL minimum sizes use window coordinates; the application minimum is
        // 640x480 logical UI units, matching the existing frontend constraint.
        int minWidth=(int)Math.Ceiling(640*Scale/density),minHeight=(int)Math.Ceiling(480*Scale/density);
        if(minWidth!=minimumWidth||minHeight!=minimumHeight)
        {NativePins.Check(SDL_SetWindowMinimumSize(window,minWidth,minHeight),"logical minimum window size");minimumWidth=minWidth;minimumHeight=minHeight;}
    }
    internal void SetPixelSize(int width,int height)
    {
        float density=SDL_GetWindowPixelDensity(window);if(!float.IsFinite(density)||density<=0)throw new InvalidOperationException("Invalid SDL pixel density");
        NativePins.Check(SDL_SetWindowSize(window,(int)Math.Round(width/density),(int)Math.Round(height/density)),"window size");
    }
    internal void Bordered(bool value)=>NativePins.Check(SDL_SetWindowBordered(window,value),"window border");
    /// <summary>Places the window in screen coordinates (as display bounds report them).</summary>
    internal void Place(int x,int y,int width,int height){Position(x,y);NativePins.Check(SDL_SetWindowSize(window,width,height),"window size");}
    internal double PixelDensity=>SDL_GetWindowPixelDensity(window);
    internal void SetFullscreen(bool value)=>NativePins.Check(SDL_SetWindowFullscreen(window,value),"fullscreen");
    internal void Maximize()=>NativePins.Check(SDL_MaximizeWindow(window),"maximize");
    internal void Restore()=>NativePins.Check(SDL_RestoreWindow(window),"restore");
    internal void Position(int x,int y){if(placeable)NativePins.Check(SDL_SetWindowPosition(window,x,y),"window position");}
    internal (int X,int Y) Position(){int x,y;NativePins.Check(SDL_GetWindowPosition(window,&x,&y),"window position");return(x,y);}
    internal void Title(string value)=>NativePins.Check(SDL_SetWindowTitle(window,value),"window title");
    internal void Show()=>NativePins.Check(SDL_ShowWindow(window),"show window");
    /// <summary>Uses EnbNext's icon (a PNG next to the program) for the window and taskbar. A missing or unreadable icon keeps the default.</summary>
    internal void SetIcon(string path)
    {
        try
        {
            using var codec=SkiaSharp.SKCodec.Create(path);if(codec is null)return;
            var info=new SkiaSharp.SKImageInfo(codec.Info.Width,codec.Info.Height,SkiaSharp.SKColorType.Bgra8888,SkiaSharp.SKAlphaType.Unpremul);
            using var image=SkiaSharp.SKBitmap.Decode(codec,info);if(image is null)return;
            SDL_Surface* surface=SDL_CreateSurfaceFrom(image.Width,image.Height,SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888,image.GetPixels(),image.RowBytes);
            if(surface==null)return;
            try{SDL_SetWindowIcon(window,surface);}finally{SDL_DestroySurface(surface);}
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){}
    }
    internal void Hide()=>SDL_HideWindow(window);
    internal void Raise()=>NativePins.Check(SDL_RaiseWindow(window),"raise window");
    internal void Clipboard(string value)=>NativePins.Check(SDL_SetClipboardText(value),"clipboard");
    internal void Message(string title,string message)=>NativePins.Check(SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR,title,message,window),"message box");
    internal bool Poll(out SDL_Event value){SDL_Event e;bool found=SDL_PollEvent(&e);value=e;return found;}
    internal SDL_Event UiEvent(SDL_Event e)=>SdlWindowCoordinates.ToUi(window,e,Scale);
    internal void Clear(){Gl.BindFramebuffer(0x8d40,0);Gl.Viewport(0,0,PixelWidth,PixelHeight);Gl.Disable(0x0c11);Gl.ClearColor(0,0,0,1);Gl.Clear(0x4000);}
    internal void Swap(){if(device is not null)device.Swap();else NativePins.Check(SDL_GL_SwapWindow(window),"swap");}
    internal static void SleepUntil(long deadline)
    {
        long remaining=deadline-Stopwatch.GetTimestamp();
        if(remaining>0)SDL_DelayNS((ulong)(remaining*(1_000_000_000d/Stopwatch.Frequency)));
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        device?.Dispose();device=null;
        if(context!=null){SDL_GL_DestroyContext(context);context=null;}
        if(window!=null){SDL_DestroyWindow(window);window=null;}
        if(initialized){SDL_Quit();initialized=false;}
    }
}

// SDL window units differ from pixels on high-density platforms. UI units are
// pixels / display scale; see SDL3/README-highdpi. Keep sampling, events and warps symmetric.
internal static unsafe class SdlWindowCoordinates
{
    internal static EnbUiPoint ToUi(SDL_Window* window,EnbUiPoint point,double scale){double factor=SDL_GetWindowPixelDensity(window)/scale;return new(point.X*factor,point.Y*factor);}
    internal static EnbUiPoint ToWindow(SDL_Window* window,EnbUiPoint point,double scale){double factor=scale/SDL_GetWindowPixelDensity(window);return new(point.X*factor,point.Y*factor);}
    internal static SDL_Event ToUi(SDL_Window* window,SDL_Event e,double scale)
    {
        float factor=(float)(SDL_GetWindowPixelDensity(window)/scale);
        switch((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:e.motion.x*=factor;e.motion.y*=factor;e.motion.xrel*=factor;e.motion.yrel*=factor;break;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:e.button.x*=factor;e.button.y*=factor;break;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:e.wheel.mouse_x*=factor;e.wheel.mouse_y*=factor;break;
        }
        return e;
    }
}
