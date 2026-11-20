namespace EnbNext.Host.Sdl;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;
using static NativePins;

internal interface IPresentationPlatform
{
    void ConfigureBeforeInit();
    void ConfigureContext();
    string ValidateContext(GlApi gl,string? expectedAdapter=null);
}

// Only this adapter chooses the Windows ANGLE backend. SDL owns the window,
// EGL surface/context and destruction through its supported public GL API.
internal sealed unsafe class WindowsAnglePlatform : IPresentationPlatform
{
    private static int attributeCalls;
    internal static int AttributeCalls => attributeCalls;
    public static string Diagnostic()
    {
        nint module=NativeLibrary.Load(Find("libEGL.dll"));
        try
        {
            var error=(delegate* unmanaged<uint>)NativeLibrary.GetExport(module,"eglGetError");
            var query=(delegate* unmanaged<nint,int,byte*>)NativeLibrary.GetExport(module,"eglQueryString");
            uint code=error();
            return $"callbacks={attributeCalls}; EGL error=0x{code:X}; clientVersion={Marshal.PtrToStringUTF8((nint)query(0,0x3054))}; extensions={Marshal.PtrToStringUTF8((nint)query(0,0x3055))}";
        }
        finally {NativeLibrary.Free(module);}
    }
    public void ConfigureBeforeInit()
    {
        string angle=Find("libGLESv2.dll"),egl=Find("libEGL.dll");
        Verify(angle,"C3E413E81F52F64415481DAF16232E18AE19CAA6B0CF2768A02A0A51D3436B60");
        Verify(egl,"F158A8DA0E8B6C56BCDAC67ED95446DF6F984ED192D895A5C8040A43E8343BEF");
        Check(SDL_SetHint("SDL_OPENGL_ES_DRIVER","1"),"force ES driver");
        Check(SDL_SetHint("SDL_OPENGL_LIBRARY",angle),"pin GLES library");
        Check(SDL_SetHint("SDL_EGL_LIBRARY",egl),"pin EGL library");
        Check(SDL_SetHint("SDL_VIDEO_WIN_D3DCOMPILER","none"),"ANGLE compiler loading");
        Check(SDL_SetHint("SDL_VIDEO_EGL_ALLOW_GETDISPLAY_FALLBACK","0"),"disable EGL fallback");
    }
    public void ConfigureContext()
    {
        Check(SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_EGL_PLATFORM,0x3202),"EGL_PLATFORM_ANGLE_ANGLE");
        SDL_EGL_SetAttributeCallbacks(&Attributes,null,null,0);
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static nint* Attributes(nint userdata)
    {
        // SDL owns/frees this array. No managed callback lifetime or exception crossing.
        var p=(nint*)SDL_malloc((nuint)(5*sizeof(nint)));
        if(p==null)return null;
        p[0]=0x3203;p[1]=0x3208; // ANGLE_TYPE = D3D11
        p[2]=0x3209;p[3]=0x320A; // ANGLE_DEVICE_TYPE = HARDWARE
        // This pinned ANGLE exposes D3D hardware selection, but not
        // EGL_ANGLE_power_preference. Validate the actual adapter after creation.
        p[4]=0x3038; // EGL_NONE
        attributeCalls++;
        return p;
    }
    public string ValidateContext(GlApi gl,string? expectedAdapter=null)
    {
        nint display=SDL_EGL_GetCurrentDisplay();
        if(display==0 || attributeCalls==0)throw new InvalidOperationException("SDL did not create the requested EGL platform context.");
        var query=(delegate* unmanaged<nint,int,byte*>)SDL_EGL_GetProcAddress("eglQueryString");
        if(query==null)throw new InvalidOperationException("eglQueryString unavailable.");
        string vendor=Marshal.PtrToStringUTF8((nint)query(display,0x3053))??"";
        string version=Marshal.PtrToStringUTF8((nint)query(display,0x3054))??"";
        string renderer=gl.String(0x1F01);
        if(!renderer.Contains("ANGLE") || (expectedAdapter is not null&&!renderer.Contains(expectedAdapter,StringComparison.OrdinalIgnoreCase)) || !renderer.Contains("Direct3D11"))
            throw new InvalidOperationException("Rejecting unexpected context/adapter: "+renderer);
        return $"EGL vendor={vendor}; EGL version={version}; GL renderer={renderer}; GL version={gl.String(0x1F02)}; attributeCallbacks={attributeCalls}";
    }
}

internal sealed class LinuxEglPlatform : IPresentationPlatform
{
    public void ConfigureBeforeInit()
    {
        Check(SDL_SetHint("SDL_VIDEO_FORCE_EGL","1"),"Linux EGL selection");
        // SDL prefers X11 on compositors without fifo-v1 (KWin 6.3). NVIDIA
        // drivers before 560 have no EGL on Xwayland, so prefer Wayland and
        // keep X11 as the fallback. SDL_VIDEO_DRIVER in the environment wins.
        Check(SDL_SetHint("SDL_VIDEO_DRIVER","wayland,x11"),"Linux video driver order");
    }
    public void ConfigureContext() { }
    public string ValidateContext(GlApi gl,string? expectedAdapter=null)
    {
        if(SDL_EGL_GetCurrentDisplay()==0)throw new InvalidOperationException("Linux EGL display missing.");
        return "Linux EGL; GL renderer="+gl.String(0x1F01)+"; runtime presentation validation deferred";
    }
}

internal sealed unsafe class GlApi
{
    private readonly delegate* unmanaged<uint,byte*> getString=(delegate* unmanaged<uint,byte*>)SDL_GL_GetProcAddress("glGetString");
    private readonly delegate* unmanaged<float,float,float,float,void> clearColor=(delegate* unmanaged<float,float,float,float,void>)SDL_GL_GetProcAddress("glClearColor");
    private readonly delegate* unmanaged<uint,void> clear=(delegate* unmanaged<uint,void>)SDL_GL_GetProcAddress("glClear");
    private readonly delegate* unmanaged<int,int,int,int,void> viewport=(delegate* unmanaged<int,int,int,int,void>)SDL_GL_GetProcAddress("glViewport");
    private readonly delegate* unmanaged<uint> getError=(delegate* unmanaged<uint>)SDL_GL_GetProcAddress("glGetError");
    public GlApi()
    {
        if(getString==null || clearColor==null || clear==null || viewport==null || getError==null)throw new InvalidOperationException("Missing GLES entry point.");
    }
    public string String(uint name)=>Marshal.PtrToStringUTF8((nint)getString(name))??"";
}
