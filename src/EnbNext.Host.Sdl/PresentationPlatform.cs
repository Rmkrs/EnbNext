namespace EnbNext.Host.Sdl;

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

// The current context's GL strings, for the presentation check at start.
internal sealed unsafe class GlApi
{
    private readonly delegate* unmanaged<uint,byte*> getString;
    public GlApi(Func<string,nint> resolve)
    {
        getString=(delegate* unmanaged<uint,byte*>)resolve("glGetString");
        foreach(string name in (string[])["glClearColor","glClear","glViewport","glGetError"])
            if(resolve(name)==0)throw new InvalidOperationException("Missing GLES entry point.");
        if(getString==null)throw new InvalidOperationException("Missing GLES entry point.");
    }
    public string String(uint name)=>Marshal.PtrToStringUTF8((nint)getString(name))??"";
}
