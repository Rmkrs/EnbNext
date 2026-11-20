using System.Runtime.InteropServices;
using EnbNext.Engine;
using EnbNext.Host.Sdl;
using EnbNext.Presentation;
using SDL;
using SkiaSharp;
namespace EnbNext.App;

internal sealed unsafe class CefBrowserService : IDisposable
{
    private readonly SdlGameWindow window;
    private readonly SdlBrowserSnapshots layers;
    private readonly Action<Action> post;
    private CefBrowserSurface? first;
    private bool initialized;
    private FileStream? cacheLease;
    private static bool resolverInstalled;
    internal CefBrowserService(SdlGameWindow window,SdlBrowserSnapshots layers,Action<Action> post){this.window=window;this.layers=layers;this.post=post;}
    internal CefBrowserSurface Create(Uri uri,string profile,string query,bool silent=false)
    {
        if(!initialized)
        {
            try
            {
            string root=Path.Combine(AppContext.BaseDirectory,"cef");
            if(OperatingSystem.IsWindows())
            {
                if(LoadLibraryExW(Path.Combine(root,"libcef.dll"),0,0x1100)==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                NativeLibrary.Load(Path.Combine(root,"EnbNext.Cef.dll"));
            }
            else {NativeLibrary.Load(Path.Combine(root,"libcef.so"));NativeLibrary.Load(Path.Combine(root,"libEnbNext.Cef.so"));}
            if(!resolverInstalled){NativeLibrary.SetDllImportResolver(typeof(CefNative).Assembly,(name,_,_)=>name=="EnbNext.Cef"?NativeLibrary.Load(Path.Combine(root,OperatingSystem.IsWindows()?"EnbNext.Cef.dll":"libEnbNext.Cef.so")):0);resolverInstalled=true;}
            string cache;
            for(int slot=0;;slot++){cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EnbNext","Cef","client-"+slot);Directory.CreateDirectory(cache);try{cacheLease=new FileStream(Path.Combine(cache,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);break;}catch(IOException ex) when((ex.HResult&65535) is 11 or 32 or 33){}}
            if(CefNative.enb_cef_initialize(Path.Combine(root,OperatingSystem.IsWindows()?"EnbNext.CefHelper.exe":"EnbNext.CefHelper"),root,cache)==0)throw new InvalidOperationException("CEF initialization failed");
            initialized=true;
            }
            catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                cacheLease?.Dispose();cacheLease=null;
                throw new InvalidOperationException("The browser runtime could not be initialized.",ex);
            }
        }
        var page=new CefBrowserSurface(this,window,layers,post,uri,profile,query,silent){Next=first};first=page;return page;
    }
    internal void Pump(){if(!initialized)return;CefNative.enb_cef_pump();CefBrowserSurface? previous=null;for(var p=first;p is not null;){var next=p.Next;if(p.Retire()){if(previous is null)first=next;else previous.Next=next;}else previous=p;p=next;}}
    internal bool Handle(SDL_Event e){for(var p=first;p is not null;p=p.Next)if(p.Handle(e))return true;return false;}
    public void Dispose()
    {
        for(var p=first;p is not null;p=p.Next)p.Dispose();
        var end=Environment.TickCount64+10000;while(first is not null&&Environment.TickCount64<end){Pump();Thread.Sleep(1);}
        if(first is not null)throw new TimeoutException("CEF close did not drain");
        if(initialized){CefNative.enb_cef_shutdown();initialized=false;}
        cacheLease?.Dispose();cacheLease=null;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint LoadLibraryExW(string path,nint file,uint flags);
}

internal sealed unsafe class CefBrowserSurface:IEnbInventoryBrowser,ISdlBrowserLayer
{
    internal CefBrowserSurface? Next;
    private readonly SdlGameWindow window;private readonly SdlBrowserSnapshots layers;private readonly Action<Action> post;
    private readonly Uri address;private readonly string query;private readonly CefNative.Event callback;private readonly CefNative.Resolve resolve;
    private nint page;private int buttons;private bool disposed,visible,focused,captured,composing,hovered;private EnbUiRect bounds;
    private TaskCompletionSource<IEnbOptionsBrowserSnapshot>? capture;private string? failure;
    internal readonly TaskCompletionSource Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<string>? Failed;
    public event Action? PageLoaded,CloseRequested,EscapeRequested;
    /// <summary>The surface was disposed (its browser is closing).</summary>
    internal event Action? Closed;
    internal CefBrowserSurface(CefBrowserService owner,SdlGameWindow window,SdlBrowserSnapshots layers,Action<Action> post,Uri uri,string profile,string query,bool silent)
    {
        this.window=window;this.layers=layers;this.post=post;address=uri;this.query=query;
        if(uri.Scheme is not ("http" or "https"))throw new ArgumentException("The terminal requires an HTTP address");
        callback=OnEvent;resolve=name=>name.StartsWith("egl",StringComparison.Ordinal)?(nint)SDL.SDL3.SDL_EGL_GetProcAddress(name):window.Gl.GetProcAddress(name);
        page=CefNative.enb_cef_create(EnbNext.Net7.Net7StationBrowser.AppendQuery(uri,query).AbsoluteUri,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile))),callback,0,resolve,window.NativeHandle,silent?1:0);
        if(page==0)throw new InvalidOperationException("CEF browser creation failed");layers.Add(this);
    }
    private int OnEvent(nint tag,int kind,nint text)
    {
        if(disposed)return kind is 4 or 5?1:0;
        string value=Marshal.PtrToStringUTF8(text)??"";
        if(kind is 4 or 5)
        {
            if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))return value=="about:blank"?0:1;
            // A page's own request to the game (the wiki window's route buttons): never loaded, handed to the window.
            if(uri.Host.Equals(EnbNext.Engine.EnbInventoryUi.PageRequestHost,StringComparison.OrdinalIgnoreCase)){post(()=>{if(!disposed)Requested?.Invoke(uri);});return 1;}
            var target=uri.Host.Equals(address.Host,StringComparison.OrdinalIgnoreCase)?EnbNext.Net7.Net7StationBrowser.AppendQuery(uri,query):uri;
            if(kind==5||target.AbsoluteUri!=uri.AbsoluteUri){post(()=>{if(!disposed)CefNative.enb_cef_navigate(page,target.AbsoluteUri);});return 1;}
            return 0;
        }
        post(()=>{if(disposed)return;switch(kind){case 1:failure=null;Ready.TrySetResult();PageLoaded?.Invoke();break;case 2:failure=value;Ready.TrySetException(new IOException(value));capture?.TrySetException(new IOException(value));capture=null;Failed?.Invoke(value);break;case 3:CloseRequested?.Invoke();break;}});return 0;
    }
    /// <summary>Loads the address again (after the page failed to load).</summary>
    public event Action<Uri>? Requested;
    /// <summary>Runs a script in the page.</summary>
    public void Script(string code){if(!disposed&&page!=0)CefNative.enb_cef_script(page,code);}
    public void Reload(){if(disposed||page==0)return;failure=null;CefNative.enb_cef_navigate(page,EnbNext.Net7.Net7StationBrowser.AppendQuery(address,query).AbsoluteUri);}
    public EnbUiRect Bounds{get=>bounds;set{bounds=value;Place();}}
    public bool IsVisible{get=>visible;set{visible=value;Place();if(!value)Focus(false);}}
    private void Place(){if(!disposed)CefNative.enb_cef_place(page,Math.Max(1,(int)Math.Round(bounds.Width)),Math.Max(1,(int)Math.Round(bounds.Height)),(float)window.Scale,visible?1:0);}
    private void Focus(bool value){focused=value;if(page!=0)CefNative.enb_cef_focus(page,value?1:0);if(value)SDL.SDL3.SDL_StartTextInput(window.Window);else{captured=false;buttons=0;if(composing&&page!=0)CefNative.enb_cef_ime(page,"",0,0,0);composing=false;SDL.SDL3.SDL_StopTextInput(window.Window);}}
    internal ulong PaintSerial=>!disposed&&CefNative.enb_cef_frame(page,0,out var frame,out _)!=0?frame.Serial:0;
    public Task<IEnbOptionsBrowserSnapshot> CaptureAsync(){if(failure is {} message)return Task.FromException<IEnbOptionsBrowserSnapshot>(new IOException(message));if(disposed)return Task.FromCanceled<IEnbOptionsBrowserSnapshot>(new(true));return (capture??=new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;}
    public void Draw(SKCanvas canvas,GRContext gpu,double scale)
    {
        if(disposed)return;
        for(int popup=0;popup<2;popup++)
        {
            if(CefNative.enb_cef_frame(page,popup,out var frame,out var rect)==0)continue;
            using var texture=new GRBackendTexture(frame.Width,frame.Height,false,new GRGlTextureInfo(0x0de1,frame.Texture,0x8058));
            using var image=SKImage.FromTexture(gpu,texture,GRSurfaceOrigin.TopLeft,SKColorType.Rgba8888,SKAlphaType.Premul)??throw new IOException("Browser GPU texture unavailable");
            if(popup==0&&capture is {} pending)
            {
                using var history=SKSurface.Create(gpu,false,new SKImageInfo(frame.Width,frame.Height,SKColorType.Rgba8888,SKAlphaType.Premul))??throw new IOException("Browser history surface unavailable");
                history.Canvas.Clear(SKColors.Transparent);history.Canvas.DrawImage(image,0,0);
                if(CefNative.enb_cef_frame(page,1,out var popupFrame,out var popupRect)!=0)
                {
                    using var popupTexture=new GRBackendTexture(popupFrame.Width,popupFrame.Height,false,new GRGlTextureInfo(0x0de1,popupFrame.Texture,0x8058));
                    using var popupImage=SKImage.FromTexture(gpu,popupTexture,GRSurfaceOrigin.TopLeft,SKColorType.Rgba8888,SKAlphaType.Premul)??throw new IOException("Browser popup texture unavailable");
                    float density=(float)(frame.Width/Math.Max(1,bounds.Width));history.Canvas.DrawImage(popupImage,SKRect.Create(popupRect.X*density,popupRect.Y*density,popupRect.Width*density,popupRect.Height*density));
                }
                history.Canvas.Flush();gpu.Flush();using var captured=history.Snapshot();capture=null;pending.TrySetResult(layers.Capture(gpu,captured,frame.Width,frame.Height));
            }
            if(!visible)continue;
            canvas.Save();canvas.ClipRect(SKRect.Create((float)bounds.X,(float)bounds.Y,(float)bounds.Width,(float)bounds.Height));
            var target=popup==0?bounds:new EnbUiRect(bounds.X+rect.X,bounds.Y+rect.Y,rect.Width,rect.Height);
            canvas.DrawImage(image,SKRect.Create((float)target.X,(float)target.Y,(float)target.Width,(float)target.Height),new SKSamplingOptions(SKFilterMode.Linear));canvas.Restore();
        }
    }
    public void ReleaseGpu(bool contextLost){if(!disposed)CefNative.enb_cef_release_gpu(page,contextLost?1:0);}
    public void Dispose(){if(disposed)return;Focus(false);disposed=true;layers.Remove(this);capture?.TrySetCanceled();Ready.TrySetCanceled();CefNative.enb_cef_close(page);Closed?.Invoke();}
    internal bool Retire(){if(!disposed||CefNative.enb_cef_closed(page)==0)return false;CefNative.enb_cef_release(page);page=0;GC.KeepAlive(callback);GC.KeepAlive(resolve);return true;}
    internal bool Handle(SDL_Event e)
    {
        if(disposed)return false;var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED){Focus(false);return false;}
        if(!visible)return false;
        int modifiers=0;uint mod=(uint)(type is SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP?e.key.mod:SDL.SDL3.SDL_GetModState());if((mod&SDL.SDL3.SDL_KMOD_SHIFT)!=0)modifiers|=2;if((mod&SDL.SDL3.SDL_KMOD_CTRL)!=0)modifiers|=4;if((mod&SDL.SDL3.SDL_KMOD_ALT)!=0)modifiers|=8;
        if(type is SDL_EventType.SDL_EVENT_MOUSE_MOTION or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP or SDL_EventType.SDL_EVENT_MOUSE_WHEEL)
        {
            double x=type==SDL_EventType.SDL_EVENT_MOUSE_MOTION?e.motion.x:type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL?e.wheel.mouse_x:e.button.x;
            double y=type==SDL_EventType.SDL_EVENT_MOUSE_MOTION?e.motion.y:type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL?e.wheel.mouse_y:e.button.y;
            bool inside=x>=bounds.X&&y>=bounds.Y&&x<bounds.X+bounds.Width&&y<bounds.Y+bounds.Height;
            if(!inside&&!captured){if(hovered){CefNative.enb_cef_mouse(page,0,(int)Math.Round(x-bounds.X),(int)Math.Round(y-bounds.Y),0,1,modifiers|buttons);hovered=false;}if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN)Focus(false);return false;}hovered=inside;modifiers|=buttons;
            int px=(int)Math.Round(x-bounds.X),py=(int)Math.Round(y-bounds.Y);
            if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)CefNative.enb_cef_mouse(page,0,px,py,0,inside?0:1,modifiers);
            else if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)CefNative.enb_cef_mouse(page,2,px,py,(int)(e.wheel.x*120),(int)(e.wheel.y*120),modifiers);
            else{bool down=type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN;int flag=e.button.button==1?16:e.button.button==2?32:64;if(down){Focus(true);buttons|=flag;captured=true;modifiers|=flag;}else{buttons&=~flag;captured=buttons!=0;modifiers&=~flag;}CefNative.enb_cef_mouse(page,1,px,py,e.button.button==1?0:e.button.button==2?1:2,down?0:1,modifiers,Math.Max(1,(int)e.button.clicks));}
            return true;
        }
        if(!focused)return false;
        if(type is SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP)
        {
            if(e.key.scancode==SDL_Scancode.SDL_SCANCODE_ESCAPE){if(type==SDL_EventType.SDL_EVENT_KEY_DOWN&&!e.key.repeat&&modifiers==0){if(EscapeRequested is {} escape)escape();else CloseRequested?.Invoke();}return true;}
            int key=SdlOptionsKeyboard.Code(e.key);if(key==0x100)key=13;CefNative.enb_cef_key(page,type==SDL_EventType.SDL_EVENT_KEY_UP?2:0,key,NativeKey(e.key,key,type==SDL_EventType.SDL_EVENT_KEY_UP),modifiers,0);return true;
        }
        if(type==SDL_EventType.SDL_EVENT_TEXT_EDITING&&e.edit.text!=null){string text=Marshal.PtrToStringUTF8((nint)e.edit.text)??"";composing=text.Length>0;int start=Utf16Offset(text,e.edit.start),end=Utf16Offset(text,e.edit.start+e.edit.length);CefNative.enb_cef_ime(page,text,start,end-start,0);return true;}
        if(type==SDL_EventType.SDL_EVENT_TEXT_INPUT&&e.text.text!=null){string text=Marshal.PtrToStringUTF8((nint)e.text.text)??"";if(composing){CefNative.enb_cef_ime(page,text,0,0,1);composing=false;}else foreach(char c in text)CefNative.enb_cef_key(page,3,c,0,modifiers,c);return true;}
        return false;
    }
    private static int Utf16Offset(string text,int codepoints){int result=0;foreach(var rune in text.EnumerateRunes()){if(codepoints--<=0)break;result+=rune.Utf16SequenceLength;}return result;}
    private static int NativeKey(SDL_KeyboardEvent key,int virtualKey,bool up)
    {
        if(!OperatingSystem.IsWindows())return (int)key.raw;
        uint scan=MapVirtualKeyW((uint)virtualKey,4);return 1|((int)(scan&255)<<16)|((scan&0xff00)!=0?1<<24:0)|(key.repeat?1<<30:0)|(up?unchecked((int)0xc0000000):0);
    }
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code,uint mapType);
}
