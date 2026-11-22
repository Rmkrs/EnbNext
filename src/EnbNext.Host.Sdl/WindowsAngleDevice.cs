using System.Runtime.InteropServices;
using static EnbNext.Host.Sdl.NativePins;

namespace EnbNext.Host.Sdl;

/// <summary>
/// The Windows presentation device: OpenGL ES 3.0 on ANGLE's Direct3D 11 backend, drawn into SDL's window.
/// EnbNext owns the EGL display, surface and context itself (SDL ties its own to the window for the window's
/// lifetime), so the graphics card can change while the game runs: <see cref="Switch"/> replaces all three on
/// the same window. The caller releases every GL object first and creates them again afterwards.
/// </summary>
internal sealed unsafe class WindowsAngleDevice : IDisposable
{
    private const int None=0x3038;
    private readonly nint window;
    private readonly nint egl;
    private readonly delegate* unmanaged<uint,nint,int*,nint> getPlatformDisplay;
    private readonly delegate* unmanaged<nint,int*,int*,uint> initialize;
    private readonly delegate* unmanaged<nint,uint> terminate;
    private readonly delegate* unmanaged<uint,uint> bindApi;
    private readonly delegate* unmanaged<nint,int*,nint*,int,int*,uint> chooseConfig;
    private readonly delegate* unmanaged<nint,nint,int,int*,uint> configAttribute;
    private readonly delegate* unmanaged<nint,nint,nint,int*,nint> createWindowSurface;
    private readonly delegate* unmanaged<nint,nint,nint,int*,nint> createContext;
    private readonly delegate* unmanaged<nint,nint,nint,nint,uint> makeCurrent;
    private readonly delegate* unmanaged<nint,nint,uint> swapBuffers,destroySurface,destroyContext;
    private readonly delegate* unmanaged<nint,int,uint> swapInterval;
    private readonly delegate* unmanaged<int> getError;
    private readonly delegate* unmanaged<byte*,nint> getProcAddress;
    private readonly delegate* unmanaged<nint,int,byte*> queryString;
    private readonly delegate* unmanaged<nint,int,nint*,uint> queryDisplay,queryDevice;
    private readonly nint gles;
    private nint display,surface,context;
    private bool disposed;

    /// <summary>The LUID asked for (null: the card Windows picks for this program).</summary>
    internal long? RequestedLuid {get;private set;}
    /// <summary>The LUID of the card ANGLE's Direct3D device actually runs on.</summary>
    internal long AdapterLuid {get;private set;}
    internal string AdapterName {get;private set;}="";

    internal WindowsAngleDevice(nint window,long? luid)
    {
        if(window==0)throw new InvalidOperationException("The game window has no native handle.");
        this.window=window;
        string angle=Find("libGLESv2.dll"),library=Find("libEGL.dll");
        Verify(angle,"C3E413E81F52F64415481DAF16232E18AE19CAA6B0CF2768A02A0A51D3436B60");
        Verify(library,"F158A8DA0E8B6C56BCDAC67ED95446DF6F984ED192D895A5C8040A43E8343BEF");
        // libEGL loads libGLESv2 by name; load the pinned copy first so that one is used.
        gles=NativeLibrary.Load(angle);egl=NativeLibrary.Load(library);
        getPlatformDisplay=(delegate* unmanaged<uint,nint,int*,nint>)Export("eglGetPlatformDisplayEXT");
        initialize=(delegate* unmanaged<nint,int*,int*,uint>)Export("eglInitialize");
        terminate=(delegate* unmanaged<nint,uint>)Export("eglTerminate");
        bindApi=(delegate* unmanaged<uint,uint>)Export("eglBindAPI");
        chooseConfig=(delegate* unmanaged<nint,int*,nint*,int,int*,uint>)Export("eglChooseConfig");
        configAttribute=(delegate* unmanaged<nint,nint,int,int*,uint>)Export("eglGetConfigAttrib");
        createWindowSurface=(delegate* unmanaged<nint,nint,nint,int*,nint>)Export("eglCreateWindowSurface");
        createContext=(delegate* unmanaged<nint,nint,nint,int*,nint>)Export("eglCreateContext");
        makeCurrent=(delegate* unmanaged<nint,nint,nint,nint,uint>)Export("eglMakeCurrent");
        swapBuffers=(delegate* unmanaged<nint,nint,uint>)Export("eglSwapBuffers");
        destroySurface=(delegate* unmanaged<nint,nint,uint>)Export("eglDestroySurface");
        destroyContext=(delegate* unmanaged<nint,nint,uint>)Export("eglDestroyContext");
        swapInterval=(delegate* unmanaged<nint,int,uint>)Export("eglSwapInterval");
        getError=(delegate* unmanaged<int>)Export("eglGetError");
        getProcAddress=(delegate* unmanaged<byte*,nint>)Export("eglGetProcAddress");
        queryString=(delegate* unmanaged<nint,int,byte*>)Export("eglQueryString");
        queryDisplay=(delegate* unmanaged<nint,int,nint*,uint>)Export("eglQueryDisplayAttribEXT");
        queryDevice=(delegate* unmanaged<nint,int,nint*,uint>)Export("eglQueryDeviceAttribEXT");
        try{Create(luid);}
        catch{Destroy();throw;}
    }
    private nint Export(string name)=>NativeLibrary.TryGetExport(egl,name,out var p)?p:throw new InvalidOperationException("ANGLE lacks "+name+".");

    private void Create(long? luid)
    {
        RequestedLuid=luid;
        int* attributes=stackalloc int[9];int n=0;
        attributes[n++]=0x3203;attributes[n++]=0x3208; // EGL_PLATFORM_ANGLE_TYPE_ANGLE = D3D11
        attributes[n++]=0x3209;attributes[n++]=0x320A; // EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE = HARDWARE
        if(luid is long chosen)
        {
            // EGL_ANGLE_platform_angle_d3d_luid: ANGLE creates its D3D11 device on this card.
            attributes[n++]=0x34A0;attributes[n++]=(int)(chosen>>32);
            attributes[n++]=0x34A1;attributes[n++]=unchecked((int)chosen);
        }
        attributes[n]=None;
        display=getPlatformDisplay(0x3202,0,attributes); // EGL_PLATFORM_ANGLE_ANGLE, default native display
        if(display==0)throw Failure("eglGetPlatformDisplayEXT");
        int major,minor;if(initialize(display,&major,&minor)==0)throw Failure("eglInitialize");
        if(bindApi(0x30A0)==0)throw Failure("eglBindAPI"); // EGL_OPENGL_ES_API
        int* wanted=stackalloc int[]{0x3024,8,0x3023,8,0x3022,8,0x3021,8,0x3025,24,0x3026,8,0x3040,0x40,0x3033,4,None};
        nint* configs=stackalloc nint[64];int count;
        if(chooseConfig(display,wanted,configs,64,&count)==0||count<=0)throw Failure("eglChooseConfig");
        // eglChooseConfig sorts deeper colour first; keep the exact 8-bit RGBA, 24/8 depth-stencil format.
        nint config=configs[0];
        for(int i=0;i<count;i++)
            if(Attribute(configs[i],0x3024)==8&&Attribute(configs[i],0x3023)==8&&Attribute(configs[i],0x3022)==8&&Attribute(configs[i],0x3021)==8&&
                Attribute(configs[i],0x3025)==24&&Attribute(configs[i],0x3026)==8){config=configs[i];break;}
        int none=None;
        surface=createWindowSurface(display,config,window,&none);
        if(surface==0)throw Failure("eglCreateWindowSurface");
        int* version=stackalloc int[]{0x3098,3,0x30FB,0,None}; // EGL_CONTEXT_MAJOR/MINOR_VERSION
        context=createContext(display,config,0,version);
        if(context==0)throw Failure("eglCreateContext");
        if(makeCurrent(display,surface,surface,context)==0)throw Failure("eglMakeCurrent");
        if(swapInterval(display,0)==0)throw Failure("eglSwapInterval");
        (AdapterLuid,AdapterName)=ActualAdapter();
        if(luid is long requested&&requested!=AdapterLuid)
            throw new InvalidOperationException("ANGLE did not start on the chosen graphics card (it uses "+AdapterName+").");
    }
    private int Attribute(nint config,int name){int value=0;configAttribute(display,config,name,&value);return value;}
    private InvalidOperationException Failure(string operation)=>new($"{operation} failed (EGL error 0x{getError():X}).");

    /// <summary>The card behind ANGLE's Direct3D 11 device (EGL_EXT_device_query, then DXGI).</summary>
    private (long Luid,string Name) ActualAdapter()
    {
        nint device=0,d3d=0;
        if(queryDisplay(display,0x322C,&device)==0||device==0||queryDevice(device,0x33A1,&d3d)==0||d3d==0)
            throw new InvalidOperationException("ANGLE's Direct3D 11 device is unavailable.");
        // The device belongs to ANGLE: query interfaces from it, never release it.
        Guid dxgiDevice=new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");nint dxgi=0,adapter=0;
        try
        {
            if(((delegate* unmanaged<nint,Guid*,nint*,int>)(*(nint**)d3d)[0])(d3d,&dxgiDevice,&dxgi)<0||dxgi==0)
                throw new InvalidOperationException("ANGLE's device has no DXGI interface.");
            if(((delegate* unmanaged<nint,nint*,int>)(*(nint**)dxgi)[7])(dxgi,&adapter)<0||adapter==0)
                throw new InvalidOperationException("ANGLE's device has no adapter.");
            byte* description=stackalloc byte[304];
            if(((delegate* unmanaged<nint,byte*,int>)(*(nint**)adapter)[8])(adapter,description)<0)
                throw new InvalidOperationException("ANGLE's adapter cannot be described.");
            string name=new string((char*)description).Trim();
            long luid=((long)*(int*)(description+300)<<32)|*(uint*)(description+296);
            return(luid,name);
        }
        finally{if(adapter!=0)WindowsGraphicsAdapters.Release(adapter);if(dxgi!=0)WindowsGraphicsAdapters.Release(dxgi);}
    }

    /// <summary>Moves presentation to another card. Every GL object of the old context must already be released;
    /// when the card cannot be used, the previous card is restored and the failure is thrown.</summary>
    internal void Switch(long? luid)
    {
        long? previous=RequestedLuid;
        Destroy();
        try{Create(luid);}
        catch(InvalidOperationException)
        {
            Destroy();
            try{Create(previous);}
            catch(InvalidOperationException){Destroy();Create(null);}
            throw;
        }
    }

    /// <summary>GL and EGL entry points, resolved as SDL resolves them for EGL 1.5: eglGetProcAddress, then the libraries' exports.</summary>
    internal nint ProcAddress(string name)
    {
        nint p;
        int length=System.Text.Encoding.ASCII.GetByteCount(name);byte* text=stackalloc byte[length+1];
        System.Text.Encoding.ASCII.GetBytes(name,new Span<byte>(text,length));text[length]=0;
        p=getProcAddress(text);if(p!=0)return p;
        if(NativeLibrary.TryGetExport(gles,name,out p))return p;
        return NativeLibrary.TryGetExport(egl,name,out p)?p:0;
    }
    internal void Swap(){if(swapBuffers(display,surface)==0)throw Failure("eglSwapBuffers");}

    /// <summary>The same check and description the SDL path gave: ANGLE on Direct3D 11, optionally on an expected card.</summary>
    internal string Describe(Func<uint,string> glString,string? expectedAdapter)
    {
        string vendor=Marshal.PtrToStringUTF8((nint)queryString(display,0x3053))??"";
        string version=Marshal.PtrToStringUTF8((nint)queryString(display,0x3054))??"";
        string renderer=glString(0x1F01);
        if(!renderer.Contains("ANGLE") || (expectedAdapter is not null&&!renderer.Contains(expectedAdapter,StringComparison.OrdinalIgnoreCase)) || !renderer.Contains("Direct3D11"))
            throw new InvalidOperationException("Rejecting unexpected context/adapter: "+renderer);
        return $"EGL vendor={vendor}; EGL version={version}; GL renderer={renderer}; GL version={glString(0x1F02)}; adapter={AdapterName}; requested={(RequestedLuid is null?"automatic":"chosen card")}";
    }

    private void Destroy()
    {
        if(display!=0)
        {
            makeCurrent(display,0,0,0);
            if(context!=0)destroyContext(display,context);
            if(surface!=0)destroySurface(display,surface);
            terminate(display);
        }
        context=surface=display=0;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        Destroy();
    }
}
