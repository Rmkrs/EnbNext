namespace EnbNext.Host.Sdl;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SDL;
using static SDL.SDL3;

internal static unsafe class NativePins
{
    public const string BindingVersion = "2026.1002.1";
    public const string AngleVersion = "Electron 41.0.3 official release / ANGLE 1d3190bf5633327395d694d621258978d989dffd";
    public static string SdlPath { get; private set; } = "";
    public static string Find(string name)
    {
        if(OperatingSystem.IsWindows() && name is "libEGL.dll" or "libGLESv2.dll")
        {
            string pinned=Path.Combine(AppContext.BaseDirectory,"runtimes","win-x64","native","enb-angle",name);
            if(File.Exists(pinned))return pinned;
        }
        string local=Path.Combine(AppContext.BaseDirectory,name);
        if(File.Exists(local))return local;
        return Path.Combine(AppContext.BaseDirectory,"runtimes",OperatingSystem.IsWindows()?"win-x64":"linux-x64","native",name);
    }
    public static void Verify(string path,string expected)
    {
        using var file=File.OpenRead(path);
        if(!Convert.ToHexString(SHA256.HashData(file)).Equals(expected,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native binary does not match pin: "+path);
    }
    public static void Load()
    {
        if(RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw new PlatformNotSupportedException("Spike pins cover x64 only.");
        SdlPath=Find(OperatingSystem.IsWindows()?"SDL3.dll":"libSDL3.so");
        Verify(SdlPath,OperatingSystem.IsWindows()?"08A8AE941E6FCB14A2CA15FFFB4B93732F0F81D965978871141D89C48ECFCFE3":"1D1285E20F7F2CFA1D12BFCA26503BF0DC67BA4FCD78A138804836A1386F3394");
        // Supported .NET resolver API; no binding/framework internals or system-SDL fallback.
        NativeLibrary.SetDllImportResolver(typeof(SDL3).Assembly,(name,_,_)=>name=="SDL3"?NativeLibrary.Load(SdlPath):0);
        if(SDL_GetVersion()!=3005000)throw new InvalidDataException("Expected packaged SDL 3.5.0 snapshot.");
        if(sizeof(SDL_Event)!=128 || sizeof(SDLBool)!=1)throw new InvalidDataException("Unexpected SDL event/bool ABI.");
    }
    public static void Check(bool success,string operation)
    {
        if(!success)throw new InvalidOperationException(operation+": "+SDL_GetError());
    }
    /// <summary>Wayland has no explicit mouse capture; the compositor's implicit
    /// grab already keeps pointer events on the window while a button is held.</summary>
    public static void CaptureMouse(bool value,string operation)
    {
        if(!SDL_CaptureMouse(value)&&SDL_GetCurrentVideoDriver()!="wayland")Check(false,operation);
    }
}
